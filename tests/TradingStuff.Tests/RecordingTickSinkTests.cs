using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.ResearchContracts;

namespace TradingStuff.Tests;

/// <summary>
/// <see cref="RecordingTickSink"/> is the sole path from a TWS callback to a persisted raw
/// observation, so its field-change detection, sentinel filtering, and lock/cross classification
/// are exercised directly — with no Postgres involved, via the narrow <c>IObservationSink</c> seam.
/// </summary>
public sealed class RecordingTickSinkTests
{
    private sealed class FakeSink : IObservationSink
    {
        public List<OptionQuoteObservation> Options { get; } = [];

        public List<UnderlyingTickObservation> Underlyings { get; } = [];

        public List<Guid> ClosedGaps { get; } = [];

        /// <summary>Every non-live report the sink raised, in order: (lease, reported type).</summary>
        public List<(Guid LeaseId, int MarketDataType)> NonLiveReports { get; } = [];

        public List<Guid> LiveReports { get; } = [];

        /// <summary>
        /// The whole ordered call sequence, because for the gap machinery the ORDER of a close and a
        /// non-live open against one single-row scope is what decides whether the alarm survives.
        /// </summary>
        public List<string> Calls { get; } = [];

        public void EnqueueOption(OptionQuoteObservation observation) => Options.Add(observation);

        public void EnqueueUnderlying(UnderlyingTickObservation observation) => Underlyings.Add(observation);

        public void NotifyGapClosed(Guid leaseId, short? effectiveMarketDataType)
        {
            ClosedGaps.Add(leaseId);
            Calls.Add($"close:{effectiveMarketDataType?.ToString() ?? "null"}");
        }

        public void NotifyNonLiveMarketData(Guid leaseId, int marketDataType)
        {
            NonLiveReports.Add((leaseId, marketDataType));
            Calls.Add($"nonlive:{marketDataType}");
        }

        public void NotifyLiveMarketData(Guid leaseId)
        {
            LiveReports.Add(leaseId);
            Calls.Add("live");
        }
    }

    private static RecordingTickSink CreateOptionSink(FakeSink sink, bool markFirstTickAsReplay = false, Action<Exception>? onFailed = null) =>
        new(conId: 12345, leaseId: Guid.NewGuid(), isOption: true, markFirstTickAsReplay, sink, onFailed ?? (_ => { }));

    private static RecordingTickSink CreateUnderlyingSink(FakeSink sink) =>
        new(conId: 416904, leaseId: Guid.NewGuid(), isOption: false, markFirstTickAsReplay: false, sink, _ => { });

    [Fact]
    public void A_price_tick_emits_with_only_that_field_flagged()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyPrice(TickType.BID, 107.40d);

        var observation = Assert.Single(fake.Options);
        Assert.Equal(QuoteFieldChanges.Bid, observation.Changed);
        Assert.Equal(107.40m, observation.Bid);
        Assert.Null(observation.Ask);
    }

    [Fact]
    public void An_unchanged_price_does_not_emit_again()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyPrice(TickType.BID, 107.40d);
        recorder.ApplyPrice(TickType.BID, 107.40d);

        Assert.Single(fake.Options);
    }

    [Fact]
    public void The_unset_price_sentinel_is_rejected()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyPrice(TickType.BID, double.MaxValue);

        Assert.Empty(fake.Options);
    }

    [Fact]
    public void Sizes_populate_bid_ask_last_size_and_volume()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplySize(TickType.BID_SIZE, 8m);
        recorder.ApplySize(TickType.ASK_SIZE, 21m);
        recorder.ApplySize(TickType.LAST_SIZE, 2m);
        recorder.ApplySize(TickType.VOLUME, 5m);

        Assert.Equal(4, fake.Options.Count);
        var last = fake.Options[^1];
        Assert.Equal(8m, last.BidSize);
        Assert.Equal(21m, last.AskSize);
        Assert.Equal(2m, last.LastSize);
        Assert.Equal(5m, last.Volume);
    }

    [Fact]
    public void The_unset_decimal_size_sentinel_is_rejected()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplySize(TickType.BID_SIZE, decimal.MaxValue);

        Assert.Empty(fake.Options);
    }

    [Fact]
    public void A_negative_size_is_rejected()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplySize(TickType.BID_SIZE, -1m);

        Assert.Empty(fake.Options);
    }

    [Theory]
    [InlineData(27)] // OPTION_CALL_OPEN_INTEREST
    [InlineData(28)] // OPTION_PUT_OPEN_INTEREST
    public void Open_interest_arrives_on_either_call_or_put_tick_type_for_an_option(int field)
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplySize(field, 8m);

        var observation = Assert.Single(fake.Options);
        Assert.Equal(8m, observation.OpenInterest);
        Assert.Equal(QuoteFieldChanges.OpenInterest, observation.Changed);
    }

    [Fact]
    public void Open_interest_ticks_are_ignored_for_an_underlying_subscription()
    {
        var fake = new FakeSink();
        var recorder = CreateUnderlyingSink(fake);

        recorder.ApplySize(TickType.OPTION_CALL_OPEN_INTEREST, 8m);

        Assert.Empty(fake.Underlyings);
    }

    [Fact]
    public void Model_greeks_populate_delta_gamma_vega_theta_iv_and_underlying_price()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyOptionComputation(
            TickType.MODEL_OPTION, impliedVolatility: 0.1357d, delta: 0.51d, gamma: 0.0015d,
            vega: 7.93d, theta: -2.48d, undPrice: 7436.57d);

        var observation = Assert.Single(fake.Options);
        Assert.Equal(GreeksVariant.Model, observation.GreeksVariant);
        Assert.Equal(0.51m, observation.Delta);
        Assert.Equal(0.0015m, observation.Gamma);
        Assert.Equal(7.93m, observation.Vega);
        Assert.Equal(-2.48m, observation.Theta);
        Assert.Equal(0.1357m, observation.Iv);
        Assert.Equal(7436.57m, observation.UnderlyingPrice);
        Assert.True((observation.Changed & QuoteFieldChanges.Greeks) != 0);
        Assert.True((observation.Changed & QuoteFieldChanges.UnderlyingPrice) != 0);
    }

    [Fact]
    public void Non_model_option_computations_are_ignored()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyOptionComputation(
            TickType.BID_OPTION, 0.20d, 0.51d, 0.0015d, 7.93d, -2.48d, 7436.57d);

        Assert.Empty(fake.Options);
    }

    [Fact]
    public void An_uncomputed_option_calculation_marked_by_delta_negative_two_is_ignored()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyOptionComputation(
            TickType.MODEL_OPTION, double.MaxValue, -2d, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue);

        Assert.Empty(fake.Options);
    }

    [Theory]
    [InlineData(1.95, 2.05, false, false)] // normal market
    [InlineData(2.00, 2.00, true, false)]  // locked
    [InlineData(2.05, 1.95, false, true)]  // crossed
    public void Locked_and_crossed_are_computed_from_bid_and_ask(double bid, double ask, bool locked, bool crossed)
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyPrice(TickType.BID, bid);
        recorder.ApplyPrice(TickType.ASK, ask);

        var last = fake.Options[^1];
        Assert.Equal(locked, last.Locked);
        Assert.Equal(crossed, last.Crossed);
    }

    [Fact]
    public void Only_the_first_tick_after_replay_is_tagged_and_closes_the_gap()
    {
        var fake = new FakeSink();
        var leaseId = Guid.NewGuid();
        var recorder = new RecordingTickSink(12345, leaseId, isOption: true, markFirstTickAsReplay: true, fake, _ => { });

        recorder.ApplyPrice(TickType.BID, 1.95d);
        recorder.ApplyPrice(TickType.ASK, 2.05d);

        Assert.Equal(ObservationOrigin.ReplayResubscribe, fake.Options[0].Envelope.Origin);
        Assert.Equal(ObservationOrigin.Stream, fake.Options[1].Envelope.Origin);
        Assert.Equal([leaseId], fake.ClosedGaps);
    }

    [Fact]
    public void Underlying_ticks_never_carry_greeks_fields()
    {
        var fake = new FakeSink();
        var recorder = CreateUnderlyingSink(fake);

        recorder.ApplyPrice(TickType.LAST, 7445.70d);

        var observation = Assert.Single(fake.Underlyings);
        Assert.Equal(7445.70m, observation.Last);
    }

    [Fact]
    public void A_tick_emitted_before_TWS_reports_a_market_data_type_carries_null()
    {
        // NULL means UNMEASURED, and there is a real window for it: reqMktData returns before the
        // marketDataType callback arrives, and ticks can land inside it. The alternative — seeding
        // the sink with the requested type, or with "assume live" — would put a claim in an
        // unrecoverable row that nothing ever measured (docs/LESSONS.md #8).
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyPrice(TickType.BID, 1.95d);

        var observation = Assert.Single(fake.Options);
        Assert.Null(observation.Envelope.MarketDataType);
    }

    [Fact]
    public void The_recorded_market_data_type_is_the_one_TWS_reported_not_the_one_requested()
    {
        // The crux. IbkrOptions.MarketDataType is what reqMarketDataType ASKED for; TWS answers an
        // unentitled request for live with 2 or 3 and no error. Requested and reported are made to
        // differ here on purpose: a column stamped from the request would read "live" over a whole
        // session of 15-minute-old quotes, and be indistinguishable from a real one afterwards.
        const int requested = 1;   // live
        const int reported = 3;    // what TWS actually served
        Assert.Equal(requested, new IbkrOptions { MarketDataType = requested }.MarketDataType);

        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        // RecordingTickSink is not given IbkrOptions at all — the requested value is unreachable
        // from here by construction, which is what makes stamping it impossible rather than merely
        // discouraged. The only input is the callback's own answer.
        recorder.ApplyMarketDataType(reported);
        recorder.ApplyPrice(TickType.BID, 1.95d);

        var observation = Assert.Single(fake.Options);
        Assert.Equal((short)reported, observation.Envelope.MarketDataType);
        Assert.NotEqual((short)requested, observation.Envelope.MarketDataType);
    }

    [Theory]
    [InlineData(2)] // frozen
    [InlineData(3)] // delayed
    [InlineData(4)] // delayed-frozen
    public void A_non_live_report_raises_the_alarm_and_stamps_every_later_tick(int reported)
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyMarketDataType(reported);

        Assert.Equal(reported, Assert.Single(fake.NonLiveReports).MarketDataType);
        Assert.Empty(fake.LiveReports);

        recorder.ApplyPrice(TickType.BID, 1.95d);
        recorder.ApplyPrice(TickType.ASK, 2.05d);

        Assert.All(fake.Options, observation => Assert.Equal((short)reported, observation.Envelope.MarketDataType));
    }

    [Fact]
    public void A_live_report_raises_no_alarm_and_retires_any_standing_one()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyMarketDataType(3);
        recorder.ApplyMarketDataType(1);

        Assert.Single(fake.NonLiveReports);
        Assert.Single(fake.LiveReports);

        recorder.ApplyPrice(TickType.BID, 1.95d);
        Assert.Equal((short)1, Assert.Single(fake.Options).Envelope.MarketDataType);
    }

    [Fact]
    public void A_repeated_report_of_the_same_type_does_not_raise_the_alarm_again()
    {
        // One alarm per regime, not one per message: a signal that fires on every repeat is one
        // operators stop reading (docs/LESSONS.md #10).
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyMarketDataType(3);
        recorder.ApplyMarketDataType(3);
        recorder.ApplyMarketDataType(3);

        Assert.Single(fake.NonLiveReports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void A_type_outside_the_schema_domain_is_reported_as_non_live_and_never_stamped(int reported)
    {
        // The column's CHECK admits 1|2|3|4 only (migration 016). Storing anything else would make
        // the COPY reject the whole 5,000-row batch it landed in, so the value is not stored — but
        // it is still an alarm, because "TWS said something about the data regime we do not
        // understand" is exactly the case that must not read as live.
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake);

        recorder.ApplyMarketDataType(reported);
        recorder.ApplyPrice(TickType.BID, 1.95d);

        Assert.Equal(reported, Assert.Single(fake.NonLiveReports).MarketDataType);
        Assert.Null(Assert.Single(fake.Options).Envelope.MarketDataType);
    }

    [Fact]
    public void The_first_tick_after_a_replay_closes_the_gap_and_re_raises_a_standing_non_live_alarm()
    {
        // Ordering, and it is load-bearing. A lease's gap scope holds ONE open row, so "a tick
        // resumed" closes whatever is there — including a non_live_market_data row this same sink
        // opened moments earlier, when TWS answered marketDataType before the first tick arrived.
        // TWS reports a ticker's type once per reqMktData, so nothing would ever raise it again:
        // the alarm would vanish on precisely the path (a 1101 replay) where it matters most.
        var fake = new FakeSink();
        var leaseId = Guid.NewGuid();
        var recorder = new RecordingTickSink(12345, leaseId, isOption: true, markFirstTickAsReplay: true, fake, _ => { });

        recorder.ApplyMarketDataType(3);
        recorder.ApplyPrice(TickType.BID, 1.95d);

        // The close carries the effective type rather than the sink firing a second, racing call:
        // whichever of the two landed first would decide the outcome, and only one order is right.
        Assert.Equal(["nonlive:3", "close:3"], fake.Calls);
    }

    [Fact]
    public void A_replay_tick_with_no_report_yet_closes_the_gap_and_raises_nothing()
    {
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake, markFirstTickAsReplay: true);

        recorder.ApplyPrice(TickType.BID, 1.95d);

        Assert.Equal(["close:null"], fake.Calls);
    }

    [Fact]
    public void The_marketDataType_callback_reaches_the_registered_sink_and_only_that_sink()
    {
        // The wiring, driven through IbkrClientWrapper rather than by calling the sink directly —
        // the failure docs/LESSONS.md #2 records verbatim ("an openOrder test that called the
        // tracker method directly and passed with the callback wiring deleted"). Every other test
        // in this file would still pass with the routing line removed from the wrapper.
        //
        // The negative half is the same assertion the replay design rests on: a report for a ticker
        // that has been deregistered — which is every ticker a re-issue supersedes — resolves to no
        // sink at all and is dropped, rather than landing on a subscription that no longer exists.
        var registry = new IbkrRequestRegistry();
        var wrapper = new IbkrClientWrapper(
            registry, new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance),
            NullLogger<IbkrClientWrapper>.Instance);

        var live = new FakeSink();
        var superseded = new FakeSink();
        var liveSink = CreateOptionSink(live);
        var supersededSink = CreateOptionSink(superseded);

        registry.Register(requestId: 4001, supersededSink);
        registry.Register(requestId: 4002, liveSink);
        registry.Remove(4001); // exactly what IssueAsync does to the ticker a re-issue displaces

        wrapper.marketDataType(4001, 3);
        wrapper.marketDataType(4002, 3);

        Assert.Empty(superseded.NonLiveReports);
        Assert.Equal(3, Assert.Single(live.NonLiveReports).MarketDataType);

        liveSink.ApplyPrice(TickType.BID, 1.95d);
        Assert.Equal((short)3, Assert.Single(live.Options).Envelope.MarketDataType);
    }

    [Fact]
    public void Fail_invokes_the_onFailed_callback()
    {
        Exception? captured = null;
        var fake = new FakeSink();
        var recorder = CreateOptionSink(fake, onFailed: ex => captured = ex);

        var error = new InvalidOperationException("connection lost");
        recorder.Fail(error);

        Assert.Same(error, captured);
    }
}
