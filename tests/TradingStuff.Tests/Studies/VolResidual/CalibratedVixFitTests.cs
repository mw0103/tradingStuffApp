using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>Baseline B1: <c>exp(a + b*logQ)</c>, fit by directly minimizing training QLIKE.</summary>
public class CalibratedVixFitTests
{
    [Fact]
    public void RecoversTheExactParametersOnNoiselessData()
    {
        const double trueA = -9.5;
        const double trueB = 1.3;

        var random = new Random(11);
        var logQ = Enumerable.Range(0, 100).Select(_ => Math.Log(0.0001 + random.NextDouble() * 0.001)).ToList();
        var actual = logQ.Select(w => Math.Exp(trueA + trueB * w)).ToList();

        var fitted = CalibratedVixFit.Fit(logQ, actual);

        Assert.Equal(trueA, fitted.A, precision: 4);
        Assert.Equal(trueB, fitted.B, precision: 4);
    }

    [Fact]
    public void DirectQlikeMinimizationBeatsOrMatchesTheNaiveOlsOnLogFit()
    {
        // Build noisy data, then compare training QLIKE under (a) Newton's direct-QLIKE fit and
        // (b) the naive OLS-on-log-then-exp forecast the registration explicitly forbids as the
        // FORECAST. The whole point of B1's fitting procedure is that it must not do worse than
        // that naive baseline on its own training data — if it did, "directly minimizing
        // training-window QLIKE" would be a name, not a property.
        var random = new Random(99);
        var logQ = new List<double>();
        var actual = new List<double>();

        for (var i = 0; i < 250; i++)
        {
            var w = Math.Log(0.0001 + random.NextDouble() * 0.002);
            var trueLogY = -9.0 + 1.1 * w;
            var y = Math.Exp(trueLogY + (random.NextDouble() - 0.5) * 0.3); // multiplicative noise
            logQ.Add(w);
            actual.Add(y);
        }

        var fitted = CalibratedVixFit.Fit(logQ, actual);
        var qlikeMinimizingLoss = MeanQlike(actual, logQ, w => fitted.PredictVariance(w));

        // Naive OLS-on-log: fit log(y) ~ a + b*w by ordinary least squares, then forecast = exp(...).
        var meanW = logQ.Average();
        var meanLogY = actual.Select(v => Math.Log(v)).Average();
        double covariance = 0.0, varianceW = 0.0;
        for (var i = 0; i < logQ.Count; i++)
        {
            var dw = logQ[i] - meanW;
            covariance += dw * (Math.Log(actual[i]) - meanLogY);
            varianceW += dw * dw;
        }
        var olsB = covariance / varianceW;
        var olsA = meanLogY - olsB * meanW;
        var naiveOlsLoss = MeanQlike(actual, logQ, w => Math.Exp(olsA + olsB * w));

        Assert.True(qlikeMinimizingLoss <= naiveOlsLoss + 1e-9);
    }

    private static double MeanQlike(IReadOnlyList<double> actual, IReadOnlyList<double> logQ, Func<double, double> forecast) =>
        actual.Zip(logQ, (y, w) => QlikeRetransformation.Loss(y, forecast(w))).Average();

    [Fact]
    public void ThrowsWithFewerThanTwoObservations() =>
        Assert.Throws<ArgumentException>(() => CalibratedVixFit.Fit([1.0], [0.001]));

    [Fact]
    public void ThrowsOnMismatchedLengths() =>
        Assert.Throws<ArgumentException>(() => CalibratedVixFit.Fit([1.0, 2.0], [0.001]));
}
