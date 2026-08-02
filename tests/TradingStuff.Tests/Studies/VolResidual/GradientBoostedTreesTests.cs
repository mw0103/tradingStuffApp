using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// The exploratory rung-4 gradient-boosted trees: that it learns, that its registered
/// hyperparameters are actually enforced rather than merely documented, and that it is deterministic.
/// </summary>
public class GradientBoostedTreesTests
{
    private static (List<double[]> Design, List<double> Targets) Synthetic(int n, int seed)
    {
        var rng = new Random(seed);
        var design = new List<double[]>(n);
        var targets = new List<double>(n);

        for (var i = 0; i < n; i++)
        {
            var x0 = rng.NextDouble();
            var x1 = rng.NextDouble();
            design.Add([x0, x1, rng.NextDouble()]);
            targets.Add(1.0 + 2.0 * x0 + (x1 > 0.5 ? 1.0 : 0.0) + (rng.NextDouble() - 0.5) * 0.05);
        }

        return (design, targets);
    }

    [Fact]
    public void TheEnsembleFitsBetterThanItsOwnBaseValue()
    {
        var (design, targets) = Synthetic(600, seed: 1);
        var model = GradientBoostedTrees.Fit(design, targets);

        var baseError = targets.Sum(t => (t - model.BaseValue) * (t - model.BaseValue));
        var modelError = design.Select((row, i) => targets[i] - model.PredictRaw(row)).Sum(e => e * e);

        Assert.True(modelError < 0.25 * baseError,
            $"boosted error {modelError} should be far below the constant-fit error {baseError}.");
    }

    [Fact]
    public void TheFitIsDeterministic()
    {
        var (design, targets) = Synthetic(400, seed: 2);

        var first = GradientBoostedTrees.Fit(design, targets);
        var second = GradientBoostedTrees.Fit(design, targets);

        foreach (var row in design)
            Assert.Equal(first.Predict(row), second.Predict(row));
    }

    [Fact]
    public void TheRegisteredHyperparametersAreEnforcedNotJustDocumented()
    {
        var (design, targets) = Synthetic(1000, seed: 3);
        var model = GradientBoostedTrees.Fit(design, targets);

        Assert.Equal(200, GradientBoostedTreeHyperparameters.MaxTrees);
        Assert.Equal(3, GradientBoostedTreeHyperparameters.MaxDepth);
        Assert.Equal(50, GradientBoostedTreeHyperparameters.MinChildSamples);
        Assert.Equal(GradientBoostedTreeHyperparameters.MaxTrees, model.Trees.Count);
    }

    [Fact]
    public void MinChildSamplesStopsSplittingASmallBlock()
    {
        // 80 rows: any split leaves one side below the registered 50-row minimum, so every tree must
        // be a single leaf and the whole ensemble must collapse to a constant.
        var (design, targets) = Synthetic(80, seed: 4);
        var model = GradientBoostedTrees.Fit(design, targets);

        var predictions = design.Select(model.PredictRaw).Distinct().ToList();
        Assert.Single(predictions);
        Assert.Equal(model.BaseValue, predictions[0], 12);
    }

    [Fact]
    public void DepthIsCappedAtThreeSplits()
    {
        // A target that is a pure function of four successive binary splits on distinct features.
        // A depth-3 tree can represent at most eight leaves and so cannot separate all sixteen
        // cells; the deepest split (feature 3) must therefore leave residual error.
        var rng = new Random(5);
        var design = new List<double[]>();
        var targets = new List<double>();
        for (var i = 0; i < 3200; i++)
        {
            double[] bits = [rng.Next(2), rng.Next(2), rng.Next(2), rng.Next(2)];
            design.Add(bits);
            targets.Add(8 * bits[0] + 4 * bits[1] + 2 * bits[2] + 1 * bits[3]);
        }

        var single = RegressionTree.Fit(design, targets, maxDepth: 3, minChildSamples: 50);

        var cells = design.Select(single.Predict).Distinct().Count();
        Assert.True(cells <= 8, $"a depth-3 tree produced {cells} distinct leaves; the cap is 8.");
    }

    [Fact]
    public void ThePositivityFloorIsTheTrainingMinimumAndIsReportedWhenItBinds()
    {
        // QLIKE is undefined at a non-positive forecast, so the floor is not optional — but it is a
        // clipping rule, and the registration requires clipping rules to be frozen and visible.
        var (design, targets) = Synthetic(400, seed: 6);
        var model = GradientBoostedTrees.Fit(design, targets);

        Assert.Equal(targets.Min(), model.PositivityFloor, 12);

        double[] extreme = [-1e6, -1e6, -1e6];
        Assert.True(model.Predict(extreme) >= model.PositivityFloor);
    }

    [Fact]
    public void NoRetransformationIsAppliedOnTopOfTheEnsemble()
    {
        // The rung predicts level-scale variance directly, so the registration's retransformation
        // rule does not apply to it — the same way it does not apply to HEAVY-RM. Applying a
        // QLIKE-optimal smearing factor by reflex would correct for a log transform never taken, and
        // would silently change the score. The forecast must therefore be the ensemble output
        // itself, with nothing between it and the floor.
        var (design, targets) = Synthetic(500, seed: 8);
        var model = GradientBoostedTrees.Fit(design, targets);

        foreach (var row in design)
        {
            var raw = model.BaseValue + model.Trees.Sum(t => model.LearningRate * t.Predict(row));
            Assert.Equal(raw, model.PredictRaw(row), 12);
            Assert.Equal(Math.Max(raw, model.PositivityFloor), model.Predict(row), 12);
        }

        // In particular the forecasts are NOT scaled by mean(actual / forecast) on the training
        // window, which is what a copied-across retransformation would do.
        var smearing = design.Select((row, i) => targets[i] / model.PredictRaw(row)).Average();
        Assert.NotEqual(1.0, smearing, 6);
        Assert.NotEqual(smearing * model.PredictRaw(design[0]), model.Predict(design[0]), 12);
    }

    [Fact]
    public void TheDescriptionSeparatesRegisteredFromFrozenImplementationConstants()
    {
        var described = GradientBoostedTreeHyperparameters.Describe();

        Assert.Contains("registered", described["maxDepth"]);
        Assert.Contains("registered", described["maxTrees"]);
        Assert.Contains("registered", described["minChildSamples"]);
        Assert.Contains("not registered", described["learningRate"]);
        Assert.Contains("no retransformation", described["target"]);
        Assert.Contains("none", described["tuning"]);
    }

    [Fact]
    public void MalformedInputIsRejected()
    {
        Assert.Equal("targets", Assert.Throws<ArgumentException>(
            () => GradientBoostedTrees.Fit([[1.0]], [1.0, 2.0])).ParamName);
        Assert.Equal("design", Assert.Throws<ArgumentException>(
            () => GradientBoostedTrees.Fit([], [])).ParamName);
    }
}
