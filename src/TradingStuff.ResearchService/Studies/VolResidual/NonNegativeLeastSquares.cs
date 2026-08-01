namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Ordinary least squares with an unconstrained intercept and non-negative slope coefficients, for
/// HAR-X's "fixed OLS spec with positivity constraint"
/// (<c>docs/research/volatility-forecast-residual-study.md</c>, baseline B3).
/// </summary>
/// <remarks>
/// <para>
/// Block coordinate descent on the (convex, quadratic) least-squares objective: cycle through the
/// intercept (closed-form, unconstrained) and each slope (closed-form unconstrained update,
/// projected onto <c>[0, infinity)</c>), holding every other coefficient fixed. Each coordinate step
/// is an exact minimization along that axis, so the objective is non-increasing every step and the
/// procedure converges to the KKT point of the constrained problem — the standard construction for
/// NNLS-with-free-intercept; see Lawson &amp; Hanson for the general (unfree-intercept) case this
/// specializes.
/// </para>
/// <para>
/// <see cref="TradingStuff.Volatility.Baselines.OrdinaryLeastSquares"/> is not reused here: it solves
/// the normal equations directly and has no mechanism to hold a subset of coefficients at a
/// boundary, which is exactly what a positivity constraint requires whenever the unconstrained
/// solution would go negative.
/// </para>
/// </remarks>
public static class NonNegativeLeastSquares
{
    /// <summary>
    /// Fits <c>y ~ intercept + sum(slope_j * x_j)</c> with every <c>slope_j &gt;= 0</c>.
    /// </summary>
    /// <returns>Coefficients, intercept at index 0, exactly as <see cref="TradingStuff.Volatility.Baselines.OrdinaryLeastSquares"/> orders them.</returns>
    public static double[] Fit(
        IReadOnlyList<double[]> design,
        IReadOnlyList<double> targets,
        int maxIterations = 500,
        double tolerance = 1e-10)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(targets);
        if (design.Count != targets.Count)
            throw new ArgumentException("design and targets must have the same number of rows.");
        if (design.Count == 0)
            throw new ArgumentException("Cannot fit a regression with no observations.");

        var n = design.Count;
        var p = design[0].Length;
        foreach (var row in design)
        {
            if (row.Length != p) throw new ArgumentException("All design rows must have the same width.");
        }

        // Precompute each column's sum of squares; used every coordinate step.
        var columnSumSquares = new double[p];
        for (var j = 0; j < p; j++)
        {
            double sumSquares = 0.0;
            for (var i = 0; i < n; i++) sumSquares += design[i][j] * design[i][j];
            columnSumSquares[j] = sumSquares;
        }

        var slopes = new double[p];
        var intercept = targets.Average();

        // Current fitted residual, maintained incrementally as coefficients change rather than
        // recomputed from scratch each coordinate step.
        var residual = new double[n];
        for (var i = 0; i < n; i++) residual[i] = targets[i] - intercept;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var maxChange = 0.0;

            // Intercept: unconstrained closed-form update (mean residual after removing the
            // intercept's own current contribution).
            double meanResidual = 0.0;
            for (var i = 0; i < n; i++) meanResidual += residual[i];
            meanResidual /= n;

            if (meanResidual != 0.0)
            {
                intercept += meanResidual;
                for (var i = 0; i < n; i++) residual[i] -= meanResidual;
                maxChange = Math.Max(maxChange, Math.Abs(meanResidual));
            }

            // Each slope, in turn.
            for (var j = 0; j < p; j++)
            {
                if (columnSumSquares[j] <= 1e-14) continue; // degenerate (constant) column; leave at 0

                double dot = 0.0;
                for (var i = 0; i < n; i++) dot += design[i][j] * residual[i];

                // Unconstrained optimum for this coordinate, given every other coefficient fixed:
                // slopes[j] + dot / columnSumSquares[j]. Projected onto [0, infinity).
                var candidate = slopes[j] + dot / columnSumSquares[j];
                var projected = Math.Max(0.0, candidate);
                var delta = projected - slopes[j];

                if (delta != 0.0)
                {
                    slopes[j] = projected;
                    for (var i = 0; i < n; i++) residual[i] -= delta * design[i][j];
                    maxChange = Math.Max(maxChange, Math.Abs(delta));
                }
            }

            if (maxChange < tolerance) break;
        }

        var coefficients = new double[p + 1];
        coefficients[0] = intercept;
        Array.Copy(slopes, 0, coefficients, 1, p);
        return coefficients;
    }

    public static double Predict(double[] coefficients, double[] features)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        ArgumentNullException.ThrowIfNull(features);
        if (coefficients.Length != features.Length + 1)
            throw new ArgumentException("Coefficient vector must be one longer than the feature vector.");

        var prediction = coefficients[0];
        for (var i = 0; i < features.Length; i++) prediction += coefficients[i + 1] * features[i];
        return prediction;
    }
}
