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

    /// <summary>
    /// How long to wait before attempting a date again after a run REFUSED it.
    /// </summary>
    /// <remarks>
    /// A refusal is the one outcome the catch-up window exists for, and it must not be terminal.
    /// <see cref="RunAtUtc"/> is by construction the EARLIEST instant this mark's input can exist —
    /// the moment the daily-close backfill slice becomes claimable — so a refusal at 00:10 most often
    /// means the coordinator has not drained that slice yet, which is a matter of minutes, not of
    /// days. Fifteen minutes gives roughly 48 attempts across the 720-minute window without polling
    /// a three-year bar load every 60 seconds. Retrying costs a bar load; NOT retrying costs the day.
    /// </remarks>
    public int RefusalRetryMinutes { get; set; } = 15;
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
/// <b>A date is CLAIMED only when the run demonstrably produced that date's row.</b> Three outcomes
/// deliberately do not claim it, because in each the mark this trigger was firing for still does not
/// exist and the catch-up window is exactly the budget for trying again: a refusal (the missing
/// daily close usually lands minutes later), a mark whose <c>MarkDate</c> is not the date that was
/// due (see <see cref="TickAsync"/>), and — indirectly — a process restart. A run that THREW does
/// claim: an unexplained failure repeated 48 times is a log flood, not a recovery.
/// </para>
/// <para>
/// <b>Idempotent per mark date, but a re-run is not free.</b> The run upserts one row per mark date,
/// so a redundant fire cannot duplicate anything. It is not, however, a no-op: <c>planner_intent</c>
/// is a LIVE gateway read taken at run time, so a second run replaces the stored intent with
/// whatever the gateway says now — and a re-run during an outage can overwrite a good recorded
/// intent with a "Gateway unreachable" refusal. That is the cost the retry policy above is priced
/// against, and it is why the retry interval is minutes rather than the poll interval.
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

    /// <summary>The last trading date this process CLAIMED. Not the idempotency guarantee — see the remarks.</summary>
    private DateOnly? _lastFired;

    /// <summary>
    /// The last attempt at a date that was not claimed, so a refusal retries on a spacing of its own
    /// rather than on the poll interval. Scoped to the date: a new date is attempted immediately.
    /// </summary>
    private (DateOnly Date, DateTimeOffset At)? _lastAttempt;

    /// <summary>
    /// Test seam: the run this trigger drives. Production leaves it null and calls
    /// <see cref="VolShadowMarkEndpoints.RunAsync"/>, which is the same code path
    /// <c>POST /research/shadow-marks/run</c> takes.
    /// </summary>
    /// <remarks>
    /// It exists because the outcome HANDLING — which outcomes claim a date and which leave it for
    /// the catch-up window — is the part of this class that can silently lose a day, and it cannot be
    /// exercised through a real run without a database, a gateway and three years of bars. Both
    /// defects this seam was added to pin (a refusal burning the date; a mark for the wrong date
    /// claiming the right one) were invisible to every test that drove only the schedule arithmetic.
    /// </remarks>
    internal Func<IServiceProvider, CancellationToken, Task<VolShadowMarkRunOutcome>>? RunOverride { get; init; }

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
            // Filtered on the STOPPING TOKEN, not on the exception type. An OperationCanceledException
            // reaching here is usually not a shutdown at all — an HttpClient timeout inside the
            // planner's quote read surfaces as TaskCanceledException, which derives from it — and
            // `ex is not OperationCanceledException` would let that one escape, fault the service, and
            // (under BackgroundServiceExceptionBehavior.Ignore, set in Program.cs) stop the trigger
            // for the life of the process with nothing logged. The token is the only thing that
            // actually distinguishes "we are shutting down" from "a dependency timed out".
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
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

    /// <summary>
    /// One check: run the due mark, if there is one, and decide whether the date is now settled.
    /// </summary>
    /// <remarks>
    /// Internal so a test can drive it without timers. The outcome handling below is the whole point
    /// of the method — see <see cref="RunOverride"/> for why it is separately testable.
    /// </remarks>
    internal async Task TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (DueTradingDate(sessionClock, _settings, now, _lastFired) is not { } tradingDate)
        {
            return;
        }

        // Spacing between attempts at a date that is due but not yet settled. Without it an
        // unsettled date would re-run a three-year bar load on every poll for the whole window.
        if (_lastAttempt is { } previous
            && previous.Date == tradingDate
            && now < previous.At + TimeSpan.FromMinutes(Math.Max(_settings.RefusalRetryMinutes, 1)))
        {
            return;
        }

        _lastAttempt = (tradingDate, now);

        // A scope per run because the run's dependencies are resolved per use and this service is a
        // singleton; resolving them once at construction would pin them for the process's life.
        using var scope = scopeFactory.CreateScope();

        VolShadowMarkRunOutcome outcome;

        try
        {
            outcome = await RunAsync(scope.ServiceProvider, cancellationToken);
        }
        catch
        {
            // A run that THREW is claimed, unlike a run that refused. A refusal is an EXPLAINED
            // absence with a known remedy — the missing input lands and the next attempt succeeds —
            // whereas an unexplained fault repeated every 15 minutes for 12 hours is a log flood that
            // buries the one line saying what broke. Shutdown claims nothing: the process is going.
            if (!cancellationToken.IsCancellationRequested)
            {
                _lastFired = tradingDate;
            }

            throw;
        }

        switch (outcome.Status)
        {
            // Claimed: a missing connection string is fixed at startup and cannot resolve inside the
            // window, so retrying it 48 times would only repeat the same warning.
            case VolShadowMarkRunStatus.NotConfigured:
                _lastFired = tradingDate;
                logger.LogWarning(
                    "The shadow mark for {TradingDate} could not run: no 'trading' connection string. " +
                    "That date will show as a gap in GET /research/shadow-marks.",
                    tradingDate);
                break;

            // NOT claimed. This is the outcome the catch-up window was built for: RunAtUtc is the
            // earliest instant the daily close can exist, so the usual cause is that the backfill
            // coordinator has not drained that slice yet — minutes away, not a lost day.
            case VolShadowMarkRunStatus.Refused:
                logger.LogWarning(
                    "The shadow mark for {TradingDate} was refused: {Refusal} Nothing was persisted. " +
                    "Retrying in {RetryMinutes} minute(s) while the catch-up window is open.",
                    tradingDate, outcome.Refusal, Math.Max(_settings.RefusalRetryMinutes, 1));
                break;

            // NOT claimed, and this one is the subtle case. The forecaster dates its mark from the
            // last COMPLETE SPX session in research.bars, which is not necessarily the last session
            // the CALENDAR closed: if the due date's bars have not landed, the run happily builds the
            // PREVIOUS date's mark and upserts it — overwriting a good row with a fresh planner_intent
            // in the process. Claiming the due date on the strength of that would retire it having
            // never written it, and it would never be marked. The rewrite already happened and cannot
            // be undone here; leaving the date unclaimed is what lets it be marked when its bars land.
            case VolShadowMarkRunStatus.Recorded when outcome.Mark is { } other && other.MarkDate != tradingDate:
                // Each placeholder appears ONCE. A message template counts occurrences positionally,
                // so a repeated {TradingDate} needs a repeated argument — and this is the one
                // diagnostic that tells an operator a date was skipped, so it has to render.
                logger.LogWarning(
                    "The shadow-mark run fired for trading date {TradingDate} but produced a mark for " +
                    "{MarkDate}, so research.bars has no complete session for the due date yet and the " +
                    "earlier date's row was rewritten instead. The due date is NOT recorded and stays " +
                    "due — check the recorder and the backfill drain for that session.",
                    tradingDate, other.MarkDate);
                break;

            default:
                _lastFired = tradingDate;
                logger.LogInformation(
                    "Recorded the shadow mark for {MarkDate}.", outcome.Mark?.MarkDate);
                break;
        }
    }

    /// <summary>The run itself, or the test seam standing in for it.</summary>
    private Task<VolShadowMarkRunOutcome> RunAsync(
        IServiceProvider services, CancellationToken cancellationToken) =>
        RunOverride is { } run
            ? run(services, cancellationToken)
            : VolShadowMarkEndpoints.RunAsync(
                services.GetRequiredService<VolResidualBarLoader>(),
                sessionClock,
                services.GetRequiredService<VolShadowMarkStore>(),
                services.GetRequiredService<SpyShortVolPlanner>(),
                logger,
                cancellationToken);

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
