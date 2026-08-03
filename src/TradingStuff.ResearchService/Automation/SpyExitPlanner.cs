using Microsoft.Extensions.Options;
using TradingStuff.Contracts;

namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// One open structure the loop manages, grouped as a single closable unit.
/// </summary>
/// <param name="ExitKey">
/// The identity a closing order is claimed against, so the same structure cannot be handed two
/// closing orders on one trading date. It is derived entirely from what the broker reports — every
/// leg's right, strike and signed quantity, in strike order — so it is reproducible on the next pass
/// from a fresh portfolio read and needs nothing remembered in this process. A structure that has
/// PARTLY closed therefore has a different key from the one that was ordered, which is the correct
/// behaviour: the remainder is a different position and its own exit decision, not a duplicate.
/// </param>
public sealed record ManagedStructure(
    string Underlying,
    DateOnly Expiration,
    IReadOnlyList<PositionSnapshot> Legs,
    string ExitKey);

/// <summary>
/// Builds the closing order for an open managed spread. The reverse of whatever is open, priced to
/// cross, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This planner selects nothing.</b> The entry planners choose strikes; this one is handed the
/// position the broker reports and reverses it leg for leg. There is no branch in which it closes
/// part of a structure, closes a different strike, or rolls: a closing order that is not the exact
/// inverse of an open position leaves a leg behind, and a naked short leg is the one shape the whole
/// defined-risk premise exists to exclude.
/// </para>
/// <para>
/// <b>Only the shape it opened.</b> Anything that is not a two-leg, same-expiry, same-right,
/// opposite-sign, equal-size vertical is a named refusal rather than a best effort. Automation did
/// not construct that position and cannot know what closing it means; the refusal is recorded on
/// every pass, which is how it stays visible instead of being quietly handled.
/// </para>
/// <para>
/// <b>No price cap, deliberately.</b> The entry planners refuse a spread that costs more than their
/// declared maximum loss — a cap on what may be OPENED. Applying one here would be a rule that can
/// leave a position unclosed at expiry, and an uncloseable position is worse than an unattractive
/// closing price. The quotes still have to exist (a missing side is a refusal, as everywhere else),
/// and the risk service remains an independent gate on the order itself.
/// </para>
/// </remarks>
public sealed class SpyExitPlanner(
    MarketDataServiceClient marketData,
    IOptions<PaperAutomationOptions> options)
{
    /// <summary>
    /// Calendar days from a trading date to an expiration. Negative once the expiration has passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Calendar days, and both dates come from <c>ISessionClock</c>'s trading date rather than a
    /// wall clock</b> — the loop reads no local date anywhere and this is not the place to start.
    /// </para>
    /// <para>
    /// <b>The convention, pinned because it is invisible in the arithmetic.</b> SPY weeklies are
    /// PM-settled: the expiration DATE is the last trading day and the contract dies at that day's
    /// close. So a DTE of 0 means "expires at today's close, still tradeable right now", not
    /// "already gone", and a threshold of 0 would still close it. Nothing here needs the time of day,
    /// and an AM-settled series (SPX, never SPY — see
    /// <see cref="PaperAutomationOptions.Underlying"/>) would need it, which is why the convention is
    /// stated rather than assumed.
    /// </para>
    /// </remarks>
    public static int DaysToExpiration(DateOnly expiration, DateOnly tradingDate) =>
        expiration.DayNumber - tradingDate.DayNumber;

    /// <summary>Whether a position is due to be closed. The whole exit rule, in one comparison.</summary>
    /// <remarks>
    /// At or below, not below: a threshold of 7 closes a position with exactly 7 days left. An
    /// already-expired position (negative days) is also at or below, and is included on purpose —
    /// if one is still being reported, a closing order is what should be attempted, not silence.
    /// </remarks>
    public static bool IsDue(DateOnly expiration, DateOnly tradingDate, int threshold) =>
        DaysToExpiration(expiration, tradingDate) <= threshold;

    /// <summary>
    /// The open structures in this account that automation manages, earliest expiration first.
    /// </summary>
    /// <remarks>
    /// Grouped by expiration because that is what makes a set of legs one spread. Positions in other
    /// underlyings are not this loop's to close and are left entirely alone; a zero quantity is a
    /// closed position the broker is still reporting and is not a structure.
    /// </remarks>
    public static IReadOnlyList<ManagedStructure> ManagedStructures(
        IReadOnlyList<PositionSnapshot> positions, string underlying)
    {
        var managed = positions
            .Where(p => p.Quantity != 0
                        && string.Equals(p.Contract.Underlying, underlying, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return
        [
            .. managed
                .GroupBy(p => p.Contract.Expiration)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var legs = group.OrderBy(p => p.Contract.Strike).ToArray();

                    return new ManagedStructure(
                        underlying, group.Key, legs, BuildExitKey(underlying, group.Key, legs));
                }),
        ];
    }

    /// <summary>Builds the closing order for one structure, or says why there is not one.</summary>
    public async Task<OrderPlanResult> PlanCloseAsync(
        string accountId,
        ManagedStructure structure,
        DateOnly tradingDate,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var shape = ValidateShape(structure);

        if (shape is { } shapeFailure)
        {
            return OrderPlanResult.Refused(shapeFailure);
        }

        // The exact inverse: a short leg is bought back, a long leg is sold. PositionEffect.Close is
        // not decoration — it is what tells the rest of the plane this order reduces exposure rather
        // than adding a second spread on top of the first.
        var legs = structure.Legs
            .Select(position => new OrderLegRequest(
                position.Contract,
                position.Quantity < 0 ? OrderSide.Buy : OrderSide.Sell,
                Math.Abs(position.Quantity),
                PositionEffect.Close))
            .ToArray();

        var quotes = await marketData.GetQuotesAsync(legs, cancellationToken);
        var priced = ComputeMarketableClose(quotes.Quotes, legs, settings.MarketableBufferDollars);

        if (priced.Failure is { } pricingFailure)
        {
            return OrderPlanResult.Refused(pricingFailure);
        }

        var netLimit = priced.Net!.Value;
        var days = DaysToExpiration(structure.Expiration, tradingDate);

        var request = new SubmitOrderRequest(
            accountId,
            StrategyKind.Vertical,
            OrderType.Limit,
            TimeInForce.Day,
            legs,
            LimitPrice: netLimit,
            StopPrice: null,
            // Fresh per submission, for the reason SpyVerticalPlanner records: it is what lets the
            // gateway's duplicate guard recognise a retry of THIS order. Idempotency across passes is
            // the exit claim's job, not this id's.
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "paper-automation");

        var strikes = string.Join(
            "/", structure.Legs.Select(leg => leg.Contract.Strike.ToString("F0")));

        return OrderPlanResult.Planned(new PlannedOrder(
            request,
            netLimit,
            LimitPriceSources.ComputedMarketable,
            $"{structure.Underlying} {structure.Expiration:yyyy-MM-dd} {strikes} " +
            $"{structure.Legs[0].Contract.Right.ToString().ToLowerInvariant()} spread, closing " +
            $"{Math.Abs(structure.Legs[0].Quantity)} lot at net {netLimit:F2} " +
            $"({(netLimit >= 0m ? "debit" : "credit")}), {days} day(s) to expiration"));
    }

    /// <summary>
    /// Why this structure cannot be reversed as a single two-leg order, or null when it can.
    /// </summary>
    /// <remarks>
    /// Every clause here is a shape <c>OrderRequestValidator</c> would reject anyway; checking them
    /// first is what turns a 400 from another service into a decision row that names what is actually
    /// in the account.
    /// </remarks>
    internal static string? ValidateShape(ManagedStructure structure)
    {
        var legs = structure.Legs;

        if (legs.Count != 2)
        {
            return $"The open {structure.Underlying} {structure.Expiration:yyyy-MM-dd} position has {legs.Count} " +
                   "leg(s), not the two of the managed spread. Automation does not construct a closing order for a " +
                   "structure it did not open; close it by hand and the next pass will see a flat account.";
        }

        if (legs[0].Contract.Right != legs[1].Contract.Right)
        {
            return $"The open {structure.Underlying} {structure.Expiration:yyyy-MM-dd} position mixes a " +
                   $"{legs[0].Contract.Right} and a {legs[1].Contract.Right}, which is not the managed vertical.";
        }

        if (legs[0].Contract.Strike == legs[1].Contract.Strike)
        {
            return $"Both legs of the open {structure.Underlying} {structure.Expiration:yyyy-MM-dd} position are at " +
                   $"{legs[0].Contract.Strike:F2}; a vertical has two strikes.";
        }

        if (Math.Sign(legs[0].Quantity) == Math.Sign(legs[1].Quantity))
        {
            return $"Both legs of the open {structure.Underlying} {structure.Expiration:yyyy-MM-dd} position are on " +
                   $"the same side ({legs[0].Quantity:+#;-#;0} and {legs[1].Quantity:+#;-#;0}); the managed spread is " +
                   "one short leg covered by one long one.";
        }

        if (Math.Abs(legs[0].Quantity) != Math.Abs(legs[1].Quantity))
        {
            return $"The open {structure.Underlying} {structure.Expiration:yyyy-MM-dd} position is " +
                   $"{Math.Abs(legs[0].Quantity)}x{Math.Abs(legs[1].Quantity)}, a ratio rather than a spread. " +
                   "Reversing it as one order would leave an uncovered leg.";
        }

        return null;
    }

    /// <summary>
    /// The marketable net price for the closing order, or why it cannot be priced. Pure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signed convention is the one every layer below already shares (see
    /// <see cref="SpyShortVolPlanner"/>): a debit is positive, a credit negative. A leg that is being
    /// bought contributes its ask; a leg being sold contributes minus its bid. Closing a credit
    /// spread therefore prices as a debit and closing a debit spread as a credit, from one formula
    /// rather than two branches — which matters because this planner sees both, and a sign convention
    /// that has to be chosen per structure is one that gets chosen wrongly.
    /// </para>
    /// <para>
    /// <b>The buffer is ADDED and the rounding goes UP in both directions.</b> Paying a little more
    /// for a debit and accepting a little less for a credit are the same move — giving up value to
    /// cross the spread — and on the signed axis both are "further positive". Rounding down would
    /// turn a marketable limit into a resting one with nothing saying so, on the one order whose
    /// whole purpose is to actually get out.
    /// </para>
    /// </remarks>
    internal static (decimal? Net, string? Failure) ComputeMarketableClose(
        IReadOnlyList<QuoteSnapshot> quotes,
        IReadOnlyList<OrderLegRequest> legs,
        decimal buffer)
    {
        var byKey = new Dictionary<OptionContractKey, QuoteSnapshot>();

        foreach (var quote in quotes)
        {
            byKey[quote.Contract.Key()] = quote;
        }

        var natural = 0m;

        foreach (var leg in legs)
        {
            if (!byKey.TryGetValue(leg.Contract.Key(), out var quote))
            {
                return (null,
                    $"No quote came back for the {leg.Contract.Strike:F2} " +
                    $"{leg.Contract.Right.ToString().ToLowerInvariant()} leg, so the closing order cannot be priced.");
            }

            if (leg.Side == OrderSide.Buy)
            {
                if (quote.Ask <= 0m)
                {
                    return (null,
                        $"The {leg.Contract.Strike:F2} {leg.Contract.Right.ToString().ToLowerInvariant()} leg has no " +
                        $"offer (ask {quote.Ask:F2}) — there is nothing to buy it back against. Outside the regular " +
                        "session SPY options have no book at all.");
                }

                natural += quote.Ask;
            }
            else
            {
                if (quote.Bid <= 0m)
                {
                    return (null,
                        $"The {leg.Contract.Strike:F2} {leg.Contract.Right.ToString().ToLowerInvariant()} leg has no " +
                        $"bid (bid {quote.Bid:F2}) — there is nothing to sell it into. Outside the regular session " +
                        "SPY options have no book at all.");
                }

                natural -= quote.Bid;
            }
        }

        return (Math.Ceiling((natural + buffer) * 100m) / 100m, null);
    }

    /// <summary>
    /// The claim key: underlying, expiration, and every leg's right, strike and signed size.
    /// </summary>
    /// <remarks>
    /// Legs arrive in strike order, so the same position produces the same string on every pass and
    /// in every process. Nothing here is parsed back out — it exists only to be compared.
    /// </remarks>
    internal static string BuildExitKey(
        string underlying, DateOnly expiration, IReadOnlyList<PositionSnapshot> legs) =>
        string.Join(
            '|',
            [
                underlying.ToUpperInvariant(),
                expiration.ToString("yyyy-MM-dd"),
                .. legs.Select(leg =>
                    $"{(leg.Contract.Right == OptionRight.Put ? 'P' : 'C')}{leg.Contract.Strike:F2}x{leg.Quantity}"),
            ]);
}
