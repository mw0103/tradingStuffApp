using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The v2 PASS/FAIL rule, at its boundaries. A FAIL stops the program, so both inequalities are
/// pinned here rather than left to whoever next reads the sentence in the registration.
///
/// v2: PASS = mean(RF/IM) &lt; 1 AND the week-clustered 95% CI for mean(RF/IM) excludes 1. Both
/// boundaries are against 1 now — the mean's and both interval endpoints' — which is why the
/// rounding-collision annotation is exercised on all three.
/// </summary>
public sealed class C1VerdictTests
{
    private static BootstrapInterval Interval(decimal lower, decimal upper) => new(lower, upper, 0.95m, 10_000, 50, 500);

    [Fact]
    public void Pass_needs_both_a_mean_below_one_and_an_interval_clear_of_one()
    {
        var verdict = C1Verdict.Decide(0.812345m, Interval(0.731m, 0.904m));

        Assert.True(verdict.Computed);
        Assert.True(verdict.Pass);
        Assert.Equal("below", verdict.Side);
        Assert.Contains("VERDICT: PASS", verdict.Line);
        Assert.Contains("0.812345", verdict.Line);
        Assert.Contains("excludes 1", verdict.Line);
    }

    [Fact]
    public void A_mean_of_exactly_one_fails_because_the_rule_says_strictly_less()
    {
        // The interval sits above 1 and so excludes it; the mean is what fails.
        var verdict = C1Verdict.Decide(1m, Interval(1.2m, 1.4m));

        Assert.False(verdict.Pass);
        Assert.False(verdict.MeanBelowOne);
        Assert.True(verdict.IntervalExcludesOne);
        Assert.Contains("VERDICT: FAIL", verdict.Line);
        Assert.Contains(">= 1", verdict.Line);
    }

    [Fact]
    public void A_mean_a_hair_below_one_passes_when_the_interval_excludes_one()
    {
        var verdict = C1Verdict.Decide(0.9999999999m, Interval(0.7m, 0.9999999999m));
        Assert.True(verdict.Pass);
        Assert.Equal("below", verdict.Side);
    }

    [Fact]
    public void An_interval_whose_upper_bound_is_exactly_one_does_not_exclude_it_and_fails()
    {
        var verdict = C1Verdict.Decide(0.8m, Interval(0.6m, 1m));

        Assert.False(verdict.Pass);
        Assert.True(verdict.MeanBelowOne);
        Assert.False(verdict.IntervalExcludesOne);
        Assert.Equal("contains", verdict.Side);
        Assert.Contains("contains 1", verdict.Line);
    }

    [Fact]
    public void An_interval_whose_lower_bound_is_exactly_one_does_not_exclude_it_either()
    {
        var verdict = C1Verdict.Decide(1.4m, Interval(1m, 1.9m));

        Assert.False(verdict.Pass);
        Assert.False(verdict.IntervalExcludesOne);
        Assert.Equal("contains", verdict.Side);
    }

    [Fact]
    public void An_interval_entirely_below_one_excludes_it_on_the_low_side_and_passes()
    {
        var verdict = C1Verdict.Decide(0.8m, Interval(0.61m, 0.99999m));

        Assert.True(verdict.IntervalExcludesOne);
        Assert.Equal("below", verdict.Side);
        Assert.True(verdict.Pass);
        Assert.Contains("(below it)", verdict.Line);
    }

    [Fact]
    public void An_interval_entirely_above_one_excludes_it_on_the_high_side_and_still_fails()
    {
        // Excluding 1 is not enough: the mean must be BELOW 1. An interval above 1 is evidence of the
        // opposite claim, and the rule has to render that as FAIL rather than as an exclusion.
        var verdict = C1Verdict.Decide(1.23m, Interval(1.10m, 1.41m));

        Assert.True(verdict.IntervalExcludesOne);
        Assert.Equal("above", verdict.Side);
        Assert.False(verdict.Pass);
        Assert.Contains("(above it)", verdict.Line);
        Assert.Contains("VERDICT: FAIL", verdict.Line);
    }

    [Fact]
    public void An_interval_that_straddles_one_fails_however_low_the_mean_is()
    {
        var verdict = C1Verdict.Decide(0.2m, Interval(0.42m, 1.58m));

        Assert.False(verdict.Pass);
        Assert.True(verdict.MeanBelowOne);
        Assert.Equal("contains", verdict.Side);
    }

    /// <summary>
    /// The comparison is made on the unrounded decimal, but the sentence is the only evidence the
    /// reader gets. At six places, 0.9999996 renders as "1.000000", so the line would read
    /// "mean(RF/IM) = 1.000000 &lt; 1" — a correct verdict whose printed evidence contradicts it.
    /// </summary>
    [Fact]
    public void A_mean_whose_rendering_collides_with_one_is_printed_unrounded_beside_it()
    {
        var verdict = C1Verdict.Decide(0.9999996m, Interval(0.6m, 0.8m));

        Assert.True(verdict.Pass);
        Assert.Contains("mean(RF/IM) = 0.9999996 (shown unrounded: F6 would print 1.000000) < 1", verdict.Line);
        Assert.DoesNotContain("= 1.000000 <", verdict.Line);
    }

    [Fact]
    public void An_interval_endpoint_whose_rendering_collides_with_one_is_printed_unrounded_beside_it()
    {
        var below = C1Verdict.Decide(0.8m, Interval(0.6m, 0.9999996m));

        Assert.True(below.IntervalExcludesOne);
        Assert.Contains("[0.600000, 0.9999996 (shown unrounded: F6 would print 1.000000)] excludes 1 (below it)", below.Line);

        // The lower endpoint too, and in the contains-1 wording as well as the excludes wording.
        var contains = C1Verdict.Decide(0.8m, Interval(0.9999996m, 1.4m));
        Assert.False(contains.IntervalExcludesOne);
        Assert.Contains("[0.9999996 (shown unrounded: F6 would print 1.000000), 1.400000] contains 1", contains.Line);
    }

    [Fact]
    public void A_value_that_really_is_the_boundary_is_printed_plainly_because_the_rendering_is_exact()
    {
        // The annotation exists for a rendering that lies. An exact 1 does not lie, and the clause
        // already says which side of the boundary it falls on.
        var mean = C1Verdict.Decide(1m, Interval(1.2m, 1.4m));
        Assert.Contains("mean(RF/IM) = 1.000000 >= 1", mean.Line);
        Assert.DoesNotContain("shown unrounded", mean.Line);

        var endpoint = C1Verdict.Decide(0.8m, Interval(0.6m, 1m));
        Assert.Contains("[0.600000, 1.000000] contains 1", endpoint.Line);
        Assert.DoesNotContain("shown unrounded", endpoint.Line);
    }

    [Fact]
    public void A_value_nowhere_near_its_boundary_keeps_the_plain_six_place_rendering()
    {
        var verdict = C1Verdict.Decide(0.812345m, Interval(0.731m, 0.904m));

        Assert.Equal(
            "**VERDICT: PASS** — mean(RF/IM) = 0.812345 < 1 and the 95% week-clustered interval for " +
            "mean(RF/IM) = [0.731000, 0.904000] excludes 1 (below it).",
            verdict.Line);
    }

    [Fact]
    public void A_missing_statistic_is_not_computed_rather_than_a_fail()
    {
        var noMean = C1Verdict.Decide(null, Interval(0.6m, 0.7m));
        Assert.False(noMean.Computed);
        Assert.False(noMean.Pass);
        Assert.Contains("NOT COMPUTED", noMean.Line);

        var noInterval = C1Verdict.Decide(0.8m, null);
        Assert.False(noInterval.Computed);
        Assert.Contains("NOT COMPUTED", noInterval.Line);

        var stated = C1Verdict.Decide(null, null, "gate 09 was not applied");
        Assert.Contains("gate 09 was not applied", stated.Line);
    }

    /// <summary>
    /// The economic cross-check's sign convention, in all three states. The convention is fixed here
    /// because the memo asserts an agreement, and an agreement whose direction was chosen after the
    /// fact is not a check.
    /// </summary>
    [Fact]
    public void The_straddle_cross_check_agrees_with_pass_only_when_the_long_straddle_loses()
    {
        var pass = C1Verdict.Decide(0.8m, Interval(0.6m, 0.9m));
        var fail = C1Verdict.Decide(1.2m, Interval(1.1m, 1.4m));

        Assert.Equal(StraddleCrossCheck.Agrees, StraddleCrossCheck.Evaluate(pass, -0.12m).Agreement);
        Assert.Equal(StraddleCrossCheck.Disagrees, StraddleCrossCheck.Evaluate(pass, 0.12m).Agreement);
        Assert.Equal(StraddleCrossCheck.Agrees, StraddleCrossCheck.Evaluate(fail, 0.12m).Agreement);
        Assert.Equal(StraddleCrossCheck.Disagrees, StraddleCrossCheck.Evaluate(fail, -0.12m).Agreement);

        // Exactly zero is non-negative: no premium is not a premium, so it agrees with FAIL.
        Assert.Equal(StraddleCrossCheck.Agrees, StraddleCrossCheck.Evaluate(fail, 0m).Agreement);
        Assert.Equal(StraddleCrossCheck.Disagrees, StraddleCrossCheck.Evaluate(pass, 0m).Agreement);

        // No mean, or no verdict: not computable, never a default agreement.
        Assert.Equal(StraddleCrossCheck.NotComputable, StraddleCrossCheck.Evaluate(pass, null).Agreement);
        Assert.Equal(StraddleCrossCheck.NotComputable, StraddleCrossCheck.Evaluate(C1Verdict.Decide(null, null), -0.5m).Agreement);
    }
}
