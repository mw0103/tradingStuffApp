using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility
{
    /// <summary>Result of sampling one session onto a fixed-interval grid.</summary>
    public class SampledSession
    {
        public List<double> Prices { get; private set; }
        public List<DateTime> Times { get; private set; }

        /// <summary>
        /// Grid points that reused the previous bar because no new bar had printed.
        /// A high count means the underlying data has holes and the realized variance
        /// for the day is biased downward.
        /// </summary>
        public int StaleSamples { get; set; }

        public SampledSession()
        {
            Prices = new List<double>();
            Times = new List<DateTime>();
        }

        public List<double> LogReturns()
        {
            var returns = new List<double>();
            for (int i = 1; i < Prices.Count; i++)
            {
                returns.Add(Math.Log(Prices[i] / Prices[i - 1]));
            }
            return returns;
        }
    }

    /// <summary>
    /// Previous-tick sampling of intraday bars onto a fixed-interval grid.
    ///
    /// Sampling interval is a bias/variance trade-off. At one minute, bid-ask bounce
    /// dominates and realized variance is biased upward (the classic volatility
    /// signature plot); at thirty minutes the estimator is unbiased but noisy. Five
    /// minutes is the conventional compromise, and subsampling across every offset of
    /// the five-minute grid recovers most of the efficiency lost by throwing away the
    /// intermediate one-minute observations.
    /// </summary>
    public static class BarResampler
    {
        /// <summary>
        /// Samples one session onto a grid of <paramref name="intervalMinutes"/> starting
        /// at the session open plus <paramref name="offsetMinutes"/>.
        /// </summary>
        /// <param name="closeTimes">Bar close times, ascending. Must align with <paramref name="closePrices"/>.</param>
        /// <param name="closePrices">Bar close prices, ascending in time.</param>
        public static SampledSession Sample(
            IReadOnlyList<DateTime> closeTimes,
            IReadOnlyList<double> closePrices,
            DateTime gridStart,
            DateTime gridEnd,
            int intervalMinutes,
            int offsetMinutes)
        {
            if (closeTimes == null) throw new ArgumentNullException("closeTimes");
            if (closePrices == null) throw new ArgumentNullException("closePrices");
            if (closeTimes.Count != closePrices.Count)
                throw new ArgumentException("closeTimes and closePrices must be the same length.");
            if (intervalMinutes <= 0)
                throw new ArgumentOutOfRangeException("intervalMinutes", "Sampling interval must be positive.");

            var result = new SampledSession();
            if (closeTimes.Count == 0) return result;

            var gridPoints = BuildGrid(gridStart, gridEnd, intervalMinutes, offsetMinutes, closeTimes[closeTimes.Count - 1]);

            int cursor = 0;
            int lastUsedIndex = -1;
            foreach (var point in gridPoints)
            {
                // Advance to the last bar that had closed at or before this grid point.
                while (cursor + 1 < closeTimes.Count && closeTimes[cursor + 1] <= point)
                {
                    cursor++;
                }

                // The grid can start before the first bar prints; nothing to sample yet.
                if (closeTimes[cursor] > point) continue;

                if (cursor == lastUsedIndex)
                {
                    result.StaleSamples++;
                }

                result.Prices.Add(closePrices[cursor]);
                result.Times.Add(point);
                lastUsedIndex = cursor;
            }

            return result;
        }

        private static List<DateTime> BuildGrid(
            DateTime gridStart,
            DateTime gridEnd,
            int intervalMinutes,
            int offsetMinutes,
            DateTime lastBarClose)
        {
            // The grid must not run past the last bar that actually printed. Extending it
            // to the scheduled close would make previous-tick sampling repeat the final
            // price, manufacturing zero returns that both understate realized variance and
            // pad the return count so a half-empty session looks like a full one.
            var effectiveEnd = lastBarClose < gridEnd ? lastBarClose : gridEnd;

            var points = new List<DateTime>();
            var current = gridStart.AddMinutes(offsetMinutes);
            while (current <= effectiveEnd)
            {
                points.Add(current);
                current = current.AddMinutes(intervalMinutes);
            }

            // The closing move is a material share of daily variance. If the last grid
            // point falls short of the final print - because the offset does not divide
            // the session evenly - append the real end so that move is captured.
            if (points.Count > 0 && points[points.Count - 1] < effectiveEnd)
            {
                points.Add(effectiveEnd);
            }

            return points;
        }

        /// <summary>
        /// Converts raw bars to (close time, close price) pairs under the given timestamp
        /// convention, so that all downstream sampling works in a single, unambiguous
        /// time base.
        /// </summary>
        public static void ToCloseSeries(
            IReadOnlyList<IntradayBar> bars,
            BarTimestampConvention convention,
            int barIntervalMinutes,
            out List<DateTime> closeTimes,
            out List<double> closePrices)
        {
            closeTimes = new List<DateTime>(bars.Count);
            closePrices = new List<double>(bars.Count);

            var shift = convention == BarTimestampConvention.BarStart
                ? TimeSpan.FromMinutes(barIntervalMinutes)
                : TimeSpan.Zero;

            for (int i = 0; i < bars.Count; i++)
            {
                closeTimes.Add(bars[i].Timestamp.Add(shift));
                closePrices.Add(bars[i].Close);
            }
        }
    }
}
