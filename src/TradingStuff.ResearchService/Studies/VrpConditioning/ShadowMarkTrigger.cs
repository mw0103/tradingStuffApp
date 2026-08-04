using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// <c>ShadowMarks:*</c> — the daily trigger for <c>POST /research/shadow-marks/run</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defaults ON, for the reason <c>PaperCaptureOptions</c> gives.</b> The opt-ins that default off
/// (<c>PaperAutomation:Enabled</c>, <c>Backfill:Enabled</c>, <c>OptionChains:Enabled</c>) guard
/// surfaces that TRADE or that consume a broker/vendor budget. This one guards a surface that only
/// reads and upserts one row a day, and the thing it protects against is a missing day in the
/// protocol's Phase 1 record — which renders as a permanent gap in <c>GET /research/shadow-marks</c>
/// and cannot be filled in later at the moment it was supposed to describe.
/// </para>
/// <para>
/// It still degrades to a no-op without a <c>trading</c> connection string, and it can reach nothing
/// that places an order: the run it drives asks the planner for an INTENT against the account id
/// <c>"SHADOW"</c>, which is not an account the execution plane will accept.
/// </para>
/// </remarks>
public sealed class ShadowMarkTriggerOptions
{
    /// <summary>Exactly <c>"false"</c> switches the trigger off. Anything else, including null, leaves it on.</summary>
    public string? Enabled { get; set; }

    /// <summary>True unless the exact opt-out value is present.</summary>
    public bool IsEnabled => !string.Equals(Enabled, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>ISessionClock</c> calendar whose sessions the mark is dated by. NYSE, matching the planner's.</summary>
    public string Calendar { get; set; } = "NYSE";

    /// <summary>Only sessions with this label count as a trading date to mark.</summary>
    public string SessionLabel { get; set; } = "RTH";

    /// <summary>
    /// UTC time of day, on the day AFTER the session closed, at which that session's mark is due.
    /// </summary>
    /// <remarks>
    /// <b>00:10 UTC is not a preference; it is a consequence of where the daily close comes from.</b>
    /// Backfill slices that contain "now" are never claimed by design, so the same-evening VIX daily
    /// close does not arrive from the live recorder — it arrives from the 1-day-cadence job
    /// <c>vix-daily-trades-2026h2</c>, whose slice for a session becomes claimable at 00:00 UTC the
    /// following day. Ten minutes past that is the earliest instant at which the input this mark
    /// needs can exist. Marking at the US close instead would produce a run whose only possible
    /// answer is the forecaster's "no VIX close for the mark date" refusal, every single day.
    /// See <see cref="AfterCloseMinutes"/> for the schedule this becomes once that stops being true.
    /// </remarks>
    public TimeSpan RunAtUtc { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When set, schedules the mark this many minutes after the session's own close instead of at
    /// <see cref="RunAtUtc"/>, and <see cref="RunAtUtc"/> is then ignored.
    /// </summary>
    /// <remarks>
    /// This is the switch to throw once the live recorder demonstrably lands same-day daily closes:
    /// <c>ShadowMarks__AfterCloseMinutes=20</c> is 16:20 ET, and it is expressed relative to the
    /// calendar's close rather than as a UTC clock time precisely so it does not need re-entering
    /// twice a year — 16:00 ET is 20:00 UTC in EDT and 21:00 UTC in EST, and a fixed UTC time would
    /// silently fire an hour early or late across every DST boundary. Null until then: firing at the
    /// close today would only manufacture refusals. The runbook records the evidence to look for
    /// before flipping it (docs/plans/paper-test/runbook.md).
    /// </remarks>
    public int? AfterCloseMinutes { get; set; }

    /// <summary>
    /// How long after a mark falls due the trigger will still run it, if the process was not up.
    /// </summary>
    /// <remarks>
    /// Twelve hours, and the number is chosen by what it must NOT reach rather than by generosity:
    /// 00:10 UTC + 12h is 12:10 UTC, which is 08:10 ET — before the 09:30 open on either side of a
    /// DST boundary. So a restart any time overnight still lands the mark with the planner reading a
    /// closed market, exactly as a 00:10 run would. Past the open the planner would quote a LIVE
    /// market and upsert that over the prior date's intent, which is a different measurement wearing
    /// the same date. Missing the window leaves a visible gap instead, which is the house rule.
    /// </remarks>
    public int CatchUpWindowMinutes { get; set; } = 720;

    /// <summary>Seconds between checks. A check with nothing due does no work and no I/O.</summary>
    public int PollSeconds { get; set; } = 60;
}

/// <summary>
/// Fires one <c>POST /research/shadow-marks/run</c> per trading date, so the protocol's Phase 1
/// standing requirement stops depending on a human remembering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Session clock, not wall clock</b>, the same discipline as <c>PaperCaptureService</c>: what is
/// due is derived from the calendar's closed sessions, so holidays and half days need no special
/// case and DST cannot shift the schedule under it.
/// </para>
/// <para>
/// <b>Idempotent, so a redundant fire is harmless.</b> The run upserts one row per mark date. The
/// in-memory <c>_lastFired</c> only stops this process re-running a date it already did inside the
/// catch-up window; it is not the idempotency guarantee and is deliberately not persisted — a
/// restart that re-runs a date costs one bar load and rewrites the same row.
/// </para>
/// <para>
/// <b>Exactly one instance should run this.</b> The auxiliary ResearchService instances in the
/// AppHost (the chain drainers and the backfill drainer) set <c>ShadowMarks__Enabled=false</c>: a
/// second copy would be correct but would repeat a three-year bar load for nothing.
/// </para>
/// </remarks>
public sealed class ShadowMarkTrigger(
    IServiceScopeFactory scopeFactory,
    ISessionClock sessionClock,
    IOptions<ShadowMarkTriggerOptions> options,
    TimeProvider timeProvider,
    ILogger<ShadowMarkTrigger> logger)
    : BackgroundService
{
    private readonly ShadowMarkTriggerOptions _settings = options.Value;

    /// <summary>The last trading date this process ran. Not the idempotency guarantee — see the remarks.</summary>
    private DateOnly? _lastFired;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.IsEnabled)
        {
            logger.LogInformation(
                "The daily shadow-mark trigger is switched off (ShadowMarks:Enabled = '{Enabled}'). Marks are " +
                "then an operator step — one POST /research/shadow-marks/run per trading date — and every day " +
                "nobody runs it shows as a gap in GET /research/shadow-marks.",
                _settings.Enabled);
            return;
        }

        logger.LogInformation(
            "The daily shadow-mark trigger is armed: {Schedule}, calendar {Calendar}/{Label}, catch-up window " +
            "{CatchUp} minutes.",
            _settings.AfterCloseMinutes is { } after
                ? $"{after} minute(s) after each session close"
                : $"{_settings.RunAtUtc:hh\\:mm} UTC on the day after each session close",
            _settings.Calendar, _settings.SessionLabel, _settings.CatchUpWindowMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ONE try around the whole check, for the reason PaperCaptureService documents: this host
            // runs with BackgroundServiceExceptionBehavior.Ignore, so anything escaping ExecuteAsync
            // stops the trigger silently and the record simply stops growing.
            try
            {
                await TickAsync(timeProvider.GetUtcNow(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The shadow-mark trigger failed; it will retry on the next poll.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.PollSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One check: run the due mark, if there is one. Internal so a test can drive it without timers.</summary>
    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (DueTradingDate(sessionClock, _settings, now, _lastFired) is not { } tradingDate)
        {
            return;
        }

        // Claimed BEFORE the run, not after: a run that throws must not be retried every poll for the
        // rest of the catch-up window. The failure is logged, the date shows as a gap, and the next
        // day's mark is unaffected — which is the honest outcome, and the one the protocol asks for.
        _lastFired = tradingDate;

        // A scope per run because the run's dependencies are resolved per use and this service is a
        // singleton; resolving them once at construction would pin them for the process's life.
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var outcome = await VolShadowMarkEndpoints.RunAsync(
            services.GetRequiredService<VolResidualBarLoader>(),
            sessionClock,
            services.GetRequiredService<VolShadowMarkStore>(),
            services.GetRequiredService<SpyShortVolPlanner>(),
            logger,
            cancellationToken);

        switch (outcome.Status)
        {
            case VolShadowMarkRunStatus.NotConfigured:
                logger.LogWarning(
                    "The shadow mark for {TradingDate} could not run: no 'trading' connection string. " +
                    "That date will show as a gap in GET /research/shadow-marks.",
                    tradingDate);
                break;

            case VolShadowMarkRunStatus.Refused:
                logger.LogWarning(
                    "The shadow mark for {TradingDate} was refused: {Refusal} Nothing was persisted; the date " +
                    "shows as a gap until the missing input lands and an operator re-runs it.",
                    tradingDate, outcome.Refusal);
                break;

            default:
                logger.LogInformation(
                    "Recorded the shadow mark for {MarkDate} (trigger fired for trading date {TradingDate}).",
                    outcome.Mark?.MarkDate, tradingDate);
                break;
        }
    }

    /// <summary>
    /// The trading date whose mark is due at <paramref name="now"/>, or null if none is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure over <see cref="ISessionClock"/>, so the whole schedule is testable without a database,
    /// a gateway, or a clock that has to be waited out — the same seam
    /// <c>PaperCaptureService.DueSessions</c> uses, and for the same reason.
    /// </para>
    /// <para>
    /// The due instant is derived from the SESSION, never from "today". Anchoring on the UTC date the
    /// process happens to be looking at would make Saturday, Sunday and Monday all resolve to
    /// Friday's session and all three read as due, so a weekend would re-run Friday's mark twice for
    /// nothing. Anchored on the session, Friday's mark is due Saturday 00:10 UTC and its window has
    /// closed by Sunday.
    /// </para>
    /// </remarks>
    internal static DateOnly? DueTradingDate(
        ISessionClock sessionClock, ShadowMarkTriggerOptions settings, DateTimeOffset now, DateOnly? lastFired)
    {
        if (MostRecentClosedSession(sessionClock, settings, now) is not { } session)
        {
            return null;
        }

        var due = settings.AfterCloseMinutes is { } afterClose
            ? session.CloseUtc + TimeSpan.FromMinutes(afterClose)
            // The day AFTER the close, in UTC, at the configured time of day: that is when the
            // backfill slice carrying this session's daily close becomes claimable. See RunAtUtc.
            : new DateTimeOffset(session.CloseUtc.UtcDateTime.Date.AddDays(1), TimeSpan.Zero) + settings.RunAtUtc;

        if (now < due || now >= due + TimeSpan.FromMinutes(Math.Max(settings.CatchUpWindowMinutes, 1)))
        {
            return null;
        }

        return lastFired == session.TradingDate ? null : session.TradingDate;
    }

    /// <summary>The most recently closed session on the configured calendar, or null if the window holds none.</summary>
    /// <remarks>
    /// The lookback is far wider than any run of holidays so that a long exchange break cannot make
    /// the trigger conclude there is nothing to mark — the bound that matters is the due window
    /// above, not this one.
    /// </remarks>
    private static TradingSession? MostRecentClosedSession(
        ISessionClock sessionClock, ShadowMarkTriggerOptions settings, DateTimeOffset now)
    {
        var today = sessionClock.TradingDateOf(settings.Calendar, now);

        return sessionClock
            .SessionsBetween(settings.Calendar, today.AddDays(-21), today.AddDays(1))
            .Where(session => session.Label.Equals(settings.SessionLabel, StringComparison.OrdinalIgnoreCase))
            .Where(session => session.CloseUtc <= now)
            .OrderByDescending(session => session.CloseUtc)
            .FirstOrDefault();
    }
}
