using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// The family of realized measures computed from one session's intraday returns.
    /// All variance figures are in squared-log-return units for a single session; use
    /// <see cref="VolatilityScaling"/> to annualize.
    /// </summary>
    public class RealizedMoments
    {
        /// <summary>Realized variance: the sum of squared intraday returns.</summary>
        public double RealizedVariance { get; set; }

        /// <summary>
        /// Bipower variation (Barndorff-Nielsen &amp; Shephard). Consistent for the
        /// continuous part of quadratic variation and robust to jumps, so the gap
        /// between it and realized variance identifies jump activity.
        /// </summary>
        public double BipowerVariation { get; set; }

        /// <summary>Realized variance contributed by positive returns only.</summary>
        public double UpsideVariance { get; set; }

        /// <summary>
        /// Realized variance contributed by negative returns only. The asymmetry
        /// between this and <see cref="UpsideVariance"/> carries the leverage effect
        /// and forecasts future volatility better than total realized variance alone.
        /// </summary>
        public double DownsideVariance { get; set; }

        /// <summary>
        /// Realized quarticity, the integrated fourth moment. Needed for the standard
        /// error of realized variance and for HAR-Q style attenuation corrections.
        /// </summary>
        public double RealizedQuarticity { get; set; }

        /// <summary>Number of intraday returns the measures were computed from.</summary>
        public int ReturnCount { get; set; }

        /// <summary>
        /// The jump contribution, floored at zero. Bipower variation can exceed realized
        /// variance in finite samples purely from sampling noise, and a negative jump
        /// component is not economically meaningful.
        /// </summary>
        public double JumpVariation
        {
            get { return Math.Max(RealizedVariance - BipowerVariation, 0.0); }
        }

        /// <summary>The continuous (diffusive) part of quadratic variation.</summary>
        public double ContinuousVariance
        {
            get { return Math.Min(RealizedVariance, BipowerVariation); }
        }

        /// <summary>Signed volatility asymmetry, positive when downside dominates.</summary>
        public double SignedVarianceAsymmetry
        {
            get { return DownsideVariance - UpsideVariance; }
        }
    }

    /// <summary>
    /// Computes realized measures from a series of intraday log returns.
    /// </summary>
    public static class RealizedVolatilityEstimator
    {
        /// <summary>E|Z| for a standard normal, the scaling constant in bipower variation.</summary>
        private static readonly double Mu1 = Math.Sqrt(2.0 / Math.PI);

        public static RealizedMoments FromReturns(IReadOnlyList<double> returns)
        {
            if (returns == null) throw new ArgumentNullException("returns");

            var moments = new RealizedMoments { ReturnCount = returns.Count };
            if (returns.Count == 0) return moments;

            double sumSquares = 0.0;
            double sumUp = 0.0;
            double sumDown = 0.0;
            double sumFourth = 0.0;

            for (int i = 0; i < returns.Count; i++)
            {
                var r = returns[i];
                var sq = r * r;
                sumSquares += sq;
                sumFourth += sq * sq;
                if (r > 0.0) sumUp += sq;
                else if (r < 0.0) sumDown += sq;
            }

            moments.RealizedVariance = sumSquares;
            moments.UpsideVariance = sumUp;
            moments.DownsideVariance = sumDown;

            var n = returns.Count;
            moments.RealizedQuarticity = (n / 3.0) * sumFourth;
            moments.BipowerVariation = ComputeBipowerVariation(returns);

            return moments;
        }

        /// <summary>
        /// BV = mu1^-2 * (n / (n-1)) * sum |r_j| * |r_{j-1}|, with the finite-sample
        /// correction that compensates for the n-1 available adjacent products.
        /// </summary>
        private static double ComputeBipowerVariation(IReadOnlyList<double> returns)
        {
            var n = returns.Count;
            if (n < 2) return 0.0;

            double sumAdjacent = 0.0;
            for (int i = 1; i < n; i++)
            {
                sumAdjacent += Math.Abs(returns[i]) * Math.Abs(returns[i - 1]);
            }

            var scale = 1.0 / (Mu1 * Mu1);
            var finiteSampleCorrection = (double)n / (n - 1);
            return scale * finiteSampleCorrection * sumAdjacent;
        }

        /// <summary>
        /// Averages realized measures computed on several offsets of the same sampling
        /// grid. Subsampling uses the one-minute observations discarded by a single
        /// five-minute grid, cutting estimator variance without reintroducing the
        /// microstructure bias that sampling at one minute would.
        /// </summary>
        public static RealizedMoments Average(IReadOnlyList<RealizedMoments> grids)
        {
            if (grids == null) throw new ArgumentNullException("grids");
            if (grids.Count == 0) return new RealizedMoments();

            var averaged = new RealizedMoments();
            double totalReturns = 0.0;

            for (int i = 0; i < grids.Count; i++)
            {
                averaged.RealizedVariance += grids[i].RealizedVariance;
                averaged.BipowerVariation += grids[i].BipowerVariation;
                averaged.UpsideVariance += grids[i].UpsideVariance;
                averaged.DownsideVariance += grids[i].DownsideVariance;
                averaged.RealizedQuarticity += grids[i].RealizedQuarticity;
                totalReturns += grids[i].ReturnCount;
            }

            var count = grids.Count;
            averaged.RealizedVariance /= count;
            averaged.BipowerVariation /= count;
            averaged.UpsideVariance /= count;
            averaged.DownsideVariance /= count;
            averaged.RealizedQuarticity /= count;
            averaged.ReturnCount = (int)Math.Round(totalReturns / count);

            return averaged;
        }
    }
}
