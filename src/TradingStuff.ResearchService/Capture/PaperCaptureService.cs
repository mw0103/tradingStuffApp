using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Capture;

/// <summary>
/// Captures the raw broker state the paper-run protocol's shadow record items 6-8 depend on: one
/// account snapshot per trading date after the session close, plus that session's executions.
/// </summary>
/// <remarks>
/// <para>
/// <b>This component OBSERVES. It never influences.</b> It reads the gateway's read-only account
/// surface and writes two append-only tables. It does not touch <c>PaperAutomationService</c>, any
/// planner, any signal, or any gate, and there is no path from here to an order. Its output is
/// deliberately raw: derived analytics (the protocol's items 10-11) are reproducible from these rows
/// at any later date, whereas a fill or a margin figure nobody captured at the time is gone.
/// </para>
/// <para>
/// <b>Session clock, not wall clock.</b> The trigger is "this calendar's session has been closed for
/// <see cref="PaperCaptureOptions.CloseDelayMinutes"/>", resolved through <see cref="ISessionClock"/>
/// — the only type permitted to convert a timezone. A wall-clock schedule would drift with DST, fire
/// on holidays, and date a capture by its UTC day, which cuts a US session in half.
/// </para>
/// <para>
/// <b>Idempotent per trading date.</b> A pass skips dates that already have a snapshot, and the
/// schema enforces it anyway (one snapshot per date, unique exec_id) so two overlapping processes
/// cannot both write. Re-running is the intended recovery path, not an anomaly.
/// </para>
/// </remarks>
public sealed class PaperCaptureService(
    IbkrGatewayClient gateway,
    ISessionClock sessionClock,
    PaperCaptureStore store,
    IOptions<PaperCaptureOptions> options,
    TimeProvider timeProvider,
    ILogger<PaperCaptureService> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Summary tags read into the typed columns, in the order preference is applied.</summary>
    private static readonly string[] BuyingPowerTags = ["BuyingPower", "AvailableFunds", "ExcessLiquidity"];

    private readonly PaperCaptureOptions _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.IsEnabled)
        {
            logger.LogInformation(
                "Paper capture is switched off (PaperCapture:Enabled = '{Enabled}'). No fills, margin, or " +
                "positions will be recorded, and the sessions that pass meanwhile cannot be captured later.",
                _settings.Enabled);
            return;
        }

        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            logger.LogWarning(
                "Paper capture has no 'trading' connection string and will not run. Every session that " +
                "closes meanwhile is unrecoverable — capture is not backfillable.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // ONE try around the whole pass, for the reason RecorderOrchestrator documents at
            // length: anything escaping ExecuteAsync faults the background service, and while this
            // host is configured with BackgroundServiceExceptionBehavior.Ignore (Program.cs) a
            // faulted service simply stops, silently, and capture ends without anyone noticing.
            try
            {
                await RunPassAsync(timeProvider.GetUtcNow(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Paper capture pass failed; will retry on the next interval.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.IntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass: capture every closed session inside the lookback window that has no snapshot yet.
    /// Internal so a test can drive a pass without the loop's timers.
    /// </summary>
    internal async Task RunPassAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var session in DueSessions(sessionClock, _settings, now))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Cheap pre-check so a captured date costs no gateway traffic. It is not the idempotency
            // guarantee — the unique index is; see PaperCaptureStore.
            if (await store.HasCaptureAsync(session.TradingDate, cancellationToken))
            {
                continue;
            }

            await CaptureSessionAsync(session, now, cancellationToken);
        }
    }

    /// <summary>
    /// The sessions a pass at <paramref name="now"/> should capture, newest first.
    /// </summary>
    /// <remarks>
    /// Pure over <see cref="ISessionClock"/>, so the trigger arithmetic is testable without a
    /// gateway or a database. Newest-first ordering is load-bearing rather than cosmetic: an
    /// execution whose TWS time string could not be parsed cannot be attributed to a session by its
    /// timestamp, so it is claimed by the most recent uncaptured session — which is where it almost
    /// certainly belongs, and the verbatim string is on the row for anyone who needs to check.
    /// </remarks>
    internal static IReadOnlyList<TradingSession> DueSessions(
        ISessionClock sessionClock, PaperCaptureOptions settings, DateTimeOffset now)
    {
        var today = sessionClock.TradingDateOf(settings.Calendar, now);
        var delay = TimeSpan.FromMinutes(settings.CloseDelayMinutes);

        // The calendar window is deliberately far wider than the session count: holidays and long
        // weekends must not be able to shrink the recovery window. What bounds the result is the
        // session count, applied after the close filter.
        return
        [
            .. sessionClock.SessionsBetween(settings.Calendar, today.AddDays(-21), today)
                .Where(session => session.Label.Equals(settings.SessionLabel, StringComparison.OrdinalIgnoreCase))
                .Where(session => now >= session.CloseUtc + delay)
                .OrderByDescending(session => session.TradingDate)
                .Take(Math.Max(settings.LookbackSessions, 1)),
        ];
    }

    /// <summary>
    /// How far before the session open the executions filter is set.
    /// </summary>
    /// <remarks>
    /// Deliberate over-fetch, and the reason is a broker ambiguity this code cannot resolve: which
    /// clock TWS reads <c>ExecutionFilter.Time</c> against is not established (see
    /// <c>IbkrExecutionsClient.FilterTimeFormat</c>). Sent as the session open and read as exchange
    /// time, the bound would exclude every fill before 09:30 ET — all of them — and the pass would
    /// then write a snapshot claiming the date was captured with zero fills, permanently, because
    /// the tables are append-only. Twelve hours is past every offset TWS could plausibly apply, and
    /// the surplus costs nothing: <see cref="SessionExecutions"/> applies the real window and
    /// <c>exec_id</c> uniqueness discards anything another session already owns.
    /// </remarks>
    private static readonly TimeSpan ExecutionFilterOverfetch = TimeSpan.FromHours(12);

    private async Task CaptureSessionAsync(
        TradingSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AccountSummaryRead summary;
        AccountPositionsRead positions;
        AccountExecutionsRead executions;

        try
        {
            summary = await gateway.GetAccountSummaryAsync(_settings.AccountId, cancellationToken);
            positions = await gateway.GetPositionsAsync(_settings.AccountId, cancellationToken);

            // Bounded below by the session OPEN, not by a UTC midnight — the trading date belongs to
            // the session, and a UTC day boundary falls in the middle of a US afternoon — and then
            // deliberately over-fetched past it. See ExecutionFilterOverfetch.
            executions = await gateway.GetExecutionsAsync(
                _settings.AccountId, session.OpenUtc - ExecutionFilterOverfetch, cancellationToken);
        }
        catch (GatewayReadException ex)
        {
            // All three reads share one try on purpose: the pass is all-or-nothing. A snapshot row
            // written without its executions would satisfy the idempotency check and the fills would
            // never be retried, which is the one way this design could lose data silently.
            var recorded = await store.RecordRefusalAsync(
                session.TradingDate, now, ex.RefusalKind, ex.Message,
                CaptureSources.GatewayAccount, cancellationToken);

            logger.Log(
                recorded ? LogLevel.Warning : LogLevel.Debug,
                "Paper capture for {TradingDate} refused ({Kind}): {Reason} " +
                "The refusal is on the record and the next pass will retry.",
                session.TradingDate, ex.RefusalKind, ex.Message);

            return;
        }

        var capture = BuildCapture(
            session, now, summary, positions, executions, NextSessionOpenUtc(session), IsLate(session, now));

        var outcome = await store.SaveAsync(capture, cancellationToken);

        if (!outcome.Stored)
        {
            // Another process (or an overlapping pass) got there first. Not an error: the schema is
            // what makes the race safe, and this is it working.
            logger.LogInformation(
                "Paper capture for {TradingDate} was already recorded; this pass added {Fills} fill(s).",
                session.TradingDate, outcome.FillsWritten);
            return;
        }

        logger.LogInformation(
            "Captured the paper account for {TradingDate}: {Positions} position(s), {Written} of {Pulled} " +
            "execution(s) new, net liquidation {NetLiquidation}, maintenance margin {MaintenanceMargin}.",
            session.TradingDate, capture.PositionCount, outcome.FillsWritten, outcome.FillsPulled,
            capture.NetLiquidation, capture.MaintenanceMargin);

        if (executions.CommissionsMissing > 0)
        {
            logger.LogWarning(
                "{Missing} captured execution(s) for {TradingDate} carry no commission: TWS did not report " +
                "one within the pull. Those rows record no commission rather than a zero one.",
                executions.CommissionsMissing, session.TradingDate);
        }
    }

    /// <summary>
    /// The open of the next session on this calendar, which is where this session's window ends.
    /// </summary>
    /// <remarks>
    /// Null when the calendar has no later session, which the caller treats as a 24-hour fallback.
    /// The lookahead is generous because it only has to clear the longest exchange holiday break.
    /// </remarks>
    private DateTimeOffset? NextSessionOpenUtc(TradingSession session) =>
        sessionClock
            .SessionsBetween(_settings.Calendar, session.TradingDate.AddDays(1), session.TradingDate.AddDays(14))
            .Where(next => next.Label.Equals(_settings.SessionLabel, StringComparison.OrdinalIgnoreCase))
            .OrderBy(next => next.OpenUtc)
            .Select(next => (DateTimeOffset?)next.OpenUtc)
            .FirstOrDefault();

    /// <summary>
    /// Whether this snapshot describes a session other than the one that just closed.
    /// </summary>
    /// <remarks>
    /// The account read is always of NOW. On a same-evening pass that is the session's own end
    /// state, which is what shadow record item 8 asks for. On a RECOVERY pass — the gateway was
    /// down all night, and Monday's pass finally captures Friday — it is Monday's net liquidation,
    /// margin and positions written against Friday's trading date, in an append-only table that can
    /// never be corrected. That row is not wrong to keep (it is the only reading there will ever be),
    /// but it must not be read as Friday's close, so it carries a different
    /// <c>capture_source</c> — the column the schema already documents as provenance.
    /// </remarks>
    internal static bool IsLate(TradingSession session, DateTimeOffset now, int timelyWindowMinutes) =>
        now > session.CloseUtc + TimeSpan.FromMinutes(timelyWindowMinutes);

    private bool IsLate(TradingSession session, DateTimeOffset now) =>
        IsLate(session, now, _settings.TimelyWindowMinutes);

    /// <summary>
    /// Assembles the capture from the three raw reads. Pure, so the projection from TWS's loose
    /// tag/value strings onto the typed money columns is testable without a broker.
    /// </summary>
    /// <param name="nextSessionOpenUtc">Upper bound of the execution window — see <see cref="SessionExecutions"/>.</param>
    /// <param name="late">
    /// True when the account read no longer describes this session's end state. Changes only the
    /// snapshot's <c>capture_source</c>; the fills are exact regardless, since each carries its own
    /// execution time.
    /// </param>
    internal static PaperAccountCapture BuildCapture(
        TradingSession session,
        DateTimeOffset snapshotAt,
        AccountSummaryRead summary,
        AccountPositionsRead positions,
        AccountExecutionsRead executions,
        DateTimeOffset? nextSessionOpenUtc = null,
        bool late = false)
    {
        var currency = PreferredCurrency(summary.Tags);

        return new PaperAccountCapture(
            session.TradingDate,
            snapshotAt,
            summary.Account,
            ReadTag(summary.Tags, "NetLiquidation"),
            ReadTag(summary.Tags, "MaintMarginReq", "FullMaintMarginReq"),
            ReadTag(summary.Tags, "InitMarginReq", "FullInitMarginReq"),
            ReadTag(summary.Tags, "ExcessLiquidity"),
            ReadTag(summary.Tags, "AvailableFunds"),
            ReadTag(summary.Tags, BuyingPowerTags),
            ReadTag(summary.Tags, "GrossPositionValue"),
            currency,
            JsonSerializer.Serialize(summary.Tags, SerializerOptions),
            JsonSerializer.Serialize(positions.Positions, SerializerOptions),
            positions.Positions.Count,
            [
                .. SessionExecutions(session, executions, nextSessionOpenUtc)
                    .Select(execution => ToFill(session, execution)),
            ],
            late ? CaptureSources.GatewayAccountLate : CaptureSources.GatewayAccount);
    }

    /// <summary>
    /// The executions that belong to this session: <c>[OpenUtc, next session's open)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TWS's executions filter takes a lower time bound only — and an over-fetched one at that (see
    /// <see cref="ExecutionFilterOverfetch"/>) — so the real window is applied here.
    /// </para>
    /// <para>
    /// <b>The upper bound runs to the NEXT session's open, not to the close.</b> Consecutive
    /// sessions' windows therefore abut exactly, and no instant falls between two of them. That is
    /// not tidiness: exercise and assignment reports post hours after the bell, an expiring short
    /// put spread's assignment is precisely the protocol's item 6/9 input, and a window that stopped
    /// an hour after the close would drop every one of them from EVERY capture, permanently. Where
    /// the calendar has no next session the bound falls back to 24 hours, which covers the same
    /// evening.
    /// </para>
    /// <para>
    /// Sessions never overlap, so an execution belongs to exactly one window and the newest-first
    /// order of <see cref="DueSessions"/> plus <c>exec_id</c> uniqueness settles anything ambiguous
    /// on first-insert-wins. An execution whose time string could not be parsed has no timestamp to
    /// bound — it is kept rather than dropped, because a fill that is not captured is gone, and the
    /// verbatim string travels with the row so a later reader can re-attribute it.
    /// </para>
    /// </remarks>
    internal static IEnumerable<AccountExecutionRead> SessionExecutions(
        TradingSession session, AccountExecutionsRead executions, DateTimeOffset? nextSessionOpenUtc = null)
    {
        var upperBound = nextSessionOpenUtc ?? session.CloseUtc.AddDays(1);

        return executions.Executions.Where(execution =>
            execution.ExecutedAt is not { } at || (at >= session.OpenUtc && at < upperBound));
    }

    private static PaperFill ToFill(TradingSession session, AccountExecutionRead execution) =>
        new(session.TradingDate,
            execution.Account,
            execution.ExecId,
            execution.PermId == 0 ? null : execution.PermId,
            execution.OrderId == 0 ? null : execution.OrderId,
            execution.ClientId,
            execution.ConId,
            execution.Symbol,
            execution.SecType,
            execution.Expiration,
            execution.Strike,
            execution.Right,
            execution.TradingClass,
            execution.Multiplier,
            execution.Side,
            execution.Quantity,
            execution.Price,
            execution.ExecutedAtRaw,
            execution.ExecutedAt,
            execution.Exchange,
            execution.Commission,
            execution.CommissionCurrency,
            execution.RealizedPnL,
            CaptureSources.GatewayExecutions);

    /// <summary>
    /// The first parseable value for any of <paramref name="tags"/>, in preference order, USD first.
    /// </summary>
    /// <remarks>
    /// Null rather than zero when no tag matches. TWS does not serve every tag for every account
    /// type, and a defaulted zero maintenance margin reads as an unmargined position — precisely the
    /// silently-fabricated input the account client's own remarks warn about.
    /// </remarks>
    internal static decimal? ReadTag(IReadOnlyList<AccountSummaryTagRead> rows, params string[] tags)
    {
        foreach (var tag in tags)
        {
            var matches = rows
                .Where(row => row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase));

            foreach (var match in matches)
            {
                if (decimal.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>The currency the typed money columns are denominated in, as reported.</summary>
    private static string? PreferredCurrency(IReadOnlyList<AccountSummaryTagRead> rows) =>
        rows.FirstOrDefault(row =>
                row.Tag.Equals("NetLiquidation", StringComparison.OrdinalIgnoreCase) &&
                row.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase))?.Currency
        ?? rows.FirstOrDefault(row => row.Tag.Equals("NetLiquidation", StringComparison.OrdinalIgnoreCase))?.Currency;
}
