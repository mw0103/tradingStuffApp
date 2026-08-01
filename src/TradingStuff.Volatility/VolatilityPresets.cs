using System;
using System.Collections.Generic;
using TradingStuff.ResearchContracts;

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
        /// <summary>SPY is NYSE-listed; its sessions follow that calendar.</summary>
        public const string SpyCalendar = "NYSE";

        /// <summary>SPX is a Cboe index; its regular session is the Cboe index RTH calendar.</summary>
        public const string SpxCalendar = "CBOE_INDEX_RTH";

        /// <summary>
        /// SPY from one-minute bars. Ex-dividend dates are deliberately left empty:
        /// populate <see cref="RealizedVolatilityOptions.ExDividends"/> from the
        /// distribution history before comparing against SPX, or the four quarterly gaps
        /// will show up as volatility that the index does not have.
        /// </summary>
        public static void Spy(out SessionQualityPolicy policy, out RealizedVolatilityOptions options)
        {
            policy = SessionQualityPolicy.UsEquity();
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
        public static void Spx(out SessionQualityPolicy policy, out RealizedVolatilityOptions options)
        {
            policy = SessionQualityPolicy.SpxIndex();
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
        /// The pre-registered target of the volatility-forecast-residual study: SPX regular
        /// session realized variance, with the overnight gap excluded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Distinct from <see cref="Spx"/> on purpose, and the difference is not cosmetic.
        /// <see cref="Spx"/> folds the close-to-open move in because an option prices calendar
        /// time, which is what a variance risk premium has to be measured against. The study's
        /// v1 label is session RV only - see
        /// <c>docs/research/volatility-forecast-residual-study.md</c>, which also defines a
        /// close-to-close variant and defers it.
        /// </para>
        /// <para>
        /// Keeping both rather than changing the default means the premium pipeline is not
        /// silently re-based by a change made for the forecasting study, and a reader can see
        /// which convention a series was built under from the call site.
        /// </para>
        /// </remarks>
        public static void SpxStudyTarget(out SessionQualityPolicy policy, out RealizedVolatilityOptions options)
        {
            Spx(out policy, out options);
            options.OvernightPolicy = OvernightPolicy.Exclude;
        }

        /// <summary>
        /// Builds the study's SPX session-RV label series. Registered variant; see
        /// <see cref="SpxStudyTarget"/>.
        /// </summary>
        public static List<RealizedVolatilityDay> BuildSpxStudyTarget(
            ISessionClock clock, IEnumerable<IntradayBar> bars)
        {
            SessionQualityPolicy policy;
            RealizedVolatilityOptions options;
            SpxStudyTarget(out policy, out options);

            return new RealizedVolatilitySeriesBuilder(clock, SpxCalendar, policy, options).Build("SPX", bars);
        }

        /// <summary>
        /// Builds a SPY series with a supplied distribution history applied.
        /// </summary>
        public static List<RealizedVolatilityDay> BuildSpy(
            ISessionClock clock,
            IEnumerable<IntradayBar> bars,
            IDictionary<DateTime, double> exDividends = null)
        {
            SessionQualityPolicy policy;
            RealizedVolatilityOptions options;
            Spy(out policy, out options);

            if (exDividends != null)
            {
                foreach (var entry in exDividends)
                {
                    options.ExDividends[entry.Key.Date] = entry.Value;
                }
            }

            return new RealizedVolatilitySeriesBuilder(clock, SpyCalendar, policy, options).Build("SPY", bars);
        }

        public static List<RealizedVolatilityDay> BuildSpx(ISessionClock clock, IEnumerable<IntradayBar> bars)
        {
            SessionQualityPolicy policy;
            RealizedVolatilityOptions options;
            Spx(out policy, out options);

            return new RealizedVolatilitySeriesBuilder(clock, SpxCalendar, policy, options).Build("SPX", bars);
        }
    }
}
