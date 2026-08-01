using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Turns raw intraday bars into a clean daily realized volatility series.
    ///
    /// The cleaning here is not incidental. Duplicate rows, reversed time ordering and
    /// unfiltered extended-hours prints all produce realized variance that looks
    /// plausible and is wrong, and none of them announce themselves downstream.
    /// </summary>
    public class RealizedVolatilitySeriesBuilder
    {
        private readonly SessionProfile _session;
        private readonly RealizedVolatilityOptions _options;

        public RealizedVolatilitySeriesBuilder(SessionProfile session, RealizedVolatilityOptions options)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (options == null) throw new ArgumentNullException("options");
            options.Validate();

            _session = session;
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

        private IEnumerable<KeyValuePair<DateTime, List<IntradayBar>>> GroupIntoSessions(List<IntradayBar> bars)
        {
            DateTime? currentDate = null;
            var current = new List<IntradayBar>();

            foreach (var bar in bars)
            {
                if (!_session.IsInRegularSession(bar.Timestamp)) continue;

                var date = bar.Timestamp.Date;
                if (currentDate.HasValue && date != currentDate.Value)
                {
                    yield return new KeyValuePair<DateTime, List<IntradayBar>>(currentDate.Value, current);
                    current = new List<IntradayBar>();
                }

                currentDate = date;
                current.Add(bar);
            }

            if (currentDate.HasValue && current.Count > 0)
            {
                yield return new KeyValuePair<DateTime, List<IntradayBar>>(currentDate.Value, current);
            }
        }

        private RealizedVolatilityDay BuildSession(
            string symbol,
            DateTime date,
            List<IntradayBar> sessionBars,
            double previousSessionClose)
        {
            if (sessionBars.Count < 2) return null;

            List<DateTime> closeTimes;
            List<double> closePrices;
            BarResampler.ToCloseSeries(sessionBars, _options.TimestampConvention, _options.SourceBarMinutes,
                out closeTimes, out closePrices);

            var gridStart = date.Add(_session.EffectiveOpen);
            var gridEnd = date.Add(_session.RegularClose);

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

            var sessionOpen = closePrices[0];
            var sessionClose = closePrices[closePrices.Count - 1];
            var lastBarTime = closeTimes[closeTimes.Count - 1];

            var day = new RealizedVolatilityDay
            {
                Symbol = symbol,
                Date = date,
                IntradayVariance = moments.RealizedVariance,
                TotalVariance = moments.RealizedVariance,
                BipowerVariation = moments.BipowerVariation,
                JumpVariation = moments.JumpVariation,
                UpsideVariance = moments.UpsideVariance,
                DownsideVariance = moments.DownsideVariance,
                RealizedQuarticity = moments.RealizedQuarticity,
                ReturnCount = moments.ReturnCount,
                StaleSamples = meanStaleSamples,
                SessionOpen = sessionOpen,
                SessionClose = sessionClose,
                FirstBarTime = closeTimes[0],
                LastBarTime = lastBarTime,
                IsShortSession = IsShortSession(date, lastBarTime),
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
            if (returnCount < _session.MinimumReturnsPerDay) return false;

            var staleFraction = (double)staleSamples / returnCount;
            return staleFraction <= _session.MaximumStaleSampleFraction;
        }

        private bool IsShortSession(DateTime date, DateTime lastBarTime)
        {
            if (_session.KnownShortSessions.Contains(date.Date)) return true;

            var scheduledClose = date.Add(_session.RegularClose);
            var shortfall = (scheduledClose - lastBarTime).TotalMinutes;
            return shortfall > _session.ShortSessionToleranceMinutes;
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
