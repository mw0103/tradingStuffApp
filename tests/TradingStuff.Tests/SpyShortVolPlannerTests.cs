using TradingStuff.Contracts;
using TradingStuff.ResearchService.Automation;

namespace TradingStuff.Tests;

/// <summary>
/// Strike selection, credit pricing, and the sign convention for the put credit spread. Pure
/// paths only, and every refusal asserts a reason — same discipline as the debit planner's suite,
/// because a planner that refuses silently reads as a healthy idle loop.
/// </summary>
public sealed class SpyShortVolPlannerTests
{
    private static OptionContract Put(decimal strike) =>
        new($"SPY20260901P{strike:F0}", "SPY", new DateOnly(2026, 9, 1), strike, OptionRight.Put, TradingClass: "SPY");

    private static OptionContract Call(decimal strike) =>
        new($"SPY20260901C{strike:F0}", "SPY", new DateOnly(2026, 9, 1), strike, OptionRight.Call, TradingClass: "SPY");

    private static QuoteSnapshot Quote(OptionContract contract, decimal bid, decimal ask) =>
        new(Guid.NewGuid(), contract, bid, ask, (bid + ask) / 2m,
            new OptionGreeks(-0.3m, 0.01m, -0.2m, 0.3m), DateTimeOffset.UtcNow, "ibkr-delayed");

    // ---- strike selection ------------------------------------------------------------------------

    [Fact]
    public void Selects_the_last_strike_at_or_below_the_otm_target_and_an_exact_width_below_it()
    {
        OptionContract[] chain = [Put(725), Put(726), Put(727), Put(728), Put(740), Call(740)];

        // Reference 740, offset 2% → short target 725.20 → last strike at or below is 725. But the
        // wing needs 724, which is missing — so this asserts the exact-width refusal too. Use a
        // chain that has both.
        OptionContract[] complete = [Put(724), Put(725), Put(726), Put(727), Put(740), Call(740)];

        var (shortLeg, longLeg, failure) = SpyShortVolPlanner.SelectPutCreditSpread(complete, 740m, 0.02m, 1m);

        Assert.Null(failure);
        Assert.Equal(725m, shortLeg!.Strike);
        Assert.Equal(724m, longLeg!.Strike);

        var (_, _, widthFailure) = SpyShortVolPlanner.SelectPutCreditSpread(chain, 740m, 0.02m, 1m);
        Assert.Contains("No put is listed at 724", widthFailure);
    }

    [Fact]
    public void The_short_put_is_never_inside_the_declared_offset()
    {
        // Every strike sits above the 2% OTM target: refusal, with the target in the reason.
        OptionContract[] chain = [Put(735), Put(736), Put(740), Call(740)];

        var (_, _, failure) = SpyShortVolPlanner.SelectPutCreditSpread(chain, 740m, 0.02m, 1m);

        Assert.NotNull(failure);
        Assert.Contains("725.20", failure);
    }

    [Fact]
    public void Calls_in_the_window_are_never_selected()
    {
        OptionContract[] chain = [Call(724), Call(725), Call(740)];

        var (_, _, failure) = SpyShortVolPlanner.SelectPutCreditSpread(chain, 740m, 0.02m, 1m);

        Assert.Equal("The chain window contains no puts.", failure);
    }

    // ---- credit pricing --------------------------------------------------------------------------

    [Fact]
    public void The_marketable_credit_is_the_natural_less_the_buffer_rounded_down()
    {
        var shortLeg = Put(725);
        var longLeg = Put(724);

        // Natural = 1.37 - 0.95 = 0.42; minus 0.05 buffer = 0.37.
        var quotes = new[] { Quote(shortLeg, 1.37m, 1.40m), Quote(longLeg, 0.92m, 0.95m) };

        var (credit, failure) = SpyShortVolPlanner.ComputeMarketableCredit(quotes, shortLeg, longLeg, 0.05m);

        Assert.Null(failure);
        Assert.Equal(0.37m, credit);
    }

    [Fact]
    public void Rounding_gives_up_credit_never_asks_for_more()
    {
        var shortLeg = Put(725);
        var longLeg = Put(724);

        // Natural = 0.423; minus buffer = 0.373 → floors to 0.37, never 0.38: rounding toward more
        // credit would put the limit on the passive side of what was computed.
        var quotes = new[] { Quote(shortLeg, 1.373m, 1.40m), Quote(longLeg, 0.95m, 0.95m) };

        var (credit, _) = SpyShortVolPlanner.ComputeMarketableCredit(quotes, shortLeg, longLeg, 0.05m);

        Assert.Equal(0.37m, credit);
    }

    [Fact]
    public void A_missing_side_is_a_refusal_never_a_substituted_price()
    {
        var shortLeg = Put(725);
        var longLeg = Put(724);

        var noBid = new[] { Quote(shortLeg, 0m, 1.40m), Quote(longLeg, 0.92m, 0.95m) };
        var (_, bidFailure) = SpyShortVolPlanner.ComputeMarketableCredit(noBid, shortLeg, longLeg, 0.05m);
        Assert.Contains("has no bid", bidFailure);

        var noAsk = new[] { Quote(shortLeg, 1.37m, 1.40m), Quote(longLeg, 0.92m, 0m) };
        var (_, askFailure) = SpyShortVolPlanner.ComputeMarketableCredit(noAsk, shortLeg, longLeg, 0.05m);
        Assert.Contains("has no offer", askFailure);
    }

    [Fact]
    public void Inverted_quotes_are_a_refusal()
    {
        var shortLeg = Put(725);
        var longLeg = Put(724);

        // Wing ask above the short bid: the natural is negative, which cannot be a real market.
        var quotes = new[] { Quote(shortLeg, 0.90m, 0.95m), Quote(longLeg, 0.92m, 0.95m) };

        var (_, failure) = SpyShortVolPlanner.ComputeMarketableCredit(quotes, shortLeg, longLeg, 0.05m);

        Assert.Contains("inverted or stale", failure);
    }

    [Fact]
    public void A_credit_consumed_by_the_buffer_is_a_refusal()
    {
        var shortLeg = Put(725);
        var longLeg = Put(724);

        // Natural = 0.04, buffer = 0.05: the spread only exists at the passive price.
        var quotes = new[] { Quote(shortLeg, 0.99m, 1.02m), Quote(longLeg, 0.92m, 0.95m) };

        var (_, failure) = SpyShortVolPlanner.ComputeMarketableCredit(quotes, shortLeg, longLeg, 0.05m);

        Assert.Contains("does not survive", failure);
    }
}
