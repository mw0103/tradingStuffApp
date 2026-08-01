using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Automation;

/// <summary>An order to submit, or the reason there is not one. Never both, and never neither.</summary>
public sealed record OrderPlanResult(PlannedOrder? Order, string? Failure)
{
    public static OrderPlanResult Planned(PlannedOrder order) => new(order, null);

    public static OrderPlanResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// Builds the one order shape this MVP knows: a 1-lot, defined-risk SPY call debit vertical.
/// </summary>
/// <remarks>
/// <para>
/// <b>SPY, not SPX/SPXW.</b> Every SPXW combo sent on 2026-07-31 was accepted by TWS and sat at
/// <c>PreSubmitted</c> indefinitely with no error and no <c>whyHeld</c> — at any price, including
/// <c>MKT</c>, inside regular hours, with and without <c>OutsideRth</c>, with precautionary settings
/// cleared. SPY combos on the identical code path filled immediately. The cause is account- or
/// routing-level and is an open question in <c>docs/STATE.md</c>; it is not something to fight
/// underneath an automation loop whose whole purpose is to demonstrably work.
/// </para>
/// <para>
/// <b>A debit vertical, because its maximum loss is the debit.</b> There is no scenario in which this
/// order loses more than what it paid, so <see cref="PaperAutomationOptions.MaxDebitDollars"/> is a
/// literal loss cap rather than a proxy for one. The risk service's limits are a second, independent
/// gate; this one exists so automation cannot ask for something it already knows is too large.
/// </para>
/// <para>
/// <b>A marketable limit, never <c>OrderType.Market</c>.</b> IBKR rejects or badly fills <c>MKT</c> on
/// multi-leg BAG orders, and a market order on a combo is the one shape whose fill price nobody can
/// predict — see the <c>ibkr</c> skill. The limit is the natural (long ask − short bid) plus a small
/// buffer, so it crosses the spread rather than resting behind it.
/// </para>
/// <para>
/// <b>Every failure is a named refusal.</b> No branch derives a spot from the contracts it got back,
/// substitutes a mid for a missing side, or falls back to a last price — the chain window says
/// whether it is spot-centred and the quotes say whether they are priceable, and when either says no
/// this returns a reason. That is not caution for its own sake: <c>NodeSelector</c> once
/// reconstructed a spot proxy from a degraded window's median strike and silently rebound all 54
/// research nodes to deep-OTM contracts that then reported full coverage.
/// </para>
/// </remarks>
public sealed class SpyVerticalPlanner(
    OptionChainClient chains,
    MarketDataServiceClient marketData,
    IOptions<PaperAutomationOptions> options,
    ILogger<SpyVerticalPlanner> logger)
{
    /// <param name="operatorLimitPrice">
    /// A limit supplied by a human on the manual endpoint, used INSTEAD of the computed marketable
    /// price. Null on every automated evaluation — the scheduled loop has no way to reach this
    /// parameter, and a row whose <c>limit_price_source</c> is <c>operator-supplied</c> was therefore
    /// not an automated decision.
    /// </param>
    public async Task<OrderPlanResult> PlanAsync(
        string accountId,
        DateOnly today,
        decimal? operatorLimitPrice,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var targetExpiration = today.AddDays(settings.TargetDaysToExpiration);

        var window = await chains.GetChainAsync(
            settings.Underlying, targetExpiration, settings.TradingClass, settings.MoneynessHalfWidth, cancellationToken);

        if (!window.SpotCentred || window.ReferencePrice is not { } reference || window.Expiration is not { } expiration)
        {
            return OrderPlanResult.Refused(
                $"No spot-centred {settings.Underlying} chain window near {targetExpiration:yyyy-MM-dd}: " +
                $"{window.Unavailable ?? "the gateway reported no reference price"}.");
        }

        var selection = SelectVertical(window.Contracts, reference, settings.SpreadWidthDollars);

        if (selection.Failure is { } selectionFailure)
        {
            return OrderPlanResult.Refused(selectionFailure);
        }

        var (longLeg, shortLeg) = (selection.Long!, selection.Short!);

        var legs = new OrderLegRequest[]
        {
            new(longLeg, OrderSide.Buy, settings.Quantity, PositionEffect.Open),
            new(shortLeg, OrderSide.Sell, settings.Quantity, PositionEffect.Open),
        };

        decimal limitPrice;
        string limitSource;

        if (operatorLimitPrice is { } supplied)
        {
            limitPrice = supplied;
            limitSource = LimitPriceSources.OperatorSupplied;

            logger.LogWarning(
                "Using an operator-supplied limit of {Limit:F2} for the {Underlying} {LongStrike}/{ShortStrike} " +
                "vertical. No quote was consulted for this price.",
                supplied, settings.Underlying, longLeg.Strike, shortLeg.Strike);
        }
        else
        {
            var quotes = await marketData.GetQuotesAsync(legs, cancellationToken);
            var priced = ComputeMarketableDebit(quotes.Quotes, longLeg, shortLeg, settings.MarketableBufferDollars);

            if (priced.Failure is { } pricingFailure)
            {
                return OrderPlanResult.Refused(pricingFailure);
            }

            limitPrice = priced.Debit!.Value;
            limitSource = LimitPriceSources.ComputedMarketable;
        }

        if (limitPrice <= 0m)
        {
            return OrderPlanResult.Refused(
                $"The computed limit of {limitPrice:F2} is not a debit. A credit on a long call vertical means " +
                "the quotes are inverted or stale; refusing rather than transmitting it.");
        }

        if (limitPrice > settings.MaxDebitDollars)
        {
            return OrderPlanResult.Refused(
                $"The {settings.Underlying} {longLeg.Strike:F0}/{shortLeg.Strike:F0} vertical prices at " +
                $"{limitPrice:F2}, above the {settings.MaxDebitDollars:F2} per-spread debit cap. The debit IS " +
                "the maximum loss, so this is a loss limit, not a preference.");
        }

        var request = new SubmitOrderRequest(
            accountId,
            StrategyKind.Vertical,
            OrderType.Limit,
            TimeInForce.Day,
            legs,
            LimitPrice: limitPrice,
            StopPrice: null,
            // A fresh id per submission. It is what makes the gateway's persisted duplicate guard and
            // ExecutionService's derived order id able to recognise a retry of THIS order; reusing one
            // across evaluations would make two genuinely different decisions collapse into one order.
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "paper-automation");

        return OrderPlanResult.Planned(new PlannedOrder(
            request,
            limitPrice,
            limitSource,
            $"{settings.Underlying} {expiration:yyyy-MM-dd} {longLeg.Strike:F0}/{shortLeg.Strike:F0} call debit " +
            $"vertical, {settings.Quantity} lot, limit {limitPrice:F2} (spot reference {reference:F2})"));
    }

    /// <summary>The two legs, or why there are not two. Pure: no chain fetch, no quotes, no clock.</summary>
    internal static (OptionContract? Long, OptionContract? Short, string? Failure) SelectVertical(
        IReadOnlyList<OptionContract> contracts, decimal reference, decimal spreadWidth)
    {
        var calls = contracts
            .Where(c => c.Right == OptionRight.Call)
            .OrderBy(c => c.Strike)
            .ToArray();

        if (calls.Length == 0)
        {
            return (null, null, "The chain window contains no calls.");
        }

        // The first strike at or above spot: an at- or just-out-of-the-money long. Chosen by an
        // explicit rule rather than "nearest", which is ambiguous at the midpoint between two strikes
        // and would pick a different leg on either side of a tick.
        if (Array.Find(calls, c => c.Strike >= reference) is not { } longLeg)
        {
            return (null, null,
                $"No call strike at or above the {reference:F2} reference is listed in the window " +
                $"(highest is {calls[^1].Strike:F2}); the window is not centred where it claims to be.");
        }

        var shortStrike = longLeg.Strike + spreadWidth;

        // An EXACT strike match, not the nearest above. A vertical whose width is not the width that
        // was asked for has a different maximum loss than the one that was checked against the cap.
        if (Array.Find(calls, c => c.Strike == shortStrike) is not { } shortLeg)
        {
            return (null, null,
                $"No call is listed at {shortStrike:F2}, so a {spreadWidth:F2}-wide vertical above " +
                $"{longLeg.Strike:F2} cannot be built from this window.");
        }

        return (longLeg, shortLeg, null);
    }

    /// <summary>
    /// The marketable debit, or why the spread cannot be priced. Pure.
    /// </summary>
    /// <remarks>
    /// Long ask minus short bid: the price at which both legs cross, plus a buffer so the combo is
    /// marketable rather than resting at the natural. A missing side is a refusal — never a mid,
    /// never a last, never a substituted zero. Legs are correlated on
    /// <see cref="OptionContractExtensions.Key"/>, never on the whole record: provider-enriched
    /// quotes carry fields the request legs do not, and a lookup keyed on the record throws the
    /// moment they differ.
    /// </remarks>
    internal static (decimal? Debit, string? Failure) ComputeMarketableDebit(
        IReadOnlyList<QuoteSnapshot> quotes,
        OptionContract longLeg,
        OptionContract shortLeg,
        decimal buffer)
    {
        var byKey = new Dictionary<OptionContractKey, QuoteSnapshot>();

        foreach (var quote in quotes)
        {
            byKey[quote.Contract.Key()] = quote;
        }

        if (!byKey.TryGetValue(longLeg.Key(), out var longQuote))
        {
            return (null, $"No quote came back for the long leg ({longLeg.Strike:F2} call).");
        }

        if (!byKey.TryGetValue(shortLeg.Key(), out var shortQuote))
        {
            return (null, $"No quote came back for the short leg ({shortLeg.Strike:F2} call).");
        }

        if (longQuote.Ask <= 0m)
        {
            return (null,
                $"The long leg ({longLeg.Strike:F2} call) has no offer (ask {longQuote.Ask:F2}) — there is nothing " +
                "to buy it against. Outside the regular session SPY options have no book at all.");
        }

        if (shortQuote.Bid <= 0m)
        {
            return (null,
                $"The short leg ({shortLeg.Strike:F2} call) has no bid (bid {shortQuote.Bid:F2}) — there is nothing " +
                "to sell it into. Outside the regular session SPY options have no book at all.");
        }

        var natural = longQuote.Ask - shortQuote.Bid;

        if (natural <= 0m)
        {
            return (null,
                $"The natural debit is {natural:F2}: the {longLeg.Strike:F2} ask ({longQuote.Ask:F2}) is at or below " +
                $"the {shortLeg.Strike:F2} bid ({shortQuote.Bid:F2}). A long call vertical cannot be a credit; " +
                "these quotes are inverted or stale.");
        }

        // Rounded UP to the cent. A buy limit rounded down is a limit that does not cross, which turns
        // a marketable order into a resting one without anything saying so.
        var debit = Math.Ceiling((natural + buffer) * 100m) / 100m;

        return (debit, null);
    }
}
