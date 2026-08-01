using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// How the close-to-open move is folded into the session's variance.
    ///
    /// This matters more than it looks. Implied volatility covers calendar time,
    /// including the overnight session; realized variance summed from intraday bars
    /// does not. Dropping the overnight move understates realized volatility against
    /// implied and manufactures a variance risk premium that is partly an artifact.
    /// </summary>
    public enum OvernightPolicy
    {
        /// <summary>Intraday variance only. Correct when comparing intraday measures to each other.</summary>
        Exclude = 0,

        /// <summary>
        /// Add the squared overnight return. Unbiased but noisy, since a single squared
        /// return is a very poor variance estimate for the overnight period.
        /// </summary>
        AddSquaredReturn = 1,

        /// <summary>
        /// Scale intraday variance by a trailing estimate of the ratio between
        /// close-to-close variance and intraday variance (Hansen-Lunde). Lower variance
        /// than adding the squared return, and computed strictly from a backward-looking
        /// window so it introduces no lookahead.
        /// </summary>
        HansenLundeScaling = 2
    }

    public class RealizedVolatilityOptions
    {
        /// <summary>Interval of the raw input bars, in minutes.</summary>
        public int SourceBarMinutes { get; set; }

        /// <summary>
        /// Sampling interval for the returns fed to the estimator. Five minutes is the
        /// conventional bias/variance compromise for liquid US equities.
        /// </summary>
        public int SamplingMinutes { get; set; }

        /// <summary>
        /// Average the estimator over every offset of the sampling grid. Strictly
        /// better than a single grid when the source bars are finer than the sampling
        /// interval; ignored otherwise.
        /// </summary>
        public bool UseSubsampling { get; set; }

        public BarTimestampConvention TimestampConvention { get; set; }

        public OvernightPolicy OvernightPolicy { get; set; }

        /// <summary>Trailing window, in sessions, for the Hansen-Lunde scaling factor.</summary>
        public int OvernightScalingWindow { get; set; }

        /// <summary>
        /// Ex-dividend amounts by date. On an ex-dividend date the price gaps down
        /// mechanically by roughly the distribution, which is not volatility. SPY
        /// distributes quarterly in one lump while SPX, a price index, absorbs its
        /// constituents' dividends continuously - so leaving this unadjusted puts four
        /// spurious variance spikes a year into SPY that SPX does not have.
        /// </summary>
        public Dictionary<DateTime, double> ExDividends { get; private set; }

        public RealizedVolatilityOptions()
        {
            SourceBarMinutes = 1;
            SamplingMinutes = 5;
            UseSubsampling = true;
            TimestampConvention = BarTimestampConvention.BarStart;
            OvernightPolicy = OvernightPolicy.HansenLundeScaling;
            OvernightScalingWindow = 252;
            ExDividends = new Dictionary<DateTime, double>();
        }

        public void Validate()
        {
            if (SourceBarMinutes <= 0)
                throw new InvalidOperationException("SourceBarMinutes must be positive.");
            if (SamplingMinutes < SourceBarMinutes)
                throw new InvalidOperationException("SamplingMinutes cannot be finer than the source bars.");
            if (OvernightScalingWindow <= 0)
                throw new InvalidOperationException("OvernightScalingWindow must be positive.");
        }

        /// <summary>Number of distinct offsets available on the sampling grid.</summary>
        public int SubsampleGridCount
        {
            get
            {
                if (!UseSubsampling) return 1;
                return Math.Max(1, SamplingMinutes / SourceBarMinutes);
            }
        }
    }
}
