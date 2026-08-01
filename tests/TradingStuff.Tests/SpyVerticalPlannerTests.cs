using TradingStuff.Contracts;
using TradingStuff.ResearchService.Automation;

namespace TradingStuff.Tests;

/// <summary>
/// Strike selection and pricing, both pure. Every failing case asserts a REASON, not just a null:
/// a planner that refuses without saying why produces a decision row that reads as "automation did
/// nothing", which is indistinguishable from a healthy idle loop.
/// </summary>
public sealed class SpyVerticalPlannerTests
{
    private static OptionContract Call(decimal strike) =>
        new($"SPY20260807C{strike:F0}", "SPY", new DateOnly(2026, 8, 7), strike, OptionRight.Call, TradingClass: "SPY");

    private static OptionContract Put(decimal strike) =>
        new($"SPY20260807P{strike:F0}", "SPY", new DateOnly(2026, 8, 7), strike, OptionRight.Put, TradingClass: "SPY");

    private static QuoteSnapshot Quote(OptionContract contract, decimal bid, decimal ask) =>
        new(Guid.NewGuid(), contract, bid, ask, (bid + ask) / 2m,
            new OptionGreeks(0.5m, 0.01m, -0.2m, 0.3m), DateTimeOffset.UtcNow, "ibkr-delayed");

    // ---- strike selection ------------------------------------------------------------------------

    [Fact]
    public void Selects_the_first_strike_at_or_above_spot_and_an_exact_width_above_it()
    {
        OptionContract[] chain = [Call(738), Call(739), Call(740), Call(741), Call(742), Put(740)];

        var (longLeg, shortLeg, failure) = SpyVerticalPlanner.SelectVertical(chain, 739.40m, 1m);

        Assert.Null(failure);
        Assert.Equal(740m, longLeg!.Strike);
        Assert.Equal(741m, shortLeg!.Strike);
    }

    [Fact]
    public void Selects_the_strike_exactly_at_spot_rather_than_the_one_above_it()
    {
        // "At or above", not "above". A rule of "strictly above" pays a full strike more for the same
        // spread and is the kind of off-by-one that never announces itself.
        OptionContract[] chain = [Call(739), Call(740), Call(741)];

        var (longLeg, _, failure) = SpyVerticalPlanner.SelectVertical(chain, 740m, 1m);

        Assert.Null(failure);
        Assert.Equal(740m, longLeg!.Strike);
    }

    [Fact]
    public void Ignores_puts_entirely()
    {
        OptionContract[] chain = [Put(738), Put(739), Put(740), Call(740), Call(741)];

        var (longLeg, shortLeg, failure) = SpyVerticalPlanner.SelectVertical(chain, 739.40m, 1m);

        Assert.Null(failure);
        Assert.Equal(OptionRight.Call, longLeg!.Right);
        Assert.Equal(OptionRight.Call, shortLeg!.Right);
    }

    [Fact]
    public void Refuses_when_the_window_contains_no_calls()
    {
        var (longLeg, _, failure) = SpyVerticalPlanner.SelectVertical([Put(740)], 739m, 1m);

        Assert.Null(longLeg);
        Assert.Contains("no calls", failure);
    }

    [Fact]
    public void Refuses_when_every_listed_strike_is_below_spot()
    {
        // A window that does not reach spot is not centred where it claims to be — the shape that
        // once let NodeSelector rebind an entire grid to deep-OTM contracts.
        var (longLeg, _, failure) = SpyVerticalPlanner.SelectVertical([Call(700), Call(701)], 739m, 1m);

        Assert.Null(longLeg);
        Assert.Contains("not centred", failure);
    }

    [Fact]
    public void Refuses_when_no_strike_sits_at_exactly_the_requested_width()
    {
        // The nearest strike above is 745, not 741. Taking it would build a 5-wide spread whose
        // maximum loss is five times the one that was checked against the debit cap.
        var (longLeg, _, failure) = SpyVerticalPlanner.SelectVertical([Call(740), Call(745)], 739m, 1m);

        Assert.Null(longLeg);
        Assert.Contains("cannot be built", failure);
    }

    // ---- pricing ---------------------------------------------------------------------------------

    [Fact]
    public void Prices_the_natural_plus_the_buffer_rounded_up_to_the_cent()
    {
        var longLeg = Call(740);
        var shortLeg = Call(741);

        // 2.41 - 1.98 = 0.43 natural, + 0.05 buffer = 0.48.
        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [Quote(longLeg, 2.38m, 2.41m), Quote(shortLeg, 1.98m, 2.01m)], longLeg, shortLeg, 0.05m);

        Assert.Null(failure);
        Assert.Equal(0.48m, debit);
    }

    [Fact]
    public void Rounds_the_limit_up_never_down()
    {
        var longLeg = Call(740);
        var shortLeg = Call(741);

        // 2.415 - 1.98 = 0.435, + 0.05 = 0.485. Rounded DOWN to 0.48 this limit does not cross, which
        // turns a marketable order into a resting one with nothing saying so.
        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [Quote(longLeg, 2.38m, 2.415m), Quote(shortLeg, 1.98m, 2.01m)], longLeg, shortLeg, 0.05m);

        Assert.Null(failure);
        Assert.Equal(0.49m, debit);
    }

    [Fact]
    public void Refuses_when_the_long_leg_has_no_offer()
    {
        // The live Saturday state: SPY options quote 0/0 outside the regular session. Measured
        // against the paper gateway on 2026-08-01 for SPY 2026-08-07 740C/742C.
        var longLeg = Call(740);
        var shortLeg = Call(741);

        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [Quote(longLeg, 0m, 0m), Quote(shortLeg, 1.98m, 2.01m)], longLeg, shortLeg, 0.05m);

        Assert.Null(debit);
        Assert.Contains("no offer", failure);
    }

    [Fact]
    public void Refuses_when_the_short_leg_has_no_bid()
    {
        var longLeg = Call(740);
        var shortLeg = Call(741);

        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [Quote(longLeg, 2.38m, 2.41m), Quote(shortLeg, 0m, 0m)], longLeg, shortLeg, 0.05m);

        Assert.Null(debit);
        Assert.Contains("no bid", failure);
    }

    [Fact]
    public void Refuses_an_inverted_book_rather_than_submitting_a_credit()
    {
        var longLeg = Call(740);
        var shortLeg = Call(741);

        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [Quote(longLeg, 1.90m, 1.95m), Quote(shortLeg, 2.10m, 2.15m)], longLeg, shortLeg, 0.05m);

        Assert.Null(debit);
        Assert.Contains("cannot be a credit", failure);
    }

    [Fact]
    public void Refuses_when_a_leg_has_no_quote_at_all()
    {
        var longLeg = Call(740);
        var shortLeg = Call(741);

        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [Quote(longLeg, 2.38m, 2.41m)], longLeg, shortLeg, 0.05m);

        Assert.Null(debit);
        Assert.Contains("short leg", failure);
    }

    [Fact]
    public void Correlates_quotes_on_the_contract_key_not_the_whole_record()
    {
        // The provider echoes a contract enriched with a field the request leg did not carry. Keyed on
        // the record, this lookup throws; keyed on OptionContractKey it resolves. PaperExecutionEngine
        // shipped the record version and threw KeyNotFoundException mid-order.
        var longLeg = Call(740) with { TradingClass = null };
        var shortLeg = Call(741) with { TradingClass = null };

        var (debit, failure) = SpyVerticalPlanner.ComputeMarketableDebit(
            [
                Quote(Call(740) with { TradingClass = "SPY" }, 2.38m, 2.41m),
                Quote(Call(741) with { TradingClass = "SPY" }, 1.98m, 2.01m),
            ],
            longLeg with { TradingClass = "spy" },
            shortLeg with { TradingClass = "SPY" },
            0.05m);

        Assert.Null(failure);
        Assert.Equal(0.48m, debit);
    }
}
