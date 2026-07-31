using IBApi;
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

        public void EnqueueOption(OptionQuoteObservation observation) => Options.Add(observation);

        public void EnqueueUnderlying(UnderlyingTickObservation observation) => Underlyings.Add(observation);

        public void NotifyGapClosed(Guid leaseId) => ClosedGaps.Add(leaseId);
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
