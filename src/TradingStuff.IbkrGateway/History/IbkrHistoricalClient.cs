using System.Globalization;
using IBApi;
using Microsoft.Extensions.Options;
using TradingStuff.IbkrGateway.Pacing;

namespace TradingStuff.IbkrGateway.History;

/// <summary>
/// Historical bars and head-timestamp lookups against the single TWS socket.
/// </summary>
/// <remarks>
/// Every request flows through <see cref="PacedSocket"/>, which acquires
/// <see cref="IbkrPacingGovernor.AcquireHistoricalAsync"/> before touching the wire — the
/// documented TWS historical pacing regime (≤54 requests/10min, 10% inside TWS's documented 60,
/// BID_ASK costing double, a 15s identical-request cooldown, and a 5-per-2s per-contract limit) is
/// enforced there, not here. This class only computes the pacing keys and maps between the wire
/// shapes and the HTTP contract.
/// </remarks>
public sealed class IbkrHistoricalClient(
    IbkrConnection connection,
    PacedSocket socket,
    IOptions<IbkrOptions> options,
    ILogger<IbkrHistoricalClient> logger)
{
    // formatDate=2 (epoch seconds) is used unconditionally for both bars and head timestamps: with
    // formatDate=1 TWS returns exchange-local timezone strings (US/Central for CBOE/CME requests,
    // US/Eastern for SPY) — a documented source of silent timezone bugs. Epoch sidesteps it
    // entirely. Daily bars still arrive as a bare yyyyMMdd date regardless; see HistoricalBarTime.
    private const int FormatDate = 2;

    private readonly IbkrOptions _options = options.Value;

    public async Task<HistoricalBarsResponse> GetHistoricalBarsAsync(
        HistoricalBarsRequest request, CancellationToken cancellationToken)
    {
        var ibContract = request.Contract.ToIbContract();
        var whatToShow = NormalizeWhatToShow(request.WhatToShow);
        var countsDouble = IsBidAsk(whatToShow);
        var endDateTime = FormatEndDateTime(request.EndDateTime);
        var contractKey = BuildContractKey(request.Contract, whatToShow);
        var requestKey = string.Join('|', contractKey, endDateTime, request.Duration, request.BarSize, request.UseRth);

        var registry = connection.Registry;
        var reqId = registry.NextRequestId();
        var pending = new ListRequest<Bar>();
        registry.Register(reqId, pending);

        try
        {
            await socket.ReqHistoricalDataAsync(
                reqId,
                ibContract,
                endDateTime,
                request.Duration,
                request.BarSize,
                whatToShow,
                request.UseRth ? 1 : 0,
                FormatDate,
                keepUpToDate: false,
                chartOptions: null,
                requestKey,
                contractKey,
                countsDouble,
                cancellationToken);

            var bars = await pending.Task
                .WaitAsync(TimeSpan.FromSeconds(_options.HistoricalRequestTimeoutSeconds), cancellationToken);

            return new HistoricalBarsResponse([.. bars.Select(MapBar)], HasData: true);
        }
        catch (IbkrRequestException ex)
            when (ex.ErrorCode == IbkrErrorCodes.NoHistoricalData && IsGenuinelyNoData(ex.TwsMessage))
        {
            // Error 162 ("HMDS query returned no data") means this slice is empty, not that the
            // request failed — a different date range on the same contract can still have data.
            logger.LogInformation(
                "No historical data for {Contract} ({WhatToShow}, {Duration} ending {End}).",
                Describe(request.Contract),
                whatToShow,
                request.Duration,
                string.IsNullOrEmpty(endDateTime) ? "now" : endDateTime);

            return new HistoricalBarsResponse([], HasData: false);
        }
        finally
        {
            registry.Remove(reqId);
        }
    }

    public async Task<HeadTimestampResponse> GetHeadTimestampAsync(
        HeadTimestampQuery query, CancellationToken cancellationToken)
    {
        var ibContract = query.Contract.ToIbContract();
        var whatToShow = NormalizeWhatToShow(query.WhatToShow);
        var countsDouble = IsBidAsk(whatToShow);
        var contractKey = BuildContractKey(query.Contract, whatToShow);
        var requestKey = string.Join('|', "headts", contractKey, query.UseRth);

        var registry = connection.Registry;
        var reqId = registry.NextRequestId();
        var pending = new HeadTimestampSink();
        registry.Register(reqId, pending);

        try
        {
            await socket.ReqHeadTimestampAsync(
                reqId,
                ibContract,
                whatToShow,
                query.UseRth ? 1 : 0,
                FormatDate,
                requestKey,
                contractKey,
                countsDouble,
                cancellationToken);

            var raw = await pending.Task
                .WaitAsync(TimeSpan.FromSeconds(_options.HistoricalRequestTimeoutSeconds), cancellationToken);

            if (!HistoricalBarTime.TryParse(raw, out var timestamp, out var tradingDate))
            {
                throw new IbkrRequestException(0, $"TWS returned an unparseable head timestamp '{raw}'.");
            }

            // Head timestamps arrive as epoch seconds in practice (formatDate=2 always yields one
            // for a single-instant request), but tolerate the bare-date shape defensively rather
            // than assume — midnight UTC on that date is the closest honest instant.
            return new HeadTimestampResponse(
                timestamp ?? new DateTimeOffset(tradingDate!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        }
        catch (IbkrRequestException ex) when (ex.ErrorCode == IbkrErrorCodes.NoHistoricalData)
        {
            if (AsMissingHeadTimestamp(ex) is not { } permanent)
            {
                // Pacing, or wording nobody has seen before. Stays transient and gets retried: the
                // cost of a needless retry is one paced request, while the cost of retiring a head
                // timestamp that does exist is every job on that contract planned against the wrong
                // floor.
                throw;
            }

            logger.LogInformation(
                "TWS reports no historical data at all for {Contract} ({WhatToShow}), so it has no head " +
                "timestamp: {Message}",
                Describe(query.Contract),
                whatToShow,
                ex.TwsMessage);

            throw permanent;
        }
        finally
        {
            // reqHeadTimeStamp counts as an ongoing historical request until cancelled, or it
            // leaks — cancel unconditionally: on success, on failure, and on timeout alike.
            await socket.CancelHeadTimestampAsync(reqId);
            registry.Remove(reqId);
        }
    }

    internal static HistoricalBar MapBar(Bar bar)
    {
        if (!HistoricalBarTime.TryParse(bar.Time, out var timestamp, out var tradingDate))
        {
            // Deliberately fails the whole slice rather than emitting a bar with an unset
            // timestamp. A timeless bar inside an otherwise-successful response is the worst
            // available outcome: the coordinator would mark the slice landed, the bar would be
            // dropped or mis-keyed on insert, and the resulting hole would be invisible to gap
            // detection — which reconciles against what the slice CLAIMED to deliver. An
            // unparseable time also means the response is not the shape we believe it to be, so
            // trusting the other fields of that response is unjustified. A visible slice failure
            // is retried and, if persistent, surfaces as a failed slice an operator can see.
            throw new InvalidOperationException(
                $"TWS returned a historical bar with an unparseable time '{bar.Time}'. Refusing to " +
                "return a partial slice — a bar with no timestamp cannot be stored or reconciled.");
        }

        return new HistoricalBar(
            timestamp,
            tradingDate,
            HistoricalBarPrice.Convert(bar.Open),
            HistoricalBarPrice.Convert(bar.High),
            HistoricalBarPrice.Convert(bar.Low),
            HistoricalBarPrice.Convert(bar.Close),
            bar.Volume,
            bar.Count,
            bar.WAP);
    }

    private static bool IsBidAsk(string whatToShow) => whatToShow == "BID_ASK";

    private static string NormalizeWhatToShow(string whatToShow) => whatToShow.Trim().ToUpperInvariant();

    private static string FormatEndDateTime(DateTimeOffset? endDateTime) =>
        endDateTime is null
            ? string.Empty
            : endDateTime.Value.UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);

    private static string BuildContractKey(HistoricalContractSpec contract, string whatToShow) => string.Join(
        '|',
        contract.Symbol.ToUpperInvariant(),
        contract.SecType.ToUpperInvariant(),
        contract.Exchange.ToUpperInvariant(),
        contract.Currency.ToUpperInvariant(),
        contract.LastTradeDateOrContractMonth ?? string.Empty,
        contract.Strike?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        contract.Right ?? string.Empty,
        contract.TradingClass ?? string.Empty,
        whatToShow);

    /// <summary>
    /// Whether a TWS error 162 genuinely means "this slice has no data", as opposed to a pacing
    /// violation — which TWS also reports as 162, with different message text.
    /// </summary>
    /// <remarks>
    /// This distinction is load-bearing and the reason the check is not simply on the error code.
    /// A backfill coordinator retires a no-data slice permanently; if a pacing violation were
    /// classified the same way, a slice that DOES have data would be marked permanently empty and
    /// silently never re-requested, leaving a hole no gap report could later explain. When the text
    /// is unrecognised the answer is deliberately "not no-data" — an ambiguous 162 surfaces as a
    /// transient error and gets retried, because a needless retry costs one paced request whereas a
    /// wrong permanent retirement costs the data.
    /// </remarks>
    internal static bool IsGenuinelyNoData(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        // TWS's pacing text ("pacing violation", "Too many requests...") shares code 162 with the
        // no-data text. Match the no-data phrasing positively rather than trying to enumerate every
        // pacing variant, and reject pacing wording first so a message containing both loses.
        if (message.Contains("pacing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Both spellings are real and were captured from live TWS during this project's probes
        // (recorded in docs/research/ibkr-data-capability-matrix.md):
        //   "HMDS query returned no data: SPX   260821C07500000@SMART"
        //   "Historical Market Data Service error message:No historical market data for SPX/IND@CBOE MidPoint 60"
        // The second contains no literal "no data" substring at all — matching only that phrasing
        // silently misclassifies every index MIDPOINT rejection as a transient error.
        return message.Contains("no data", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no historical", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reclassifies a <c>reqHeadTimeStamp</c> rejection that genuinely means "this contract has no
    /// history" as a permanent failure; returns null for any other 162, which must stay retryable.
    /// </summary>
    /// <remarks>
    /// The bars path answers a genuine 162 with <c>HasData: false</c> — a confirmed-empty SLICE,
    /// which says nothing about the contract, because a different date range may still have data.
    /// A head timestamp has no other date range to try: it IS the question "where does this
    /// contract's data start", and "no historical market data for SPX/IND@CBOE MidPoint" answers it
    /// permanently (RUNTIME-verified for SPX MIDPOINT — see
    /// <c>docs/research/ibkr-data-capability-matrix.md</c>).
    /// <para>
    /// Left transient, that answer is an unbounded probe loop rather than a slow retry: the backfill
    /// coordinator declines to plan the job without a head timestamp, does not cache the failure,
    /// and re-probes on every pass — one paced historical request each time, forever, with the job
    /// never planning a single slice. Permanent maps to 400, which both callers already handle by
    /// degrading honestly (plan the full declared range unclamped, or skip the contract this scan).
    /// </para>
    /// </remarks>
    internal static IbkrRequestException? AsMissingHeadTimestamp(IbkrRequestException ex) =>
        ex.ErrorCode == IbkrErrorCodes.NoHistoricalData && IsGenuinelyNoData(ex.TwsMessage)
            ? new IbkrRequestException(ex.ErrorCode, ex.TwsMessage, permanent: true)
            : null;

    private static string Describe(HistoricalContractSpec contract) =>
        $"{contract.Symbol} {contract.SecType} {contract.LastTradeDateOrContractMonth} " +
        $"{contract.Strike} {contract.Right} {contract.TradingClass}".Trim();
}
