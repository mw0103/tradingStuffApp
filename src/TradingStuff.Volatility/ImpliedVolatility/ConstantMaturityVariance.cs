using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    public class ConstantMaturityResult
    {
        /// <summary>Annualized implied variance at the target maturity.</summary>
        public double Variance { get; set; }

        public double ImpliedVolatility
        {
            get { return Math.Sqrt(Math.Max(Variance, 0.0)); }
        }

        /// <summary>Target maturity in calendar days.</summary>
        public int TargetDays { get; set; }

        public double NearTermDays { get; set; }
        public double NextTermDays { get; set; }
        public double NearTermVariance { get; set; }
        public double NextTermVariance { get; set; }

        /// <summary>
        /// True when the target maturity falls outside the two expirations used, so the
        /// result is an extrapolation rather than an interpolation.
        /// </summary>
        public bool IsExtrapolated { get; set; }

        public int TotalStrikesUsed { get; set; }
        public double WidestStrikeSpacing { get; set; }
    }

    public class ConstantMaturityOptions
    {
        /// <summary>Target maturity in calendar days. Thirty matches the VIX convention.</summary>
        public int TargetDays { get; set; }

        /// <summary>
        /// Shortest expiration eligible as the near term. Very short-dated options have
        /// erratic quotes and a settlement convention that dominates the time
        /// calculation, so VIX rolls out of them at 24 days.
        /// </summary>
        public double MinimumNearTermDays { get; set; }

        /// <summary>Longest expiration eligible as the next term.</summary>
        public double MaximumNextTermDays { get; set; }

        /// <summary>Permit extrapolation when no pair brackets the target.</summary>
        public bool AllowExtrapolation { get; set; }

        public ConstantMaturityOptions()
        {
            TargetDays = 30;
            MinimumNearTermDays = 23.0;
            MaximumNextTermDays = 37.0;
            AllowExtrapolation = false;
        }
    }

    /// <summary>
    /// Interpolates two expirations' model-free variance onto a fixed maturity.
    ///
    /// The interpolation is in total variance rather than in volatility or in annualized
    /// variance, which is the only version that is arbitrage-consistent: variance is
    /// additive in time, volatility is not.
    /// </summary>
    public static class ConstantMaturityVariance
    {
        private const double MinutesPerDay = 1440.0;
        private const double MinutesPerYear = 365.0 * MinutesPerDay;

        public static ConstantMaturityResult Interpolate(
            IReadOnlyList<ModelFreeVarianceResult> expirations,
            ConstantMaturityOptions options = null)
        {
            if (expirations == null) throw new ArgumentNullException("expirations");
            options = options ?? new ConstantMaturityOptions();

            var usable = expirations
                .Where(e => e.Variance > 0.0)
                .OrderBy(e => e.TimeToExpiryYears)
                .ToList();

            if (usable.Count < 2)
                throw new InvalidOperationException(
                    "Two expirations with positive variance are required to build a constant-maturity series.");

            var targetYears = options.TargetDays / 365.0;

            var near = usable.LastOrDefault(e => e.TimeToExpiryYears <= targetYears
                                                 && DaysOf(e) >= options.MinimumNearTermDays);
            var next = usable.FirstOrDefault(e => e.TimeToExpiryYears > targetYears
                                                  && DaysOf(e) <= options.MaximumNextTermDays);

            var extrapolated = false;

            if (near == null || next == null)
            {
                if (!options.AllowExtrapolation)
                    throw new InvalidOperationException(string.Format(
                        "No pair of expirations brackets {0} days within the {1}-{2} day window.",
                        options.TargetDays, options.MinimumNearTermDays, options.MaximumNextTermDays));

                // Fall back to the two expirations closest to the target and extrapolate
                // along the same line.
                var ordered = usable.OrderBy(e => Math.Abs(e.TimeToExpiryYears - targetYears)).Take(2)
                    .OrderBy(e => e.TimeToExpiryYears).ToList();
                near = ordered[0];
                next = ordered[1];
                extrapolated = true;
            }

            var nearMinutes = near.TimeToExpiryYears * MinutesPerYear;
            var nextMinutes = next.TimeToExpiryYears * MinutesPerYear;
            var targetMinutes = options.TargetDays * MinutesPerDay;

            if (Math.Abs(nextMinutes - nearMinutes) < 1e-9)
                throw new InvalidOperationException("The two selected expirations settle at the same moment.");

            var nearWeight = (nextMinutes - targetMinutes) / (nextMinutes - nearMinutes);
            var nextWeight = (targetMinutes - nearMinutes) / (nextMinutes - nearMinutes);

            // Interpolate total variance, then rescale to the target window and annualize.
            var blendedTotalVariance = near.TotalVariance * nearWeight + next.TotalVariance * nextWeight;
            var variance = blendedTotalVariance * (MinutesPerYear / targetMinutes);

            return new ConstantMaturityResult
            {
                Variance = variance,
                TargetDays = options.TargetDays,
                NearTermDays = DaysOf(near),
                NextTermDays = DaysOf(next),
                NearTermVariance = near.Variance,
                NextTermVariance = next.Variance,
                IsExtrapolated = extrapolated,
                TotalStrikesUsed = near.StrikesUsed + next.StrikesUsed,
                WidestStrikeSpacing = Math.Max(near.MedianStrikeSpacing, next.MedianStrikeSpacing)
            };
        }

        private static double DaysOf(ModelFreeVarianceResult result)
        {
            return result.TimeToExpiryYears * 365.0;
        }
    }
}
