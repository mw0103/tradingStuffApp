using System;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// One session's realized volatility record. This is the row the forecasting models
    /// consume and the row that gets persisted.
    /// </summary>
    public class RealizedVolatilityDay
    {
        public string Symbol { get; set; }

        /// <summary>Session date (local exchange date, time component stripped).</summary>
        public DateTime Date { get; set; }

        /// <summary>Intraday realized variance, excluding the overnight move.</summary>
        public double IntradayVariance { get; set; }

        /// <summary>
        /// Total session variance after the configured overnight policy is applied.
        /// This is the field forecasting targets should be built from.
        /// </summary>
        public double TotalVariance { get; set; }

        public double BipowerVariation { get; set; }
        public double JumpVariation { get; set; }
        public double UpsideVariance { get; set; }
        public double DownsideVariance { get; set; }
        public double RealizedQuarticity { get; set; }

        /// <summary>Log return from the prior session's close to this session's open.</summary>
        public double OvernightReturn { get; set; }

        /// <summary>
        /// Log return from the prior session's close to this session's close. Used to
        /// calibrate the overnight scaling factor; zero on the first session, which has
        /// no prior close.
        /// </summary>
        public double CloseToCloseReturn { get; set; }

        /// <summary>False on the first session of the series, which has no prior close.</summary>
        public bool HasOvernightReturn { get; set; }

        /// <summary>
        /// Dividend per share applied when de-adjusting the overnight return, if any.
        /// Non-zero only on ex-dividend dates.
        /// </summary>
        public double DividendAdjustment { get; set; }

        public int ReturnCount { get; set; }
        public int StaleSamples { get; set; }

        /// <summary>First and last sampled prices of the session.</summary>
        public double SessionOpen { get; set; }
        public double SessionClose { get; set; }

        public DateTime FirstBarTime { get; set; }
        public DateTime LastBarTime { get; set; }

        /// <summary>True when the session closed early (a half day).</summary>
        public bool IsShortSession { get; set; }

        /// <summary>
        /// False when the session had too few usable returns to trust. The row is still
        /// emitted so gaps are visible rather than silently absent.
        /// </summary>
        public bool IsComplete { get; set; }

        /// <summary>Annualized realized volatility implied by <see cref="TotalVariance"/>.</summary>
        public double AnnualizedVolatility
        {
            get { return VolatilityScaling.AnnualizeVolatility(TotalVariance); }
        }

        public double SessionMinutes
        {
            get { return (LastBarTime - FirstBarTime).TotalMinutes; }
        }
    }
}
