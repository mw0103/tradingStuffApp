namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// The one loss this study is allowed to compute, frozen exactly as
/// <c>docs/research/volatility-forecast-residual-study.md</c> states it, and the training-only,
/// per-model retransformation the same document requires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Normalized QLIKE.</b> <c>L_Q(y, yhat) = y/yhat - log(y/yhat) - 1</c>. No other loss may be
/// computed or returned by this study — see the "HARD CORRECTNESS CONSTRAINTS" the runner was built
/// against.
/// </para>
/// <para>
/// <b>Retransformation.</b> Every model here is estimated in log-variance space, so a raw forecast
/// is only ever "log variance" until something turns it back into a variance level. The
/// pre-registration bans the naive move — exponentiate the log-OLS fit and call it the forecast —
/// because that targets the conditional MEDIAN of a lognormal, not the conditional MEAN QLIKE is
/// minimized by, and bans the classical lognormal smearing correction
/// (<c>exp(logForecast + 0.5*residualVariance)</c>, what <see cref="TradingStuff.Volatility.Baselines.HarRvModel.PredictVariance"/>
/// does) for the same reason: that correction is optimal for a DIFFERENT loss (squared error under a
/// lognormal assumption), not for QLIKE.
/// </para>
/// <para>
/// Instead: fix the model's raw exponentiated forecast <c>x_i = exp(rawLogForecast_i)</c> on its own
/// training window, and choose a single multiplicative factor <c>c</c> so that <c>c * x_i</c>
/// minimizes mean training QLIKE. That objective is convex in <c>c</c> and has a closed form —
/// <see cref="FitFactor"/> derives it — so "directly minimizing training-window QLIKE" costs nothing
/// beyond a mean of ratios, refit fresh per model per fold, on that model's own training residuals,
/// never shared and never touching evaluation data. See <see cref="FitFactor"/>'s remarks for the
/// derivation.
/// </para>
/// </remarks>
public static class QlikeRetransformation
{
    /// <summary>
    /// Normalized QLIKE: <c>y/yhat - log(y/yhat) - 1</c>. Both arguments must be strictly positive —
    /// a realized variance of exactly zero or a non-positive forecast make the loss undefined, and
    /// the caller (never this method) decides whether that day is dropped or flagged.
    /// </summary>
    public static double Loss(double actualVariance, double forecastVariance)
    {
        if (actualVariance <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(actualVariance), actualVariance, "QLIKE requires a strictly positive actual variance.");
        if (forecastVariance <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(forecastVariance), forecastVariance, "QLIKE requires a strictly positive forecast variance.");

        var ratio = actualVariance / forecastVariance;
        return ratio - Math.Log(ratio) - 1.0;
    }

    /// <summary>
    /// The registered improvement figure: <c>100 * (1 - mean(candidate) / mean(gate))</c>. Positive
    /// means the candidate's pooled loss is lower than the gate's.
    /// </summary>
    public static double ImprovementPercent(double candidatePooledQlike, double gatePooledQlike)
    {
        if (gatePooledQlike <= 0.0) return 0.0;
        return 100.0 * (1.0 - candidatePooledQlike / gatePooledQlike);
    }

    /// <summary>
    /// The QLIKE-optimal multiplicative retransformation factor for a model's raw exponentiated
    /// log-space forecasts, fit on that model's own training window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Minimizing <c>(1/n) * sum_i [ y_i/(c*x_i) - log(y_i/(c*x_i)) - 1 ]</c> over <c>c &gt; 0</c>
    /// (the additive <c>-log(y_i/x_i) - 1</c> terms do not depend on <c>c</c> and drop out of the
    /// derivative):
    /// </para>
    /// <code>
    /// f(c)  = sum_i (y_i / x_i) / c + n * log(c) + const
    /// f'(c) = -sum_i (y_i / x_i) / c^2 + n / c
    /// f'(c) = 0  =>  n * c = sum_i (y_i / x_i)  =>  c* = mean_i(y_i / x_i)
    /// </code>
    /// <para>
    /// <c>f''(c) = 2*sum(y_i/x_i)/c^3 - n/c^2</c>, which at <c>c = c*</c> equals
    /// <c>n/c*^2 &gt; 0</c>: the stationary point is the unique minimum since <c>f</c> is convex in
    /// <c>c</c> everywhere it is defined (both terms are convex in <c>c</c> for <c>c &gt; 0</c>: the
    /// first is a positive multiple of <c>1/c</c>, the second is <c>log(c)</c>).
    /// </para>
    /// </remarks>
    /// <param name="trainingActuals">Realized variance for each training-window observation.</param>
    /// <param name="trainingRawForecasts">
    /// This model's own raw exponentiated log-forecast (<c>exp(rawLogForecast)</c>) for the SAME
    /// training observations, in the same order.
    /// </param>
    public static double FitFactor(
        IReadOnlyList<double> trainingActuals, IReadOnlyList<double> trainingRawForecasts)
    {
        ArgumentNullException.ThrowIfNull(trainingActuals);
        ArgumentNullException.ThrowIfNull(trainingRawForecasts);

        if (trainingActuals.Count == 0)
            throw new ArgumentException("Cannot fit a retransformation factor with no training observations.", nameof(trainingActuals));
        if (trainingActuals.Count != trainingRawForecasts.Count)
            throw new ArgumentException("Actuals and raw forecasts must be the same length.", nameof(trainingRawForecasts));

        double sumRatio = 0.0;
        var n = 0;

        for (var i = 0; i < trainingActuals.Count; i++)
        {
            var actual = trainingActuals[i];
            var raw = trainingRawForecasts[i];

            // Both must be strictly positive for the ratio to be a meaningful QLIKE argument; a
            // degenerate observation is skipped from the factor fit rather than poisoning it, the
            // same posture the estimator takes toward incomplete sessions elsewhere in this
            // codebase (see RealizedVolatilitySeriesBuilder.IsComplete).
            if (actual <= 0.0 || raw <= 0.0 || double.IsNaN(raw) || double.IsInfinity(raw)) continue;

            sumRatio += actual / raw;
            n++;
        }

        if (n == 0)
            throw new InvalidOperationException("No usable (positive, finite) training observations to fit a retransformation factor from.");

        return sumRatio / n;
    }
}
