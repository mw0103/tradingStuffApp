using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>
    /// One day's variance risk premium.
    ///
    /// Two quantities here are easy to confuse and must not be. The ex-post premium
    /// compares implied variance against the variance that actually turned up over the
    /// following window, so it cannot be known until that window closes - it is a label
    /// and a research diagnostic, never a signal. The ex-ante premium compares implied
    /// variance against a forecast made from information available on the day, and is
    /// the only one of the two that can be traded on.
    /// </summary>
    public class VarianceRiskPremiumDay
    {
        public string Symbol { get; set; }
        public DateTime Date { get; set; }

        /// <summary>Annualized model-free implied variance observed on this date.</summary>
        public double ImpliedVariance { get; set; }

        public double ImpliedVolatility
        {
            get { return Math.Sqrt(Math.Max(ImpliedVariance, 0.0)); }
        }

        /// <summary>Annualized realized variance over the forward window. Requires hindsight.</summary>
        public double RealizedForwardVariance { get; set; }

        public double RealizedForwardVolatility
        {
            get { return Math.Sqrt(Math.Max(RealizedForwardVariance, 0.0)); }
        }

        /// <summary>False for the final horizon of dates, whose forward window has not closed.</summary>
        public bool HasRealizedForward { get; set; }

        /// <summary>Forecast realized variance from information available on this date.</summary>
        public double ForecastVariance { get; set; }

        public bool HasForecast { get; set; }

        /// <summary>Horizon of the comparison, in trading days.</summary>
        public int HorizonTradingDays { get; set; }

        /// <summary>
        /// Implied minus subsequently realized, in annualized variance units. Positive
        /// the large majority of the time - that persistent positive average is the
        /// premium sellers of volatility earn, and the forecasting problem is really
        /// about when it collapses or inverts.
        /// </summary>
        public double ExPostPremium
        {
            get { return ImpliedVariance - RealizedForwardVariance; }
        }

        /// <summary>The same comparison in volatility points, which is easier to read.</summary>
        public double ExPostPremiumVolatilityPoints
        {
            get { return ImpliedVolatility - RealizedForwardVolatility; }
        }

        /// <summary>Log ratio of implied to realized variance, the scale-free version.</summary>
        public double LogVarianceRatio
        {
            get { return Math.Log(ImpliedVariance / RealizedForwardVariance); }
        }

        /// <summary>Implied minus forecast: the tradeable signal.</summary>
        public double ExAntePremium
        {
            get { return ImpliedVariance - ForecastVariance; }
        }

        public double ExAntePremiumVolatilityPoints
        {
            get { return ImpliedVolatility - Math.Sqrt(Math.Max(ForecastVariance, 0.0)); }
        }
    }

    public class VarianceRiskPremiumSummary
    {
        public int Observations { get; set; }
        public double MeanPremiumVolatilityPoints { get; set; }
        public double MedianPremiumVolatilityPoints { get; set; }

        /// <summary>
        /// Share of days on which implied exceeded subsequent realized. In broad index
        /// data this sits around 0.8; a materially different number is a signal that the
        /// two series are misaligned rather than a discovery.
        /// </summary>
        public double PositivePremiumShare { get; set; }

        public double MeanImpliedVolatility { get; set; }
        public double MeanRealizedVolatility { get; set; }

        public override string ToString()
        {
            return string.Format(
                "n={0}  mean premium={1:F4} vol pts  median={2:F4}  positive {3:P1}  " +
                "mean IV={4:P2}  mean RV={5:P2}",
                Observations, MeanPremiumVolatilityPoints, MedianPremiumVolatilityPoints,
                PositivePremiumShare, MeanImpliedVolatility, MeanRealizedVolatility);
        }
    }

    public static class VarianceRiskPremiumBuilder
    {
        /// <summary>
        /// Joins an implied variance series to the realized variance that followed it.
        ///
        /// The horizon must match the implied series' maturity. A thirty calendar day
        /// implied volatility spans roughly twenty-one trading days, and comparing it
        /// against a window of any other length produces a premium that is mostly a
        /// mismatch of horizons.
        /// </summary>
        public static List<VarianceRiskPremiumDay> Build(
            IReadOnlyList<ImpliedVarianceDay> impliedDays,
            IReadOnlyList<RealizedVolatilityDay> realizedDays,
            int horizonTradingDays)
        {
            if (impliedDays == null) throw new ArgumentNullException("impliedDays");
            if (realizedDays == null) throw new ArgumentNullException("realizedDays");
            if (horizonTradingDays <= 0) throw new ArgumentOutOfRangeException("horizonTradingDays");

            var realized = realizedDays
                .Where(d => d.IsComplete && d.TotalVariance > 0.0)
                .OrderBy(d => d.Date)
                .ToList();

            var indexByDate = new Dictionary<DateTime, int>();
            for (int i = 0; i < realized.Count; i++) indexByDate[realized[i].Date.Date] = i;

            var series = new List<VarianceRiskPremiumDay>();

            foreach (var implied in impliedDays.Where(d => d.IsUsable).OrderBy(d => d.Date))
            {
                var day = new VarianceRiskPremiumDay
                {
                    Symbol = implied.Symbol,
                    Date = implied.Date.Date,
                    ImpliedVariance = implied.ImpliedVariance,
                    HorizonTradingDays = horizonTradingDays,
                    HasRealizedForward = false
                };

                int index;
                if (indexByDate.TryGetValue(implied.Date.Date, out index)
                    && index + horizonTradingDays < realized.Count)
                {
                    // Strictly forward: the window opens on the session after the implied
                    // volatility was observed.
                    var forward = realized.Skip(index + 1).Take(horizonTradingDays).ToList();
                    if (forward.Count == horizonTradingDays)
                    {
                        day.RealizedForwardVariance =
                            VolatilityScaling.AnnualizeVariance(forward.Average(d => d.TotalVariance));
                        day.HasRealizedForward = true;
                    }
                }

                series.Add(day);
            }

            return series;
        }

        /// <summary>
        /// Convenience overload that converts a calendar-day maturity to trading days.
        /// </summary>
        public static List<VarianceRiskPremiumDay> BuildForCalendarMaturity(
            IReadOnlyList<ImpliedVarianceDay> impliedDays,
            IReadOnlyList<RealizedVolatilityDay> realizedDays,
            int calendarDays)
        {
            return Build(impliedDays, realizedDays, VolatilityScaling.CalendarDaysToTradingDays(calendarDays));
        }

        /// <summary>
        /// Attaches point-in-time forecasts to produce the tradeable ex-ante premium. The
        /// supplied function must only use information available on the date it is given;
        /// nothing here can enforce that, and it is the easiest place in the whole
        /// pipeline to leak the answer.
        /// </summary>
        public static void AttachForecasts(
            IEnumerable<VarianceRiskPremiumDay> days,
            Func<DateTime, double?> forecastForDate)
        {
            if (days == null) throw new ArgumentNullException("days");
            if (forecastForDate == null) throw new ArgumentNullException("forecastForDate");

            foreach (var day in days)
            {
                var forecast = forecastForDate(day.Date);
                if (!forecast.HasValue || forecast.Value <= 0.0) continue;

                day.ForecastVariance = forecast.Value;
                day.HasForecast = true;
            }
        }

        public static VarianceRiskPremiumSummary Summarize(IReadOnlyList<VarianceRiskPremiumDay> days)
        {
            if (days == null) throw new ArgumentNullException("days");

            var usable = days.Where(d => d.HasRealizedForward && d.ImpliedVariance > 0.0).ToList();
            if (usable.Count == 0)
                throw new ArgumentException("No days have both an implied variance and a closed forward window.");

            var premiums = usable.Select(d => d.ExPostPremiumVolatilityPoints).OrderBy(p => p).ToList();
            var middle = premiums.Count / 2;

            return new VarianceRiskPremiumSummary
            {
                Observations = usable.Count,
                MeanPremiumVolatilityPoints = premiums.Average(),
                MedianPremiumVolatilityPoints = premiums.Count % 2 == 1
                    ? premiums[middle]
                    : (premiums[middle - 1] + premiums[middle]) / 2.0,
                PositivePremiumShare = (double)usable.Count(d => d.ExPostPremium > 0.0) / usable.Count,
                MeanImpliedVolatility = usable.Average(d => d.ImpliedVolatility),
                MeanRealizedVolatility = usable.Average(d => d.RealizedForwardVolatility)
            };
        }
    }
}
