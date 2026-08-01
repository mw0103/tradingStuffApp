using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>HAR-X's "fixed OLS spec with positivity constraint" building block.</summary>
public class NonNegativeLeastSquaresTests
{
    [Fact]
    public void MatchesUnconstrainedOlsWhenTheTrueSlopesAreAlreadyNonNegative()
    {
        var random = new Random(1);
        var design = new List<double[]>();
        var targets = new List<double>();

        for (var i = 0; i < 200; i++)
        {
            var x1 = random.NextDouble() * 2.0;
            var x2 = random.NextDouble() * 2.0;
            var noise = (random.NextDouble() - 0.5) * 0.01;
            design.Add([x1, x2]);
            targets.Add(1.0 + 2.0 * x1 + 0.5 * x2 + noise); // true slopes 2.0, 0.5 — both non-negative
        }

        var ols = OrdinaryLeastSquares.Fit(design, targets);
        var nnls = NonNegativeLeastSquares.Fit(design, targets);

        for (var i = 0; i < ols.Length; i++)
        {
            Assert.Equal(ols[i], nnls[i], precision: 2);
        }
    }

    [Fact]
    public void ClipsANegativeUnconstrainedSlopeToZeroRatherThanReturningIt()
    {
        // x2 is constructed to be negatively related to y once x1 is controlled for, so the
        // unconstrained OLS slope on x2 is negative. NNLS must not return a negative coefficient.
        var random = new Random(2);
        var design = new List<double[]>();
        var targets = new List<double>();

        for (var i = 0; i < 300; i++)
        {
            var x1 = random.NextDouble() * 3.0;
            var x2 = random.NextDouble() * 3.0;
            var noise = (random.NextDouble() - 0.5) * 0.02;
            design.Add([x1, x2]);
            targets.Add(5.0 + 1.0 * x1 - 2.0 * x2 + noise); // true slope on x2 is strongly negative
        }

        var unconstrained = OrdinaryLeastSquares.Fit(design, targets);
        Assert.True(unconstrained[2] < 0.0, "test setup should produce a negative unconstrained slope on x2");

        var constrained = NonNegativeLeastSquares.Fit(design, targets);
        Assert.True(constrained[1] >= 0.0);
        Assert.True(constrained[2] >= 0.0);
        Assert.Equal(0.0, constrained[2], precision: 9); // driven all the way to the boundary
    }

    [Fact]
    public void PredictMatchesTheOrdinaryLeastSquaresConvention()
    {
        double[] coefficients = [1.0, 2.0, 3.0]; // intercept, slope1, slope2
        double[] features = [4.0, 5.0];

        Assert.Equal(
            OrdinaryLeastSquares.Predict(coefficients, features),
            NonNegativeLeastSquares.Predict(coefficients, features));
    }

    [Fact]
    public void PredictThrowsOnAMismatchedFeatureVector() =>
        Assert.Throws<ArgumentException>(() => NonNegativeLeastSquares.Predict([1.0, 2.0], [1.0, 2.0]));
}
