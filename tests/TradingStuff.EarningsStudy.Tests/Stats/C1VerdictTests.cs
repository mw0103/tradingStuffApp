using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The PASS/FAIL rule, at its boundaries. A FAIL stops the program, so both inequalities are pinned
/// here rather than left to whoever next reads the sentence in the pre-registration.
/// </summary>
public sealed class C1VerdictTests
{
    private static BootstrapInterval Interval(decimal lower, decimal upper) => new(lower, upper, 0.95m, 10_000, 50, 500);

    [Fact]
    public void Pass_needs_both_a_median_below_one_and_an_interval_clear_of_one_half()
    {
        var verdict = C1Verdict.Decide(0.812345m, Interval(0.531m, 0.604m));

        Assert.True(verdict.Computed);
        Assert.True(verdict.Pass);
        Assert.Equal("above", verdict.Side);
        Assert.Contains("VERDICT: PASS", verdict.Line);
        Assert.Contains("0.812345", verdict.Line);
        Assert.Contains("excludes 0.5", verdict.Line);
    }

    [Fact]
    public void A_median_of_exactly_one_fails_because_the_rule_says_strictly_less()
    {
        var verdict = C1Verdict.Decide(1m, Interval(0.6m, 0.7m));

        Assert.False(verdict.Pass);
        Assert.False(verdict.MedianBelowOne);
        Assert.True(verdict.IntervalExcludesHalf);
        Assert.Contains("VERDICT: FAIL", verdict.Line);
        Assert.Contains(">= 1", verdict.Line);
    }

    [Fact]
    public void A_median_a_hair_below_one_passes_when_the_interval_excludes_one_half()
    {
        var verdict = C1Verdict.Decide(0.9999999999m, Interval(0.5000000001m, 0.7m));
        Assert.True(verdict.Pass);
    }

    [Fact]
    public void An_interval_whose_lower_bound_is_exactly_one_half_does_not_exclude_it_and_fails()
    {
        var verdict = C1Verdict.Decide(0.8m, Interval(0.5m, 0.7m));

        Assert.False(verdict.Pass);
        Assert.True(verdict.MedianBelowOne);
        Assert.False(verdict.IntervalExcludesHalf);
        Assert.Equal("contains", verdict.Side);
        Assert.Contains("contains 0.5", verdict.Line);
    }

    [Fact]
    public void An_interval_whose_upper_bound_is_exactly_one_half_does_not_exclude_it_either()
    {
        var verdict = C1Verdict.Decide(0.8m, Interval(0.3m, 0.5m));

        Assert.False(verdict.Pass);
        Assert.False(verdict.IntervalExcludesHalf);
        Assert.Equal("contains", verdict.Side);
    }

    [Fact]
    public void An_interval_entirely_below_one_half_excludes_it_on_the_low_side()
    {
        var verdict = C1Verdict.Decide(0.8m, Interval(0.30m, 0.49999m));

        Assert.True(verdict.IntervalExcludesHalf);
        Assert.Equal("below", verdict.Side);
        Assert.True(verdict.Pass);
        Assert.Contains("(below it)", verdict.Line);
    }

    [Fact]
    public void An_interval_that_straddles_one_half_fails_however_low_the_median_is()
    {
        var verdict = C1Verdict.Decide(0.2m, Interval(0.42m, 0.58m));

        Assert.False(verdict.Pass);
        Assert.True(verdict.MedianBelowOne);
        Assert.Equal("contains", verdict.Side);
    }

    [Fact]
    public void A_missing_statistic_is_not_computed_rather_than_a_fail()
    {
        var noMedian = C1Verdict.Decide(null, Interval(0.6m, 0.7m));
        Assert.False(noMedian.Computed);
        Assert.False(noMedian.Pass);
        Assert.Contains("NOT COMPUTED", noMedian.Line);

        var noInterval = C1Verdict.Decide(0.8m, null);
        Assert.False(noInterval.Computed);
        Assert.Contains("NOT COMPUTED", noInterval.Line);

        var stated = C1Verdict.Decide(null, null, "gate 09 was not applied");
        Assert.Contains("gate 09 was not applied", stated.Line);
    }
}
