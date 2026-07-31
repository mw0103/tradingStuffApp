using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.History;

namespace TradingStuff.Tests;

/// <summary>
/// Unit coverage for the historical-data adapter logic that does not need a socket: bar-time
/// parsing, OHLC sentinel guarding, and the accumulate/fault request-correlation flow. Anything
/// requiring a live TWS belongs in a separate, explicitly-run integration suite — never here.
/// </summary>
public sealed class IbkrHistoricalTests
{
    private static IbkrClientWrapper NewWrapper(IbkrRequestRegistry registry) =>
        new(registry, new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance), NullLogger<IbkrClientWrapper>.Instance);

    // ---- bar time parsing (formatDate=2) -----------------------------------------------------

    [Fact]
    public void Epoch_seconds_bar_time_parses_to_the_correct_utc_instant()
    {
        // 2026-07-31T13:30:00Z, a realistic intraday bar under formatDate=2.
        Assert.True(HistoricalBarTime.TryParse("1785504600", out var timestamp, out var tradingDate));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785504600), timestamp);
        Assert.Null(tradingDate);
    }

    [Fact]
    public void Daily_bar_time_parses_to_a_trading_date_not_an_intraday_instant()
    {
        // Daily (and coarser) bars return a bare yyyyMMdd date even under formatDate=2.
        Assert.True(HistoricalBarTime.TryParse("20260731", out var timestamp, out var tradingDate));

        Assert.Equal(new DateOnly(2026, 7, 31), tradingDate);
        Assert.Null(timestamp);
    }

    [Fact]
    public void A_daily_bar_never_populates_both_fields()
    {
        // The contract that stops a caller from misreading a daily bar's date as an intraday
        // instant: exactly one of Timestamp/TradingDate is set, never both.
        Assert.True(HistoricalBarTime.TryParse("20260731", out var timestamp, out var tradingDate));
        Assert.True(tradingDate.HasValue);
        Assert.False(timestamp.HasValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-time")]
    public void Unparseable_bar_times_are_rejected_rather_than_guessed(string? raw)
    {
        Assert.False(HistoricalBarTime.TryParse(raw, out var timestamp, out var tradingDate));
        Assert.Null(timestamp);
        Assert.Null(tradingDate);
    }

    // ---- OHLC sentinel guarding (reusing QuoteRequest's converters) --------------------------

    [Fact]
    public void Bar_price_conversion_rejects_the_unset_sentinel()
    {
        // Same double.MaxValue "not computed" marker QuoteRequest already guards against.
        Assert.Equal(0m, HistoricalBarPrice.Convert(double.MaxValue));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Bar_price_conversion_rejects_unusable_values(double value)
    {
        Assert.Equal(0m, HistoricalBarPrice.Convert(value));
    }

    [Fact]
    public void Bar_price_conversion_preserves_a_real_negative_print()
    {
        // Unlike a bid/ask quote, a bar close is not necessarily positive (crude oil, April 2020),
        // so this must use the sign-preserving guard rather than the price guard that would
        // silently zero out a genuine negative print.
        Assert.Equal(-37.63m, HistoricalBarPrice.Convert(-37.63d));
    }

    [Fact]
    public void Bar_price_conversion_accepts_a_real_price()
    {
        Assert.Equal(432.15m, HistoricalBarPrice.Convert(432.15d));
    }

    // ---- accumulate-then-complete-on-historicalDataEnd ---------------------------------------

    [Fact]
    public async Task Historical_bars_accumulate_then_complete_in_order_on_historicalDataEnd()
    {
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry);
        var reqId = registry.NextRequestId();
        var pending = new ListRequest<Bar>();
        registry.Register(reqId, pending);

        wrapper.historicalData(reqId, new Bar("1785504600", 100d, 101d, 99d, 100.5d, 1000m, 12, 100.2m));
        wrapper.historicalData(reqId, new Bar("1785504660", 100.5d, 102d, 100d, 101.5d, 1500m, 20, 101.1m));
        wrapper.historicalData(reqId, new Bar("1785504720", 101.5d, 103d, 101d, 102.5d, 900m, 9, 102.0m));

        Assert.False(pending.Task.IsCompleted);

        wrapper.historicalDataEnd(reqId, "20260731  09:30:00", "20260731  09:35:00");

        var bars = await pending.Task;

        Assert.Equal(3, bars.Count);
        Assert.Equal("1785504600", bars[0].Time);
        Assert.Equal("1785504660", bars[1].Time);
        Assert.Equal("1785504720", bars[2].Time);
        Assert.Equal(0, registry.InFlightCount); // historicalDataEnd removes the completed request
    }

    [Fact]
    public async Task An_empty_bar_set_still_completes_on_historicalDataEnd()
    {
        // No bars arrived (e.g. useRTH excluded the only session in the window) but TWS still
        // signals completion; the caller must not hang waiting for a bar that will never come.
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry);
        var reqId = registry.NextRequestId();
        var pending = new ListRequest<Bar>();
        registry.Register(reqId, pending);

        wrapper.historicalDataEnd(reqId, "", "");

        var bars = await pending.Task;
        Assert.Empty(bars);
    }

    // ---- request correlation: error faults rather than hangs ---------------------------------

    [Fact]
    public async Task A_failed_historical_request_faults_rather_than_hanging()
    {
        // Every pending request must be reachable from the error callback, or a rejected request
        // waits forever for a reply that will never come — this is the historical-data instance of
        // the general rule already proven for contract/chain requests.
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry);
        var reqId = registry.NextRequestId();
        var pending = new ListRequest<Bar>();
        registry.Register(reqId, pending);

        wrapper.error(reqId, 0, IbkrErrorCodes.NoSecurityDefinition, "No security definition found", "");

        var error = await Assert.ThrowsAsync<IbkrRequestException>(() => pending.Task);
        Assert.Equal(IbkrErrorCodes.NoSecurityDefinition, error.ErrorCode);
        Assert.True(error.IsPermanent);
    }

    [Fact]
    public async Task A_head_timestamp_request_faults_rather_than_hanging_on_error()
    {
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry);
        var reqId = registry.NextRequestId();
        var pending = new HeadTimestampSink();
        registry.Register(reqId, pending);

        wrapper.error(reqId, 0, IbkrErrorCodes.NoSecurityDefinition, "No security definition found", "");

        await Assert.ThrowsAsync<IbkrRequestException>(() => pending.Task);
    }

    [Fact]
    public async Task A_head_timestamp_completes_from_a_single_callback()
    {
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry);
        var reqId = registry.NextRequestId();
        var pending = new HeadTimestampSink();
        registry.Register(reqId, pending);

        wrapper.headTimestamp(reqId, "1078358400"); // 2004-03-04, SPX's real head timestamp

        var raw = await pending.Task;
        Assert.True(HistoricalBarTime.TryParse(raw, out var timestamp, out _));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1078358400), timestamp);
    }

    // ---- error 162: no data, distinguishable from a transport failure ------------------------

    [Fact]
    public void No_historical_data_is_not_classified_as_a_permanent_or_informational_failure()
    {
        // 162 means THIS slice is empty; a different date range on the same contract can still
        // have data, so it must never be treated as "never retry this contract" (permanent), and it
        // is a genuine failure notice, not a benign farm-status message (informational).
        Assert.False(IbkrErrorCodes.IsPermanentRequestFailure(IbkrErrorCodes.NoHistoricalData));
        Assert.False(IbkrErrorCodes.IsInformational(IbkrErrorCodes.NoHistoricalData));
    }

    [Fact]
    public async Task No_historical_data_error_is_distinguishable_from_a_connection_failure()
    {
        // Two failure modes a backfill coordinator must tell apart: "this slice is confirmed
        // empty" (162 — mark permanently empty, do not retry this exact slice) versus "the socket
        // dropped" (retry later, everything in flight is suspect). Both fault the pending request,
        // but with distinguishable exception shapes.
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry);

        var noDataReqId = registry.NextRequestId();
        var noDataPending = new ListRequest<Bar>();
        registry.Register(noDataReqId, noDataPending);
        wrapper.error(noDataReqId, 0, IbkrErrorCodes.NoHistoricalData, "HMDS query returned no data", "");

        var noDataError = await Assert.ThrowsAsync<IbkrRequestException>(() => noDataPending.Task);
        Assert.Equal(IbkrErrorCodes.NoHistoricalData, noDataError.ErrorCode);
        Assert.False(noDataError.IsPermanent);

        var connReqId = registry.NextRequestId();
        var connPending = new ListRequest<Bar>();
        registry.Register(connReqId, connPending);
        registry.Fail(connReqId, new IbkrConnectionException("The TWS connection closed while requests were in flight."));

        await Assert.ThrowsAsync<IbkrConnectionException>(() => connPending.Task);
    }

    [Fact]
    public void Continuous_future_past_end_date_rejection_is_permanent()
    {
        // CONTFUT rejects a past endDateTime outright (error 10339) — retrying the identical
        // request never succeeds; the caller must walk expired individual contracts instead.
        Assert.True(IbkrErrorCodes.IsPermanentRequestFailure(IbkrErrorCodes.ContinuousFutureEndDateNotAllowed));
    }

    // ---- contract spec mapping ----------------------------------------------------------------

    [Fact]
    public void Contract_spec_maps_option_fields_onto_the_ib_contract()
    {
        var spec = new HistoricalContractSpec(
            "SPX",
            "OPT",
            Exchange: "SMART",
            Currency: "USD",
            LastTradeDateOrContractMonth: "20260731",
            Strike: 7435m,
            Right: "C",
            Multiplier: "100",
            TradingClass: "SPXW");

        var contract = spec.ToIbContract();

        Assert.Equal("SPX", contract.Symbol);
        Assert.Equal("OPT", contract.SecType);
        Assert.Equal("20260731", contract.LastTradeDateOrContractMonth);
        Assert.Equal(7435d, contract.Strike);
        Assert.Equal("C", contract.Right);
        Assert.Equal("SPXW", contract.TradingClass);
        Assert.False(contract.IncludeExpired);
    }

    [Fact]
    public void Contract_spec_leaves_strike_at_zero_for_a_non_option()
    {
        // A CONTFUT/underlying spec never sets Strike; the IB contract must not inherit a stray
        // option strike from an uninitialised nullable default.
        var spec = new HistoricalContractSpec("ES", "CONTFUT", Exchange: "CME", IncludeExpired: true);

        var contract = spec.ToIbContract();

        Assert.Equal(0d, contract.Strike);
        Assert.True(contract.IncludeExpired);
        Assert.Null(contract.Right);
    }

    // ---- error 162 is overloaded: no-data AND pacing share the code ---------------------------
    // A backfill coordinator retires a no-data slice permanently. If a pacing violation were
    // classified as no-data, a slice that DOES have data would be silently retired and never
    // re-requested, leaving a hole no gap report could explain. Hence: match no-data positively,
    // and treat anything unrecognised as transient (retryable) rather than permanent.

    [Theory]
    [InlineData("HMDS query returned no data: SPX   260821C07500000@SMART")]
    [InlineData("Historical Market Data Service error message:No historical market data for SPX/IND@CBOE MidPoint 60")]
    public void A_genuine_no_data_162_is_recognised(string message)
    {
        Assert.True(IbkrHistoricalClient.IsGenuinelyNoData(message));
    }

    [Theory]
    [InlineData("Historical Market Data Service query message:pacing violation")]
    [InlineData("Historical Market Data Service error message:Too many requests in the last 10 minutes.")]
    public void A_pacing_162_is_not_treated_as_no_data(string message)
    {
        // Must stay retryable — retiring this slice would discard data that exists.
        Assert.False(IbkrHistoricalClient.IsGenuinelyNoData(message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some unrecognised future TWS phrasing")]
    public void An_ambiguous_162_defaults_to_retryable_not_permanently_empty(string message)
    {
        // Fail safe in the direction that costs a paced request rather than the direction that
        // costs the data.
        Assert.False(IbkrHistoricalClient.IsGenuinelyNoData(message));
    }
}
