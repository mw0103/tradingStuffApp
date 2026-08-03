using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>
    /// Builds one session date of the A4 term structure — the 9-day and 30-day constant-maturity
    /// model-free variance points and their slope — exactly per the frozen construction
    /// (docs/research/a4-slope-construction.md). Every rule this class applies is fixed there;
    /// nothing here is tunable.
    /// </summary>
    /// <remarks>
    /// The frozen bracketing (§5) deliberately differs from <see cref="ConstantMaturityVariance"/>'s
    /// default VIX-style 23–37-day window: legs are the tightest settlements around the target
    /// moment with no width cap, and — §6 — a selected leg that fails
    /// <see cref="ModelFreeVarianceResult.IsUsable"/> fails its point outright. There is no silent
    /// substitution of the next-tighter expiration: a substituted bracket would be a different
    /// measurement pretending to be the declared one.
    /// </remarks>
    public class TermStructureBuilder
    {
        private const double MinutesPerDay = 1440.0;

        private static readonly int[] Targets = { 9, 30 };

        private readonly IRiskFreeRateSource _rates;
        private readonly ModelFreeVarianceOptions _varianceOptions;

        public TermStructureBuilder(IRiskFreeRateSource rates, ModelFreeVarianceOptions varianceOptions = null)
        {
            if (rates == null) throw new ArgumentNullException(nameof(rates));

            _rates = rates;
            _varianceOptions = varianceOptions ?? new ModelFreeVarianceOptions();
        }

        /// <summary>
        /// Computes one session date from the snapshot's chain slices. <paramref name="slices"/>
        /// must carry ObservedAt/SettlesAt on a common clock (UTC instants — the two fields are
        /// only ever subtracted, and wall-clock arithmetic across a DST change would misstate the
        /// remaining life by an hour, which is material at the 9-day tenor).
        /// </summary>
        public TermStructureDay BuildDay(DateTime sessionDate, IReadOnlyList<OptionChainSlice> slices)
        {
            if (slices == null) throw new ArgumentNullException(nameof(slices));

            var day = new TermStructureDay { Date = sessionDate.Date };

            // §4: the eligibility floor — at least one full calendar day from snapshot to
            // settlement — plus the identical-settlement dedupe (keep the slice with more
            // two-sided strikes).
            var eligible = slices
                .Where(s => (s.SettlesAt - s.ObservedAt).TotalMinutes >= MinutesPerDay)
                .GroupBy(s => s.SettlesAt)
                .Select(g => g.OrderByDescending(s => s.Quotes.Count(q => q.HasTwoSidedMarket)).First())
                .OrderBy(s => s.SettlesAt)
                .ToList();

            // §6: one model-free variance per eligible expiration, computed once and shared by
            // both points. A slice that throws is recorded and treated as an unusable leg if
            // the bracketing selects it.
            var variances = new Dictionary<DateTime, (ModelFreeVarianceResult Result, string Error)>();

            foreach (var slice in eligible)
            {
                try
                {
                    var rate = _rates.RateFor(sessionDate, slice.TimeToExpiryYears);
                    variances[slice.SettlesAt] = (ModelFreeVariance.Compute(slice, rate, _varianceOptions), null);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    variances[slice.SettlesAt] = (null, ex.Message);
                }
            }

            day.NineDay = BuildPoint(Targets[0], eligible, variances);
            day.ThirtyDay = BuildPoint(Targets[1], eligible, variances);

            return day;
        }

        private static TermStructurePoint BuildPoint(
            int targetDays,
            IReadOnlyList<OptionChainSlice> eligible,
            IReadOnlyDictionary<DateTime, (ModelFreeVarianceResult Result, string Error)> variances)
        {
            var point = new TermStructurePoint { TargetDays = targetDays, IsUsable = false };

            if (eligible.Count == 0)
            {
                point.Note = "No eligible expirations at the snapshot.";
                return point;
            }

            // §5: the target moment is snapshot + τ calendar days; the legs are the tightest
            // settlements at-or-before and after it. No width cap, no extrapolation.
            var snapshot = eligible[0].ObservedAt;
            var targetMoment = snapshot.AddMinutes(targetDays * MinutesPerDay);

            var near = eligible.LastOrDefault(s => s.SettlesAt <= targetMoment);
            var far = eligible.FirstOrDefault(s => s.SettlesAt > targetMoment);

            if (near == null || far == null)
            {
                point.Note = string.Format(
                    "The {0}-day point is not bracketed: {1} leg missing among {2} eligible expirations.",
                    targetDays, near == null ? "near" : "far", eligible.Count);
                return point;
            }

            // §6: the SELECTED legs must be usable; a failed leg fails the point.
            foreach (var leg in new[] { near, far })
            {
                var (result, error) = variances[leg.SettlesAt];

                if (result == null)
                {
                    point.Note = string.Format(
                        "Selected leg {0:yyyy-MM-dd} failed to compute: {1}", leg.SettlesAt, error);
                    return point;
                }

                if (!result.IsUsable)
                {
                    point.Note = string.Format(
                        "Selected leg {0:yyyy-MM-dd} is unusable ({1} strikes{2}{3}).",
                        leg.SettlesAt, result.StrikesUsed,
                        result.TruncatedLowSide ? ", truncated low" : "",
                        result.TruncatedHighSide ? ", truncated high" : "");
                    return point;
                }
            }

            // §7: total-variance interpolation through the existing arbitrage-consistent path,
            // handed exactly the two selected legs so its internal selection cannot substitute.
            try
            {
                var interpolated = ConstantMaturityVariance.Interpolate(
                    new List<ModelFreeVarianceResult>
                    {
                        variances[near.SettlesAt].Result,
                        variances[far.SettlesAt].Result
                    },
                    new ConstantMaturityOptions
                    {
                        TargetDays = targetDays,
                        MinimumNearTermDays = 1.0,
                        MaximumNextTermDays = double.MaxValue,
                        AllowExtrapolation = false
                    });

                point.Variance = interpolated.Variance;
                point.NearTermDays = interpolated.NearTermDays;
                point.NextTermDays = interpolated.NextTermDays;
                point.StrikesUsed = interpolated.TotalStrikesUsed;
                point.IsUsable = interpolated.Variance > 0.0;
                if (!point.IsUsable) point.Note = "Interpolated variance was not positive.";
            }
            catch (InvalidOperationException ex)
            {
                point.Note = ex.Message;
            }

            return point;
        }
    }
}
