using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility.Forecasting
{
    /// <summary>Outcome of a one-sided stationary block bootstrap on a mean loss advantage.</summary>
    public class StationaryBlockBootstrapResult
    {
        /// <summary>The observed sample mean of the supplied differential series.</summary>
        public double SampleMean { get; set; }

        /// <summary>
        /// Lower end of the one-sided <c>(1 - alpha)</c> confidence interval for the mean, by the
        /// percentile method: the alpha-quantile of the resampled means.
        /// </summary>
        public double LowerBound { get; set; }

        public double Alpha { get; set; }

        public int Resamples { get; set; }

        public double MeanBlockLength { get; set; }

        public ulong Seed { get; set; }

        public int Observations { get; set; }

        /// <summary>The H1 condition: the interval must not contain zero.</summary>
        public bool ExcludesZero
        {
            get { return LowerBound > 0.0; }
        }

        public override string ToString()
        {
            return string.Format(
                "mean={0:+0.000000;-0.000000}  lower{1:P0}={2:+0.000000;-0.000000}  excludesZero={3}  B={4}  L={5}  seed={6}",
                SampleMean, 1.0 - Alpha, LowerBound, ExcludesZero, Resamples, MeanBlockLength, Seed);
        }
    }

    /// <summary>
    /// Politis-Romano (1994) stationary bootstrap for the mean of a serially dependent series, and
    /// the one-sided lower confidence bound the study's H1 gate requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blocks of geometric length with mean <c>L</c> (so restart probability <c>p = 1/L</c>) are
    /// laid end to end, wrapping circularly, until the resample matches the original length. The
    /// randomised block length is the whole point of the stationary bootstrap over the moving-block
    /// bootstrap: a fixed block length makes the resampled series non-stationary, which biases the
    /// variance of exactly the statistic being bounded here.
    /// </para>
    /// <para>
    /// <b>The generator is deliberately not <c>System.Random</c>.</b> The registered requirement is
    /// that an identical rerun produces an identical interval, and .NET explicitly does not
    /// guarantee <c>Random</c>'s sequence across runtime versions — a framework upgrade would
    /// silently move a published confidence bound. SplitMix64 is fifteen lines, is fully specified
    /// by its constants, and will produce the same stream in ten years.
    /// </para>
    /// <para>
    /// <b>Percentile method, not basic or BCa.</b> The interval is the alpha-quantile of the
    /// resampled means directly. The basic (reflected) interval and BCa would both be defensible;
    /// the percentile method is what "block bootstrap CI on the mean loss advantage" is normally
    /// taken to mean in this literature, and picking among them after seeing which one clears zero
    /// is precisely the discretion the pre-registration exists to remove. Frozen here.
    /// </para>
    /// </remarks>
    public static class StationaryBlockBootstrap
    {
        /// <summary>The pre-registered mean block length for the daily label: 20 trading days.</summary>
        public const double RegisteredMeanBlockLength = 20.0;

        /// <summary>The pre-registered resample count.</summary>
        public const int RegisteredResamples = 10000;

        /// <summary>One-sided 95%: the interval runs from the 5th percentile upward.</summary>
        public const double RegisteredAlpha = 0.05;

        /// <summary>
        /// One-sided lower confidence bound on the mean of <paramref name="differentials"/>.
        /// </summary>
        /// <param name="differentials">
        /// The per-day loss advantage series, oriented so that positive favours the candidate.
        /// </param>
        /// <param name="seed">
        /// Fixed by the caller and recorded in the result. Reproducibility is a gate condition, not
        /// a nicety, so there is no unseeded overload.
        /// </param>
        public static StationaryBlockBootstrapResult LowerBound(
            IReadOnlyList<double> differentials,
            ulong seed,
            double meanBlockLength = RegisteredMeanBlockLength,
            int resamples = RegisteredResamples,
            double alpha = RegisteredAlpha)
        {
            if (differentials == null) throw new ArgumentNullException("differentials");
            if (differentials.Count < 2)
                throw new ArgumentException("At least two observations are required.", "differentials");
            if (meanBlockLength < 1.0)
                throw new ArgumentOutOfRangeException("meanBlockLength", meanBlockLength, "The mean block length must be at least one observation.");
            if (resamples < 1)
                throw new ArgumentOutOfRangeException("resamples", resamples, "At least one resample is required.");
            if (alpha <= 0.0 || alpha >= 1.0)
                throw new ArgumentOutOfRangeException("alpha", alpha, "alpha must lie strictly between zero and one.");

            var n = differentials.Count;

            double sampleMean = 0.0;
            for (int i = 0; i < n; i++) sampleMean += differentials[i];
            sampleMean /= n;

            var restartProbability = 1.0 / meanBlockLength;
            var state = seed;
            var means = new double[resamples];

            for (int b = 0; b < resamples; b++)
            {
                var index = (int)(NextDouble(ref state) * n);
                if (index >= n) index = n - 1; // NextDouble is in [0,1); guard the 1-ulp edge anyway.

                double sum = 0.0;
                for (int t = 0; t < n; t++)
                {
                    if (t > 0)
                    {
                        if (NextDouble(ref state) < restartProbability)
                        {
                            index = (int)(NextDouble(ref state) * n);
                            if (index >= n) index = n - 1;
                        }
                        else
                        {
                            // Circular wrap. Without it the tail of the series would be sampled
                            // less often than the head and the bound would inherit that bias.
                            index = index + 1 == n ? 0 : index + 1;
                        }
                    }

                    sum += differentials[index];
                }

                means[b] = sum / n;
            }

            Array.Sort(means);

            return new StationaryBlockBootstrapResult
            {
                SampleMean = sampleMean,
                LowerBound = Quantile(means, alpha),
                Alpha = alpha,
                Resamples = resamples,
                MeanBlockLength = meanBlockLength,
                Seed = seed,
                Observations = n,
            };
        }

        /// <summary>
        /// Linearly interpolated quantile of an already-sorted sample, at position
        /// <c>q*(B-1)</c> — the "type 7" definition, which is what R's <c>quantile</c> and NumPy's
        /// <c>percentile</c> both default to. Pinned so a bound is comparable with one computed
        /// outside this codebase.
        /// </summary>
        private static double Quantile(double[] sorted, double q)
        {
            if (sorted.Length == 1) return sorted[0];

            var position = q * (sorted.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = lower + 1;
            if (upper >= sorted.Length) return sorted[sorted.Length - 1];

            var weight = position - lower;
            return sorted[lower] + weight * (sorted[upper] - sorted[lower]);
        }

        /// <summary>
        /// SplitMix64 (Steele, Lea &amp; Flood 2014), advancing <paramref name="state"/> in place and
        /// returning a double in <c>[0, 1)</c> from the top 53 bits.
        /// </summary>
        private static double NextDouble(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;

            return (z >> 11) * (1.0 / 9007199254740992.0);
        }
    }
}
