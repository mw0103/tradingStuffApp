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

    /// <summary>
    /// The comparison is made on the unrounded decimal, but the sentence is the only evidence the
    /// reader gets. At six places, 0.9999996 renders as "1.000000", so the line read
    /// "median(RF/IM) = 1.000000 &lt; 1" — a correct verdict whose printed evidence contradicts it.
    /// </summary>
    [Fact]
    public void A_median_whose_rendering_collides_with_one_is_printed_unrounded_beside_it()
    {
        var verdict = C1Verdict.Decide(0.9999996m, Interval(0.6m, 0.7m));

        Assert.True(verdict.Pass);
        Assert.Contains("median(RF/IM) = 0.9999996 (shown unrounded: F6 would print 1.000000) < 1", verdict.Line);
        Assert.DoesNotContain("= 1.000000 <", verdict.Line);
    }

    [Fact]
    public void An_interval_endpoint_whose_rendering_collides_with_one_half_is_printed_unrounded_beside_it()
    {
        var below = C1Verdict.Decide(0.8m, Interval(0.3m, 0.4999996m));

        Assert.True(below.IntervalExcludesHalf);
        Assert.Contains("[0.300000, 0.4999996 (shown unrounded: F6 would print 0.500000)] excludes 0.5 (below it)", below.Line);

        // The lower endpoint too, and in the contains-0.5 wording as well as the excludes wording.
        var contains = C1Verdict.Decide(0.8m, Interval(0.4999996m, 0.7m));
        Assert.False(contains.IntervalExcludesHalf);
        Assert.Contains("[0.4999996 (shown unrounded: F6 would print 0.500000), 0.700000] contains 0.5", contains.Line);
    }

    [Fact]
    public void A_value_that_really_is_the_boundary_is_printed_plainly_because_the_rendering_is_exact()
    {
        // The annotation exists for a rendering that lies. An exact 1 and an exact 0.5 do not lie, and
        // the clause already says which side of the boundary they fall on.
        var median = C1Verdict.Decide(1m, Interval(0.6m, 0.7m));
        Assert.Contains("median(RF/IM) = 1.000000 >= 1", median.Line);
        Assert.DoesNotContain("shown unrounded", median.Line);

        var endpoint = C1Verdict.Decide(0.8m, Interval(0.5m, 0.7m));
        Assert.Contains("[0.500000, 0.700000] contains 0.5", endpoint.Line);
        Assert.DoesNotContain("shown unrounded", endpoint.Line);
    }

    [Fact]
    public void A_value_nowhere_near_its_boundary_keeps_the_plain_six_place_rendering()
    {
        var verdict = C1Verdict.Decide(0.812345m, Interval(0.531m, 0.604m));

        Assert.Equal(
            "**VERDICT: PASS** — median(RF/IM) = 0.812345 < 1 and the 95% week-clustered interval for " +
            "P(RF < IM) = [0.531000, 0.604000] excludes 0.5 (above it).",
            verdict.Line);
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
