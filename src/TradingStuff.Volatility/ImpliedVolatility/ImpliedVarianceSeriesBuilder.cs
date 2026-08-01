using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>
    /// Supplies the risk-free rate used to discount option prices and imply the forward.
    /// </summary>
    public interface IRiskFreeRateSource
    {
        /// <summary>Continuously compounded rate for the given observation date and horizon.</summary>
        double RateFor(DateTime date, double yearsToExpiry);
    }

    /// <summary>A single constant rate. Adequate for a first pass, not for a long history.</summary>
    public class FlatRiskFreeRate : IRiskFreeRateSource
    {
        private readonly double _rate;

        public FlatRiskFreeRate(double rate)
        {
            _rate = rate;
        }

        public double RateFor(DateTime date, double yearsToExpiry)
        {
            return _rate;
        }
    }

    /// <summary>
    /// Rates from a date-keyed history, carried forward to dates in between. Over a
    /// decade the short rate moves from zero to five percent and back, which shifts the
    /// implied forward enough to matter for the correction term, so a flat rate is a poor
    /// choice for a long sample.
    /// </summary>
    public class HistoricalRiskFreeRate : IRiskFreeRateSource
    {
        private readonly List<KeyValuePair<DateTime, double>> _rates;

        public HistoricalRiskFreeRate(IEnumerable<KeyValuePair<DateTime, double>> rates)
        {
            if (rates == null) throw new ArgumentNullException("rates");

            _rates = rates.OrderBy(r => r.Key).ToList();
            if (_rates.Count == 0) throw new ArgumentException("At least one rate observation is required.");
        }

        public double RateFor(DateTime date, double yearsToExpiry)
        {
            var index = _rates.FindLastIndex(r => r.Key <= date.Date);
            return index < 0 ? _rates[0].Value : _rates[index].Value;
        }
    }

    /// <summary>
    /// Turns per-date option chains into a daily constant-maturity implied variance
    /// series. A date that cannot be computed is emitted as unusable with a reason
    /// attached rather than dropped, so gaps in the series stay visible.
    /// </summary>
    public class ImpliedVarianceSeriesBuilder
    {
        private readonly IRiskFreeRateSource _rates;
        private readonly ModelFreeVarianceOptions _varianceOptions;
        private readonly ConstantMaturityOptions _maturityOptions;

        public ImpliedVarianceSeriesBuilder(
            IRiskFreeRateSource rates,
            ModelFreeVarianceOptions varianceOptions = null,
            ConstantMaturityOptions maturityOptions = null)
        {
            if (rates == null) throw new ArgumentNullException("rates");

            _rates = rates;
            _varianceOptions = varianceOptions ?? new ModelFreeVarianceOptions();
            _maturityOptions = maturityOptions ?? new ConstantMaturityOptions();
        }

        /// <summary>
        /// Computes one day from all the expirations observed on that date.
        /// </summary>
        public ImpliedVarianceDay BuildDay(string symbol, DateTime date, IReadOnlyList<OptionChainSlice> slices)
        {
            if (slices == null) throw new ArgumentNullException("slices");

            var day = new ImpliedVarianceDay
            {
                Symbol = symbol,
                Date = date.Date,
                TargetDays = _maturityOptions.TargetDays,
                IsUsable = false
            };

            var expirations = new List<ModelFreeVarianceResult>();
            var failures = new List<string>();

            foreach (var slice in slices)
            {
                if (slice.TimeToExpiryYears <= 0.0) continue;

                try
                {
                    var rate = _rates.RateFor(date, slice.TimeToExpiryYears);
                    expirations.Add(ModelFreeVariance.Compute(slice, rate, _varianceOptions));
                }
                catch (Exception ex)
                {
                    failures.Add(string.Format("{0:yyyy-MM-dd}: {1}", slice.SettlesAt, ex.Message));
                }
            }

            if (expirations.Count < 2)
            {
                day.Note = string.Format("Only {0} expiration(s) computed. {1}",
                    expirations.Count, string.Join("; ", failures.Take(3)));
                return day;
            }

            try
            {
                var interpolated = ConstantMaturityVariance.Interpolate(expirations, _maturityOptions);

                day.ImpliedVariance = interpolated.Variance;
                day.NearTermDays = interpolated.NearTermDays;
                day.NextTermDays = interpolated.NextTermDays;
                day.StrikesUsed = interpolated.TotalStrikesUsed;
                day.WidestStrikeSpacing = interpolated.WidestStrikeSpacing;
                day.IsExtrapolated = interpolated.IsExtrapolated;
                day.IsUsable = interpolated.Variance > 0.0;

                if (!day.IsUsable) day.Note = "Interpolated variance was not positive.";
                else if (interpolated.IsExtrapolated) day.Note = "Extrapolated beyond the available expirations.";
            }
            catch (Exception ex)
            {
                day.Note = ex.Message;
            }

            return day;
        }

        /// <summary>
        /// Builds the full series from chains grouped by observation date.
        /// </summary>
        public List<ImpliedVarianceDay> Build(
            string symbol,
            IEnumerable<KeyValuePair<DateTime, List<OptionChainSlice>>> chainsByDate)
        {
            if (chainsByDate == null) throw new ArgumentNullException("chainsByDate");

            return chainsByDate
                .OrderBy(entry => entry.Key)
                .Select(entry => BuildDay(symbol, entry.Key, entry.Value))
                .ToList();
        }
    }
}
