using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.ResearchContracts;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Turns raw intraday bars into a clean daily realized volatility series.
    ///
    /// The cleaning here is not incidental. Duplicate rows, reversed time ordering and
    /// unfiltered extended-hours prints all produce realized variance that looks
    /// plausible and is wrong, and none of them announce themselves downstream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session boundaries, trading dates and half days all come from <see cref="ISessionClock"/>.
    /// This class performs no timezone conversion and holds no wall-clock times, per the
    /// UTC-canonical doctrine: bar timestamps are UTC instants, and which session an instant
    /// belongs to is the clock's question to answer.
    /// </para>
    /// <para>
    /// That is a correctness matter, not a tidiness one. The estimator's own notion of a
    /// session could not see a holiday, and inferred a half day from how early the last bar
    /// arrived - which reads a genuine early close and a feed that stopped reporting as the
    /// same thing. The clock knows the difference.
    /// </para>
    /// </remarks>
    public class RealizedVolatilitySeriesBuilder
    {
        private const string RegularTradingHours = "RTH";

        private readonly ISessionClock _clock;
        private readonly string _calendar;
        private readonly SessionQualityPolicy _policy;
        private readonly RealizedVolatilityOptions _options;

        /// <param name="clock">The platform's session authority.</param>
        /// <param name="calendar">
        /// Calendar key to attribute trading dates with. Use the RTH key: it answers correctly
        /// for every instant, whereas a GTH key hands a midday instant to the following
        /// session's trading date.
        /// </param>
        public RealizedVolatilitySeriesBuilder(
            ISessionClock clock,
            string calendar,
            SessionQualityPolicy policy,
            RealizedVolatilityOptions options)
        {
            if (clock == null) throw new ArgumentNullException("clock");
            if (string.IsNullOrWhiteSpace(calendar)) throw new ArgumentException("A calendar key is required.", "calendar");
            if (policy == null) throw new ArgumentNullException("policy");
            if (options == null) throw new ArgumentNullException("options");

            policy.Validate();
            options.Validate();

            _clock = clock;
            _calendar = calendar;
            _policy = policy;
            _options = options;
        }

        public List<RealizedVolatilityDay> Build(string symbol, IEnumerable<IntradayBar> bars)
        {
            if (bars == null) throw new ArgumentNullException("bars");

            var cleaned = Clean(bars);
            var days = new List<RealizedVolatilityDay>();

            double previousSessionClose = 0.0;

            foreach (var session in GroupIntoSessions(cleaned))
            {
                var day = BuildSession(symbol, session.Key, session.Value, previousSessionClose);
                if (day == null) continue;

                days.Add(day);
                previousSessionClose = day.SessionClose;
            }

            ApplyOvernightPolicy(days);
            return days;
        }

        /// <summary>
        /// Sorts ascending, removes duplicate timestamps and drops unusable prices.
        /// Vendors commonly return newest-first, and re-pulling an overlapping date
        /// range duplicates bars; either one silently corrupts every downstream measure.
        /// </summary>
        private static List<IntradayBar> Clean(IEnumerable<IntradayBar> bars)
        {
            var ordered = bars
                .Where(b => b.HasUsablePrices)
                .OrderBy(b => b.Timestamp)
                .ToList();

            var deduplicated = new List<IntradayBar>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                // Keep the last record for a repeated timestamp: a re-pull is more likely
                // to be a correction than a regression.
                if (i + 1 < ordered.Count && ordered[i + 1].Timestamp == ordered[i].Timestamp) continue;
                deduplicated.Add(ordered[i]);
            }

            return deduplicated;
        }

        /// <summary>
        /// Buckets bars into the regular session of the trading date the clock assigns them,
        /// discarding anything outside it.
        /// </summary>
        /// <remarks>
        /// Bars on a holiday are attributed forward to the next trading date and then fall
        /// outside that session's window, so they are dropped rather than folded into a
        /// neighbouring day - which is what a calendar-date bucketing would have done.
        /// </remarks>
        private IEnumerable<KeyValuePair<TradingSession, List<IntradayBar>>> GroupIntoSessions(
            List<IntradayBar> bars)
        {
            TradingSession current = null;
            var members = new List<IntradayBar>();

            foreach (var bar in bars)
            {
                var session = RegularSessionFor(bar.Timestamp);
                if (session == null) continue;
                if (!IsInsideSession(session, bar.Timestamp)) continue;

                if (current != null && session.TradingDate != current.TradingDate)
                {
                    if (members.Count > 0)
                        yield return new KeyValuePair<TradingSession, List<IntradayBar>>(current, members);
                    members = new List<IntradayBar>();
                }

                current = session;
                members.Add(bar);
            }

            if (current != null && members.Count > 0)
            {
                yield return new KeyValuePair<TradingSession, List<IntradayBar>>(current, members);
            }
        }

        /// <summary>The regular session containing this instant, if any.</summary>
        /// <remarks>
        /// The search spans a few trading dates either side of the attributed one rather than
        /// just the attributed date itself. An instant exactly at a close belongs to no
        /// session under the clock's half-open rule, so it is attributed forward to the next
        /// trading date - but for a bar-end feed that instant is the previous session's final
        /// bar, and the closing move is a material share of daily variance. Looking back
        /// recovers it without asking the clock to change its convention.
        /// </remarks>
        private TradingSession RegularSessionFor(DateTime instantUtc)
        {
            var instant = new DateTimeOffset(DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc));
            var tradingDate = _clock.TradingDateOf(_calendar, instant);

            foreach (var session in _clock.SessionsBetween(_calendar, tradingDate.AddDays(-4), tradingDate))
            {
                // A calendar can carry both an overnight and a regular row for one trading
                // date; realized variance is defined over the regular session.
                if (session.Label != RegularTradingHours) continue;

                if (instant >= session.OpenUtc && instant <= session.CloseUtc) return session;
            }

            return null;
        }

        /// <summary>
        /// Whether a bar falls in the part of the session the estimator uses: from the open
        /// plus the configured skip, through the close inclusive.
        /// </summary>
        /// <remarks>
        /// The close is inclusive here although <c>SessionAt</c> treats sessions as half-open.
        /// The closing print is a material share of daily variance and dropping it would bias
        /// every session low; the half-open rule exists to stop two sessions claiming one
        /// instant, which is not a risk inside a single session's own window.
        /// </remarks>
        private bool IsInsideSession(TradingSession session, DateTime instantUtc)
        {
            var instant = new DateTimeOffset(DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc));
            return instant >= EffectiveOpen(session) && instant <= session.CloseUtc;
        }

        private DateTimeOffset EffectiveOpen(TradingSession session)
        {
            return session.OpenUtc.AddMinutes(_policy.SkipMinutesAfterOpen);
        }

        private RealizedVolatilityDay BuildSession(
            string symbol,
            TradingSession session,
            List<IntradayBar> sessionBars,
            double previousSessionClose)
        {
            if (sessionBars.Count < 2) return null;

            List<DateTime> closeTimes;
            List<double> closePrices;
            BarResampler.ToCloseSeries(sessionBars, _options.TimestampConvention, _options.SourceBarMinutes,
                out closeTimes, out closePrices);

            var gridStart = EffectiveOpen(session).UtcDateTime;
            var gridEnd = session.CloseUtc.UtcDateTime;

            var grids = new List<RealizedMoments>();
            var staleSamples = 0;

            for (int offset = 0; offset < _options.SubsampleGridCount; offset++)
            {
                var sampled = BarResampler.Sample(
                    closeTimes, closePrices, gridStart, gridEnd,
                    _options.SamplingMinutes, offset * _options.SourceBarMinutes);

                var returns = sampled.LogReturns();
                if (returns.Count == 0) continue;

                grids.Add(RealizedVolatilityEstimator.FromReturns(returns));
                staleSamples += sampled.StaleSamples;
            }

            if (grids.Count == 0) return null;

            var moments = RealizedVolatilityEstimator.Average(grids);

            // Report the per-grid average so the count is comparable to ReturnCount,
            // which is itself averaged across grids.
            var meanStaleSamples = (int)Math.Round((double)staleSamples / grids.Count);

            var day = new RealizedVolatilityDay
            {
                Symbol = symbol,
                Date = session.TradingDate.ToDateTime(TimeOnly.MinValue),
                IntradayVariance = moments.RealizedVariance,
                TotalVariance = moments.RealizedVariance,
                BipowerVariation = moments.BipowerVariation,
                JumpVariation = moments.JumpVariation,
                UpsideVariance = moments.UpsideVariance,
                DownsideVariance = moments.DownsideVariance,
                RealizedQuarticity = moments.RealizedQuarticity,
                ReturnCount = moments.ReturnCount,
                StaleSamples = meanStaleSamples,
                SessionOpen = closePrices[0],
                SessionClose = closePrices[closePrices.Count - 1],
                FirstBarTime = closeTimes[0],
                LastBarTime = closeTimes[closeTimes.Count - 1],

                // The calendar says so, rather than the data being asked to imply it. A feed
                // that stops early is an incomplete session, not a half day, and the two want
                // different handling downstream.
                IsShortSession = session.IsHalfDay,
                IsComplete = IsComplete(moments.ReturnCount, meanStaleSamples)
            };

            ApplyOvernightReturn(day, previousSessionClose);
            return day;
        }

        /// <summary>
        /// A session is trusted only when it has enough sampled returns and few enough of
        /// them came from a repeated bar. Either failure leaves the row in place with the
        /// flag cleared, so gaps stay visible to whatever consumes the series.
        /// </summary>
        private bool IsComplete(int returnCount, int staleSamples)
        {
            if (returnCount < _policy.MinimumReturnsPerDay) return false;

            var staleFraction = (double)staleSamples / returnCount;
            return staleFraction <= _policy.MaximumStaleSampleFraction;
        }

        private void ApplyOvernightReturn(RealizedVolatilityDay day, double previousSessionClose)
        {
            if (previousSessionClose <= 0.0)
            {
                day.HasOvernightReturn = false;
                return;
            }

            double dividend;
            _options.ExDividends.TryGetValue(day.Date.Date, out dividend);
            day.DividendAdjustment = dividend;

            // Adding the distribution back removes the mechanical ex-dividend gap, which
            // is a cash transfer rather than a move in the underlying's value.
            var adjustedOpen = day.SessionOpen + dividend;

            day.OvernightReturn = Math.Log(adjustedOpen / previousSessionClose);
            day.CloseToCloseReturn = day.OvernightReturn + Math.Log(day.SessionClose / day.SessionOpen);
            day.HasOvernightReturn = true;
        }

        /// <summary>
        /// Folds the overnight session into total variance. The Hansen-Lunde factor is
        /// computed from a strictly trailing window, so no observation is scaled using
        /// information from its own day or later.
        /// </summary>
        private void ApplyOvernightPolicy(List<RealizedVolatilityDay> days)
        {
            if (_options.OvernightPolicy == OvernightPolicy.Exclude)
            {
                foreach (var day in days) day.TotalVariance = day.IntradayVariance;
                return;
            }

            if (_options.OvernightPolicy == OvernightPolicy.AddSquaredReturn)
            {
                foreach (var day in days)
                {
                    day.TotalVariance = day.IntradayVariance
                        + (day.HasOvernightReturn ? day.OvernightReturn * day.OvernightReturn : 0.0);
                }
                return;
            }

            ApplyHansenLundeScaling(days);
        }

        private void ApplyHansenLundeScaling(List<RealizedVolatilityDay> days)
        {
            // Minimum trailing sample before the ratio is stable enough to use.
            const int MinimumCalibrationDays = 20;

            for (int i = 0; i < days.Count; i++)
            {
                var windowStart = Math.Max(0, i - _options.OvernightScalingWindow);

                double sumCloseToCloseSquared = 0.0;
                double sumIntradayVariance = 0.0;
                var usableDays = 0;

                for (int j = windowStart; j < i; j++)
                {
                    var prior = days[j];
                    if (!prior.HasOvernightReturn || !prior.IsComplete || prior.IntradayVariance <= 0.0) continue;

                    sumCloseToCloseSquared += prior.CloseToCloseReturn * prior.CloseToCloseReturn;
                    sumIntradayVariance += prior.IntradayVariance;
                    usableDays++;
                }

                if (usableDays < MinimumCalibrationDays || sumIntradayVariance <= 0.0)
                {
                    // Warm-up: fall back to the noisier but immediately available estimate.
                    days[i].TotalVariance = days[i].IntradayVariance
                        + (days[i].HasOvernightReturn ? days[i].OvernightReturn * days[i].OvernightReturn : 0.0);
                    continue;
                }

                var scale = sumCloseToCloseSquared / sumIntradayVariance;

                // The overnight session adds variance, so the factor cannot sensibly be
                // below one; a lower value means the trailing window is degenerate.
                if (scale < 1.0) scale = 1.0;

                days[i].TotalVariance = days[i].IntradayVariance * scale;
            }
        }
    }
}
