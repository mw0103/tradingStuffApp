using TradingStuff.Volatility;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the OLS solver and the HAR baseline's fit, prediction and evaluation.
/// </summary>
/// <remarks>
/// The retransformation correction and the QLIKE loss get particular attention. Both are
/// silent when wrong: exponentiating a log forecast without the variance correction returns
/// the median rather than the mean, which understates variance and therefore widens an
/// apparent variance risk premium that is really a units error.
/// </remarks>
public class HarModelTests
{
    // ---------- OLS: validation ----------

    [Fact]
    public void FitRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Fit(null!, new[] { 1.0 }));
        Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Fit(new[] { new[] { 1.0 } }, null!));
    }

    [Fact]
    public void FitRejectsMismatchedRowCounts() =>
        Assert.Throws<ArgumentException>(() =>
            OrdinaryLeastSquares.Fit([[1.0], [2.0]], [1.0]));

    [Fact]
    public void FitRejectsAnEmptyDesign() =>
        Assert.Throws<ArgumentException>(() => OrdinaryLeastSquares.Fit([], []));

    [Fact]
    public void FitRejectsAnUnderdeterminedSystem() =>
        // Two observations, two predictors plus an intercept: fewer rows than parameters.
        Assert.Throws<ArgumentException>(() =>
            OrdinaryLeastSquares.Fit([[1.0, 2.0], [3.0, 4.0]], [1.0, 2.0]));

    [Fact]
    public void FitRejectsRaggedDesignRows() =>
        Assert.Throws<ArgumentException>(() =>
            OrdinaryLeastSquares.Fit([[1.0], [2.0, 3.0], [4.0]], [1.0, 2.0, 3.0]));

    [Fact]
    public void FitRejectsAConstantColumn()
    {
        // A column with no variation is collinear with the intercept. The ridge term is
        // deliberately too small to mask it, so this must surface rather than return noise.
        var design = Enumerable.Range(0, 20).Select(_ => new[] { 1.0 }).ToList();
        var targets = Enumerable.Range(0, 20).Select(i => (double)i).ToList();

        Assert.Throws<InvalidOperationException>(() => OrdinaryLeastSquares.Fit(design, targets, ridge: 0.0));
    }

    // ---------- OLS: recovery ----------

    [Fact]
    public void FitRecoversAnExactLinearRelationship()
    {
        var design = new List<double[]>();
        var targets = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            double x1 = i, x2 = (i * 7) % 13;
            design.Add([x1, x2]);
            targets.Add(2.5 + 1.5 * x1 - 0.75 * x2);
        }

        var beta = OrdinaryLeastSquares.Fit(design, targets);

        Assert.Equal(2.5, beta[0], 6);
        Assert.Equal(1.5, beta[1], 6);
        Assert.Equal(-0.75, beta[2], 6);
    }

    [Fact]
    public void FitPrependsTheIntercept()
    {
        var design = Enumerable.Range(0, 10).Select(i => new[] { (double)i }).ToList();
        var targets = design.Select(r => 3.0).ToList();

        var beta = OrdinaryLeastSquares.Fit(design, targets);

        Assert.Equal(2, beta.Length);
        Assert.Equal(3.0, beta[0], 6);
        Assert.Equal(0.0, beta[1], 6);
    }

    [Fact]
    public void PartialPivotingHandlesAZeroLeadingPivot()
    {
        // Rows arranged so the first pivot candidate is ~0 and a swap is required.
        List<double[]> design = [[0.0, 1.0], [0.0, 2.0], [1.0, 0.0], [2.0, 0.0], [3.0, 5.0], [4.0, 1.0]];
        var targets = design.Select(r => 1.0 + 2.0 * r[0] + 3.0 * r[1]).ToList();

        var beta = OrdinaryLeastSquares.Fit(design, targets);

        Assert.Equal(1.0, beta[0], 6);
        Assert.Equal(2.0, beta[1], 6);
        Assert.Equal(3.0, beta[2], 6);
    }

    [Fact]
    public void TheRidgeTermKeepsACollinearSolveFinite()
    {
        // x2 is an exact multiple of x1: singular without regularization.
        var design = Enumerable.Range(1, 30).Select(i => new[] { (double)i, 2.0 * i }).ToList();
        var targets = design.Select(r => 1.0 + r[0]).ToList();

        Assert.Throws<InvalidOperationException>(() => OrdinaryLeastSquares.Fit(design, targets, ridge: 0.0));

        var beta = OrdinaryLeastSquares.Fit(design, targets, ridge: 1e-8);
        Assert.All(beta, b => Assert.True(double.IsFinite(b)));
    }

    // ---------- OLS: predict ----------

    [Fact]
    public void PredictAppliesTheInterceptAndSlopes() =>
        Assert.Equal(1.0 + 2.0 * 3.0 + 4.0 * 5.0, OrdinaryLeastSquares.Predict([1.0, 2.0, 4.0], [3.0, 5.0]), 12);

    [Fact]
    public void PredictWithNoFeaturesReturnsTheIntercept() =>
        Assert.Equal(7.0, OrdinaryLeastSquares.Predict([7.0], []), 12);

    [Fact]
    public void PredictRejectsMismatchedShapes()
    {
        Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Predict(null!, [1.0]));
        Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Predict([1.0, 2.0], null!));
        Assert.Throws<ArgumentException>(() => OrdinaryLeastSquares.Predict([1.0, 2.0], [1.0, 2.0]));
        Assert.Throws<ArgumentException>(() => OrdinaryLeastSquares.Predict([1.0], [1.0]));
    }

    // ---------- HAR: lifecycle ----------

    private static List<HarSample> LinearSamples(int count = 60, double noise = 0.0)
    {
        var rng = new Random(17);
        var samples = new List<HarSample>(count);
        for (int i = 0; i < count; i++)
        {
            double d = -9.0 + (i % 11) * 0.1, w = -9.0 + (i % 7) * 0.1, m = -9.0 + (i % 5) * 0.1;
            var target = -1.0 + 0.5 * d + 0.3 * w + 0.2 * m + (noise > 0 ? (rng.NextDouble() - 0.5) * noise : 0.0);
            samples.Add(new HarSample
            {
                Date = new DateTime(2024, 1, 1).AddDays(i),
                Features = [d, w, m],
                Target = target,
                ForwardVariance = Math.Exp(target),
                RandomWalkForecast = Math.Exp(-9.0),
            });
        }
        return samples;
    }

    [Fact]
    public void AnUnfittedModelRefusesToPredict()
    {
        var model = new HarRvModel();

        Assert.False(model.IsFitted);
        Assert.Throws<InvalidOperationException>(() => model.PredictLogVariance([1.0, 2.0, 3.0]));
        Assert.Throws<InvalidOperationException>(() => model.PredictVariance([1.0, 2.0, 3.0]));
        Assert.Throws<InvalidOperationException>(() => model.PredictAnnualizedVolatility([1.0, 2.0, 3.0]));
        Assert.Throws<InvalidOperationException>(() => model.Evaluate(LinearSamples()));
    }

    [Fact]
    public void FitRejectsMissingOrEmptySamples()
    {
        var model = new HarRvModel();

        Assert.Throws<ArgumentNullException>(() => model.Fit(null!));
        Assert.Throws<ArgumentException>(() => model.Fit([]));
    }

    [Fact]
    public void FitRecoversKnownCoefficientsAndRecordsFeatureNames()
    {
        var model = new HarRvModel();
        var names = new HarDatasetOptions().FeatureNames();

        model.Fit(LinearSamples(), names);

        Assert.True(model.IsFitted);
        Assert.Same(names, model.FeatureNames);
        Assert.Equal(4, model.Coefficients.Count);
        Assert.Equal(-1.0, model.Coefficients[0], 5);
        Assert.Equal(0.5, model.Coefficients[1], 5);
        Assert.Equal(0.3, model.Coefficients[2], 5);
        Assert.Equal(0.2, model.Coefficients[3], 5);
    }

    [Fact]
    public void AnExactFitHasZeroResidualVariance()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples());

        Assert.Equal(0.0, model.ResidualVariance, 10);
    }

    [Fact]
    public void ResidualVarianceGrowsWithNoise()
    {
        var clean = new HarRvModel();
        clean.Fit(LinearSamples());

        var noisy = new HarRvModel();
        noisy.Fit(LinearSamples(noise: 0.5));

        Assert.True(noisy.ResidualVariance > clean.ResidualVariance);
    }

    [Fact]
    public void ResidualVarianceUsesDegreesOfFreedomNotSampleCount()
    {
        // With n == parameters the denominator would be zero; it floors at 1 instead of
        // returning infinity.
        var samples = LinearSamples(4, noise: 0.4);
        var model = new HarRvModel();

        model.Fit(samples);

        Assert.True(double.IsFinite(model.ResidualVariance));
        Assert.True(model.ResidualVariance >= 0.0);
    }

    [Fact]
    public void FeatureNamesDefaultToNullWhenNotSupplied()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples());

        Assert.Null(model.FeatureNames);
    }

    // ---------- HAR: prediction ----------

    [Fact]
    public void PredictLogVarianceAppliesTheFittedCoefficients()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples());

        Assert.Equal(-1.0 + 0.5 * -9.0 + 0.3 * -8.5 + 0.2 * -8.0, model.PredictLogVariance([-9.0, -8.5, -8.0]), 5);
    }

    [Fact]
    public void PredictVarianceAppliesTheRetransformationCorrection()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples(noise: 0.6));

        var features = new[] { -9.0, -8.5, -8.0 };
        var log = model.PredictLogVariance(features);

        // exp(mu + sigma^2/2), the mean of the lognormal, not exp(mu), its median.
        Assert.Equal(Math.Exp(log + 0.5 * model.ResidualVariance), model.PredictVariance(features), 15);

        // With real residual variance the correction must actually raise the level.
        Assert.True(model.ResidualVariance > 0.0);
        Assert.True(model.PredictVariance(features) > Math.Exp(log));
    }

    [Fact]
    public void TheCorrectionVanishesOnAnExactFit()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples());

        var features = new[] { -9.0, -8.5, -8.0 };

        Assert.Equal(Math.Exp(model.PredictLogVariance(features)), model.PredictVariance(features), 10);
    }

    [Fact]
    public void AnnualizedVolatilityFollowsTheScalingConvention()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples(noise: 0.3));

        var features = new[] { -9.0, -8.5, -8.0 };

        Assert.Equal(
            VolatilityScaling.AnnualizeVolatility(model.PredictVariance(features)),
            model.PredictAnnualizedVolatility(features), 12);
    }

    // ---------- QLIKE ----------

    [Fact]
    public void QuasiLikelihoodIsZeroOnlyForAPerfectForecast()
    {
        Assert.Equal(0.0, HarRvModel.QuasiLikelihood(1e-4, 1e-4), 15);
        Assert.True(HarRvModel.QuasiLikelihood(1e-4, 2e-4) > 0.0);
        Assert.True(HarRvModel.QuasiLikelihood(1e-4, 5e-5) > 0.0);
    }

    [Fact]
    public void QuasiLikelihoodMatchesItsDefinition()
    {
        var ratio = 3e-4 / 1e-4;

        Assert.Equal(ratio - Math.Log(ratio) - 1.0, HarRvModel.QuasiLikelihood(3e-4, 1e-4), 15);
    }

    [Fact]
    public void QuasiLikelihoodPenalizesUnderForecastingMoreThanOver()
    {
        // Asymmetry is the point of QLIKE: halving the forecast hurts more than doubling it.
        var under = HarRvModel.QuasiLikelihood(1e-4, 5e-5);
        var over = HarRvModel.QuasiLikelihood(1e-4, 2e-4);

        Assert.True(under > over);
    }

    [Theory]
    [InlineData(1e-4, 0.0)]
    [InlineData(1e-4, -1e-4)]
    [InlineData(0.0, 1e-4)]
    [InlineData(-1e-4, 1e-4)]
    public void QuasiLikelihoodRejectsNonPositiveVariances(double actual, double forecast) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HarRvModel.QuasiLikelihood(actual, forecast));

    // ---------- evaluation ----------

    [Fact]
    public void EvaluateRejectsMissingOrEmptySamples()
    {
        var model = new HarRvModel();
        model.Fit(LinearSamples());

        Assert.Throws<ArgumentNullException>(() => model.Evaluate(null!));
        Assert.Throws<ArgumentException>(() => model.Evaluate([]));
    }

    [Fact]
    public void EvaluateReportsAPerfectFitAsZeroErrorAndUnitRSquared()
    {
        var samples = LinearSamples();
        var model = new HarRvModel();
        model.Fit(samples);

        var evaluation = model.Evaluate(samples);

        Assert.Equal(samples.Count, evaluation.Observations);
        Assert.Equal(0.0, evaluation.LogMeanSquaredError, 10);
        Assert.Equal(1.0, evaluation.RSquaredVersusMean, 10);
        Assert.Equal(1.0, evaluation.RSquaredVersusRandomWalk, 10);
        Assert.Equal(0.0, evaluation.QuasiLikelihoodLoss, 10);
    }

    [Fact]
    public void EvaluateAveragesLossesOverObservations()
    {
        var samples = LinearSamples(noise: 0.4);
        var model = new HarRvModel();
        model.Fit(samples);

        var evaluation = model.Evaluate(samples);

        // Recomputed independently of the implementation's accumulation order.
        var expectedMse = samples.Average(s => Math.Pow(s.Target - model.PredictLogVariance(s.Features), 2));
        var expectedQlike = samples.Average(s =>
            HarRvModel.QuasiLikelihood(s.ForwardVariance, model.PredictVariance(s.Features)));
        var expectedRwQlike = samples.Average(s =>
            HarRvModel.QuasiLikelihood(s.ForwardVariance, s.RandomWalkForecast));

        Assert.Equal(expectedMse, evaluation.LogMeanSquaredError, 12);
        Assert.Equal(expectedQlike, evaluation.QuasiLikelihoodLoss, 12);
        Assert.Equal(expectedRwQlike, evaluation.RandomWalkQuasiLikelihoodLoss, 12);
    }

    [Fact]
    public void BeatingTheRandomWalkRequiresBothRSquaredAndQlike()
    {
        // Both conditions are needed: a positive R2 with a worse QLIKE is not a win, because
        // squared error in logs and QLIKE in levels disagree about which errors matter.
        Assert.True(new HarEvaluation
        {
            RSquaredVersusRandomWalk = 0.1, QuasiLikelihoodLoss = 0.5, RandomWalkQuasiLikelihoodLoss = 0.6,
        }.BeatsRandomWalk);

        Assert.False(new HarEvaluation
        {
            RSquaredVersusRandomWalk = 0.1, QuasiLikelihoodLoss = 0.7, RandomWalkQuasiLikelihoodLoss = 0.6,
        }.BeatsRandomWalk);

        Assert.False(new HarEvaluation
        {
            RSquaredVersusRandomWalk = -0.1, QuasiLikelihoodLoss = 0.5, RandomWalkQuasiLikelihoodLoss = 0.6,
        }.BeatsRandomWalk);

        // Exactly zero is not a win either.
        Assert.False(new HarEvaluation
        {
            RSquaredVersusRandomWalk = 0.0, QuasiLikelihoodLoss = 0.5, RandomWalkQuasiLikelihoodLoss = 0.6,
        }.BeatsRandomWalk);

        Assert.False(new HarEvaluation
        {
            RSquaredVersusRandomWalk = 0.1, QuasiLikelihoodLoss = 0.6, RandomWalkQuasiLikelihoodLoss = 0.6,
        }.BeatsRandomWalk);
    }

    [Fact]
    public void EvaluationRendersItsHeadlineNumbers()
    {
        var text = new HarEvaluation
        {
            Observations = 42,
            LogMeanSquaredError = 0.125,
            RSquaredVersusMean = 0.5,
            RSquaredVersusRandomWalk = 0.25,
            QuasiLikelihoodLoss = 0.0625,
            RandomWalkQuasiLikelihoodLoss = 0.125,
        }.ToString();

        Assert.Contains("n=42", text, StringComparison.Ordinal);
        Assert.Contains("0.12500", text, StringComparison.Ordinal);
        Assert.Contains("beatsRW=True", text, StringComparison.Ordinal);
    }
}
