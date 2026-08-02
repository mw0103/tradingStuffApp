namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// The pre-registration's rung-4 hyperparameters, frozen. There is no search, no grid, and no
/// setter: rung 4 is being run outside the registered ladder (rung 3 failed H1), and a
/// hyperparameter search on top of that would be the exact false-discovery move the ladder rule
/// exists to prevent.
/// </summary>
/// <remarks>
/// The registration fixes three of these — "depth &lt;= 3, &lt;= 200 trees, min-child &gt;= 50". The
/// other two are implementation constants it does not name, and they are frozen here rather than
/// chosen per run so that they cannot become a search surface by the back door. They are labelled as
/// such in <see cref="Describe"/> so a reader can see which numbers came from the registration and
/// which did not.
/// </remarks>
public static class GradientBoostedTreeHyperparameters
{
    /// <summary>Registered: maximum split depth. Three levels of splits, so at most eight leaves.</summary>
    public const int MaxDepth = 3;

    /// <summary>Registered: maximum number of boosting rounds.</summary>
    public const int MaxTrees = 200;

    /// <summary>Registered: a split is rejected unless both children keep at least this many rows.</summary>
    public const int MinChildSamples = 50;

    /// <summary>
    /// NOT from the registration — an implementation constant, frozen. At 200 rounds a shrinkage of
    /// 0.05 is the conventional pairing; it was not selected by trying alternatives.
    /// </summary>
    public const double LearningRate = 0.05;

    /// <summary>
    /// NOT from the registration — an implementation constant, frozen. No row or column subsampling,
    /// which also makes the fit fully deterministic and removes the need for a seed.
    /// </summary>
    public const double Subsample = 1.0;

    public static IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["maxDepth"] = $"{MaxDepth} (registered)",
        ["maxTrees"] = $"{MaxTrees} (registered)",
        ["minChildSamples"] = $"{MinChildSamples} (registered)",
        ["learningRate"] = $"{LearningRate} (frozen implementation constant, not registered)",
        ["subsample"] = $"{Subsample} (frozen implementation constant, not registered; fit is deterministic)",
        ["target"] = "realized variance, level scale (no log transform, therefore no retransformation)",
        ["loss"] = "squared error on the level-scale target",
        ["tuning"] = "none — no grid, no search, no early stopping on validation loss",
    };
}

/// <summary>A fitted gradient-boosted regression ensemble on the level-scale variance target.</summary>
public sealed class GradientBoostedTreesModel
{
    public required double BaseValue { get; init; }
    public required IReadOnlyList<RegressionTree> Trees { get; init; }
    public required double LearningRate { get; init; }

    /// <summary>
    /// The strictly-positive floor a forecast is raised to. Squared-error boosting on a level-scale
    /// target is unconstrained in sign, and normalized QLIKE is undefined at a non-positive forecast,
    /// so SOME floor is unavoidable. It is the training window's smallest realized variance: a
    /// forecast below anything the model was ever shown is outside its evidence. Frozen, and the
    /// number of days it actually binds on is reported, because the registration lists "forecast
    /// floor / clipping rule, if any" among the things that must be frozen rather than chosen —
    /// QLIKE punishes under-prediction hardest, so a clipping rule moves the score.
    /// </summary>
    public required double PositivityFloor { get; init; }

    /// <summary>The raw ensemble output, before the positivity floor.</summary>
    public double PredictRaw(double[] features)
    {
        var prediction = BaseValue;
        foreach (var tree in Trees) prediction += LearningRate * tree.Predict(features);
        return prediction;
    }

    /// <summary>
    /// The forecast. <b>No retransformation is applied, and that is not an omission.</b> The
    /// registration's rule is that every model estimated on a TRANSFORMED target must produce an
    /// original-scale conditional-mean forecast using a retransformation from its own training
    /// window; it also states explicitly that a model already forecasting level-scale variance
    /// directly (it names HEAVY-RM) is not retransformed. This model is fit on level-scale variance
    /// under squared error, so its output already targets the conditional mean. Multiplying it by a
    /// QLIKE-optimal smearing factor would be applying a correction for a log transform that was
    /// never taken.
    /// </summary>
    public double Predict(double[] features) => Math.Max(PredictRaw(features), PositivityFloor);

    public bool FloorBinds(double[] features) => PredictRaw(features) < PositivityFloor;
}

/// <summary>One CART regression tree: binary splits on a single feature at a threshold.</summary>
public sealed class RegressionTree
{
    private readonly Node _root;

    private RegressionTree(Node root) => _root = root;

    public double Predict(double[] features)
    {
        var node = _root;
        while (node.Left is not null)
        {
            node = features[node.FeatureIndex] <= node.Threshold ? node.Left : node.Right!;
        }

        return node.Value;
    }

    private sealed class Node
    {
        public int FeatureIndex;
        public double Threshold;
        public double Value;
        public Node? Left;
        public Node? Right;
    }

    /// <summary>
    /// Fits by exhaustive best-split search: every feature, every distinct value in the node, no
    /// histogram binning. Binning is the usual speed optimization and it changes which split is
    /// chosen; at this data size (a few thousand rows, twelve features, 200 shallow trees) the exact
    /// search runs in seconds, so the approximation buys nothing and would be one more unregistered
    /// choice.
    /// </summary>
    public static RegressionTree Fit(
        IReadOnlyList<double[]> design, IReadOnlyList<double> targets, int maxDepth, int minChildSamples)
    {
        var indices = Enumerable.Range(0, design.Count).ToArray();
        return new RegressionTree(Build(design, targets, indices, maxDepth, minChildSamples));
    }

    private static Node Build(
        IReadOnlyList<double[]> design, IReadOnlyList<double> targets, int[] indices, int depthRemaining, int minChildSamples)
    {
        var node = new Node { Value = Mean(targets, indices) };

        if (depthRemaining <= 0 || indices.Length < 2 * minChildSamples) return node;

        var featureCount = design[0].Length;
        var bestGain = 0.0;
        var bestFeature = -1;
        var bestThreshold = 0.0;

        var total = 0.0;
        foreach (var i in indices) total += targets[i];
        var totalSquared = total * total / indices.Length;

        for (var feature = 0; feature < featureCount; feature++)
        {
            var order = indices.OrderBy(i => design[i][feature]).ToArray();

            var leftSum = 0.0;
            for (var k = 0; k < order.Length - 1; k++)
            {
                leftSum += targets[order[k]];
                var leftCount = k + 1;
                var rightCount = order.Length - leftCount;

                if (leftCount < minChildSamples || rightCount < minChildSamples) continue;

                // A split between two identical feature values is not a split.
                var here = design[order[k]][feature];
                var next = design[order[k + 1]][feature];
                if (next <= here) continue;

                var rightSum = total - leftSum;

                // Reduction in sum of squared error, which for a mean-valued leaf is
                // sum_left^2/n_left + sum_right^2/n_right - sum_total^2/n_total.
                var gain = leftSum * leftSum / leftCount + rightSum * rightSum / rightCount - totalSquared;

                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestFeature = feature;
                    bestThreshold = 0.5 * (here + next);
                }
            }
        }

        if (bestFeature < 0) return node;

        var left = indices.Where(i => design[i][bestFeature] <= bestThreshold).ToArray();
        var right = indices.Where(i => design[i][bestFeature] > bestThreshold).ToArray();

        // Defensive: a midpoint threshold cannot in principle separate the split differently from
        // the scan that chose it, but a degenerate partition here would silently produce an infinite
        // recursion or a NaN leaf, so it falls back to a leaf rather than trusting the invariant.
        if (left.Length < minChildSamples || right.Length < minChildSamples) return node;

        node.FeatureIndex = bestFeature;
        node.Threshold = bestThreshold;
        node.Left = Build(design, targets, left, depthRemaining - 1, minChildSamples);
        node.Right = Build(design, targets, right, depthRemaining - 1, minChildSamples);

        return node;
    }

    private static double Mean(IReadOnlyList<double> targets, int[] indices)
    {
        var sum = 0.0;
        foreach (var i in indices) sum += targets[i];
        return sum / indices.Length;
    }
}

/// <summary>
/// Least-squares gradient boosting on the level-scale realized-variance target, with the
/// registration's rung-4 hyperparameters frozen.
/// </summary>
public static class GradientBoostedTrees
{
    public static GradientBoostedTreesModel Fit(IReadOnlyList<double[]> design, IReadOnlyList<double> targets)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(targets);
        if (design.Count != targets.Count)
            throw new ArgumentException("design and targets must have the same number of rows.", nameof(targets));
        if (design.Count == 0)
            throw new ArgumentException("Cannot fit on an empty training block.", nameof(design));

        var baseValue = targets.Average();
        var predictions = new double[targets.Count];
        Array.Fill(predictions, baseValue);

        var trees = new List<RegressionTree>(GradientBoostedTreeHyperparameters.MaxTrees);

        for (var round = 0; round < GradientBoostedTreeHyperparameters.MaxTrees; round++)
        {
            var residuals = new double[targets.Count];
            for (var i = 0; i < targets.Count; i++) residuals[i] = targets[i] - predictions[i];

            var tree = RegressionTree.Fit(
                design, residuals,
                GradientBoostedTreeHyperparameters.MaxDepth,
                GradientBoostedTreeHyperparameters.MinChildSamples);

            trees.Add(tree);

            for (var i = 0; i < targets.Count; i++)
                predictions[i] += GradientBoostedTreeHyperparameters.LearningRate * tree.Predict(design[i]);
        }

        return new GradientBoostedTreesModel
        {
            BaseValue = baseValue,
            Trees = trees,
            LearningRate = GradientBoostedTreeHyperparameters.LearningRate,
            PositivityFloor = targets.Where(t => t > 0.0).DefaultIfEmpty(double.Epsilon).Min(),
        };
    }
}
