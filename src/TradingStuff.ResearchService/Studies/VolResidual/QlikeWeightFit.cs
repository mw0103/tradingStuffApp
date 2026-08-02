namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Fits non-negative combination weights by minimizing training QLIKE directly.
/// </summary>
/// <remarks>
/// <para>
/// The reason this exists: the first combination candidates (B2–B4) fitted their weights by least
/// squares on the LEVEL variance while every model in this study is scored by QLIKE. That is a
/// loss mismatch, and not a subtle one — realized variance is severely right-skewed, so squared
/// error is dominated by a handful of extreme days, while QLIKE weights proportional errors evenly
/// across the range. Weights chosen to minimize one are not the weights that minimize the other,
/// and a combination fitted under squared error tells you nothing about whether combining helps
/// under QLIKE. It was a defective test of the idea, not a verdict on it.
/// </para>
/// <para>
/// QLIKE is <c>a/f - log(a/f) - 1</c>, which for a non-negative combination
/// <c>f_i = sum_k w_k * f_ki</c> is smooth in <c>w</c> with gradient
/// <c>d/dw_k = sum_i (1/f_i - a_i/f_i^2) * f_ki</c>. Minimized here by projected gradient descent
/// with backtracking line search from equal weights — deterministic, no RNG, no tuning constant
/// that changes the answer. Weights are NOT constrained to sum to one: QLIKE is scale-sensitive,
/// so letting the total float is what lets the fit calibrate the level as well as the mix.
/// </para>
/// </remarks>
public static class QlikeWeightFit
{
    /// <summary>Iteration cap. Reached only if the line search stops making progress.</summary>
    public const int MaxIterations = 500;

    /// <summary>Relative objective improvement below which the fit is converged.</summary>
    public const double Tolerance = 1e-12;

    /// <summary>
    /// Weights minimizing mean training QLIKE for the combination of <paramref name="memberForecasts"/>.
    /// </summary>
    /// <param name="memberForecasts">Per-row member forecasts; every row must have the same length.</param>
    /// <param name="actuals">Realized variance per row, strictly positive.</param>
    public static double[] Fit(IReadOnlyList<double[]> memberForecasts, IReadOnlyList<double> actuals)
    {
        ArgumentNullException.ThrowIfNull(memberForecasts);
        ArgumentNullException.ThrowIfNull(actuals);
        if (memberForecasts.Count != actuals.Count)
            throw new ArgumentException("Forecasts and actuals must cover the same rows.");
        if (memberForecasts.Count == 0)
            throw new ArgumentException("At least one row is required.", nameof(memberForecasts));

        var k = memberForecasts[0].Length;

        // Equal weights is the honest starting point: it is B1, the null hypothesis of combining,
        // so the optimizer can only improve on the simplest scheme, never start below it by luck.
        var weights = new double[k];
        Array.Fill(weights, 1.0 / k);

        var objective = MeanLoss(memberForecasts, actuals, weights);
        var step = 1.0;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var gradient = Gradient(memberForecasts, actuals, weights);

            // Backtracking: halve the step until it actually reduces the objective. A step that
            // cannot is convergence, not a reason to keep moving.
            var improved = false;
            for (var backtrack = 0; backtrack < 40; backtrack++)
            {
                var trial = new double[k];
                for (var j = 0; j < k; j++) trial[j] = Math.Max(weights[j] - step * gradient[j], 0.0);

                if (trial.All(w => w <= 0.0)) { step *= 0.5; continue; }

                var trialObjective = MeanLoss(memberForecasts, actuals, trial);
                if (trialObjective < objective)
                {
                    var relative = (objective - trialObjective) / Math.Max(Math.Abs(objective), 1e-300);
                    weights = trial;
                    objective = trialObjective;
                    improved = relative > Tolerance;
                    step *= 1.5;
                    break;
                }

                step *= 0.5;
            }

            if (!improved) break;
        }

        return weights;
    }

    /// <summary>The combined forecast, floored so a degenerate all-zero weight vector stays scoreable.</summary>
    public static double Predict(double[] weights, double[] memberForecasts)
    {
        var combined = 0.0;
        for (var j = 0; j < weights.Length; j++) combined += weights[j] * memberForecasts[j];
        return Math.Max(combined, 1e-12);
    }

    private static double MeanLoss(
        IReadOnlyList<double[]> memberForecasts, IReadOnlyList<double> actuals, double[] weights)
    {
        var total = 0.0;
        for (var i = 0; i < actuals.Count; i++)
        {
            total += QlikeRetransformation.Loss(actuals[i], Predict(weights, memberForecasts[i]));
        }

        return total / actuals.Count;
    }

    private static double[] Gradient(
        IReadOnlyList<double[]> memberForecasts, IReadOnlyList<double> actuals, double[] weights)
    {
        var k = weights.Length;
        var gradient = new double[k];

        for (var i = 0; i < actuals.Count; i++)
        {
            var f = Predict(weights, memberForecasts[i]);
            // d/df of (a/f - log(a/f) - 1) is (1/f - a/f^2).
            var dLossDf = 1.0 / f - actuals[i] / (f * f);
            for (var j = 0; j < k; j++) gradient[j] += dLossDf * memberForecasts[i][j];
        }

        for (var j = 0; j < k; j++) gradient[j] /= actuals.Count;
        return gradient;
    }
}
