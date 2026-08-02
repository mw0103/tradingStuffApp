using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the margin-adjusted Diebold-Mariano test — the study's PRIMARY H1 statistic.
/// </summary>
/// <remarks>
/// The failure mode these exist for is a tau that does nothing. An implementation that computes the
/// margin-adjusted mean but reuses the unadjusted standard error, or that applies tau to the
/// reported point estimate and not to the tested series, produces a p-value identical to the
/// conventional one and a gate that silently reverts to "beats the benchmark by any positive
/// amount" — the exact null the pre-registration says is not the gate. So the tests check the
/// long-run VARIANCE moves with tau, not only the mean.
/// </remarks>
public class MarginAdjustedDieboldMarianoTests
{
    [Fact]
    public void TheMarginAdjustmentMovesTheMeanTheVarianceAndThePValue()
    {
        // A persistent, mildly candidate-favouring differential, so all three quantities are
        // non-degenerate and a tau that did nothing would be visible.
        var rng = new Random(11);
        var gate = new double[400];
        var candidate = new double[400];
        var level = 0.0;
        for (var i = 0; i < gate.Length; i++)
        {
            level = 0.85 * level + (rng.NextDouble() - 0.5) * 0.05;
            gate[i] = 0.20 + level;
            candidate[i] = gate[i] - 0.004 - 0.1 * level;
        }

        var plain = DieboldMariano.CompareWithMargin(candidate, gate, tau: 0.0);
        var adjusted = DieboldMariano.CompareWithMargin(candidate, gate, tau: 0.02);

        // The margin subtracts 2% of the gate's loss from the candidate's advantage.
        Assert.Equal(plain.MeanLossAdvantage - 0.02 * gate.Average(), adjusted.MeanLossAdvantage, 12);

        // Scaling the gate leg rescales the differential's autocovariances too. If these were equal
        // the adjustment would only have moved the point estimate.
        Assert.NotEqual(plain.LongRunVariance, adjusted.LongRunVariance);

        // Harder gate, so the evidence against it must be weaker.
        Assert.True(adjusted.Statistic < plain.Statistic);
        Assert.True(adjusted.OneSidedPValue > plain.OneSidedPValue);
        Assert.Equal(0.02, adjusted.Tau);
    }

    [Fact]
    public void ThePValueIsOneSidedInTheCandidatesFavour()
    {
        double[] candidate = [0.10, 0.11, 0.09, 0.12, 0.10, 0.11, 0.10, 0.09];
        double[] gate = [0.30, 0.29, 0.32, 0.28, 0.31, 0.30, 0.29, 0.31];

        var winning = DieboldMariano.CompareWithMargin(candidate, gate, tau: 0.0, hacLag: 1);
        var losing = DieboldMariano.CompareWithMargin(gate, candidate, tau: 0.0, hacLag: 1);

        Assert.True(winning.Statistic > 0.0);
        Assert.True(winning.MeanLossAdvantage > 0.0);
        Assert.True(winning.OneSidedPValue < 0.05);

        // Swapping the arguments must not produce the same p-value: a two-sided test would.
        Assert.True(losing.OneSidedPValue > 0.95);
        Assert.Equal(1.0, winning.OneSidedPValue + losing.OneSidedPValue, 10);
    }

    [Fact]
    public void ADegenerateDifferentialIsReportedAsUntestedRatherThanAsAFiftyFiftyCoinFlip()
    {
        // Identical losses: no differential, nothing tested. The upper-tail probability of the
        // placeholder zero statistic is 0.5, which would read as a result. It must not be returned.
        var losses = new[] { 0.1, 0.2, 0.3, 0.4, 0.5 };

        var result = DieboldMariano.CompareWithMargin(losses, losses, tau: 0.0);

        Assert.True(result.Degenerate);
        Assert.Equal(1.0, result.OneSidedPValue);
        Assert.Equal(0.0, result.Statistic);
    }

    [Fact]
    public void TauIsRejectedOutsideTheUnitInterval()
    {
        double[] a = [0.1, 0.2];
        Assert.Equal("tau", Assert.Throws<ArgumentOutOfRangeException>(
            () => DieboldMariano.CompareWithMargin(a, a, tau: -0.01)).ParamName);
        Assert.Equal("tau", Assert.Throws<ArgumentOutOfRangeException>(
            () => DieboldMariano.CompareWithMargin(a, a, tau: 1.0)).ParamName);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.6448536269514722, 0.05)]
    [InlineData(-1.6448536269514722, 0.95)]
    [InlineData(2.3263478740408408, 0.01)]
    public void TheUpperTailNormalPValueIsAccurate(double statistic, double expected) =>
        Assert.Equal(expected, DieboldMariano.UpperTailNormalPValue(statistic), 6);
}
