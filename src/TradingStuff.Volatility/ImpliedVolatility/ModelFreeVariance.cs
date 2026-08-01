using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>Model-free implied variance for one expiration, plus the diagnostics needed to trust it.</summary>
    public class ModelFreeVarianceResult
    {
        /// <summary>Annualized implied variance for this expiration.</summary>
        public double Variance { get; set; }

        /// <summary>Total (non-annualized) implied variance over the option's remaining life.</summary>
        public double TotalVariance
        {
            get { return Variance * TimeToExpiryYears; }
        }

        public double ImpliedVolatility
        {
            get { return Math.Sqrt(Math.Max(Variance, 0.0)); }
        }

        public double TimeToExpiryYears { get; set; }
        public DateTime SettlesAt { get; set; }

        /// <summary>Forward level implied by put-call parity at the most at-the-money strike.</summary>
        public double Forward { get; set; }

        /// <summary>First strike at or below the forward.</summary>
        public double AtTheMoneyStrike { get; set; }

        public int StrikesUsed { get; set; }
        public double LowestStrike { get; set; }
        public double HighestStrike { get; set; }

        /// <summary>
        /// Median gap between adjacent included strikes. The discretized sum overstates
        /// variance when the grid is coarse - on a 30-day 20% option, moving from
        /// 1-point to 5-point strikes inflates the result by roughly 6% - so a series
        /// built from chains with inconsistent spacing carries a bias that moves around.
        /// </summary>
        public double MedianStrikeSpacing { get; set; }

        /// <summary>
        /// True when the outermost included option still carried meaningful value, which
        /// means the strike range was cut before the tail died out and the integral is
        /// missing variance. Truncation biases the result downward.
        /// </summary>
        public bool TruncatedLowSide { get; set; }
        public bool TruncatedHighSide { get; set; }

        public bool IsUsable
        {
            get { return Variance > 0.0 && StrikesUsed >= 5 && !TruncatedLowSide && !TruncatedHighSide; }
        }
    }

    public class ModelFreeVarianceOptions
    {
        /// <summary>
        /// Consecutive zero-bid strikes that terminate the search outward from the money.
        /// The CBOE methodology uses two: a single gap is usually a quoting artifact,
        /// two in a row means the tail has genuinely run out.
        /// </summary>
        public int ConsecutiveZeroBidsToStop { get; set; }

        /// <summary>Minimum strikes required before a result is produced at all.</summary>
        public int MinimumStrikes { get; set; }

        /// <summary>
        /// Price of the outermost included option, as a fraction of the forward, above
        /// which the tail is treated as truncated.
        /// </summary>
        public double TruncationPriceThreshold { get; set; }

        public ModelFreeVarianceOptions()
        {
            ConsecutiveZeroBidsToStop = 2;
            MinimumStrikes = 5;
            TruncationPriceThreshold = 0.0005;
        }
    }

    /// <summary>
    /// Model-free implied variance, following the CBOE VIX methodology.
    ///
    ///   sigma^2 = (2/T) * sum_i (dK_i / K_i^2) * e^{RT} * Q(K_i) - (1/T) * [F/K_0 - 1]^2
    ///
    /// This is the risk-neutral expectation of realized variance over the option's life -
    /// the fair strike of a variance swap - rather than the Black-Scholes volatility of
    /// any single contract. That distinction is the reason to prefer it here: the
    /// variance risk premium is defined against the whole risk-neutral distribution, so
    /// integrating the full strike range captures the skew that an at-the-money implied
    /// volatility discards.
    /// </summary>
    public static class ModelFreeVariance
    {
        public static ModelFreeVarianceResult Compute(
            OptionChainSlice slice,
            double riskFreeRate,
            ModelFreeVarianceOptions options = null)
        {
            if (slice == null) throw new ArgumentNullException("slice");
            options = options ?? new ModelFreeVarianceOptions();

            var timeToExpiry = slice.TimeToExpiryYears;
            if (timeToExpiry <= 0.0)
                throw new ArgumentException("The chain slice settles at or before its observation time.");

            var calls = ToStrikeMap(slice.Quotes, OptionRight.Call);
            var puts = ToStrikeMap(slice.Quotes, OptionRight.Put);

            double forward, atTheMoneyStrike;
            ResolveForward(calls, puts, riskFreeRate, timeToExpiry, out forward, out atTheMoneyStrike);

            bool truncatedLow, truncatedHigh;
            var selected = SelectOutOfTheMoneyStrikes(
                calls, puts, atTheMoneyStrike, options, out truncatedLow, out truncatedHigh);

            if (selected.Count < options.MinimumStrikes)
                throw new InvalidOperationException(string.Format(
                    "Only {0} usable strikes for {1} expiring {2:yyyy-MM-dd}; need at least {3}.",
                    selected.Count, slice.Root, slice.SettlesAt, options.MinimumStrikes));

            var strikes = selected.Keys.OrderBy(k => k).ToList();
            var discount = Math.Exp(riskFreeRate * timeToExpiry);

            double contribution = 0.0;
            for (int i = 0; i < strikes.Count; i++)
            {
                var strike = strikes[i];
                var strikeWidth = StrikeWidth(strikes, i);
                contribution += (strikeWidth / (strike * strike)) * discount * selected[strike];
            }

            var forwardCorrection = (forward / atTheMoneyStrike) - 1.0;
            var variance = (2.0 / timeToExpiry) * contribution
                           - (1.0 / timeToExpiry) * forwardCorrection * forwardCorrection;

            return new ModelFreeVarianceResult
            {
                Variance = variance,
                TimeToExpiryYears = timeToExpiry,
                SettlesAt = slice.SettlesAt,
                Forward = forward,
                AtTheMoneyStrike = atTheMoneyStrike,
                StrikesUsed = strikes.Count,
                LowestStrike = strikes[0],
                HighestStrike = strikes[strikes.Count - 1],
                MedianStrikeSpacing = MedianSpacing(strikes),
                TruncatedLowSide = truncatedLow,
                TruncatedHighSide = truncatedHigh
            };
        }

        private static Dictionary<double, OptionQuote> ToStrikeMap(
            IEnumerable<OptionQuote> quotes, OptionRight right)
        {
            var map = new Dictionary<double, OptionQuote>();
            foreach (var quote in quotes)
            {
                if (quote.Right != right) continue;
                map[quote.Strike] = quote;
            }
            return map;
        }

        /// <summary>
        /// Recovers the forward from put-call parity at the strike where the call and put
        /// prices are closest. Using the index level instead would ignore financing and
        /// dividends, which at a one-month horizon shifts the at-the-money point enough
        /// to matter for the correction term.
        /// </summary>
        private static void ResolveForward(
            Dictionary<double, OptionQuote> calls,
            Dictionary<double, OptionQuote> puts,
            double riskFreeRate,
            double timeToExpiry,
            out double forward,
            out double atTheMoneyStrike)
        {
            var paired = calls.Keys.Where(puts.ContainsKey).OrderBy(k => k).ToList();
            if (paired.Count == 0)
                throw new InvalidOperationException("No strike has both a call and a put quote.");

            var bestStrike = paired[0];
            var smallestGap = double.MaxValue;

            foreach (var strike in paired)
            {
                var call = calls[strike];
                var put = puts[strike];
                if (!call.HasTwoSidedMarket || !put.HasTwoSidedMarket) continue;

                var gap = Math.Abs(call.Mid - put.Mid);
                if (gap < smallestGap)
                {
                    smallestGap = gap;
                    bestStrike = strike;
                }
            }

            if (smallestGap == double.MaxValue)
                throw new InvalidOperationException("No strike has a two-sided market on both the call and the put.");

            var impliedForward = bestStrike + Math.Exp(riskFreeRate * timeToExpiry)
                                 * (calls[bestStrike].Mid - puts[bestStrike].Mid);
            forward = impliedForward;

            var below = calls.Keys.Concat(puts.Keys).Distinct().Where(k => k <= impliedForward).ToList();
            if (below.Count == 0)
                throw new InvalidOperationException("No strike sits at or below the implied forward.");

            atTheMoneyStrike = below.Max();
        }

        /// <summary>
        /// Builds the out-of-the-money price series: puts below the money, calls above,
        /// and the average of the two at the money. Walking outward in each direction, the
        /// search stops after the configured run of zero bids and discards everything
        /// beyond it.
        /// </summary>
        private static Dictionary<double, double> SelectOutOfTheMoneyStrikes(
            Dictionary<double, OptionQuote> calls,
            Dictionary<double, OptionQuote> puts,
            double atTheMoneyStrike,
            ModelFreeVarianceOptions options,
            out bool truncatedLow,
            out bool truncatedHigh)
        {
            var selected = new Dictionary<double, double>();

            OptionQuote atTheMoneyCall, atTheMoneyPut;
            var hasCall = calls.TryGetValue(atTheMoneyStrike, out atTheMoneyCall) && atTheMoneyCall.HasTwoSidedMarket;
            var hasPut = puts.TryGetValue(atTheMoneyStrike, out atTheMoneyPut) && atTheMoneyPut.HasTwoSidedMarket;

            if (hasCall && hasPut) selected[atTheMoneyStrike] = (atTheMoneyCall.Mid + atTheMoneyPut.Mid) / 2.0;
            else if (hasCall) selected[atTheMoneyStrike] = atTheMoneyCall.Mid;
            else if (hasPut) selected[atTheMoneyStrike] = atTheMoneyPut.Mid;

            var lowestIncluded = WalkOutward(
                puts, atTheMoneyStrike, descending: true, options: options, selected: selected);
            var highestIncluded = WalkOutward(
                calls, atTheMoneyStrike, descending: false, options: options, selected: selected);

            // If the last option we kept is still worth something, the wing was cut short
            // rather than dying out naturally, and the integral is missing variance.
            truncatedLow = lowestIncluded.HasValue
                && selected[lowestIncluded.Value] > options.TruncationPriceThreshold * atTheMoneyStrike;
            truncatedHigh = highestIncluded.HasValue
                && selected[highestIncluded.Value] > options.TruncationPriceThreshold * atTheMoneyStrike;

            return selected;
        }

        private static double? WalkOutward(
            Dictionary<double, OptionQuote> quotes,
            double atTheMoneyStrike,
            bool descending,
            ModelFreeVarianceOptions options,
            Dictionary<double, double> selected)
        {
            var candidates = descending
                ? quotes.Keys.Where(k => k < atTheMoneyStrike).OrderByDescending(k => k).ToList()
                : quotes.Keys.Where(k => k > atTheMoneyStrike).OrderBy(k => k).ToList();

            var consecutiveZeroBids = 0;
            double? lastIncluded = null;

            foreach (var strike in candidates)
            {
                var quote = quotes[strike];
                if (!quote.HasTwoSidedMarket)
                {
                    consecutiveZeroBids++;
                    if (consecutiveZeroBids >= options.ConsecutiveZeroBidsToStop) break;
                    continue;
                }

                consecutiveZeroBids = 0;
                selected[strike] = quote.Mid;
                lastIncluded = strike;
            }

            return lastIncluded;
        }

        /// <summary>
        /// Half the distance between neighbouring strikes, with one-sided differences at
        /// the ends of the range.
        /// </summary>
        private static double StrikeWidth(IReadOnlyList<double> strikes, int index)
        {
            if (strikes.Count == 1) return strikes[0];
            if (index == 0) return strikes[1] - strikes[0];
            if (index == strikes.Count - 1) return strikes[index] - strikes[index - 1];
            return (strikes[index + 1] - strikes[index - 1]) / 2.0;
        }

        private static double MedianSpacing(IReadOnlyList<double> strikes)
        {
            if (strikes.Count < 2) return 0.0;

            var gaps = new List<double>(strikes.Count - 1);
            for (int i = 1; i < strikes.Count; i++) gaps.Add(strikes[i] - strikes[i - 1]);
            gaps.Sort();

            var middle = gaps.Count / 2;
            return gaps.Count % 2 == 1 ? gaps[middle] : (gaps[middle - 1] + gaps[middle]) / 2.0;
        }
    }
}
