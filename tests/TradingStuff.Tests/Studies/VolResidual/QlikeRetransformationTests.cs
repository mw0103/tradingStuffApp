using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Pins the one loss this study is allowed to compute, against a value worked out by hand, and the
/// closed-form training-window retransformation factor derivation.
/// </summary>
public class QlikeRetransformationTests
{
    [Fact]
    public void LossMatchesAHandComputedValue()
    {
        // y=2, yhat=1: ratio=2, loss = 2 - ln(2) - 1 = 1 - 0.6931471805599453 = 0.3068528194400547.
        var loss = QlikeRetransformation.Loss(2.0, 1.0);
        Assert.Equal(0.3068528194400547, loss, precision: 12);
    }

    [Fact]
    public void LossMatchesASecondHandComputedValue()
    {
        // y=1, yhat=4: ratio=0.25, loss = 0.25 - ln(0.25) - 1 = 0.25 + 1.3862943611198906 - 1 = 0.6362943611198906.
        var loss = QlikeRetransformation.Loss(1.0, 4.0);
        Assert.Equal(0.6362943611198906, loss, precision: 12);
    }

    [Fact]
    public void LossIsZeroWhenForecastEqualsActual()
    {
        Assert.Equal(0.0, QlikeRetransformation.Loss(3.5, 3.5), precision: 12);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(1.0, -1.0)]
    public void LossRejectsNonPositiveArguments(double actual, double forecast) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QlikeRetransformation.Loss(actual, forecast));

    [Fact]
    public void ImprovementPercentMatchesTheFrozenFormula()
    {
        // 100 * (1 - 0.9/1.0) = 10%.
        Assert.Equal(10.0, QlikeRetransformation.ImprovementPercent(0.9, 1.0), precision: 10);
    }

    [Fact]
    public void ImprovementPercentIsNegativeWhenTheCandidateIsWorse()
    {
        Assert.True(QlikeRetransformation.ImprovementPercent(1.1, 1.0) < 0.0);
    }

    [Fact]
    public void FitFactorRecoversAnExactKnownMultiplicativeMisspecification()
    {
        // Construct raw forecasts that are systematically off from actuals by a fixed factor k: the
        // QLIKE-optimal correction has the closed form mean(y_i/x_i), and when x_i = y_i/k for every
        // i that reduces to exactly k regardless of the y_i distribution.
        const double k = 1.7;
        var random = new Random(42);
        var actuals = new List<double>();
        var raw = new List<double>();

        for (var i = 0; i < 200; i++)
        {
            var y = 0.0001 + random.NextDouble() * 0.01; // representative daily variance magnitudes
            actuals.Add(y);
            raw.Add(y / k);
        }

        var factor = QlikeRetransformation.FitFactor(actuals, raw);
        Assert.Equal(k, factor, precision: 9);
    }

    [Fact]
    public void FitFactorOfOneReproducesTheRawForecastWhenAlreadyWellCalibrated()
    {
        var random = new Random(7);
        var actuals = Enumerable.Range(0, 50).Select(_ => 0.0005 + random.NextDouble() * 0.002).ToList();

        // Raw forecast equals the actual exactly: the correction should be 1.
        var factor = QlikeRetransformation.FitFactor(actuals, actuals);
        Assert.Equal(1.0, factor, precision: 9);
    }

    [Fact]
    public void FitFactorAppliedToRawForecastsStrictlyReducesOrMatchesTrainingQlikeVersusNoCorrection()
    {
        // The factor is defined as the training-QLIKE minimizer among constant multiplicative
        // corrections, so applying it can never do worse on the SAME training data than leaving the
        // raw (uncorrected, factor=1) forecast in place.
        var random = new Random(123);
        var actuals = new List<double>();
        var raw = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var y = 0.0001 + random.NextDouble() * 0.02;
            actuals.Add(y);
            // A raw forecast that is a noisy, biased version of the actual.
            raw.Add(Math.Max(1e-8, y * 0.6 + random.NextDouble() * 0.001));
        }

        var factor = QlikeRetransformation.FitFactor(actuals, raw);

        double UncorrectedMeanLoss() => actuals.Zip(raw, (y, x) => QlikeRetransformation.Loss(y, x)).Average();
        double CorrectedMeanLoss() => actuals.Zip(raw, (y, x) => QlikeRetransformation.Loss(y, factor * x)).Average();

        Assert.True(CorrectedMeanLoss() <= UncorrectedMeanLoss() + 1e-12);
    }

    [Fact]
    public void FitFactorThrowsOnMismatchedLengths() =>
        Assert.Throws<ArgumentException>(() => QlikeRetransformation.FitFactor([1.0, 2.0], [1.0]));

    [Fact]
    public void FitFactorThrowsOnEmptyInput() =>
        Assert.Throws<ArgumentException>(() => QlikeRetransformation.FitFactor([], []));
}
