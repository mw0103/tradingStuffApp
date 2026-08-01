using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the Diebold-Mariano gate statistic and its Newey-West variance.
/// </summary>
/// <remarks>
/// The HAC correction is the part that matters. Daily loss differentials from persistent
/// volatility regimes are strongly autocorrelated, and treating them as independent understates
/// the standard error — which inflates the statistic and dresses ordinary persistence up as
/// skill. These tests check the correction actually fires, in the right direction, and by an
/// amount computed independently of the implementation.
/// </remarks>
public class DieboldMarianoTests
{
    /// <summary>The Bartlett-weighted long-run variance, recomputed from the definition.</summary>
    private static double LongRunVariance(double[] differentials, int lag)
    {
        var n = differentials.Length;
        var mean = differentials.Average();

        double Gamma(int j)
        {
            double sum = 0.0;
            for (int i = j; i < n; i++) sum += (differentials[i] - mean) * (differentials[i - j] - mean);
            return sum / n;
        }

        var variance = Gamma(0);
        for (int j = 1; j <= lag; j++) variance += 2.0 * (1.0 - (double)j / (lag + 1)) * Gamma(j);
        return variance;
    }

    // ---------- guards ----------

    [Fact]
    public void ComparisonRejectsMalformedInput()
    {
        Assert.Equal("candidateLosses",
            Assert.Throws<ArgumentNullException>(() => DieboldMariano.Compare(null!, [1.0, 2.0])).ParamName);
        Assert.Equal("baselineLosses",
            Assert.Throws<ArgumentNullException>(() => DieboldMariano.Compare([1.0, 2.0], null!)).ParamName);
        Assert.Contains("same days",
            Assert.Throws<ArgumentException>(() => DieboldMariano.Compare([1.0], [1.0, 2.0])).Message,
            StringComparison.Ordinal);
        Assert.Contains("two observations",
            Assert.Throws<ArgumentException>(() => DieboldMariano.Compare([1.0], [1.0])).Message,
            StringComparison.Ordinal);
        Assert.Equal("hacLag",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DieboldMariano.Compare([1.0, 2.0], [1.0, 2.0], -1)).ParamName);
    }

    // ---------- degenerate comparisons ----------

    [Fact]
    public void IdenticalForecastsAreNotAComparison()
    {
        var losses = new[] { 0.1, 0.2, 0.3, 0.4 };

        var result = DieboldMariano.Compare(losses, losses);

        // No differential at all: reporting a statistic would assert something about a
        // comparison that was never made.
        Assert.Equal(0.0, result.MeanDifferential, 15);
        Assert.Equal(0.0, result.Statistic);
        Assert.Equal(1.0, result.PValue);
        Assert.False(result.CandidateHasLowerLoss);
    }

    [Fact]
    public void AConstantDifferentialHasNoSamplingVariationToTest()
    {
        // Uniformly better by a fixed amount, so the differential has zero variance. The
        // long-run variance is zero and no significance can be claimed from it.
        var candidate = new[] { 0.1, 0.2, 0.3, 0.4 };
        var baseline = candidate.Select(l => l + 0.05).ToArray();

        var result = DieboldMariano.Compare(candidate, baseline);

        Assert.Equal(-0.05, result.MeanDifferential, 12);
        Assert.True(result.CandidateHasLowerLoss);
        Assert.Equal(0.0, result.Statistic);
        Assert.Equal(1.0, result.PValue);
    }

    // ---------- the statistic ----------

    [Fact]
    public void WithNoLagTheVarianceIsTheOrdinarySampleVariance()
    {
        double[] candidate = [0.10, 0.30, 0.05, 0.40, 0.20, 0.35];
        double[] baseline = [0.20, 0.20, 0.20, 0.20, 0.20, 0.20];
        var differentials = candidate.Zip(baseline, (c, b) => c - b).ToArray();

        var result = DieboldMariano.Compare(candidate, baseline, hacLag: 0);

        var expectedVariance = LongRunVariance(differentials, 0);
        Assert.Equal(expectedVariance, result.LongRunVariance, 12);
        Assert.Equal(differentials.Average() / Math.Sqrt(expectedVariance / differentials.Length),
            result.Statistic, 12);
        Assert.Equal(0, result.HacLag);
    }

    [Fact]
    public void TheHacVarianceMatchesTheBartlettDefinition()
    {
        double[] candidate = [0.10, 0.12, 0.30, 0.32, 0.09, 0.11, 0.28, 0.31, 0.10, 0.13];
        double[] baseline = Enumerable.Repeat(0.20, 10).ToArray();
        var differentials = candidate.Zip(baseline, (c, b) => c - b).ToArray();

        var result = DieboldMariano.Compare(candidate, baseline, hacLag: 3);

        Assert.Equal(LongRunVariance(differentials, 3), result.LongRunVariance, 12);
        Assert.Equal(3, result.HacLag);
    }

    [Fact]
    public void PositiveAutocorrelationInflatesTheVarianceAndShrinksTheStatistic()
    {
        // A persistent differential: without the HAC correction its standard error is far too
        // small and the statistic correspondingly too large.
        var rng = new Random(7);
        var differentials = new double[200];
        var level = 0.0;
        for (int i = 0; i < differentials.Length; i++)
        {
            level = 0.9 * level + (rng.NextDouble() - 0.5) * 0.1;
            differentials[i] = -0.02 + level;
        }

        var baseline = Enumerable.Repeat(0.5, differentials.Length).ToArray();
        var candidate = differentials.Select((d, i) => baseline[i] + d).ToArray();

        var uncorrected = DieboldMariano.Compare(candidate, baseline, hacLag: 0);
        var corrected = DieboldMariano.Compare(candidate, baseline, hacLag: 5);

        Assert.True(corrected.LongRunVariance > uncorrected.LongRunVariance);
        Assert.True(Math.Abs(corrected.Statistic) < Math.Abs(uncorrected.Statistic));
        Assert.True(corrected.PValue > uncorrected.PValue);
    }

    [Fact]
    public void TheSignFollowsWhichForecastLosesLess()
    {
        double[] better = [0.10, 0.11, 0.09, 0.12, 0.10];
        double[] worse = [0.30, 0.31, 0.29, 0.32, 0.30];

        var candidateWins = DieboldMariano.Compare(better, worse, hacLag: 1);
        var candidateLoses = DieboldMariano.Compare(worse, better, hacLag: 1);

        Assert.True(candidateWins.MeanDifferential < 0.0);
        Assert.True(candidateWins.CandidateHasLowerLoss);
        Assert.False(candidateLoses.CandidateHasLowerLoss);

        // The test is two-sided, so swapping the arguments flips the sign but not the p-value.
        Assert.Equal(-candidateWins.Statistic, candidateLoses.Statistic, 12);
        Assert.Equal(candidateWins.PValue, candidateLoses.PValue, 12);
    }

    [Fact]
    public void TheLagIsClampedToTheSample()
    {
        var result = DieboldMariano.Compare([0.1, 0.3, 0.2], [0.2, 0.2, 0.2], hacLag: 50);

        // A truncation lag longer than the series would run the Bartlett sum off the end.
        Assert.Equal(2, result.HacLag);
        Assert.Equal(3, result.Observations);
    }

    // ---------- p-values ----------

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.959963985, 0.05)]
    [InlineData(-1.959963985, 0.05)]
    [InlineData(2.575829304, 0.01)]
    [InlineData(1.0, 0.3173105078629141)]
    public void TheTwoSidedNormalPValueIsAccurate(double statistic, double expected) =>
        Assert.Equal(expected, DieboldMariano.TwoSidedNormalPValue(statistic), 6);

    [Fact]
    public void ASmallVariationIsStillTestedRatherThanTreatedAsDegenerate()
    {
        // The degeneracy threshold must reject floating-point residue, not real signal.
        // These differentials vary by 1e-9 — tiny, but eleven orders of magnitude above the
        // residue the guard exists to catch.
        var candidate = Enumerable.Range(0, 50).Select(i => 0.2 + (i % 2) * 1e-9).ToArray();
        var baseline = Enumerable.Repeat(0.2, 50).ToArray();

        var result = DieboldMariano.Compare(candidate, baseline, hacLag: 2);

        Assert.NotEqual(0.0, result.Statistic);
        Assert.True(result.LongRunVariance > 0.0);
    }

    [Fact]
    public void ThePValueIsSymmetricAndBounded()
    {
        foreach (var z in new[] { 0.25, 1.0, 2.5, 6.0 })
        {
            Assert.Equal(DieboldMariano.TwoSidedNormalPValue(z), DieboldMariano.TwoSidedNormalPValue(-z), 15);
            Assert.InRange(DieboldMariano.TwoSidedNormalPValue(z), 0.0, 1.0);
        }

        // A far-tail statistic must not underflow to a negative probability.
        Assert.InRange(DieboldMariano.TwoSidedNormalPValue(40.0), 0.0, 1e-12);
    }

    [Fact]
    public void TheResultRendersItsHeadlineNumbers()
    {
        var text = DieboldMariano.Compare([0.1, 0.3, 0.2, 0.25], [0.2, 0.2, 0.2, 0.2], hacLag: 1).ToString();

        Assert.Contains("DM=", text, StringComparison.Ordinal);
        Assert.Contains("p=", text, StringComparison.Ordinal);
        Assert.Contains("n=4", text, StringComparison.Ordinal);
        Assert.Contains("hacLag=1", text, StringComparison.Ordinal);
    }
}
