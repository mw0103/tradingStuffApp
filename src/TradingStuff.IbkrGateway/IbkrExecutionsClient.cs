using System.Collections.Concurrent;
using System.Globalization;
using IBApi;
using Microsoft.Extensions.Options;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway;

/// <summary>One execution report exactly as TWS delivered it, plus its commission if one arrived.</summary>
/// <remarks>
/// Deliberately not mapped onto <c>FillReport</c> or <c>OptionContract</c>. This is a broker reading
/// destined for the append-only capture tables (research migration 024), and a leg whose expiry or
/// right the platform's option model cannot represent must still be recorded intact. The one
/// conversion done here is the <c>double</c>-to-<c>decimal</c> narrowing that every IBApi boundary
/// owes the rest of the system.
/// </remarks>
public sealed record IbkrExecutionRow(
    string ExecId,
    long PermId,
    int OrderId,
    int ClientId,
    string Account,
    int ConId,
    string Symbol,
    string SecType,
    DateOnly? Expiration,
    decimal? Strike,
    string? Right,
    string? TradingClass,
    int? Multiplier,
    string? Exchange,
    string Side,
    decimal Quantity,
    decimal Price,
    string ExecutedAtRaw,
    DateTimeOffset? ExecutedAt,
    decimal? Commission,
    string? CommissionCurrency,
    decimal? RealizedPnL);

/// <summary>
/// The answer to one <c>reqExecutions</c>, and what it could not establish.
/// </summary>
/// <param name="CommissionsMissing">
/// How many rows came back without a commission report. Reported rather than hidden: commission
/// arrives on a separate callback that is not sequenced against <c>execDetailsEnd</c>, so a nonzero
/// count means "ask again later", not "these trades were free".
/// </param>
public sealed record IbkrExecutionsReport(
    string Account,
    DateTimeOffset CapturedAt,
    DateTimeOffset SinceUtc,
    IReadOnlyList<IbkrExecutionRow> Executions,
    int CommissionsMissing);

/// <summary>
/// Where an in-flight <c>reqExecutions</c> waits for commission reports.
/// </summary>
/// <remarks>
/// <c>commissionAndFeesReport</c> is the one TWS callback in this adapter that carries NO request
/// id, so the registry cannot route it — <see cref="IbkrOrderTracker"/> solves that for orders it
/// placed by keeping its own execId map, and a solicited executions pull needs the same trick. It
/// is a type of its own, with no dependencies, so that <see cref="IbkrClientWrapper"/> and
/// <see cref="IbkrExecutionsClient"/> can both hold it without either depending on the other:
/// the wrapper is constructed by <see cref="IbkrConnection"/>, which this client depends on, so a
/// direct reference in either direction would close a DI cycle.
/// </remarks>
public sealed class ExecutionCommissionRouter
{
    private readonly ConcurrentDictionary<int, ExecutionsRequest> _active = new();

    internal void Attach(int requestId, ExecutionsRequest request) => _active[requestId] = request;

    internal void Detach(int requestId) => _active.TryRemove(requestId, out _);

    /// <summary>
    /// Offers a commission report to every in-flight executions pull. Each takes it only if it holds
    /// the matching execId, so an overlapping pull cannot claim another's commission.
    /// </summary>
    internal void Apply(CommissionAndFeesReport report)
    {
        foreach (var request in _active.Values)
        {
            request.ApplyCommission(report);
        }
    }
}

/// <summary>
/// One <c>reqExecutions</c> in flight: accumulates <c>execDetails</c> and completes on
/// <c>execDetailsEnd</c>, faulting on the shared <c>error</c> callback like every other request.
/// </summary>
internal sealed class ExecutionsRequest : IPendingRequest
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, (IbContract Contract, Execution Execution)> _executions = new();
    private readonly ConcurrentDictionary<string, CommissionAndFeesReport> _commissions = new();

    /// <summary>Completes on <c>execDetailsEnd</c>. Commissions may still be arriving after it.</summary>
    public Task Delivered => _completion.Task;

    public void Add(IbContract contract, Execution execution)
    {
        // Dedupe on execId for the same reason the order tracker does: executions replay after a
        // reconnect, and a replayed report is the same fill, not a second one.
        _executions.TryAdd(execution.ExecId, (contract, execution));
    }

    public void ApplyCommission(CommissionAndFeesReport report)
    {
        if (_executions.ContainsKey(report.ExecId))
        {
            _commissions[report.ExecId] = report;
        }
    }

    public void Complete() => _completion.TrySetResult();

    public void Fail(Exception error) => _completion.TrySetException(error);

    /// <summary>True once every execution received has a commission report to go with it.</summary>
    public bool CommissionsComplete => _commissions.Count >= _executions.Count;

    public IReadOnlyList<(IbContract Contract, Execution Execution, CommissionAndFeesReport? Commission)> Snapshot() =>
    [
        .. _executions.Values.Select(entry =>
            (entry.Contract,
             entry.Execution,
             _commissions.TryGetValue(entry.Execution.ExecId, out var commission) ? commission : null)),
    ];
}

/// <summary>
/// Read-only pull of the account's execution reports over the single TWS socket.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here places, modifies, or cancels an order, and nothing here feeds
/// <see cref="IbkrOrderTracker"/> — a solicited pull is routed to its own request by
/// <see cref="IbkrClientWrapper.execDetails"/> precisely so that replaying a past day's executions
/// cannot touch the live order path. This surface OBSERVES.
/// </para>
/// <para>
/// <b>Pacing.</b> One <c>reqExecutions</c> is one Normal-class socket message through
/// <see cref="Pacing.PacedSocket"/>, the same budget every non-order request draws on. Unlike
/// <c>reqHistoricalData</c> it has no separate TWS pacing window, and unlike <c>reqMktData</c> it
/// consumes no market-data line, so there is nothing extra to acquire or release. Calls are
/// serialised anyway: the intended caller runs once per trading date after the close, and a second
/// concurrent pull would only duplicate work while making the commission fan-out ambiguous.
/// </para>
/// </remarks>
public sealed class IbkrExecutionsClient(
    IbkrConnection connection,
    Pacing.PacedSocket socket,
    ExecutionCommissionRouter commissions,
    IOptions<IbkrOptions> options,
    ILogger<IbkrExecutionsClient> logger)
{
    /// <summary>
    /// The shape <c>ExecutionFilter.Time</c> is sent in. <b>Which clock TWS reads it against is NOT
    /// established.</b>
    /// </summary>
    /// <remarks>
    /// IBKR's own source comments the field as UTC, but TWS has historically interpreted it in the
    /// timezone configured for the API, and no version guarantee is documented. The consequence of
    /// guessing wrong is silent and unrecoverable: a bound sent as 13:30 UTC and read as 13:30 ET
    /// excludes every fill before 09:30 ET — i.e. all of them — and the capture pass then marks the
    /// date captured with zero fills, permanently, because the tables are append-only.
    /// <para>
    /// So this client does NOT resolve the ambiguity; callers must over-fetch past any plausible
    /// offset and apply their own window client-side. <c>PaperCaptureService</c> sends the session
    /// open less twelve hours for exactly that reason, and dedupes on exec_id.
    /// </para>
    /// </remarks>
    private const string FilterTimeFormat = "yyyyMMdd-HH:mm:ss";

    /// <summary>
    /// How long to keep listening for commission reports after <c>execDetailsEnd</c>.
    /// </summary>
    /// <remarks>
    /// <c>commissionAndFeesReport</c> is not sequenced against <c>execDetailsEnd</c> — the order
    /// tracker learned the same lesson from live fills, where commissions landed seconds after a
    /// terminal status. A short grace collects the overwhelming majority; whatever has not arrived
    /// is reported as missing rather than defaulted to zero.
    /// </remarks>
    private static readonly TimeSpan CommissionGrace = TimeSpan.FromSeconds(3);

    private readonly IbkrOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Every execution the account has reported since <paramref name="sinceUtc"/>.
    /// </summary>
    /// <param name="accountId">
    /// Optional. Must name a TWS-managed account when supplied; omit it for the account this gateway
    /// trades.
    /// </param>
    /// <param name="sinceUtc">
    /// Lower bound on execution time. <b>Over-fetch.</b> Which clock TWS reads it against is not
    /// established — see <see cref="FilterTimeFormat"/> — so a caller that needs a specific window
    /// must send a bound well past any plausible offset and narrow client-side.
    /// </param>
    /// <remarks>
    /// <c>ExecutionFilter.ClientId</c> is left at 0. <b>What that returns is not verified here.</b>
    /// IBKR documents executions as visible only to the API client that placed them, unless the
    /// connecting client id is TWS's configured <i>Master API Client ID</i>, in which case every
    /// client's executions are returned; a filter <c>ClientId</c> of 0 is then "do not filter by
    /// client" rather than a guarantee of cross-client visibility. Establishing which applies needs
    /// a socket, so it is recorded as an operational precondition in <c>docs/FOLLOWUP.md</c> §5
    /// rather than asserted here. If it turns out to be own-client-only, a fill placed by hand in
    /// TWS is invisible to the capture layer and the capture is incomplete without saying so.
    /// </remarks>
    public async Task<IbkrExecutionsReport> GetExecutionsAsync(
        string? accountId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var account = IbkrAccountClient.SelectAccount(
            connection.GetStatus().ManagedAccounts, accountId, _options.AccountId);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            return await ReadExecutionsAsync(account, sinceUtc, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IbkrExecutionsReport> ReadExecutionsAsync(
        string account,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var registry = connection.Registry;
        var requestId = registry.NextRequestId();
        var request = new ExecutionsRequest();

        var filter = new ExecutionFilter
        {
            AcctCode = account,
            Time = sinceUtc.ToUniversalTime().ToString(FilterTimeFormat, CultureInfo.InvariantCulture),
        };

        registry.Register(requestId, request);
        commissions.Attach(requestId, request);

        try
        {
            await socket.ReqExecutionsAsync(requestId, filter, cancellationToken);

            await request.Delivered.WaitAsync(
                TimeSpan.FromSeconds(_options.RequestTimeoutSeconds), cancellationToken);

            // execDetailsEnd has landed, so no further executions are coming; only the unsequenced
            // commission reports are still outstanding. Polled rather than slept flat so a pull
            // whose commissions all arrived returns immediately.
            await WaitForCommissionsAsync(request, cancellationToken);

            var rows = Project(request.Snapshot());
            var missing = rows.Count(row => row.Commission is null);

            if (missing > 0)
            {
                logger.LogWarning(
                    "{Missing} of {Total} execution(s) for {Account} came back without a commission report " +
                    "within {Grace}s; those rows record no commission rather than a zero one.",
                    missing, rows.Count, account, CommissionGrace.TotalSeconds);
            }

            logger.LogInformation(
                "Read {Count} execution(s) for {Account} since {Since:O}.", rows.Count, account, sinceUtc);

            return new IbkrExecutionsReport(account, DateTimeOffset.UtcNow, sinceUtc, rows, missing);
        }
        finally
        {
            // Both directions, always: a registry entry left behind swallows every later callback on
            // this id, and a router entry left behind keeps offering commissions to a dead request.
            registry.Remove(requestId);
            commissions.Detach(requestId);
        }
    }

    private static async Task WaitForCommissionsAsync(ExecutionsRequest request, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CommissionGrace;

        while (!request.CommissionsComplete && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    internal static IReadOnlyList<IbkrExecutionRow> Project(
        IReadOnlyList<(IbContract Contract, Execution Execution, CommissionAndFeesReport? Commission)> received)
    {
        var rows = new List<IbkrExecutionRow>(received.Count);

        foreach (var (contract, execution, commission) in received)
        {
            QuoteRequest.TryConvertPrice(execution.Price, out var price);

            rows.Add(new IbkrExecutionRow(
                execution.ExecId ?? string.Empty,
                execution.PermId,
                execution.OrderId,
                execution.ClientId,
                execution.AcctNumber ?? string.Empty,
                contract.ConId,
                contract.Symbol ?? string.Empty,
                contract.SecType ?? string.Empty,
                TryParseExpiration(contract.LastTradeDateOrContractMonth),
                QuoteRequest.TryReadStrike(contract.Strike),
                string.IsNullOrWhiteSpace(contract.Right) ? null : contract.Right,
                string.IsNullOrWhiteSpace(contract.TradingClass) ? null : contract.TradingClass,
                int.TryParse(contract.Multiplier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var multiplier)
                    ? multiplier
                    : null,
                string.IsNullOrWhiteSpace(execution.Exchange) ? null : execution.Exchange,
                execution.Side ?? string.Empty,
                execution.Shares,
                price,
                execution.Time ?? string.Empty,
                TryParseExecutionTime(execution.Time),
                // A commission report that never arrived is null, not zero — see the record's remarks.
                commission is null ? null : ToDecimal(commission.CommissionAndFees),
                commission?.Currency,
                commission is null ? null : ToDecimal(commission.RealizedPNL)));
        }

        return rows;
    }

    /// <summary>
    /// The execution instant, or null when TWS's string was not a shape this adapter recognises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE shape is accepted: the hyphenated <c>yyyyMMdd-HH:mm:ss</c> that TWS 10.x emits and that
    /// IBKR's API specifies as UTC. The space-separated legacy form is deliberately NOT accepted,
    /// even though parsing it would be trivial — that form predates the UTC specification and
    /// carries TWS-local wall-clock time with no offset, so reading it as UTC would silently
    /// mis-date every fill by the exchange's offset (four or five hours for a US account). A
    /// mis-dated fill is worse than an undated one here: it lands inside a different session's
    /// window and is attributed to the wrong trading date, in the table the protocol's items 6 and 9
    /// are reconstructed from.
    /// </para>
    /// <para>
    /// Anything unrecognised returns null, and the caller keeps the verbatim string in
    /// <c>executed_at_raw</c>, so nothing is lost and a later reader can re-derive the instant once
    /// the convention is known. <c>LiveTwsAccountCaptureTests</c> asserts against a real socket that
    /// what TWS actually sends parses, so a TWS that reverts to the legacy form fails loudly and
    /// names the fix rather than quietly producing wrong timestamps.
    /// </para>
    /// </remarks>
    internal static DateTimeOffset? TryParseExecutionTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTime.TryParseExact(
            raw.Trim(), "yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    private static DateOnly? TryParseExpiration(string? raw) =>
        DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>Narrows a broker <c>double</c>, treating TWS's not-computed sentinel as absent.</summary>
    private static decimal? ToDecimal(double value) =>
        QuoteRequest.TryConvertSigned(value, out var converted) ? converted : null;
}
