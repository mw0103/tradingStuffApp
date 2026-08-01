using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.Volatility
{
    /// <summary>One date on which both series have a realized volatility observation.</summary>
    public class VolatilityDivergence
    {
        public DateTime Date { get; set; }
        public double SourceAnnualizedVolatility { get; set; }
        public double TargetAnnualizedVolatility { get; set; }

        /// <summary>Log ratio of target to source variance. Zero means the two agree exactly.</summary>
        public double LogVarianceRatio { get; set; }

        public double AbsoluteLogVarianceRatio
        {
            get { return Math.Abs(LogVarianceRatio); }
        }
    }

    /// <summary>
    /// Quantified comparison of two realized volatility series.
    ///
    /// Written for the SPY to SPX question specifically. The two track each other
    /// closely but not identically, and the differences are structural rather than
    /// random: SPY distributes dividends in quarterly lumps while the price index
    /// absorbs its constituents' dividends continuously, SPY carries ETF-level
    /// microstructure noise the index does not, and the index open is stitched from
    /// staggered constituent prints rather than being a tradeable simultaneous price.
    ///
    /// Rather than assume the transfer is one-to-one, this measures it: the level bias,
    /// the dispersion around it, and the specific dates where the relationship breaks.
    /// </summary>
    public class VolatilityComparisonResult
    {
        public int MatchedDays { get; set; }

        /// <summary>Correlation of log variance across the two series.</summary>
        public double LogVarianceCorrelation { get; set; }

        /// <summary>
        /// Mean log variance ratio. A positive value means the target series is
        /// systematically more volatile than the source; exponentiating gives the
        /// multiplicative bias a transferred forecast needs to correct for.
        /// </summary>
        public double MeanLogVarianceRatio { get; set; }

        /// <summary>
        /// Dispersion of the log ratio. This is the part a level correction cannot fix,
        /// and it is the honest measure of how far from one-to-one the transfer is.
        /// </summary>
        public double LogVarianceRatioStdDev { get; set; }

        /// <summary>Intercept of log target variance regressed on log source variance.</summary>
        public double CalibrationIntercept { get; set; }

        /// <summary>
        /// Slope of the same regression. A slope below one means the target's volatility
        /// is compressed relative to the source's, so a transferred forecast needs
        /// rescaling and not just a level shift.
        /// </summary>
        public double CalibrationSlope { get; set; }

        /// <summary>R squared of the calibration regression.</summary>
        public double CalibrationRSquared { get; set; }

        /// <summary>Dates with the widest disagreement, worst first.</summary>
        public List<VolatilityDivergence> LargestDivergences { get; set; }

        /// <summary>Applies the fitted calibration to map a source variance onto the target.</summary>
        public double TransferVariance(double sourceVariance)
        {
            if (sourceVariance <= 0.0) throw new ArgumentOutOfRangeException("sourceVariance");
            return Math.Exp(CalibrationIntercept + CalibrationSlope * Math.Log(sourceVariance));
        }

        public override string ToString()
        {
            return string.Format(
                "n={0}  corr(logRV)={1:F4}  meanLogRatio={2:F4} (x{3:F3})  sd={4:F4}  " +
                "calib: log(target)={5:F4}+{6:F4}*log(source), R2={7:P1}",
                MatchedDays, LogVarianceCorrelation, MeanLogVarianceRatio,
                Math.Exp(MeanLogVarianceRatio), LogVarianceRatioStdDev,
                CalibrationIntercept, CalibrationSlope, CalibrationRSquared);
        }
    }

    public static class VolatilityComparison
    {
        /// <summary>
        /// Compares two daily realized volatility series on their common dates.
        /// </summary>
        /// <param name="source">The series a model is trained on (SPY).</param>
        /// <param name="target">The series the model is meant to be applied to (SPX).</param>
        /// <param name="topDivergences">How many worst-disagreement dates to return.</param>
        public static VolatilityComparisonResult Compare(
            IReadOnlyList<RealizedVolatilityDay> source,
            IReadOnlyList<RealizedVolatilityDay> target,
            int topDivergences = 20)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (target == null) throw new ArgumentNullException("target");

            var targetByDate = new Dictionary<DateTime, RealizedVolatilityDay>();
            foreach (var day in target)
            {
                if (day.IsComplete && day.TotalVariance > 0.0) targetByDate[day.Date.Date] = day;
            }

            var divergences = new List<VolatilityDivergence>();
            var sourceLogs = new List<double>();
            var targetLogs = new List<double>();

            foreach (var sourceDay in source.OrderBy(d => d.Date))
            {
                if (!sourceDay.IsComplete || sourceDay.TotalVariance <= 0.0) continue;

                RealizedVolatilityDay targetDay;
                if (!targetByDate.TryGetValue(sourceDay.Date.Date, out targetDay)) continue;

                var sourceLog = Math.Log(sourceDay.TotalVariance);
                var targetLog = Math.Log(targetDay.TotalVariance);

                sourceLogs.Add(sourceLog);
                targetLogs.Add(targetLog);

                divergences.Add(new VolatilityDivergence
                {
                    Date = sourceDay.Date,
                    SourceAnnualizedVolatility = sourceDay.AnnualizedVolatility,
                    TargetAnnualizedVolatility = targetDay.AnnualizedVolatility,
                    LogVarianceRatio = targetLog - sourceLog
                });
            }

            if (divergences.Count < 3)
                throw new InvalidOperationException(
                    "Need at least three overlapping complete sessions to compare two volatility series.");

            var ratios = divergences.Select(d => d.LogVarianceRatio).ToList();
            var meanRatio = ratios.Average();

            var design = sourceLogs.Select(v => new[] { v }).ToList();
            var coefficients = OrdinaryLeastSquares.Fit(design, targetLogs);

            return new VolatilityComparisonResult
            {
                MatchedDays = divergences.Count,
                LogVarianceCorrelation = Correlation(sourceLogs, targetLogs),
                MeanLogVarianceRatio = meanRatio,
                LogVarianceRatioStdDev = StandardDeviation(ratios, meanRatio),
                CalibrationIntercept = coefficients[0],
                CalibrationSlope = coefficients[1],
                CalibrationRSquared = RSquared(design, targetLogs, coefficients),
                LargestDivergences = divergences
                    .OrderByDescending(d => d.AbsoluteLogVarianceRatio)
                    .Take(topDivergences)
                    .ToList()
            };
        }

        private static double Correlation(IReadOnlyList<double> left, IReadOnlyList<double> right)
        {
            var meanLeft = left.Average();
            var meanRight = right.Average();

            double covariance = 0.0;
            double varianceLeft = 0.0;
            double varianceRight = 0.0;

            for (int i = 0; i < left.Count; i++)
            {
                var dl = left[i] - meanLeft;
                var dr = right[i] - meanRight;
                covariance += dl * dr;
                varianceLeft += dl * dl;
                varianceRight += dr * dr;
            }

            var denominator = Math.Sqrt(varianceLeft * varianceRight);

            // Scaled rather than compared against zero, for the reason given on RSquared.
            return denominator > DegenerateSumOfSquares(left) ? covariance / denominator : 0.0;
        }

        /// <summary>
        /// Threshold below which a sum of squared deviations is indistinguishable from zero.
        /// </summary>
        /// <remarks>
        /// A constant series does not produce exactly zero here. Summing n identical doubles
        /// and dividing by n need not return the original value, so each deviation carries a
        /// one-ulp residue and the total lands just above zero. A bare <c>&gt; 0.0</c> guard
        /// then passes and the ratio built on it explodes - which is how a flat target
        /// produced an R-squared around -5e16 rather than the zero the guard intended.
        /// </remarks>
        private static double DegenerateSumOfSquares(IReadOnlyList<double> values)
        {
            double scale = 0.0;
            for (int i = 0; i < values.Count; i++)
            {
                var magnitude = Math.Abs(values[i]);
                if (magnitude > scale) scale = magnitude;
            }

            return 1e-20 * values.Count * Math.Max(1.0, scale * scale);
        }

        private static double StandardDeviation(IReadOnlyList<double> values, double mean)
        {
            if (values.Count < 2) return 0.0;

            double sumSquares = 0.0;
            for (int i = 0; i < values.Count; i++)
            {
                var deviation = values[i] - mean;
                sumSquares += deviation * deviation;
            }
            return Math.Sqrt(sumSquares / (values.Count - 1));
        }

        private static double RSquared(IReadOnlyList<double[]> design, IReadOnlyList<double> targets, double[] coefficients)
        {
            var mean = targets.Average();
            double residualSumSquares = 0.0;
            double totalSumSquares = 0.0;

            for (int i = 0; i < targets.Count; i++)
            {
                var residual = targets[i] - OrdinaryLeastSquares.Predict(coefficients, design[i]);
                residualSumSquares += residual * residual;

                var deviation = targets[i] - mean;
                totalSumSquares += deviation * deviation;
            }

            // A target with no variation carries no explainable variance, so R squared is
            // undefined and reported as zero. The comparison is against a scaled tolerance
            // rather than against zero - see DegenerateSumOfSquares.
            return totalSumSquares > DegenerateSumOfSquares(targets)
                ? 1.0 - (residualSumSquares / totalSumSquares)
                : 0.0;
        }
    }
}
