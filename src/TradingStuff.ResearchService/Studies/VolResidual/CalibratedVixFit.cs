namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Baseline B1 — calibrated VIX: <c>RVhat_t = exp(a + b*log(q_t))</c>, <c>q_t = (VIX_t/100)^2</c>,
/// with <c>(a, b)</c> fit by directly minimizing training-window QLIKE
/// (<c>docs/research/volatility-forecast-residual-study.md</c>, baseline B1).
/// </summary>
/// <remarks>
/// <para>
/// This is a training-CALIBRATED forecast, not a variance-premium-debiased one — the registration is
/// explicit that naming it otherwise would claim an economic decomposition this fit does not
/// perform. <c>(a, b)</c> absorb average premium bias, maturity mismatch, calendar-vs-trading time
/// and annualization scaling all at once.
/// </para>
/// <para>
/// Unlike HAR/HAR-X/the candidate, B1 needs no separate <see cref="QlikeRetransformation"/> step:
/// its two parameters are already fit directly against QLIKE on the original variance scale, so the
/// "retransformation" and the "model" are the same fit here. Layering the multiplicative factor on
/// top as well would double-count exactly what minimizing over <c>a</c> already achieves (the factor
/// would converge to 1 by construction, since <c>a</c> already absorbs any constant multiplicative
/// misfit — but computing it anyway on a model whose own fit already targets the same objective the
/// factor targets is not "needs no correction" in the B2 sense of a different scale entirely; it is
/// simply redundant here, and redundant is not what the registration's "not every model receives a
/// correction" carve-out was written for. B1 is included in the general rule; it just happens that
/// its correction is embedded in the fit itself rather than a distinct trailing step).
/// </para>
/// <para>
/// <b>Why Newton's method is exact here, not merely a heuristic.</b> Let <c>u_i = a + b*w_i</c>
/// (<c>w_i = log(q_i)</c>, linear in the parameters). Dropping the additive terms independent of
/// <c>(a,b)</c>: with <c>yhat_i = exp(u_i)</c>, <c>QLIKE_i = y_i*exp(-u_i) - log(y_i) + u_i - 1</c>
/// (expanding <c>log(y_i/yhat_i) = log(y_i) - u_i</c>), so dropping the additive constant
/// <c>-log(y_i) - 1</c> the training QLIKE objective is <c>f(a,b) = mean_i[ y_i*exp(-u_i) + u_i ]</c>
/// — note the <c>+u_i</c>, not <c>-u_i</c>; getting this sign wrong was an early defect in this
/// file, caught by <c>CalibratedVixFitTests.RecoversTheExactParametersOnNoiselessData</c>, which a
/// wrong-signed gradient fails outright (Newton climbs away from the optimum instead of descending
/// to it, and the noiseless recovery test catches that immediately — see that test's own remarks).
/// Each term is convex in <c>u_i</c> (its second derivative is <c>y_i*exp(-u_i) &gt; 0</c> since
/// <c>y_i &gt; 0</c>), and <c>u_i</c> is affine in <c>(a,b)</c>, so <c>f</c> is convex in
/// <c>(a,b)</c>: a sum of convex-of-affine functions. Newton's method on a smooth convex function
/// with a positive-definite Hessian converges to the unique global minimum; the Hessian here is a
/// weighted Gram matrix (weights <c>y_i*exp(-u_i) &gt; 0</c>) and is positive definite whenever the
/// <c>w_i</c> are not all identical, which VIX levels never are in practice.
/// </para>
/// </remarks>
public static class CalibratedVixFit
{
    public sealed record Parameters(double A, double B)
    {
        public double PredictVariance(double logQ) => Math.Exp(A + B * logQ);
    }

    /// <param name="logQ">Training <c>log((VIX_t/100)^2)</c> values.</param>
    /// <param name="actualVariance">Training realized variance, same order.</param>
    public static Parameters Fit(
        IReadOnlyList<double> logQ, IReadOnlyList<double> actualVariance,
        int maxIterations = 100, double gradientTolerance = 1e-10)
    {
        ArgumentNullException.ThrowIfNull(logQ);
        ArgumentNullException.ThrowIfNull(actualVariance);
        if (logQ.Count != actualVariance.Count)
            throw new ArgumentException("logQ and actualVariance must be the same length.");
        if (logQ.Count < 2)
            throw new ArgumentException("At least two observations are required to fit two parameters.");

        var n = logQ.Count;

        // Warm start: OLS of log(actualVariance) on logQ. Not the final answer (that would be
        // "OLS-on-log then exp()", exactly what the registration forbids as the FORECAST), but a
        // sound starting point for Newton to refine under the real objective, QLIKE.
        var meanW = logQ.Average();
        var meanLogY = actualVariance.Select(v => Math.Log(v)).Average();
        double covariance = 0.0, varianceW = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dw = logQ[i] - meanW;
            covariance += dw * (Math.Log(actualVariance[i]) - meanLogY);
            varianceW += dw * dw;
        }

        var b = varianceW > 1e-14 ? covariance / varianceW : 0.0;
        var a = meanLogY - b * meanW;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            double gradA = 0.0, gradB = 0.0;
            double hessAA = 0.0, hessAB = 0.0, hessBB = 0.0;

            for (var i = 0; i < n; i++)
            {
                var u = a + b * logQ[i];
                var weight = actualVariance[i] * Math.Exp(-u); // y_i * exp(-u_i), always > 0

                // f_i(u) = y_i*exp(-u) + u  =>  f_i'(u) = -y_i*exp(-u) + 1 = 1 - weight.
                gradA += 1.0 - weight;
                gradB += (1.0 - weight) * logQ[i];

                hessAA += weight;
                hessAB += weight * logQ[i];
                hessBB += weight * logQ[i] * logQ[i];
            }

            gradA /= n; gradB /= n;
            hessAA /= n; hessAB /= n; hessBB /= n;

            var gradientNorm = Math.Sqrt(gradA * gradA + gradB * gradB);
            if (gradientNorm < gradientTolerance) break;

            var determinant = hessAA * hessBB - hessAB * hessAB;
            if (Math.Abs(determinant) < 1e-14) break; // degenerate Hessian (near-constant logQ); stop at current estimate

            // Newton step: [da, db] = -H^-1 * grad, solved directly for the 2x2 system.
            var deltaA = -(hessBB * gradA - hessAB * gradB) / determinant;
            var deltaB = -(-hessAB * gradA + hessAA * gradB) / determinant;

            // Damped step: full Newton steps can overshoot badly from a poor warm start on a
            // sharply curved objective (exp(-u) blows up for very negative u); halving on the first
            // few iterations if the step would be huge keeps this inside the region Newton's
            // quadratic convergence actually applies to, without changing the fixed point.
            var stepScale = 1.0;
            while (Math.Abs(stepScale * deltaA) > 5.0 || Math.Abs(stepScale * deltaB) > 5.0)
            {
                stepScale *= 0.5;
                if (stepScale < 1e-6) break;
            }

            a += stepScale * deltaA;
            b += stepScale * deltaB;
        }

        return new Parameters(a, b);
    }
}
