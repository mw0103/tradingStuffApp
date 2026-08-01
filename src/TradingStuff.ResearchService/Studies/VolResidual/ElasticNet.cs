namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// A fitted elastic-net model: standardized-feature coordinate descent coefficients, carried back
/// onto the original feature scale so <see cref="Predict"/> takes raw features directly.
/// </summary>
public sealed class ElasticNetModel
{
    public required double Intercept { get; init; }
    public required double[] Coefficients { get; init; }
    public required double Alpha { get; init; }
    public required double Lambda { get; init; }

    public double Predict(double[] features)
    {
        var prediction = Intercept;
        for (var i = 0; i < features.Length; i++) prediction += Coefficients[i] * features[i];
        return prediction;
    }
}

/// <summary>
/// Elastic net via cyclic coordinate descent (Friedman, Hastie &amp; Tibshirani 2010), with the
/// pre-registered selection procedure: <c>alpha in {0, 0.5, 1}</c>, <c>lambda</c> chosen by inner
/// blocked 5-fold CV on the training block only
/// (<c>docs/research/volatility-forecast-residual-study.md</c>, candidate rung 3).
/// </summary>
/// <remarks>
/// <para>
/// "Blocked" means contiguous chronological folds, not shuffled ones: the training rows are already
/// date-ordered, and shuffling before cross-validating a time series lets a fold's neighbours (which
/// share almost all of their HAR window) leak into both the fit and its held-out score. Splitting the
/// ordered sequence into five contiguous blocks is the closest analogue to the outer walk-forward
/// design available inside a single training window.
/// </para>
/// <para>
/// Features are standardized (training-block mean zero, unit population variance) before fitting,
/// because coordinate descent's soft-thresholding step assumes columns on a comparable scale — the
/// regressors here mix log-variance levels, day-of-week dummies and z-scored VIX terms, which are
/// not remotely comparable raw. Standardization statistics come from the same training block being
/// fit, never from validation or test rows, and coefficients are rescaled back to the original
/// feature units before being returned so <see cref="ElasticNetModel.Predict"/> is a plain function
/// of raw features.
/// </para>
/// </remarks>
public static class ElasticNet
{
    public static readonly double[] RegisteredAlphaGrid = [0.0, 0.5, 1.0];

    /// <summary>
    /// Fits with the registered alpha grid and an inner blocked 5-fold CV lambda search, on the
    /// training block only.
    /// </summary>
    public static ElasticNetModel FitWithCrossValidation(
        IReadOnlyList<double[]> design,
        IReadOnlyList<double> targets,
        int cvFolds = 5,
        int lambdaGridSize = 30)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(targets);
        if (design.Count != targets.Count)
            throw new ArgumentException("design and targets must have the same number of rows.");
        if (design.Count < cvFolds * 2)
            throw new ArgumentException("Not enough training rows for a blocked cross-validation.", nameof(design));

        var lambdaMax = LambdaMax(design, targets);
        var lambdaGrid = LogSpace(lambdaMax * 1e-3, lambdaMax, lambdaGridSize);

        var bestAlpha = RegisteredAlphaGrid[0];
        var bestLambda = lambdaGrid[0];
        var bestCvError = double.PositiveInfinity;

        foreach (var alpha in RegisteredAlphaGrid)
        {
            foreach (var lambda in lambdaGrid)
            {
                var cvError = BlockedCrossValidationError(design, targets, alpha, lambda, cvFolds);
                if (cvError < bestCvError)
                {
                    bestCvError = cvError;
                    bestAlpha = alpha;
                    bestLambda = lambda;
                }
            }
        }

        return Fit(design, targets, bestAlpha, bestLambda);
    }

    /// <summary>Fits one (alpha, lambda) pair on the full supplied block.</summary>
    public static ElasticNetModel Fit(
        IReadOnlyList<double[]> design, IReadOnlyList<double> targets, double alpha, double lambda)
    {
        var n = design.Count;
        var p = design[0].Length;

        Standardize(design, out var means, out var scales, out var standardized);

        var targetMean = targets.Average();
        var beta = new double[p];

        var residual = new double[n];
        for (var i = 0; i < n; i++) residual[i] = targets[i] - targetMean;

        const int maxIterations = 1000;
        const double tolerance = 1e-8;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var maxChange = 0.0;

            for (var j = 0; j < p; j++)
            {
                // Partial residual for column j: add back j's own current contribution.
                if (beta[j] != 0.0)
                {
                    for (var i = 0; i < n; i++) residual[i] += beta[j] * standardized[i][j];
                }

                double z = 0.0;
                for (var i = 0; i < n; i++) z += standardized[i][j] * residual[i];
                z /= n;

                // Columns are standardized so (1/n)*sum(x_j^2) = 1, which is what makes the
                // soft-threshold denominator below just "1 + lambda*(1-alpha)" rather than needing
                // the column's own sum of squares.
                var updated = SoftThreshold(z, lambda * alpha) / (1.0 + lambda * (1.0 - alpha));

                // `residual` currently holds the PARTIAL residual (j's own contribution already
                // added back above, unconditionally true here: either it was added back because
                // beta[j] was nonzero, or beta[j] was already zero so the partial residual equals
                // the full residual to begin with). Converting back to the full residual under the
                // NEW coefficient therefore always subtracts `updated`, never the delta from the
                // old coefficient — subtracting the delta here was the bug: it left the OLD
                // coefficient's contribution doubled into every subsequent pass, and each pass
                // doubling compounds geometrically. (Confirmed by reproducing it: with the delta
                // form, beta grew by a roughly constant ~1.6x multiple every iteration and reached
                // 1e212 within 1000 iterations on a plain noiseless two-feature fit that should
                // have converged in single digits of iterations.)
                var change = Math.Abs(updated - beta[j]);
                if (updated != 0.0 || beta[j] != 0.0)
                {
                    for (var i = 0; i < n; i++) residual[i] -= updated * standardized[i][j];
                }
                maxChange = Math.Max(maxChange, change);

                beta[j] = updated;
            }

            if (maxChange < tolerance) break;
        }

        // Rescale coefficients from standardized space back to raw feature units:
        // standardized_j = (x_j - mean_j) / scale_j, so
        // y = targetMean + sum(beta_j * (x_j - mean_j)/scale_j)
        //   = [targetMean - sum(beta_j*mean_j/scale_j)] + sum((beta_j/scale_j) * x_j)
        var rawCoefficients = new double[p];
        var rawIntercept = targetMean;
        for (var j = 0; j < p; j++)
        {
            if (scales[j] <= 1e-14)
            {
                rawCoefficients[j] = 0.0;
                continue;
            }

            rawCoefficients[j] = beta[j] / scales[j];
            rawIntercept -= beta[j] * means[j] / scales[j];
        }

        return new ElasticNetModel
        {
            Intercept = rawIntercept,
            Coefficients = rawCoefficients,
            Alpha = alpha,
            Lambda = lambda,
        };
    }

    private static double BlockedCrossValidationError(
        IReadOnlyList<double[]> design, IReadOnlyList<double> targets, double alpha, double lambda, int folds)
    {
        var n = design.Count;
        var blockSize = n / folds;
        double totalSquaredError = 0.0;
        var totalCount = 0;

        for (var fold = 0; fold < folds; fold++)
        {
            var validationStart = fold * blockSize;
            var validationEnd = fold == folds - 1 ? n : validationStart + blockSize;

            var trainDesign = new List<double[]>(n - (validationEnd - validationStart));
            var trainTargets = new List<double>(trainDesign.Capacity);
            var validationDesign = new List<double[]>(validationEnd - validationStart);
            var validationTargets = new List<double>(validationDesign.Capacity);

            for (var i = 0; i < n; i++)
            {
                if (i >= validationStart && i < validationEnd)
                {
                    validationDesign.Add(design[i]);
                    validationTargets.Add(targets[i]);
                }
                else
                {
                    trainDesign.Add(design[i]);
                    trainTargets.Add(targets[i]);
                }
            }

            if (trainDesign.Count == 0 || validationDesign.Count == 0) continue;

            var model = Fit(trainDesign, trainTargets, alpha, lambda);

            for (var i = 0; i < validationDesign.Count; i++)
            {
                var error = validationTargets[i] - model.Predict(validationDesign[i]);
                totalSquaredError += error * error;
                totalCount++;
            }
        }

        return totalCount == 0 ? double.PositiveInfinity : totalSquaredError / totalCount;
    }

    private static void Standardize(
        IReadOnlyList<double[]> design, out double[] means, out double[] scales, out double[][] standardized)
    {
        var n = design.Count;
        var p = design[0].Length;

        means = new double[p];
        scales = new double[p];

        for (var j = 0; j < p; j++)
        {
            double sum = 0.0;
            for (var i = 0; i < n; i++) sum += design[i][j];
            means[j] = sum / n;
        }

        for (var j = 0; j < p; j++)
        {
            double sumSquares = 0.0;
            for (var i = 0; i < n; i++)
            {
                var centered = design[i][j] - means[j];
                sumSquares += centered * centered;
            }

            // Population variance (divide by n, not n-1): matches the coordinate-descent update's
            // assumption that (1/n)*sum(standardized^2) = 1 exactly.
            scales[j] = Math.Sqrt(sumSquares / n);
        }

        standardized = new double[n][];
        for (var i = 0; i < n; i++)
        {
            standardized[i] = new double[p];
            for (var j = 0; j < p; j++)
            {
                standardized[i][j] = scales[j] <= 1e-14 ? 0.0 : (design[i][j] - means[j]) / scales[j];
            }
        }
    }

    /// <summary>
    /// The smallest lambda that drives every coefficient to zero at alpha=1 (pure lasso); the
    /// registered grid runs from a small fraction of this down, since anything larger fits nothing.
    /// </summary>
    private static double LambdaMax(IReadOnlyList<double[]> design, IReadOnlyList<double> targets)
    {
        Standardize(design, out _, out _, out var standardized);
        var n = design.Count;
        var p = design[0].Length;
        var targetMean = targets.Average();

        var max = 0.0;
        for (var j = 0; j < p; j++)
        {
            double dot = 0.0;
            for (var i = 0; i < n; i++) dot += standardized[i][j] * (targets[i] - targetMean);
            var value = Math.Abs(dot) / n;
            if (value > max) max = value;
        }

        return max <= 0.0 ? 1e-3 : max;
    }

    private static double[] LogSpace(double from, double to, int count)
    {
        if (from <= 0.0) from = 1e-8;
        var logFrom = Math.Log(from);
        var logTo = Math.Log(to);
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0.0 : (double)i / (count - 1);
            values[i] = Math.Exp(logFrom + t * (logTo - logFrom));
        }

        return values;
    }

    private static double SoftThreshold(double z, double threshold)
    {
        if (z > threshold) return z - threshold;
        if (z < -threshold) return z + threshold;
        return 0.0;
    }
}
