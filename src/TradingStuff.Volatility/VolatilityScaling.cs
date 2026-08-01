using System;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Conversions between session variance, annualized variance and annualized
    /// volatility. Kept in one place because mixing conventions is the single easiest
    /// way to produce a variance risk premium that is pure units error.
    /// </summary>
    public static class VolatilityScaling
    {
        /// <summary>Trading days per year, the standard annualization factor.</summary>
        public const double TradingDaysPerYear = 252.0;

        /// <summary>
        /// Calendar-to-trading day ratio. A 30-calendar-day implied volatility spans
        /// roughly 21 trading days, which is the horizon a realized measure must cover
        /// for the two to be comparable.
        /// </summary>
        public const double TradingDaysPerCalendarDay = TradingDaysPerYear / 365.0;

        public static int CalendarDaysToTradingDays(int calendarDays)
        {
            if (calendarDays <= 0) throw new ArgumentOutOfRangeException("calendarDays");
            return Math.Max(1, (int)Math.Round(calendarDays * TradingDaysPerCalendarDay));
        }

        /// <summary>Annualizes a single session's variance.</summary>
        public static double AnnualizeVariance(double sessionVariance)
        {
            return sessionVariance * TradingDaysPerYear;
        }

        /// <summary>
        /// Annualizes a mean daily variance into a volatility, matching the convention
        /// an option's implied volatility is quoted in.
        /// </summary>
        public static double AnnualizeVolatility(double meanDailyVariance)
        {
            if (meanDailyVariance < 0.0) throw new ArgumentOutOfRangeException("meanDailyVariance");
            return Math.Sqrt(meanDailyVariance * TradingDaysPerYear);
        }

        /// <summary>Inverse of <see cref="AnnualizeVolatility"/>.</summary>
        public static double ToMeanDailyVariance(double annualizedVolatility)
        {
            return (annualizedVolatility * annualizedVolatility) / TradingDaysPerYear;
        }
    }
}
