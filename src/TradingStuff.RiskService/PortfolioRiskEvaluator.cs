using TradingStuff.Contracts;

namespace TradingStuff.RiskService;

public sealed class PortfolioRiskEvaluator(RiskLimits limits)
{
    public RiskEvaluationResult Evaluate(RiskEvaluationRequest request)
    {
        var breaches = new List<RiskLimitBreach>();
        var order = request.Order;
        var legs = order.Legs;

        // Leg count is the one precondition nothing else can work around: every formula below reads
        // two legs positionally.
        if (legs is not { Count: 2 })
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.UnsupportedLegCount,
                "V1 prices two-leg option structures only.",
                legs?.Count ?? 0,
                2m));

            return Refused(breaches);
        }

        // Shape and prices are both checked before any arithmetic, and both report so an operator
        // sees everything wrong at once — but neither degrades into an estimate. Every number this
        // method returns is derived from the leg quantities and the leg quotes; when either is
        // unusable the arithmetic still yields a number, that number is almost always $0, and $0
        // reads downstream as "no risk" and approves the order.
        var shapeIsPriceable = HasPriceableQuantities(legs, breaches);
        var quotesArePriceable = TryResolveLegQuotes(order, request.Quotes, breaches, out var legQuotes);

        if (!shapeIsPriceable || !quotesArePriceable)
        {
            return Refused(breaches);
        }

        var exposureDelta = CalculateExposureDelta(order, legQuotes);
        var estimatedMaxLoss = EstimateMaxLoss(order, legQuotes, breaches);
        var estimatedBuyingPowerImpact = estimatedMaxLoss;
        var totalContracts = legs.Sum(leg => leg.Quantity);

        if (totalContracts > limits.MaxContractsPerOrder)
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.MaxContracts,
                "Order contract count exceeds the configured per-order limit.",
                totalContracts,
                limits.MaxContractsPerOrder));
        }

        if (estimatedMaxLoss > limits.MaxLossPerOrder)
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.MaxLossPerOrder,
                "Estimated maximum loss exceeds the configured per-order limit.",
                estimatedMaxLoss,
                limits.MaxLossPerOrder));
        }

        if (estimatedBuyingPowerImpact > request.Portfolio.BuyingPower)
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.BuyingPower,
                "Estimated buying-power impact exceeds available buying power.",
                estimatedBuyingPowerImpact,
                request.Portfolio.BuyingPower));
        }

        if (estimatedBuyingPowerImpact > limits.MaxBuyingPowerUsage)
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.MaxBuyingPowerUsage,
                "Estimated buying-power impact exceeds the configured order limit.",
                estimatedBuyingPowerImpact,
                limits.MaxBuyingPowerUsage));
        }

        var currentDailyLoss = Math.Max(0m, -request.Portfolio.DailyPnL);
        if (currentDailyLoss > limits.MaxDailyLoss)
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.MaxDailyLoss,
                "Current daily loss exceeds the configured limit.",
                currentDailyLoss,
                limits.MaxDailyLoss));
        }

        AddGreekBreaches(request.Portfolio.ExistingGreeks + exposureDelta, breaches);

        return new RiskEvaluationResult(
            Guid.NewGuid(),
            breaches.Count == 0 ? RiskDecision.Approved : RiskDecision.Rejected,
            breaches,
            exposureDelta,
            estimatedMaxLoss,
            estimatedBuyingPowerImpact,
            DateTimeOffset.UtcNow);
    }

    /// <summary>A rejection carrying no risk figures, for an order the engine refused to price.</summary>
    /// <remarks>
    /// The zeros are not estimates and must never be read as any. <see cref="RiskDecision.Rejected"/>
    /// is the answer and the breach list is the reason; the fields are zero only because
    /// <see cref="RiskEvaluationResult"/> has nowhere to say "unknown".
    /// </remarks>
    private static RiskEvaluationResult Refused(IReadOnlyList<RiskLimitBreach> breaches) =>
        new(
            Guid.NewGuid(),
            RiskDecision.Rejected,
            breaches,
            GreeksVector.Zero,
            0m,
            0m,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Whether the leg sizes describe a structure the formulas below actually cover.
    /// </summary>
    /// <remarks>
    /// <c>OrderRequestValidator</c> checks the same things, but it lives in a different service on
    /// the far side of an HTTP hop: the component deciding whether capital may be committed does not
    /// get to assume its caller ran, or ran the same version of these rules.
    /// </remarks>
    private static bool HasPriceableQuantities(IReadOnlyList<OrderLegRequest> legs, List<RiskLimitBreach> breaches)
    {
        var priceable = true;

        foreach (var leg in legs)
        {
            if (leg.Quantity <= 0)
            {
                breaches.Add(new RiskLimitBreach(
                    RiskBreachCodes.NonPositiveLegQuantity,
                    "Every leg quantity must be positive; direction is carried by the leg side.",
                    leg.Quantity,
                    1m));

                priceable = false;
            }

            // The multiplier is every money figure's scale factor, so a zero one prices the whole
            // order — premium, width, exposure, buying power — at nothing. It is also a number the
            // caller supplies and the broker ignores: conIds identify the contract at TWS, so a
            // leg claiming a multiplier of 0 still trades its real size.
            if (leg.Contract.Multiplier <= 0)
            {
                breaches.Add(new RiskLimitBreach(
                    RiskBreachCodes.NonPositiveMultiplier,
                    "Every leg must carry a positive contract multiplier.",
                    leg.Contract.Multiplier,
                    1m));

                priceable = false;
            }
        }

        // Unequal quantities make a ratio spread, which is not a defined-risk structure and has no
        // closed form here: a 1x10 "vertical" is nine naked short calls, and the width-based formula
        // would have priced the whole thing at one spread's width — about $150 for unbounded loss.
        if (priceable && legs[0].Quantity != legs[1].Quantity)
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.UnequalLegQuantities,
                "V1 prices one-to-one two-leg structures only; a ratio spread is not defined-risk.",
                Math.Abs(legs[0].Quantity - legs[1].Quantity),
                0m));

            priceable = false;
        }

        return priceable;
    }

    /// <summary>One quote per leg, positionally, refusing anything the engine cannot honestly price.</summary>
    /// <remarks>
    /// The gateway completes a quote request partially on timeout rather than failing it, so a leg
    /// with no market comes back as a real <see cref="QuoteSnapshot"/> carrying Bid 0, Ask 0 and
    /// zeroed Greeks — and SPY options genuinely quote 0/0 before the open. Fed to the formulas
    /// below that produced net debit 0, max loss 0, exposure 0 and buying-power impact 0, so a
    /// ten-lot five-point credit vertical cleared a $2,500 per-order loss limit at an estimated $0
    /// and, with <c>Execution:Router=ibkr</c>, transmitted and filled at the open.
    /// <para><c>IbkrAccountClient</c> already refuses to trust all-zero Greeks and
    /// <c>PaperExecutionEngine</c> already fails an unquoted leg closed. The risk engine was the one
    /// component that priced them anyway.</para>
    /// </remarks>
    private static bool TryResolveLegQuotes(
        SubmitOrderRequest order,
        IReadOnlyList<QuoteSnapshot> quotes,
        List<RiskLimitBreach> breaches,
        out QuoteSnapshot[] legQuotes)
    {
        // Keyed on contract identity, never on the whole record: a broker-backed provider echoes
        // contracts carrying fields the inbound leg does not, so record equality misses every
        // lookup. The previous dictionary did exactly that, and answered each miss with a
        // TryGetValue-continue that dropped the leg's Greeks and premium to zero rather than
        // refusing. Last quote wins if a provider sends duplicates, as elsewhere.
        var quoteByContract = quotes
            .GroupBy(quote => quote.Contract.Key())
            .ToDictionary(group => group.Key, group => group.Last());

        var resolved = new QuoteSnapshot[order.Legs.Count];
        var priceable = true;

        for (var index = 0; index < order.Legs.Count; index++)
        {
            var leg = order.Legs[index];

            if (!quoteByContract.TryGetValue(leg.Contract.Key(), out var quote))
            {
                breaches.Add(Unpriceable(index, leg, "no quote was supplied for it"));
                priceable = false;
                continue;
            }

            if (UnpriceableReason(leg, quote) is { } reason)
            {
                breaches.Add(Unpriceable(index, leg, reason));
                priceable = false;
                continue;
            }

            resolved[index] = quote;
        }

        legQuotes = priceable ? resolved : [];

        return priceable;
    }

    /// <summary>Why a quote cannot price a leg, or null when it can.</summary>
    /// <remarks>
    /// The price test is side-specific rather than "no market at all", which is deliberately
    /// stricter. A zero bid on a leg being sold is not the conservative case: it prices the credit
    /// at nothing, which pushes the order's net debit across zero and so hands a credit spread the
    /// debit-spread formula — reporting the long leg's premium as the whole maximum loss and
    /// dropping the strike width entirely.
    /// </remarks>
    private static string? UnpriceableReason(OrderLegRequest leg, QuoteSnapshot quote)
    {
        if (leg.Side == OrderSide.Buy && quote.Ask <= 0m)
        {
            return "there is no offer to buy it against";
        }

        if (leg.Side == OrderSide.Sell && quote.Bid <= 0m)
        {
            return "there is no bid to sell it into";
        }

        // Every Greek at exactly zero is not a live option, it is the gateway's timeout snapshot: a
        // live option always carries non-zero gamma and vega. The four Greek exposure limits are
        // hard limits, so accepting the zeros would silently exempt the order from all of them.
        if (quote.Greeks is { Delta: 0m, Gamma: 0m, Theta: 0m, Vega: 0m })
        {
            return "its quote carries no Greeks";
        }

        return null;
    }

    private static RiskLimitBreach Unpriceable(int index, OrderLegRequest leg, string reason) =>
        new(
            RiskBreachCodes.UnpriceableLeg,
            $"Leg {index} ({leg.Contract.Underlying} {leg.Contract.Expiration:yyyy-MM-dd} " +
            $"{leg.Contract.Strike} {leg.Contract.Right}) cannot be priced: {reason}.",
            1m,
            0m);

    private void AddGreekBreaches(GreeksVector projectedExposure, List<RiskLimitBreach> breaches)
    {
        AddGreekBreach(RiskBreachCodes.MaxDelta, "delta", projectedExposure.Delta, limits.MaxAbsGreeks.Delta, breaches);
        AddGreekBreach(RiskBreachCodes.MaxGamma, "gamma", projectedExposure.Gamma, limits.MaxAbsGreeks.Gamma, breaches);
        AddGreekBreach(RiskBreachCodes.MaxTheta, "theta", projectedExposure.Theta, limits.MaxAbsGreeks.Theta, breaches);
        AddGreekBreach(RiskBreachCodes.MaxVega, "vega", projectedExposure.Vega, limits.MaxAbsGreeks.Vega, breaches);
    }

    private static void AddGreekBreach(
        string code,
        string greek,
        decimal actual,
        decimal limit,
        List<RiskLimitBreach> breaches)
    {
        var absoluteActual = Math.Abs(actual);
        if (absoluteActual <= limit)
        {
            return;
        }

        breaches.Add(new RiskLimitBreach(
            code,
            $"Projected absolute {greek} exposure exceeds the configured limit.",
            absoluteActual,
            limit));
    }

    private static GreeksVector CalculateExposureDelta(SubmitOrderRequest order, QuoteSnapshot[] legQuotes)
    {
        var exposure = GreeksVector.Zero;

        for (var index = 0; index < order.Legs.Count; index++)
        {
            var leg = order.Legs[index];
            var quote = legQuotes[index];
            var direction = leg.Side == OrderSide.Buy ? 1m : -1m;
            var multiplier = leg.Contract.Multiplier * leg.Quantity * direction;

            exposure += new GreeksVector(
                quote.Greeks.Delta * multiplier,
                quote.Greeks.Gamma * multiplier,
                quote.Greeks.Theta * multiplier,
                quote.Greeks.Vega * multiplier);
        }

        return exposure;
    }

    private static decimal EstimateMaxLoss(
        SubmitOrderRequest order,
        QuoteSnapshot[] legQuotes,
        List<RiskLimitBreach> breaches)
    {
        var netDebit = WorstCaseNetDebit(order, legQuotes);

        return order.Strategy switch
        {
            StrategyKind.Vertical or StrategyKind.Calendar or StrategyKind.Diagonal =>
                EstimateDefinedRiskSpreadMaxLoss(order, netDebit, breaches),

            StrategyKind.Straddle or StrategyKind.Strangle =>
                EstimateVolatilitySpreadMaxLoss(order, netDebit, breaches),

            // No default estimate. A StrategyKind with no formula here — an out-of-range value off
            // the wire, or a member added to the enum without one — used to fall through to the net
            // debit, and for a naked short strangle the net debit is a credit and therefore $0. A
            // missing formula has exactly one honest answer.
            _ => RefuseUnsupportedStrategy(order, breaches),
        };
    }

    private static decimal RefuseUnsupportedStrategy(SubmitOrderRequest order, List<RiskLimitBreach> breaches)
    {
        breaches.Add(new RiskLimitBreach(
            RiskBreachCodes.UnsupportedStrategy,
            $"No maximum-loss formula covers strategy '{order.Strategy}'.",
            (decimal)order.Strategy,
            0m));

        return 0m;
    }

    /// <summary>The worst net debit the order can be filled at, in account currency.</summary>
    /// <remarks>
    /// Positive is a debit paid and negative a credit received — the convention
    /// <c>SubmitOrderRequest.LimitPrice</c>, <c>PaperExecutionEngine</c>, and <c>IbkrOrderBuilder</c>
    /// already share.
    /// <para>A limit order authorises a fill at its limit price, not at whatever happened to be
    /// quoted when risk ran, and the limit can sit well through the market. The quote is indicative;
    /// the limit is what the order is actually allowed to do, so the worse of the two is the only
    /// figure that cannot under-state the position being authorised.</para>
    /// </remarks>
    private static decimal WorstCaseNetDebit(SubmitOrderRequest order, QuoteSnapshot[] legQuotes)
    {
        var quoted = 0m;

        for (var index = 0; index < order.Legs.Count; index++)
        {
            var leg = order.Legs[index];
            var quote = legQuotes[index];
            var price = leg.Side == OrderSide.Buy ? quote.Ask : quote.Bid;
            var signedPrice = leg.Side == OrderSide.Buy ? price : -price;

            quoted += signedPrice * leg.Contract.Multiplier * leg.Quantity;
        }

        if (order.OrderType is not (OrderType.Limit or OrderType.StopLimit) || order.LimitPrice is not { } limitPrice)
        {
            return quoted;
        }

        // LimitPrice is the net across the whole order in per-share terms — leg quantity is already
        // inside it, so only the contract multiplier converts it to money. The largest multiplier is
        // used because it authorises the most.
        var authorised = limitPrice * order.Legs.Max(leg => leg.Contract.Multiplier);

        return Math.Max(quoted, authorised);
    }

    /// <summary>
    /// Maximum loss for a vertical, calendar, or diagonal: the net debit plus whatever strike
    /// distance the short leg concedes to the long leg covering it.
    /// </summary>
    /// <remarks>
    /// Which side of a spread is exposed is a property of the strikes, not of the price. The
    /// previous version branched on the sign of the net debit — width-plus-debit for a credit, the
    /// debit alone otherwise — so any quote that pushed a credit spread across zero bought it the
    /// debit-spread formula and dropped the entire width from the estimate. A single missing bid on
    /// the short leg does precisely that.
    /// </remarks>
    private static decimal EstimateDefinedRiskSpreadMaxLoss(
        SubmitOrderRequest order,
        decimal netDebit,
        List<RiskLimitBreach> breaches)
    {
        if (HasUncoveredShortLeg(order))
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.UncoveredShortOption,
                "Short option leg is not covered by a compatible long leg of at least equal quantity.",
                1m,
                0m));
        }

        return Math.Max(0m, AdverseStrikeRisk(order) + netDebit);
    }

    private static decimal EstimateVolatilitySpreadMaxLoss(
        SubmitOrderRequest order,
        decimal netDebit,
        List<RiskLimitBreach> breaches)
    {
        if (order.Legs.Any(leg => leg.Side == OrderSide.Sell))
        {
            breaches.Add(new RiskLimitBreach(
                RiskBreachCodes.UncoveredShortVolatilitySpread,
                "Short straddles and strangles are rejected in v1 because loss is not bounded.",
                1m,
                0m));
        }

        // Long-only, so the whole risk is the premium paid — which the leg quantities are already
        // inside, via the multiplier-and-quantity terms of the net debit.
        return Math.Max(0m, netDebit);
    }

    /// <summary>The strike distance short legs concede to the long legs covering them, in currency.</summary>
    /// <remarks>
    /// Zero for a calendar (same strike) and for any spread whose long leg sits at the better
    /// strike; the full width for a credit vertical, or for a diagonal sold below its long call or
    /// above its long put. That width is the part of the loss the premium does not account for.
    /// </remarks>
    private static decimal AdverseStrikeRisk(SubmitOrderRequest order)
    {
        var risk = 0m;

        foreach (var shortLeg in order.Legs.Where(leg => leg.Side == OrderSide.Sell))
        {
            var longLeg = order.Legs.FirstOrDefault(leg =>
                leg.Side == OrderSide.Buy && leg.Contract.Right == shortLeg.Contract.Right);

            if (longLeg is null)
            {
                // Nothing covers it, so no width bounds it and there is no number to add.
                // HasUncoveredShortLeg has already refused the order.
                continue;
            }

            var conceded = shortLeg.Contract.Right == OptionRight.Call
                ? longLeg.Contract.Strike - shortLeg.Contract.Strike
                : shortLeg.Contract.Strike - longLeg.Contract.Strike;

            risk += Math.Max(0m, conceded) * shortLeg.Contract.Multiplier * shortLeg.Quantity;
        }

        return risk;
    }

    /// <summary>Whether any short contract is left without a long contract standing behind it.</summary>
    /// <remarks>
    /// Cover is counted in contracts, not in legs. The previous version asked only whether *a*
    /// compatible long existed, so one long call "covered" ten short calls and a 1x10 ratio calendar
    /// was approved at an estimated maximum loss of $0.
    /// <para>Internal so it can be tested directly: the leg-quantity guard in
    /// <see cref="Evaluate"/> refuses unequal quantities before this runs, which is the intended
    /// belt-and-braces ordering but leaves this unreachable from the public surface.</para>
    /// </remarks>
    internal static bool HasUncoveredShortLeg(SubmitOrderRequest order)
    {
        var legs = order.Legs;
        var longRemaining = new int[legs.Count];

        for (var index = 0; index < legs.Count; index++)
        {
            longRemaining[index] = legs[index].Side == OrderSide.Buy ? legs[index].Quantity : 0;
        }

        // Latest-expiring shorts claim cover first: a long only covers a short expiring on or before
        // it, so the most constrained short must take its capacity before an easier one spends it.
        var shortIndexes = Enumerable.Range(0, legs.Count)
            .Where(index => legs[index].Side == OrderSide.Sell)
            .OrderByDescending(index => legs[index].Contract.Expiration);

        foreach (var shortIndex in shortIndexes)
        {
            var shortLeg = legs[shortIndex];
            var uncovered = shortLeg.Quantity;

            // Nearest-expiring eligible long first, so a longer-dated long is kept for the shorts
            // that only it can cover. Materialised before any capacity is consumed below.
            int[] candidates =
            [
                .. Enumerable.Range(0, legs.Count)
                    .Where(index => longRemaining[index] > 0 && Covers(legs[index], shortLeg))
                    .OrderBy(index => legs[index].Contract.Expiration)
            ];

            foreach (var longIndex in candidates)
            {
                var claimed = Math.Min(uncovered, longRemaining[longIndex]);
                longRemaining[longIndex] -= claimed;
                uncovered -= claimed;

                if (uncovered == 0)
                {
                    break;
                }
            }

            if (uncovered > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Covers(OrderLegRequest longLeg, OrderLegRequest shortLeg) =>
        string.Equals(
            longLeg.Contract.Underlying,
            shortLeg.Contract.Underlying,
            StringComparison.OrdinalIgnoreCase) &&
        longLeg.Contract.Right == shortLeg.Contract.Right &&
        longLeg.Contract.Expiration >= shortLeg.Contract.Expiration;
}
