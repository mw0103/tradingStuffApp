using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>The candidate rung's elastic net on the residual target.</summary>
public class ElasticNetTests
{
    [Fact]
    public void FitAtZeroLambdaApproximatesOrdinaryLeastSquares()
    {
        var random = new Random(3);
        var design = new List<double[]>();
        var targets = new List<double>();

        for (var i = 0; i < 100; i++)
        {
            var x1 = random.NextDouble() * 2.0 - 1.0;
            var x2 = random.NextDouble() * 2.0 - 1.0;
            design.Add([x1, x2]);
            targets.Add(3.0 * x1 - 1.5 * x2); // no noise, no intercept
        }

        var model = ElasticNet.Fit(design, targets, alpha: 0.0, lambda: 1e-6);

        // Near-zero regularization should recover the true coefficients closely.
        Assert.Equal(3.0, model.Coefficients[0], precision: 2);
        Assert.Equal(-1.5, model.Coefficients[1], precision: 2);
        Assert.Equal(0.0, model.Intercept, precision: 2);
    }

    [Fact]
    public void LargeLambdaShrinksEveryCoefficientTowardZero()
    {
        var random = new Random(4);
        var design = new List<double[]>();
        var targets = new List<double>();

        for (var i = 0; i < 100; i++)
        {
            var x1 = random.NextDouble() * 2.0 - 1.0;
            var x2 = random.NextDouble() * 2.0 - 1.0;
            design.Add([x1, x2]);
            targets.Add(3.0 * x1 - 1.5 * x2);
        }

        var lightlyRegularized = ElasticNet.Fit(design, targets, alpha: 0.5, lambda: 1e-4);
        var heavilyRegularized = ElasticNet.Fit(design, targets, alpha: 0.5, lambda: 10.0);

        Assert.True(Math.Abs(heavilyRegularized.Coefficients[0]) < Math.Abs(lightlyRegularized.Coefficients[0]));
        Assert.True(Math.Abs(heavilyRegularized.Coefficients[1]) < Math.Abs(lightlyRegularized.Coefficients[1]));
    }

    [Fact]
    public void PureLassoCanDriveAnIrrelevantFeatureExactlyToZero()
    {
        var random = new Random(5);
        var design = new List<double[]>();
        var targets = new List<double>();

        for (var i = 0; i < 150; i++)
        {
            var x1 = random.NextDouble() * 2.0 - 1.0;
            var irrelevant = random.NextDouble() * 2.0 - 1.0;
            design.Add([x1, irrelevant]);
            targets.Add(4.0 * x1 + (random.NextDouble() - 0.5) * 0.001); // irrelevant has no true effect
        }

        // alpha=1 is pure lasso; a lambda well above zero but below the point that kills everything.
        var model = ElasticNet.Fit(design, targets, alpha: 1.0, lambda: 0.3);

        Assert.Equal(0.0, model.Coefficients[1], precision: 9);
        Assert.True(Math.Abs(model.Coefficients[0]) > 0.5); // the real signal survives
    }

    [Fact]
    public void CrossValidatedFitOnACleanLinearRelationshipPredictsBetterThanTheMean()
    {
        var random = new Random(6);
        var design = new List<double[]>();
        var targets = new List<double>();

        for (var i = 0; i < 60; i++)
        {
            var x1 = random.NextDouble() * 2.0 - 1.0;
            var x2 = random.NextDouble() * 2.0 - 1.0;
            var noise = (random.NextDouble() - 0.5) * 0.05;
            design.Add([x1, x2]);
            targets.Add(2.0 * x1 - 1.0 * x2 + noise);
        }

        var model = ElasticNet.FitWithCrossValidation(design, targets);

        var meanTarget = targets.Average();
        var meanSquaredErrorOfModel = design.Zip(targets, (x, y) => Math.Pow(y - model.Predict(x), 2)).Average();
        var meanSquaredErrorOfConstantMean = targets.Select(y => Math.Pow(y - meanTarget, 2)).Average();

        Assert.True(meanSquaredErrorOfModel < meanSquaredErrorOfConstantMean);
        Assert.True(model.Alpha is 0.0 or 0.5 or 1.0);
    }

    [Fact]
    public void ThrowsWithFewerRowsThanTwiceTheFoldCount() =>
        Assert.Throws<ArgumentException>(() => ElasticNet.FitWithCrossValidation(
            [[1.0], [2.0], [3.0]], [1.0, 2.0, 3.0], cvFolds: 5));
}
