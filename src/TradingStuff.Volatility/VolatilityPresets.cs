using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Recommended configurations for the instruments this project targets.
    ///
    /// SPY and SPX track the same underlying exposure but are not interchangeable
    /// series, and the differences are structural rather than random:
    ///
    ///  - SPY distributes dividends in four quarterly lumps, each a mechanical gap of
    ///    roughly 0.3-0.4%. SPX is a price index whose constituents go ex-dividend on
    ///    their own staggered schedules, so it absorbs the same cash continuously and
    ///    never shows the lump. Left unadjusted, SPY carries four variance spikes a year
    ///    that SPX does not.
    ///  - The printed SPX open is assembled from staggered constituent opening prints
    ///    and is not a tradeable simultaneous price, so the first minutes of the index
    ///    series are stale in a way SPY's are not.
    ///  - SPY carries ETF-level microstructure noise - bid-ask bounce on a single
    ///    security - while the index is an average over 500 names and is correspondingly
    ///    smoother at high frequency.
    ///
    /// The consequence for training on SPY and applying to SPX is that the transfer
    /// should be measured rather than assumed. Build both series, run
    /// <see cref="VolatilityComparison"/>, and use the fitted calibration.
    /// </summary>
    public static class VolatilityPresets
    {
        /// <summary>
        /// SPY from one-minute bars. Ex-dividend dates are deliberately left empty:
        /// populate <see cref="RealizedVolatilityOptions.ExDividends"/> from the
        /// distribution history before comparing against SPX, or the four quarterly gaps
        /// will show up as volatility that the index does not have.
        /// </summary>
        public static void Spy(out SessionProfile session, out RealizedVolatilityOptions options)
        {
            session = SessionProfile.UsEquity();
            options = new RealizedVolatilityOptions
            {
                SourceBarMinutes = 1,
                SamplingMinutes = 5,
                UseSubsampling = true,
                TimestampConvention = BarTimestampConvention.BarStart,
                OvernightPolicy = OvernightPolicy.HansenLundeScaling,
                OvernightScalingWindow = 252
            };
        }

        /// <summary>
        /// SPX from one-minute index levels. The index has no overnight session of its
        /// own - it simply stops being disseminated - so the close-to-open move is
        /// entirely a jump in the constituents' prices. Scaling is still the right
        /// treatment, since an option on the index prices calendar time either way.
        /// </summary>
        public static void Spx(out SessionProfile session, out RealizedVolatilityOptions options)
        {
            session = SessionProfile.SpxIndex();
            options = new RealizedVolatilityOptions
            {
                SourceBarMinutes = 1,
                SamplingMinutes = 5,
                UseSubsampling = true,
                TimestampConvention = BarTimestampConvention.BarStart,
                OvernightPolicy = OvernightPolicy.HansenLundeScaling,
                OvernightScalingWindow = 252
            };
        }

        /// <summary>
        /// Builds a SPY series with a supplied distribution history applied.
        /// </summary>
        public static List<RealizedVolatilityDay> BuildSpy(
            IEnumerable<IntradayBar> bars,
            IDictionary<DateTime, double> exDividends = null)
        {
            SessionProfile session;
            RealizedVolatilityOptions options;
            Spy(out session, out options);

            if (exDividends != null)
            {
                foreach (var entry in exDividends)
                {
                    options.ExDividends[entry.Key.Date] = entry.Value;
                }
            }

            return new RealizedVolatilitySeriesBuilder(session, options).Build("SPY", bars);
        }

        public static List<RealizedVolatilityDay> BuildSpx(IEnumerable<IntradayBar> bars)
        {
            SessionProfile session;
            RealizedVolatilityOptions options;
            Spx(out session, out options);

            return new RealizedVolatilitySeriesBuilder(session, options).Build("SPX", bars);
        }
    }
}
