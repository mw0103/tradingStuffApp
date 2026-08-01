using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Summary of a built realized volatility series, intended to be the first thing
    /// looked at after ingesting a new symbol. Nothing here is used by the models; it
    /// exists so that data problems surface before a month of training time is spent on
    /// them.
    /// </summary>
    public class SeriesDiagnostics
    {
        public string Symbol { get; set; }
        public int TotalSessions { get; set; }
        public int CompleteSessions { get; set; }
        public int ShortSessions { get; set; }
        public int SessionsWithStaleSamples { get; set; }
        public int ZeroVarianceSessions { get; set; }

        public DateTime FirstDate { get; set; }
        public DateTime LastDate { get; set; }

        /// <summary>Largest calendar gap between consecutive sessions, in days.</summary>
        public int LargestGapDays { get; set; }

        public double MedianAnnualizedVolatility { get; set; }
        public double MeanAnnualizedVolatility { get; set; }
        public double MinAnnualizedVolatility { get; set; }
        public double MaxAnnualizedVolatility { get; set; }

        /// <summary>
        /// Sessions whose annualized volatility is implausible for a broad index. These
        /// are almost always data faults rather than market events, and they dominate any
        /// squared-error objective if left in.
        /// </summary>
        public List<RealizedVolatilityDay> Outliers { get; set; }

        public override string ToString()
        {
            return string.Format(
                "{0}: {1} sessions ({2} complete, {3} short, {4} with gaps, {5} zero-variance)\n" +
                "  {6:yyyy-MM-dd} to {7:yyyy-MM-dd}, largest gap {8}d\n" +
                "  annualized vol: median {9:P2}, mean {10:P2}, range {11:P2} to {12:P2}\n" +
                "  {13} outlier session(s) flagged",
                Symbol, TotalSessions, CompleteSessions, ShortSessions,
                SessionsWithStaleSamples, ZeroVarianceSessions,
                FirstDate, LastDate, LargestGapDays,
                MedianAnnualizedVolatility, MeanAnnualizedVolatility,
                MinAnnualizedVolatility, MaxAnnualizedVolatility,
                Outliers == null ? 0 : Outliers.Count);
        }

        /// <summary>
        /// Summarizes a series.
        /// </summary>
        /// <param name="implausibleAnnualizedVolatility">
        /// Threshold above which a session is treated as suspect. The default of 300%
        /// is far above anything a broad index has realized over a full session, so
        /// anything it catches is worth looking at by hand.
        /// </param>
        public static SeriesDiagnostics Summarize(
            IReadOnlyList<RealizedVolatilityDay> days,
            double implausibleAnnualizedVolatility = 3.0)
        {
            if (days == null) throw new ArgumentNullException("days");
            if (days.Count == 0) throw new ArgumentException("Cannot summarize an empty series.");

            var ordered = days.OrderBy(d => d.Date).ToList();
            var complete = ordered.Where(d => d.IsComplete && d.TotalVariance > 0.0).ToList();

            var volatilities = complete.Select(d => d.AnnualizedVolatility).OrderBy(v => v).ToList();

            var largestGap = 0;
            for (int i = 1; i < ordered.Count; i++)
            {
                var gap = (int)(ordered[i].Date - ordered[i - 1].Date).TotalDays;
                if (gap > largestGap) largestGap = gap;
            }

            return new SeriesDiagnostics
            {
                Symbol = ordered[0].Symbol,
                TotalSessions = ordered.Count,
                CompleteSessions = complete.Count,
                ShortSessions = ordered.Count(d => d.IsShortSession),
                SessionsWithStaleSamples = ordered.Count(d => d.StaleSamples > 0),
                ZeroVarianceSessions = ordered.Count(d => d.TotalVariance <= 0.0),
                FirstDate = ordered[0].Date,
                LastDate = ordered[ordered.Count - 1].Date,
                LargestGapDays = largestGap,
                MedianAnnualizedVolatility = volatilities.Count > 0 ? Median(volatilities) : 0.0,
                MeanAnnualizedVolatility = volatilities.Count > 0 ? volatilities.Average() : 0.0,
                MinAnnualizedVolatility = volatilities.Count > 0 ? volatilities[0] : 0.0,
                MaxAnnualizedVolatility = volatilities.Count > 0 ? volatilities[volatilities.Count - 1] : 0.0,
                // Restricted to sessions with positive variance. A zero or negative variance
                // is already reported by ZeroVarianceSessions, and annualizing one throws -
                // which would crash the very summary that exists to surface such faults. The
                // builder cannot emit a negative, but TotalVariance is a settable property on
                // a row loaded from storage, which is exactly how one would arrive.
                Outliers = ordered
                    .Where(d => d.TotalVariance > 0.0
                                && d.AnnualizedVolatility > implausibleAnnualizedVolatility)
                    .OrderByDescending(d => d.AnnualizedVolatility)
                    .ToList()
            };
        }

        private static double Median(IReadOnlyList<double> sorted)
        {
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }
    }
}
