using IBApi;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;
using TradingStuff.MarketDataService;

namespace TradingStuff.Tests;

/// <summary>
/// Unit coverage for the IBKR adapter logic that does not need a socket. Anything requiring a live
/// TWS belongs in a separate, explicitly-run integration suite — never in the default test run.
/// </summary>
public sealed class IbkrGatewayTests
{
    private static readonly OptionContract SampleContract = new(
        "XYZ20260821C100",
        "XYZ",
        new DateOnly(2026, 8, 21),
        100m,
        OptionRight.Call);

    // ---- tick sentinel handling -------------------------------------------------------------

    [Fact]
    public void Price_conversion_rejects_the_unset_sentinel()
    {
        // TWS sends double.MaxValue for a field it has not computed. Casting it to decimal overflows.
        Assert.False(QuoteRequest.TryConvertPrice(double.MaxValue, out _));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Price_conversion_rejects_unusable_values(double value)
    {
        Assert.False(QuoteRequest.TryConvertPrice(value, out _));
    }

    [Fact]
    public void Price_conversion_accepts_a_real_price()
    {
        Assert.True(QuoteRequest.TryConvertPrice(2.35d, out var price));
        Assert.Equal(2.35m, price);
    }

    [Fact]
    public void Greek_conversion_accepts_negative_values()
    {
        // A deep in-the-money put has delta -1 and theta is normally negative. Applying the price
        // rule (reject anything negative) to Greeks would silently discard valid data.
        Assert.True(QuoteRequest.TryConvertGreek(-1d, out var delta));
        Assert.Equal(-1m, delta);

        Assert.True(QuoteRequest.TryConvertGreek(-0.045d, out var theta));
        Assert.Equal(-0.045m, theta);
    }

    [Fact]
    public void Greek_conversion_rejects_the_unset_sentinel()
    {
        Assert.False(QuoteRequest.TryConvertGreek(double.MaxValue, out _));
    }

    // ---- quote accumulation ------------------------------------------------------------------

    [Fact]
    public async Task Quote_completes_once_bid_ask_and_model_greeks_arrive()
    {
        var request = new QuoteRequest(SampleContract, "test");

        request.ApplyPrice(TickType.BID, 1.95d);
        request.ApplyPrice(TickType.ASK, 2.05d);
        Assert.False(request.Task.IsCompleted);

        request.ApplyOptionComputation(TickType.MODEL_OPTION, 0.20d, 0.55d, 0.02d, 0.09d, -0.03d, 100d);

        var quote = await request.Task;
        Assert.Equal(1.95m, quote.Bid);
        Assert.Equal(2.05m, quote.Ask);
        Assert.Equal(0.55m, quote.Greeks.Delta);
        Assert.Equal(-0.03m, quote.Greeks.Theta);
    }

    [Fact]
    public async Task Quote_completes_from_delayed_tick_fields()
    {
        // Under reqMarketDataType(3) TWS sends 66/67 and 83 rather than 1/2 and 13. Handling only
        // the live fields yields a subscription that connects and never produces a quote.
        var request = new QuoteRequest(SampleContract, "test");

        request.ApplyPrice(TickType.DELAYED_BID, 1.10d);
        request.ApplyPrice(TickType.DELAYED_ASK, 1.20d);
        request.ApplyOptionComputation(TickType.DELAYED_MODEL_OPTION, 0.20d, -0.40d, 0.03d, 0.08d, -0.02d, 100d);

        var quote = await request.Task;
        Assert.Equal(1.10m, quote.Bid);
        Assert.Equal(1.20m, quote.Ask);
        Assert.Equal(-0.40m, quote.Greeks.Delta);
    }

    [Fact]
    public void Quote_ignores_non_model_option_computations()
    {
        // Bid/ask/last computations are each derived from one side of the book and disagree.
        var request = new QuoteRequest(SampleContract, "test");

        request.ApplyPrice(TickType.BID, 1.95d);
        request.ApplyPrice(TickType.ASK, 2.05d);
        request.ApplyOptionComputation(TickType.BID_OPTION, 0.20d, 0.55d, 0.02d, 0.09d, -0.03d, 100d);

        Assert.False(request.Task.IsCompleted);
    }

    [Fact]
    public void Quote_ignores_an_uncomputed_option_calculation()
    {
        var request = new QuoteRequest(SampleContract, "test");

        request.ApplyPrice(TickType.BID, 1.95d);
        request.ApplyPrice(TickType.ASK, 2.05d);
        request.ApplyOptionComputation(TickType.MODEL_OPTION, 0.20d, -2d, double.MaxValue, double.MaxValue, double.MaxValue, 100d);

        Assert.False(request.Task.IsCompleted);
    }

    [Fact]
    public async Task Partial_quote_settles_rather_than_hanging()
    {
        // An illiquid series may never publish a full set; a partial snapshot beats a hung request.
        var request = new QuoteRequest(SampleContract, "test");
        request.ApplyPrice(TickType.BID, 1.95d);

        request.CompletePartial();

        var quote = await request.Task;
        Assert.Equal(1.95m, quote.Bid);
        Assert.Equal(0m, quote.Ask);
    }

    // ---- partial quotes are distinguishable from real ones -------------------------------------
    //
    // A settled-on-timeout quote fabricates the fields TWS never sent: bid and ask default to 0 and
    // the Greeks to a zero vector. Those are plausible values, not obviously-missing ones, so a
    // consumer cannot tell them apart from a real quote by looking at the numbers — and the pre-trade
    // risk path reads exactly those numbers. QuoteSnapshot's shape is fixed, so the source string
    // carries the distinction.

    [Fact]
    public async Task A_quote_settled_by_timeout_is_marked_partial()
    {
        var request = new QuoteRequest(SampleContract, "ibkr-delayed");
        request.ApplyPrice(TickType.BID, 1.95d);

        request.CompletePartial();

        var quote = await request.Task;
        Assert.Equal("ibkr-delayed" + QuoteRequest.PartialSourceSuffix, quote.Source);
        Assert.True(QuoteRequest.IsPartial(quote));
    }

    [Fact]
    public async Task A_fully_assembled_quote_is_not_marked_partial()
    {
        var request = new QuoteRequest(SampleContract, "ibkr-delayed");

        request.ApplyPrice(TickType.BID, 1.95d);
        request.ApplyPrice(TickType.ASK, 2.05d);
        request.ApplyOptionComputation(TickType.MODEL_OPTION, 0.20d, 0.55d, 0.02d, 0.09d, -0.03d, 100d);

        var quote = await request.Task;
        Assert.Equal("ibkr-delayed", quote.Source);
        Assert.False(QuoteRequest.IsPartial(quote));
    }

    [Fact]
    public async Task A_real_zero_bid_is_not_a_partial_quote()
    {
        // Deep out-of-the-money series genuinely trade 0.00 bid against a live ask. The
        // discriminator is "TWS never sent the field", never "the value is zero" — marking these
        // partial would make every consumer that fails closed refuse perfectly good quotes.
        var request = new QuoteRequest(SampleContract, "ibkr-delayed");

        request.ApplyPrice(TickType.BID, 0d);
        request.ApplyPrice(TickType.ASK, 0.05d);
        request.ApplyOptionComputation(TickType.MODEL_OPTION, 0.85d, 0.01d, 0.001d, 0.02d, -0.004d, 100d);

        var quote = await request.Task;
        Assert.Equal(0m, quote.Bid);
        Assert.Equal("ibkr-delayed", quote.Source);
        Assert.False(QuoteRequest.IsPartial(quote));
    }

    [Fact]
    public async Task A_quote_whose_bid_arrived_only_as_the_no_quote_marker_is_partial()
    {
        // TWS reports "there is no bid" as -1, which TryConvertPrice rejects — so the field was
        // never received and the 0 in the snapshot is fabricated, unlike the case above.
        var request = new QuoteRequest(SampleContract, "ibkr-delayed");

        request.ApplyPrice(TickType.BID, -1d);
        request.ApplyPrice(TickType.ASK, 0.05d);
        request.ApplyOptionComputation(TickType.MODEL_OPTION, 0.85d, 0.01d, 0.001d, 0.02d, -0.004d, 100d);
        request.CompletePartial();

        var quote = await request.Task;
        Assert.Equal(0m, quote.Bid);
        Assert.True(QuoteRequest.IsPartial(quote));
    }

    [Fact]
    public async Task A_quote_with_a_full_book_but_no_greeks_is_partial()
    {
        // Zeroed Greeks are the most dangerous fabrication of the three: a zero delta reads as a
        // position with no directional exposure at all.
        var request = new QuoteRequest(SampleContract, "ibkr-delayed");

        request.ApplyPrice(TickType.BID, 1.95d);
        request.ApplyPrice(TickType.ASK, 2.05d);
        request.CompletePartial();

        var quote = await request.Task;
        Assert.Equal(1.95m, quote.Bid);
        Assert.Equal(0m, quote.Greeks.Delta);
        Assert.True(QuoteRequest.IsPartial(quote));
    }

    [Fact]
    public async Task A_non_option_quote_is_not_partial_merely_for_having_no_greeks()
    {
        // requireGreeks:false is the underlying-quote path, where no option computation tick will
        // ever arrive. Marking it partial would condemn every one of them.
        var request = new QuoteRequest(SampleContract, "ibkr-delayed", requireGreeks: false);

        request.ApplyPrice(TickType.BID, 1.95d);
        request.ApplyPrice(TickType.ASK, 2.05d);

        var quote = await request.Task;
        Assert.False(QuoteRequest.IsPartial(quote));
    }

    [Fact]
    public async Task Spot_request_completes_without_option_computations()
    {
        var request = new SpotPriceRequest();

        request.ApplyPrice(TickType.LAST, 431.27d);

        Assert.Equal(431.27m, await request.Task);
    }

    [Fact]
    public async Task Spot_request_falls_back_to_the_mid()
    {
        var request = new SpotPriceRequest();

        request.ApplyPrice(TickType.DELAYED_BID, 100d);
        request.ApplyPrice(TickType.DELAYED_ASK, 102d);

        Assert.Equal(101m, await request.Task);
    }

    // ---- request correlation -----------------------------------------------------------------

    [Fact]
    public async Task A_failed_request_faults_rather_than_hanging()
    {
        // Every pending request must be reachable from the error callback, or a rejected request
        // waits forever for a reply that will never come.
        var registry = new IbkrRequestRegistry();
        var requestId = registry.NextRequestId();
        var request = new ListRequest<string>();

        registry.Register(requestId, request);
        Assert.True(registry.Fail(requestId, new IbkrRequestException(200, "No security definition found")));

        var error = await Assert.ThrowsAsync<IbkrRequestException>(() => request.Task);
        Assert.Equal(200, error.ErrorCode);
        Assert.True(error.IsPermanent);
    }

    [Fact]
    public async Task Dropping_the_socket_faults_everything_in_flight()
    {
        var registry = new IbkrRequestRegistry();
        var first = new ListRequest<string>();
        var second = new ListRequest<string>();

        registry.Register(registry.NextRequestId(), first);
        registry.Register(registry.NextRequestId(), second);

        registry.FailAll(new IbkrConnectionException("closed"));

        await Assert.ThrowsAsync<IbkrConnectionException>(() => first.Task);
        await Assert.ThrowsAsync<IbkrConnectionException>(() => second.Task);
        Assert.Equal(0, registry.InFlightCount);
    }

    [Fact]
    public void Request_ids_are_unique()
    {
        var registry = new IbkrRequestRegistry();

        var ids = Enumerable.Range(0, 200).Select(_ => registry.NextRequestId()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    // ---- error classification ----------------------------------------------------------------

    [Theory]
    [InlineData(2104)] // market data farm connection is OK
    [InlineData(2106)] // historical data farm connection is OK
    [InlineData(2158)] // security definition farm connection is OK
    [InlineData(10167)] // displaying delayed data instead of live
    public void Status_notices_are_not_treated_as_errors(int errorCode)
    {
        // These arrive on the same callback as real failures; faulting requests on them is a bug.
        Assert.True(IbkrErrorCodes.IsInformational(errorCode));
    }

    [Theory]
    [InlineData(10090)] // part of the data is unsubscribed; independent ticks still stream
    [InlineData(10091)] // needs a subscription, but delayed data is available
    public void Delayed_data_notices_do_not_fault_the_request(int errorCode)
    {
        // Observed against a live paper account: requesting SPY option data on an account without an
        // OPRA subscription raises 10091 while TWS still streams delayed ticks. Treating it as a
        // failure threw away data that was already on its way.
        Assert.True(IbkrErrorCodes.IsInformational(errorCode));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(354)]
    [InlineData(10168)] // not subscribed AND delayed data disabled: nothing will ever arrive
    public void Real_failures_are_not_informational(int errorCode)
    {
        Assert.False(IbkrErrorCodes.IsInformational(errorCode));
    }

    [Fact]
    public void A_missing_security_definition_is_permanent()
    {
        Assert.True(IbkrErrorCodes.IsPermanentRequestFailure(IbkrErrorCodes.NoSecurityDefinition));
        Assert.False(IbkrErrorCodes.IsPermanentRequestFailure(IbkrErrorCodes.ConnectivityLost));
    }

    // ---- option chain segment selection ------------------------------------------------------

    [Fact]
    public void Chain_selection_ignores_adjusted_option_classes()
    {
        // Reproduces the real SPY response: 39 segments, and the ONLY SMART one is the adjusted
        // "2SPY" class left behind by a corporate action, carrying 3 strikes. Preferring SMART
        // therefore selects a near-empty, untradeable chain. Trading class is the reliable signal.
        var segments = new List<OptionChainSegment>
        {
            new("SMART", 1, "2SPY", "100", ["20260904"], [668d, 672d, 682d]),
            new("CBOE", 1, "2SPY", "100", ["20260904"], [668d, 672d, 682d]),
            new("NASDAQOM", 1, "SPY", "100", ["20260731", "20260904"], [.. Enumerable.Range(700, 489).Select(v => (double)v)]),
            new("AMEX", 1, "SPY", "100", ["20260731", "20260904"], [.. Enumerable.Range(700, 489).Select(v => (double)v)]),
        };

        var selected = IbkrMarketDataClient.SelectChainSegment(segments, "SPY");

        Assert.Equal("SPY", selected.TradingClass);
        Assert.Equal(489, selected.Strikes.Count);
    }

    [Fact]
    public void Chain_selection_falls_back_to_the_richest_segment()
    {
        // No segment matches the symbol's trading class; take the most complete one rather than
        // whichever happened to arrive first.
        var segments = new List<OptionChainSegment>
        {
            new("CBOE", 1, "1ABC", "100", ["20260904"], [10d]),
            new("AMEX", 1, "7ABC", "100", ["20260904"], [10d, 20d, 30d]),
        };

        var selected = IbkrMarketDataClient.SelectChainSegment(segments, "ABC");

        Assert.Equal(3, selected.Strikes.Count);
    }

    [Fact]
    public void An_explicit_trading_class_wins_over_the_symbol_match()
    {
        // The real SPX response: SPX is the AM-settled monthly series (20 expirations) and SPXW the
        // PM-settled weeklies/dailies (39) that trade in global hours. Matching on the symbol alone
        // silently returns the monthlies, which do not trade pre-market.
        var segments = new List<OptionChainSegment>
        {
            new("CBOE", 416904, "SPX", "100", [.. Enumerable.Range(0, 20).Select(i => $"2026090{i % 10}")], [.. Enumerable.Range(6000, 574).Select(v => (double)v)]),
            new("CBOE", 416904, "SPXW", "100", [.. Enumerable.Range(0, 39).Select(i => $"2026080{i % 10}")], [.. Enumerable.Range(6000, 728).Select(v => (double)v)]),
        };

        Assert.Equal("SPXW", IbkrMarketDataClient.SelectChainSegment(segments, "SPX", "SPXW").TradingClass);

        // Without one, the symbol-matched standard class still wins.
        Assert.Equal("SPX", IbkrMarketDataClient.SelectChainSegment(segments, "SPX").TradingClass);
    }

    [Fact]
    public void An_unknown_trading_class_falls_back_rather_than_returning_nothing()
    {
        var segments = new List<OptionChainSegment>
        {
            new("CBOE", 1, "SPX", "100", ["20260904"], [6000d, 6005d]),
        };

        Assert.Equal("SPX", IbkrMarketDataClient.SelectChainSegment(segments, "SPX", "NOPE").TradingClass);
    }

    [Fact]
    public void Trading_class_is_sent_so_spx_and_spxw_are_not_ambiguous()
    {
        // SPX and SPXW list the same strike on the same expiration. Without the trading class,
        // reqContractDetails matches both and resolution picks whichever arrives first.
        var weekly = new OptionContract(
            "SPXW202607317435C", "SPX", new DateOnly(2026, 7, 31), 7435m, OptionRight.Call, TradingClass: "SPXW");

        var resolved = IbkrMarketDataClient.ToIbOption(weekly);

        Assert.Equal("SPXW", resolved.TradingClass);
        Assert.Equal("OPT", resolved.SecType);
        Assert.Equal("20260731", resolved.LastTradeDateOrContractMonth);

        // Underlyings with a single series leave it unset rather than guessing.
        Assert.Null(IbkrMarketDataClient.ToIbOption(SampleContract).TradingClass);
    }

    [Fact]
    public void Trading_class_distinguishes_otherwise_identical_contracts()
    {
        var monthly = new OptionContract("a", "SPX", new DateOnly(2026, 7, 31), 7435m, OptionRight.Call, TradingClass: "SPX");
        var weekly = new OptionContract("b", "SPX", new DateOnly(2026, 7, 31), 7435m, OptionRight.Call, TradingClass: "SPXW");

        Assert.NotEqual(monthly.Key(), weekly.Key());
        Assert.Equal(weekly.Key(), (weekly with { Symbol = "different" }).Key());
    }

    // ---- provider selection ------------------------------------------------------------------

    [Theory]
    [InlineData("ibkr-live", true)]
    [InlineData("ibkr-delayed", true)]
    [InlineData("IBKR-Delayed", true)]
    [InlineData("ibkr-deterministic-paper-feed", false)]
    [InlineData("typo", false)]
    [InlineData(null, false)]
    public void Only_known_ibkr_sources_route_to_the_broker(string? source, bool expected)
    {
        // An unrecognised value must fall back to fake data rather than silently hitting a broker.
        Assert.Equal(expected, MarketDataSources.UsesIbkrGateway(source));
    }
}
