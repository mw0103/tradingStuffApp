using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The calibration that motivated the v2 amendment, run through the REAL compute step end to end.
///
/// A FAIRLY PRICED synthetic market: every event's implied move is set to the EXPECTED absolute move
/// of the distribution that generates its realized move, so there is no premium anywhere in the
/// population by construction. Absolute moves are right-skewed, so the retired v0 criterion —
/// median(RF/IM) &lt; 1 with the interval for P(RF &lt; IM) excluding 0.5 — is satisfied by this
/// population; the v2 criterion is not, because under fair per-event pricing mean(RF/IM) is exactly
/// 1 by the tower property. That is the whole reason the criterion was amended, and it is asserted
/// here on a population the compute step actually processes rather than argued in a comment.
///
/// The same population with every implied move inflated by 30% — a real 30% premium — must then
/// PASS under v2. A criterion that fails the fair market and passes the dear one is doing its job;
/// one that passes both is measuring shape.
///
/// <b>The population.</b> Twenty absolute-move magnitudes with a right-skewed shape, each appearing
/// exactly twelve times across 240 events, spread over 24 earnings weeks by a rotation coprime with
/// both counts so no week is a copy of another. They sum to exactly 1, so their mean is 0.05 and
/// every ratio m / 0.05 = m x 20 is exact in <c>decimal</c> — the fair-pricing mean is exactly 1 and
/// not a rounding away from it. Their median is 0.0355, so median/mean = 0.71, and 13 of the 20 sit
/// below the mean: P(RF &lt; IM) = 0.65. Nothing is drawn at random and nothing is rounded.
///
/// <b>The straddle.</b> Held to expiry, an ATM straddle is worth |S_T - K|, so each event's exit mid
/// is its own realized move x spot. Under fair pricing the mean hold-through return is then exactly
/// zero, and under a 30% premium it is exactly 1/1.3 - 1. The economic cross-check therefore falls
/// out of the same fixture instead of being asserted separately.
/// </summary>
public sealed class FairPricingCalibrationTests
{
    /// <summary>Twenty right-skewed absolute moves summing to exactly 1. Mean 0.05, median 0.0355, 13 of 20 below the mean.</summary>
    private static readonly decimal[] Magnitudes =
    [
        0.002m, 0.004m, 0.007m, 0.010m, 0.013m, 0.016m, 0.020m, 0.024m, 0.028m, 0.033m,
        0.038m, 0.043m, 0.048m, 0.052m, 0.060m, 0.072m, 0.090m, 0.115m, 0.145m, 0.180m
    ];

    /// <summary>The expected absolute move of that distribution: they sum to 1, so 1 / 20. This is the fair implied move.</summary>
    private const decimal FairImpliedMove = 0.05m;

    private const int Events = 240;
    private const int Weeks = 24;
    private const int Reps = 400;

    /// <summary>
    /// The population is only "fairly priced" if the implied move really is the expected absolute
    /// move, so that is checked before anything is concluded from it. A table edited into a shape
    /// that no longer averages to <see cref="FairImpliedMove"/> would make every other assertion in
    /// this file a statement about a market with a premium in it.
    /// </summary>
    [Fact]
    public void The_population_really_is_priced_at_its_own_expected_absolute_move()
    {
        Assert.Equal(20, Magnitudes.Length);
        Assert.Equal<IEnumerable<decimal>>(Magnitudes, [.. Magnitudes.Order()]);
        Assert.Equal(FairImpliedMove, Statistics.Mean(Magnitudes));
        Assert.True(Statistics.Median(Magnitudes) < FairImpliedMove, "the magnitudes are not right-skewed");
        Assert.Equal(13, Magnitudes.Count(m => m < FairImpliedMove));
        Assert.Equal(0, Events % Magnitudes.Length);
    }

    [Fact]
    public async Task A_fairly_priced_population_fails_v2_with_the_interval_containing_one()
    {
        using var study = Population(FairImpliedMove);

        Assert.Equal(0, await study.RunWithRealDefaultsAsync("--reps", Reps.ToString()));

        var sample = PrimarySample(study);
        Assert.Equal(Events, sample.Count);

        // No premium: the mean ratio is exactly 1, so the strict inequality fails on its own.
        var mean = Statistics.Mean([.. sample.Select(e => e.Ratio)]);
        Assert.Equal(1m, mean);

        var bootstrap = Bootstrap(sample);
        var verdict = C1Verdict.Decide(mean, bootstrap.Mean);

        Assert.False(verdict.Pass);
        Assert.False(verdict.MeanBelowOne);
        Assert.False(verdict.IntervalExcludesOne);
        Assert.Equal("contains", verdict.Side);
        Assert.Contains(
            "**VERDICT: FAIL** — mean(RF/IM) = 1.000000 >= 1 and the 95% week-clustered interval for " +
            "mean(RF/IM) = [0.912500, 1.084000] contains 1.",
            study.Memo);

        // The interval really does straddle 1 rather than sitting on it.
        Assert.True(bootstrap.Mean.Lower < 1m, $"lower endpoint {bootstrap.Mean.Lower} is not below 1");
        Assert.True(bootstrap.Mean.Upper > 1m, $"upper endpoint {bootstrap.Mean.Upper} is not above 1");

        // And the economic cross-check agrees with the FAIL: a fairly priced straddle held through
        // the print returns exactly zero on average, which is non-negative.
        Assert.Equal(0m, Statistics.Straddle(sample).Mean);
        Assert.Contains(
            "Mean hold-through return over the primary sample = **0.000000**, which **agrees** with the FAIL above.",
            study.Memo);
    }

    /// <summary>
    /// The retired v0 criterion, applied to that same fairly priced population and to the same
    /// bootstrap replicates. It PASSES — which is the proof that it measured distribution shape and
    /// not a premium, and the reason v2 exists. Nothing in the product evaluates this rule any more;
    /// it is reconstructed here from the production statistics so the claim stays checkable.
    /// </summary>
    [Fact]
    public async Task The_retired_v0_criterion_passes_that_same_fairly_priced_population()
    {
        using var study = Population(FairImpliedMove);

        Assert.Equal(0, await study.RunWithRealDefaultsAsync("--reps", Reps.ToString()));

        var sample = PrimarySample(study);
        var bootstrap = Bootstrap(sample);

        var median = Statistics.Median([.. sample.Select(e => e.Ratio)]);
        var proportion = Statistics.ProportionLess(sample);

        // median(m) / mean(m) = 0.0355 / 0.05 = 0.71, and 13 of the 20 magnitudes are below the mean.
        Assert.Equal(0.71m, median);
        Assert.Equal(0.65m, proportion);

        // v0's rule, spelled out: median below 1 AND the interval for P(RF < IM) clear of 0.5.
        Assert.True(median < 1m);
        Assert.True(bootstrap.ProportionLess.Lower > 0.5m,
            $"v0 would not have passed: P(RF < IM) interval [{bootstrap.ProportionLess.Lower}, {bootstrap.ProportionLess.Upper}] does not exclude 0.5");

        // Both v0 statistics are still printed, labelled as readouts that decide nothing — and this is
        // what they say on a market with no premium in it at all.
        Assert.Contains("| median(RF/IM) — DESCRIPTIVE READOUT | 0.710000 | [0.660000, 0.760000] |", study.Memo);
        Assert.Contains("| P(RF < IM) — DESCRIPTIVE READOUT | 0.650000 | [0.616667, 0.683333] |", study.Memo);
        Assert.Contains("**VERDICT: FAIL**", study.Memo);
    }

    [Fact]
    public async Task The_same_population_with_a_thirty_percent_premium_passes_v2()
    {
        using var study = Population(FairImpliedMove * 1.3m);

        Assert.Equal(0, await study.RunWithRealDefaultsAsync("--reps", Reps.ToString()));

        var sample = PrimarySample(study);
        var mean = Statistics.Mean([.. sample.Select(e => e.Ratio)]);
        var bootstrap = Bootstrap(sample);
        var verdict = C1Verdict.Decide(mean, bootstrap.Mean);

        // 1 / 1.3 to decimal's precision.
        Assert.Equal(0.769231m, Math.Round(mean!.Value, 6));
        Assert.True(verdict.Pass);
        Assert.Equal("below", verdict.Side);
        Assert.True(bootstrap.Mean.Upper < 1m, $"upper endpoint {bootstrap.Mean.Upper} does not clear 1");
        Assert.Contains(
            "**VERDICT: PASS** — mean(RF/IM) = 0.769231 < 1 and the 95% week-clustered interval for " +
            "mean(RF/IM) = [0.701923, 0.833846] excludes 1 (below it).",
            study.Memo);

        // The long straddle loses 1 - 1/1.3 of its premium on average, which agrees with PASS.
        var straddle = Statistics.Straddle(sample).Mean;
        Assert.True(straddle < 0m, $"the straddle return {straddle} is not negative");
        Assert.Contains(
            "Mean hold-through return over the primary sample = **-0.230769**, which **agrees** with the PASS above.",
            study.Memo);
    }

    /// <summary>
    /// The third state of the cross-check: a PASS whose straddle return is POSITIVE. The memo must
    /// say "disagrees" rather than quietly print a number the reader has to interpret — an agreement
    /// reported only when it agrees is not a check.
    /// </summary>
    [Fact]
    public async Task A_pass_whose_straddle_return_is_positive_is_reported_as_a_disagreement()
    {
        using var study = Population(FairImpliedMove * 1.3m, exitMidMultiple: 3m);

        Assert.Equal(0, await study.RunWithRealDefaultsAsync("--reps", Reps.ToString()));

        Assert.Contains("**VERDICT: PASS**", study.Memo);
        Assert.True(Statistics.Straddle(PrimarySample(study)).Mean > 0m);
        Assert.Contains("which **disagrees** with the PASS above", study.Memo);
    }

    /// <summary>
    /// The population, priced at <paramref name="impliedMove"/>. Every event's realized move is one
    /// of the twenty magnitudes; the rotation by 7 (coprime with 20) and by 11 (coprime with 24)
    /// gives every magnitude exactly twelve appearances and every week a different mix.
    /// </summary>
    private static StudyHarness Population(decimal impliedMove, decimal? exitMidMultiple = null)
    {
        var study = new StudyHarness();
        for (var i = 0; i < Events; i++)
        {
            var magnitude = Magnitudes[(i * 7) % Magnitudes.Length];

            // Held to expiry an ATM straddle is worth |S_T - K| = magnitude x spot, and spot is 100.
            var exitMid = (exitMidMultiple ?? 1m) * magnitude * 100m;

            study.AddEvent(
                $"e{i:000}",
                rf: magnitude,
                im: impliedMove,
                week: $"2024-W{(i * 11) % Weeks:00}",
                straddleMidExit: exitMid);
        }

        return study;
    }

    /// <summary>
    /// The primary sample rebuilt from the event table the run wrote. Reading it back rather than
    /// from memory is deliberate: the memo claims to be re-derivable from that file, and every
    /// statistic asserted here is then computed from the same rows a reader would use.
    /// </summary>
    private static List<SampleEvent> PrimarySample(StudyHarness study) =>
    [
        .. study.EventTable
            .Where(r => r is { InPrimary: true, RatioMeasurable: true })
            .OrderBy(r => r.EventId, StringComparer.Ordinal)
            .Select(r => new SampleEvent(
                r.EventId,
                r.ClusterKey,
                r.Ratio!.Value,
                r.RfLessThanIm!.Value,
                r.RealizedMove == r.ImpliedMove,
                r.MarketCap,
                r.SpreadFraction,
                r.DteCalendarDays,
                r.TimingClass,
                r.StraddleReturn))
    ];

    /// <summary>The same draw the memo's primary interval used: the registered seed mixed with that analysis name.</summary>
    private static BootstrapResult Bootstrap(List<SampleEvent> sample) =>
        ClusteredBootstrap.Run(sample, Reps, C1Registration.ConfidenceLevel,
            ClusteredBootstrap.SeedFor(C1Registration.BootstrapSeed, "primary:overall"))!;
}
