using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility.Forecasting
{
    /// <summary>Outcome of a Diebold-Mariano comparison of two forecasts' losses.</summary>
    public class DieboldMarianoResult
    {
        /// <summary>Mean loss differential, candidate minus baseline. Negative favours the candidate.</summary>
        public double MeanDifferential { get; set; }

        /// <summary>Newey-West long-run variance of the differential series.</summary>
        public double LongRunVariance { get; set; }

        /// <summary>The DM statistic, asymptotically standard normal under equal predictive accuracy.</summary>
        public double Statistic { get; set; }

        /// <summary>Two-sided p-value.</summary>
        public double PValue { get; set; }

        public int Observations { get; set; }

        /// <summary>Newey-West truncation lag actually used.</summary>
        public int HacLag { get; set; }

        /// <summary>
        /// True when the differential series carried no usable variation, so no test was performed
        /// and <see cref="Statistic"/> / <see cref="PValue"/> are placeholders rather than results.
        /// Callers deriving a one-sided p-value must not read 0.0 as "exactly on the boundary".
        /// </summary>
        public bool Degenerate { get; set; }

        /// <summary>
        /// True when the candidate's mean loss is lower. Says nothing about significance on
        /// its own - the sign and the p-value are separate questions and the gate needs both.
        /// </summary>
        public bool CandidateHasLowerLoss
        {
            get { return MeanDifferential < 0.0; }
        }

        public override string ToString()
        {
            return string.Format(
                "DM={0:F4}  p={1:F4}  meanDiff={2:+0.000000;-0.000000}  n={3}  hacLag={4}",
                Statistic, PValue, MeanDifferential, Observations, HacLag);
        }
    }

    /// <summary>
    /// Outcome of a margin-adjusted Diebold-Mariano comparison, oriented so that positive means the
    /// candidate is ahead. See <see cref="DieboldMariano.CompareWithMargin"/>.
    /// </summary>
    public class MarginAdjustedDieboldMarianoResult
    {
        /// <summary>The materiality margin used, as a fraction. 0.02 is the study's registered gate.</summary>
        public double Tau { get; set; }

        /// <summary>Mean of <c>(1 - tau)*L_gate - L_candidate</c>. Positive favours the candidate.</summary>
        public double MeanLossAdvantage { get; set; }

        /// <summary>DM statistic, positive when the candidate is ahead.</summary>
        public double Statistic { get; set; }

        /// <summary>
        /// One-sided p-value for the directional alternative "the candidate beats the gate by at
        /// least tau". This, not the two-sided value, is what the gate is written against.
        /// </summary>
        public double OneSidedPValue { get; set; }

        /// <summary>Reported for completeness; the gate never reads it.</summary>
        public double TwoSidedPValue { get; set; }

        public double LongRunVariance { get; set; }

        public int Observations { get; set; }

        public int HacLag { get; set; }

        /// <summary>No usable variation in the differential: nothing was tested. See <see cref="DieboldMarianoResult.Degenerate"/>.</summary>
        public bool Degenerate { get; set; }

        public override string ToString()
        {
            return string.Format(
                "tau={0:F4}  DM={1:F4}  p1={2:F4}  meanAdvantage={3:+0.000000;-0.000000}  n={4}  hacLag={5}",
                Tau, Statistic, OneSidedPValue, MeanLossAdvantage, Observations, HacLag);
        }
    }

    /// <summary>
    /// Diebold-Mariano test of equal predictive accuracy, with a Newey-West HAC variance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The HAC correction is not optional here. Daily loss differentials from overlapping or
    /// persistent volatility regimes are strongly autocorrelated, and treating them as
    /// independent understates the standard error - which inflates the statistic and turns
    /// ordinary persistence into apparent skill. The pre-registered configuration is lag 5 for
    /// the daily label.
    /// </para>
    /// <para>
    /// This is the raw DM statistic against the normal distribution. Harvey-Leybourne-Newbold
    /// small-sample correction is deliberately not applied: the registered folds run to
    /// hundreds of trading days, where the correction is negligible, and adding an unregistered
    /// adjustment to a gate statistic is the kind of change that has to be registered rather
    /// than slipped in.
    /// </para>
    /// </remarks>
    public static class DieboldMariano
    {
        /// <summary>
        /// Compares two loss series observed on the same days.
        /// </summary>
        /// <param name="candidateLosses">Per-day loss of the model under test.</param>
        /// <param name="baselineLosses">Per-day loss of the gate baseline.</param>
        /// <param name="hacLag">
        /// Newey-West truncation lag. Five for the daily label, nine for the five-day label.
        /// </param>
        public static DieboldMarianoResult Compare(
            IReadOnlyList<double> candidateLosses,
            IReadOnlyList<double> baselineLosses,
            int hacLag = 5)
        {
            if (candidateLosses == null) throw new ArgumentNullException("candidateLosses");
            if (baselineLosses == null) throw new ArgumentNullException("baselineLosses");
            if (candidateLosses.Count != baselineLosses.Count)
                throw new ArgumentException("Loss series must cover the same days.");
            if (candidateLosses.Count < 2)
                throw new ArgumentException("At least two observations are required.");
            if (hacLag < 0) throw new ArgumentOutOfRangeException("hacLag", "The truncation lag cannot be negative.");

            var n = candidateLosses.Count;
            var differentials = new double[n];
            for (int i = 0; i < n; i++) differentials[i] = candidateLosses[i] - baselineLosses[i];

            var mean = 0.0;
            for (int i = 0; i < n; i++) mean += differentials[i];
            mean /= n;

            // The lag cannot exceed the sample, or the Bartlett sum runs off the end.
            var lag = Math.Min(hacLag, n - 1);
            var longRunVariance = NeweyWestVariance(differentials, mean, lag);

            var result = new DieboldMarianoResult
            {
                MeanDifferential = mean,
                LongRunVariance = longRunVariance,
                Observations = n,
                HacLag = lag
            };

            if (longRunVariance <= DegenerateVariance(differentials))
            {
                // Identical forecasts, or a differential with no variation at all - one model
                // uniformly better by a fixed amount. There is nothing to test, and reporting
                // a statistic would assert something about a comparison never made.
                //
                // The threshold is scaled rather than zero. A constant series does not sum and
                // divide back to itself exactly, so each deviation carries a one-ulp residue
                // and the variance lands fractionally above zero; a bare `<= 0.0` guard passes
                // and the statistic reaches ~1e16.
                result.Statistic = 0.0;
                result.PValue = 1.0;
                result.Degenerate = true;
                return result;
            }

            result.Statistic = mean / Math.Sqrt(longRunVariance / n);
            result.PValue = TwoSidedNormalPValue(result.Statistic);
            return result;
        }

        /// <summary>
        /// Variance below which a differential series is treated as having no variation,
        /// scaled to the magnitude of the data rather than tested against zero.
        /// </summary>
        private static double DegenerateVariance(double[] differentials)
        {
            var scale = 0.0;
            for (int i = 0; i < differentials.Length; i++)
            {
                var magnitude = Math.Abs(differentials[i]);
                if (magnitude > scale) scale = magnitude;
            }

            return 1e-20 * Math.Max(1.0, scale * scale);
        }

        /// <summary>
        /// Newey-West long-run variance with a Bartlett kernel: gamma0 + 2*sum w_j*gamma_j.
        /// </summary>
        private static double NeweyWestVariance(double[] values, double mean, int lag)
        {
            var n = values.Length;

            double gamma0 = 0.0;
            for (int i = 0; i < n; i++)
            {
                var deviation = values[i] - mean;
                gamma0 += deviation * deviation;
            }
            gamma0 /= n;

            var variance = gamma0;

            for (int j = 1; j <= lag; j++)
            {
                double gamma = 0.0;
                for (int i = j; i < n; i++)
                {
                    gamma += (values[i] - mean) * (values[i - j] - mean);
                }
                gamma /= n;

                // Bartlett weights decline linearly to zero, which is what keeps the estimate
                // positive semi-definite; a flat weighting can produce a negative variance.
                var weight = 1.0 - (double)j / (lag + 1);
                variance += 2.0 * weight * gamma;
            }

            return variance;
        }

        /// <summary>
        /// Margin-adjusted Diebold-Mariano, in the orientation the study's gate is written in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pre-registration ("SPA, specified precisely") is explicit that the ordinary
        /// differential is the wrong null for this study. <c>d_t = L_gate,t - L_cand,t</c> has null
        /// <c>E[d_t] &lt;= 0</c>, so rejecting it establishes only that the candidate beats the gate
        /// by SOME positive amount. The registered gate is materiality: beat it by tau. Hence
        /// <c>d_t(tau) = (1 - tau)*L_gate,t - L_cand,t</c>, whose alternative is
        /// <c>E[L_cand] &lt; (1 - tau)*E[L_gate]</c> — the gate as written. tau = 0 may be reported
        /// alongside, labelled "evidence of some superiority", and may never stand in for the
        /// materiality claim.
        /// </para>
        /// <para>
        /// This delegates to <see cref="Compare"/> rather than re-deriving a HAC variance: the
        /// margin adjustment is a linear rescale of one of the two loss series, so the Bartlett sum,
        /// the lag clamp and the degeneracy guard are all the same machinery applied to a different
        /// series. Note that the adjustment moves the long-run variance too, not just the mean —
        /// scaling the gate leg changes the differential's autocovariances. A tau that only shifted
        /// the point estimate and left the standard error alone would be an implementation bug.
        /// </para>
        /// <para>
        /// The sign convention here is deliberately the opposite of <see cref="Compare"/>'s:
        /// <see cref="MarginAdjustedDieboldMarianoResult.Statistic"/> is POSITIVE when the candidate
        /// is ahead, because the gate, its one-sided p-value and every number reported beside it are
        /// written that way. The flip happens once, here, rather than at each of the call sites that
        /// would otherwise have to remember it.
        /// </para>
        /// </remarks>
        /// <param name="candidateLosses">Per-day loss of the model under test.</param>
        /// <param name="gateLosses">Per-day loss of the gate baseline.</param>
        /// <param name="tau">
        /// The registered materiality margin, as a fraction (0.02 for the study's 2% gate). Zero
        /// gives the conventional test.
        /// </param>
        /// <param name="hacLag">Newey-West truncation lag; five for the daily label.</param>
        public static MarginAdjustedDieboldMarianoResult CompareWithMargin(
            IReadOnlyList<double> candidateLosses,
            IReadOnlyList<double> gateLosses,
            double tau,
            int hacLag = 5)
        {
            if (gateLosses == null) throw new ArgumentNullException("gateLosses");
            if (tau < 0.0 || tau >= 1.0)
                throw new ArgumentOutOfRangeException("tau", tau, "The materiality margin must lie in [0, 1).");

            var scaledGate = new double[gateLosses.Count];
            for (int i = 0; i < gateLosses.Count; i++) scaledGate[i] = (1.0 - tau) * gateLosses[i];

            var inner = Compare(candidateLosses, scaledGate, hacLag);

            return new MarginAdjustedDieboldMarianoResult
            {
                Tau = tau,
                // Compare works in candidate-minus-baseline; the study speaks in advantage.
                MeanLossAdvantage = -inner.MeanDifferential,
                Statistic = -inner.Statistic,
                // A degenerate differential was never tested. Reporting the upper-tail probability of
                // a placeholder zero statistic would hand back 0.5 — a number that looks like a
                // result and is not one. One is the only honest answer: nothing was demonstrated.
                OneSidedPValue = inner.Degenerate ? 1.0 : UpperTailNormalPValue(-inner.Statistic),
                TwoSidedPValue = inner.PValue,
                LongRunVariance = inner.LongRunVariance,
                Observations = inner.Observations,
                HacLag = inner.HacLag,
                Degenerate = inner.Degenerate,
            };
        }

        /// <summary>Two-sided p-value from the standard normal distribution.</summary>
        public static double TwoSidedNormalPValue(double statistic)
        {
            return Erfc(Math.Abs(statistic) / Math.Sqrt(2.0));
        }

        /// <summary>
        /// Upper-tail p-value, <c>P(Z &gt;= statistic)</c>. This is the one the gate needs: H1 is a
        /// directional claim (the candidate is better), and a two-sided p-value tests a hypothesis
        /// nobody registered.
        /// </summary>
        public static double UpperTailNormalPValue(double statistic)
        {
            return 0.5 * Erfc(statistic / Math.Sqrt(2.0));
        }

        /// <summary>
        /// Complementary error function via the Numerical Recipes rational approximation.
        /// Fractional error below 1.2e-7, which is far finer than any gate expressed at two
        /// decimal places.
        /// </summary>
        private static double Erfc(double x)
        {
            var z = Math.Abs(x);
            var t = 2.0 / (2.0 + z);
            var y = 4.0 * t - 2.0;

            var coefficients = new[]
            {
                -1.3026537197817094, 6.4196979235649026e-1, 1.9476473204185836e-2,
                -9.561514786808631e-3, -9.46595344482036e-4, 3.66839497852761e-4,
                4.2523324806907e-5, -2.0278578112534e-5, -1.624290004647e-6,
                1.303655835580e-6, 1.5626441722e-8, -8.5238095915e-8,
                6.529054439e-9, 5.059343495e-9, -9.91364156e-10,
                -2.27365122e-10, 9.6467911e-11, 2.394038e-12,
                -6.886027e-12, 8.94487e-13, 3.13092e-13,
                -1.12708e-13, 3.81e-16, 7.106e-15
            };

            double d = 0.0, dd = 0.0;
            for (int j = coefficients.Length - 1; j > 0; j--)
            {
                var temp = d;
                d = y * d - dd + coefficients[j];
                dd = temp;
            }

            var answer = t * Math.Exp(-z * z + 0.5 * (coefficients[0] + y * d) - dd);
            return x >= 0.0 ? answer : 2.0 - answer;
        }
    }
}
