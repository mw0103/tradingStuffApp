using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Writes the daily realized volatility series to CSV.
    ///
    /// Worth using before anything is trained on the series. Bad intraday data does not
    /// announce itself in an aggregate metric, but it is obvious the moment you plot
    /// annualized realized volatility and see a day at 400% or a run of sessions at
    /// zero.
    /// </summary>
    public static class RealizedVolatilityCsv
    {
        private const string Header =
            "symbol,date,annualized_vol,total_variance,intraday_variance,bipower,jump," +
            "upside,downside,quarticity,overnight_return,close_to_close_return,dividend," +
            "returns,stale,session_open,session_close,first_bar,last_bar,short_session,complete";

        public static void Write(string path, IEnumerable<RealizedVolatilityDay> days)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (days == null) throw new ArgumentNullException("days");

            using (var writer = new StreamWriter(path, false, Encoding.UTF8))
            {
                writer.WriteLine(Header);
                foreach (var day in days)
                {
                    writer.WriteLine(Format(day));
                }
            }
        }

        private static string Format(RealizedVolatilityDay day)
        {
            var culture = CultureInfo.InvariantCulture;
            return string.Join(",",
                day.Symbol,
                day.Date.ToString("yyyy-MM-dd", culture),
                day.AnnualizedVolatility.ToString("G17", culture),
                day.TotalVariance.ToString("G17", culture),
                day.IntradayVariance.ToString("G17", culture),
                day.BipowerVariation.ToString("G17", culture),
                day.JumpVariation.ToString("G17", culture),
                day.UpsideVariance.ToString("G17", culture),
                day.DownsideVariance.ToString("G17", culture),
                day.RealizedQuarticity.ToString("G17", culture),
                day.OvernightReturn.ToString("G17", culture),
                day.CloseToCloseReturn.ToString("G17", culture),
                day.DividendAdjustment.ToString("G17", culture),
                day.ReturnCount.ToString(culture),
                day.StaleSamples.ToString(culture),
                day.SessionOpen.ToString("G17", culture),
                day.SessionClose.ToString("G17", culture),
                day.FirstBarTime.ToString("yyyy-MM-dd HH:mm:ss", culture),
                day.LastBarTime.ToString("yyyy-MM-dd HH:mm:ss", culture),
                day.IsShortSession ? "1" : "0",
                day.IsComplete ? "1" : "0");
        }
    }
}
