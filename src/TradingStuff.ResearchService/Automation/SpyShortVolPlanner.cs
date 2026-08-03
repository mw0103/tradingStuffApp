using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// Builds the structure the hypothesis is actually about: a 1-lot, defined-risk SPY put credit
/// spread — short volatility, premium received, maximum loss capped by the long wing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a put credit spread.</b> The research finding this planner exists to test is the
/// variance-risk-premium harvest: implied exceeds subsequent realized essentially always ex ante
/// (decision-layer run 2026-08-02), and the QCJ forecast's demonstrated value is ranking and
/// drawdown, not timing. The closest defined-risk, two-leg expression the existing execution
/// plane already understands is a credit vertical; the put side is where the premium is richest
/// (index skew) and where a short-vol position pays for the risk it actually carries. An iron
/// condor is the neutral version but needs four legs and a new <see cref="StrategyKind"/>;
/// two legs through the proven path beats four through an unproven one for a plumbing test.
/// </para>
/// <para>
/// <b>The credit is a NEGATIVE net limit price, and every layer already agrees on that.</b>
/// <c>IbkrOrderBuilder</c> submits combos as "BUY as defined, signed net price";
/// <c>PaperExecutionEngine.IsExecutable</c> compares signed net debits; and
/// <c>PortfolioRiskEvaluator</c> documents positive-debit/negative-credit as the shared
/// convention, prices a credit spread's maximum loss as the strike width plus the (negative)
/// credit, and refuses any spread with an uncovered short leg. This planner introduces no new
/// convention — it is the first caller of one that was already designed in.
/// </para>
/// <para>
/// <b>Same refusal discipline as <see cref="SpyVerticalPlanner"/>.</b> No derived spots, no
/// substituted mids, no fallback prices: the chain window says whether it is spot-centred, the
/// quotes say whether both legs are priceable, and anything else is a named refusal.
/// </para>
/// </remarks>
public sealed class SpyShortVolPlanner(
    OptionChainClient chains,
    MarketDataServiceClient marketData,
    IOptions<PaperAutomationOptions> options,
    ILogger<SpyShortVolPlanner> logger)
{
    public async Task<OrderPlanResult> PlanAsync(
        string accountId,
        DateOnly today,
        decimal? operatorLimitPrice,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var targetExpiration = today.AddDays(settings.ShortVolTargetDaysToExpiration);

        var window = await chains.GetChainAsync(
            settings.Underlying, targetExpiration, settings.TradingClass,
            settings.ShortVolMoneynessHalfWidth, cancellationToken);

        if (!window.SpotCentred || window.ReferencePrice is not { } reference || window.Expiration is not { } expiration)
        {
            return OrderPlanResult.Refused(
                $"No spot-centred {settings.Underlying} chain window near {targetExpiration:yyyy-MM-dd}: " +
                $"{window.Unavailable ?? "the gateway reported no reference price"}.");
        }

        var selection = SelectPutCreditSpread(
            window.Contracts, reference, settings.ShortVolOtmOffsetFraction, settings.SpreadWidthDollars);

        if (selection.Failure is { } selectionFailure)
        {
            return OrderPlanResult.Refused(selectionFailure);
        }

        var (shortLeg, longLeg) = (selection.Short!, selection.Long!);

        var legs = new OrderLegRequest[]
        {
            new(shortLeg, OrderSide.Sell, settings.Quantity, PositionEffect.Open),
            new(longLeg, OrderSide.Buy, settings.Quantity, PositionEffect.Open),
        };

        decimal netLimit;
        string limitSource;

        // The NBBO the limit was computed from, carried out to the persisted record. Null on the
        // operator-supplied branch because no quote was read there — see PlannedOrder.LegQuotes.
        IReadOnlyList<PlannedLegQuote>? legQuotes = null;

        if (operatorLimitPrice is { } supplied)
        {
            netLimit = supplied;
            limitSource = LimitPriceSources.OperatorSupplied;

            logger.LogWarning(
                "Using an operator-supplied net limit of {Limit:F2} for the {Underlying} {Short}/{Long} put credit " +
                "spread. No quote was consulted for this price.",
                supplied, settings.Underlying, shortLeg.Strike, longLeg.Strike);
        }
        else
        {
            var quotes = await marketData.GetQuotesAsync(legs, cancellationToken);
            var priced = ComputeMarketableCredit(quotes.Quotes, shortLeg, longLeg, settings.MarketableBufferDollars);

            if (priced.Failure is { } pricingFailure)
            {
                return OrderPlanResult.Refused(pricingFailure);
            }

            // The shared signed convention: a credit received is a NEGATIVE net price.
            netLimit = -priced.Credit!.Value;
            limitSource = LimitPriceSources.ComputedMarketable;

            // Captured AFTER pricing succeeded and from the same quote set the price came from, so
            // the record cannot disagree with the limit it accompanies. Observation only.
            legQuotes = CaptureLegQuotes(quotes.Quotes, shortLeg, longLeg);
        }

        if (netLimit >= 0m)
        {
            return OrderPlanResult.Refused(
                $"The net limit of {netLimit:F2} is not a credit. A put credit spread that pays nothing carries " +
                "its full downside for free premium that does not exist; the quotes are inverted or stale.");
        }

        // Maximum loss per share: the strike width minus the credit received. This is the number
        // the cap constrains — the risk service will recompute its own version independently.
        var width = shortLeg.Strike - longLeg.Strike;
        var maxLossPerShare = width + netLimit;

        if (maxLossPerShare > settings.ShortVolMaxRiskDollars)
        {
            return OrderPlanResult.Refused(
                $"The {settings.Underlying} {shortLeg.Strike:F0}/{longLeg.Strike:F0} put credit spread risks " +
                $"{maxLossPerShare:F2} per share (width {width:F2} less credit {-netLimit:F2}), above the " +
                $"{settings.ShortVolMaxRiskDollars:F2} cap. The cap is a loss limit, not a preference.");
        }

        var request = new SubmitOrderRequest(
            accountId,
            StrategyKind.Vertical,
            OrderType.Limit,
            TimeInForce.Day,
            legs,
            LimitPrice: netLimit,
            StopPrice: null,
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "paper-automation");

        return OrderPlanResult.Planned(new PlannedOrder(
            request,
            netLimit,
            limitSource,
            $"{settings.Underlying} {expiration:yyyy-MM-dd} {shortLeg.Strike:F0}/{longLeg.Strike:F0} put credit " +
            $"spread, {settings.Quantity} lot, net {netLimit:F2} (credit {-netLimit:F2}, max loss " +
            $"{maxLossPerShare:F2}/share, spot reference {reference:F2})",
            legQuotes));
    }

    /// <summary>
    /// The two legs' NBBO as read, in short-then-long order. Pure.
    /// </summary>
    /// <remarks>
    /// Only quotes that were actually matched to a leg are returned — a leg with no quote is absent
    /// rather than represented by zeros, and it cannot happen on the path that calls this, since
    /// <see cref="ComputeMarketableCredit"/> has already refused when either side is missing. The
    /// side strings are the planner's own intent ("SELL" the short put, "BUY" the wing), because a
    /// bid/ask pair means something different depending on which way the leg is going.
    /// </remarks>
    internal static IReadOnlyList<PlannedLegQuote> CaptureLegQuotes(
        IReadOnlyList<QuoteSnapshot> quotes,
        OptionContract shortLeg,
        OptionContract longLeg)
    {
        var byKey = new Dictionary<OptionContractKey, QuoteSnapshot>();

        foreach (var quote in quotes)
        {
            byKey[quote.Contract.Key()] = quote;
        }

        var captured = new List<PlannedLegQuote>(2);

        foreach (var (leg, side) in new[] { (shortLeg, OrderSide.Sell), (longLeg, OrderSide.Buy) })
        {
            if (byKey.TryGetValue(leg.Key(), out var quote))
            {
                captured.Add(new PlannedLegQuote(
                    leg.Underlying,
                    leg.Expiration,
                    leg.Strike,
                    leg.Right.ToString(),
                    side.ToString(),
                    quote.Bid,
                    quote.Ask,
                    quote.Last,
                    quote.CapturedAt,
                    quote.Source));
            }
        }

        return captured;
    }

    /// <summary>
    /// The two legs, or why there are not two. Pure. The SHORT put sits at the last strike at or
    /// below <c>reference * (1 - otmOffsetFraction)</c>; the LONG wing sits exactly
    /// <paramref name="spreadWidth"/> below it.
    /// </summary>
    /// <remarks>
    /// Exact-width matching for the same reason the debit planner insists on it: a spread whose
    /// width is not the width that was asked for has a different maximum loss than the one that
    /// was checked against the cap.
    /// </remarks>
    internal static (OptionContract? Short, OptionContract? Long, string? Failure) SelectPutCreditSpread(
        IReadOnlyList<OptionContract> contracts,
        decimal reference,
        decimal otmOffsetFraction,
        decimal spreadWidth)
    {
        var puts = contracts
            .Where(c => c.Right == OptionRight.Put)
            .OrderBy(c => c.Strike)
            .ToArray();

        if (puts.Length == 0)
        {
            return (null, null, "The chain window contains no puts.");
        }

        var shortTarget = reference * (1m - otmOffsetFraction);

        // The LAST strike at or below the target: out-of-the-money by at least the declared
        // offset, never inside it. Chosen by an explicit rule, not "nearest".
        var shortLeg = puts.LastOrDefault(c => c.Strike <= shortTarget);

        if (shortLeg is null)
        {
            return (null, null,
                $"No put strike at or below the {shortTarget:F2} short target ({otmOffsetFraction:P1} under the " +
                $"{reference:F2} reference) is listed in the window (lowest is {puts[0].Strike:F2}).");
        }

        var longStrike = shortLeg.Strike - spreadWidth;

        if (Array.Find(puts, c => c.Strike == longStrike) is not { } longLeg)
        {
            return (null, null,
                $"No put is listed at {longStrike:F2}, so a {spreadWidth:F2}-wide credit spread below " +
                $"{shortLeg.Strike:F2} cannot be built from this window.");
        }

        return (shortLeg, longLeg, null);
    }

    /// <summary>
    /// The marketable credit, or why the spread cannot be priced. Pure.
    /// </summary>
    /// <remarks>
    /// Short bid minus long ask: the price at which both legs cross. The buffer is SUBTRACTED —
    /// giving up a little credit is what makes a credit order marketable, the mirror of the debit
    /// planner adding it. Rounded DOWN to the cent for the same reason the debit rounds up: the
    /// rounding must never move the limit to the passive side of what was computed.
    /// </remarks>
    internal static (decimal? Credit, string? Failure) ComputeMarketableCredit(
        IReadOnlyList<QuoteSnapshot> quotes,
        OptionContract shortLeg,
        OptionContract longLeg,
        decimal buffer)
    {
        var byKey = new Dictionary<OptionContractKey, QuoteSnapshot>();

        foreach (var quote in quotes)
        {
            byKey[quote.Contract.Key()] = quote;
        }

        if (!byKey.TryGetValue(shortLeg.Key(), out var shortQuote))
        {
            return (null, $"No quote came back for the short leg ({shortLeg.Strike:F2} put).");
        }

        if (!byKey.TryGetValue(longLeg.Key(), out var longQuote))
        {
            return (null, $"No quote came back for the long leg ({longLeg.Strike:F2} put).");
        }

        if (shortQuote.Bid <= 0m)
        {
            return (null,
                $"The short leg ({shortLeg.Strike:F2} put) has no bid (bid {shortQuote.Bid:F2}) — there is nothing " +
                "to sell it into. Outside the regular session SPY options have no book at all.");
        }

        if (longQuote.Ask <= 0m)
        {
            return (null,
                $"The long leg ({longLeg.Strike:F2} put) has no offer (ask {longQuote.Ask:F2}) — there is nothing " +
                "to buy the wing against. Outside the regular session SPY options have no book at all.");
        }

        var natural = shortQuote.Bid - longQuote.Ask;

        if (natural <= 0m)
        {
            return (null,
                $"The natural credit is {natural:F2}: the {shortLeg.Strike:F2} bid ({shortQuote.Bid:F2}) is at or " +
                $"below the {longLeg.Strike:F2} ask ({longQuote.Ask:F2}). A wider-strike put cannot be worth less " +
                "than its wing; these quotes are inverted or stale.");
        }

        var marketable = natural - buffer;

        if (marketable <= 0m)
        {
            return (null,
                $"The natural credit {natural:F2} does not survive the {buffer:F2} marketable buffer. A spread " +
                "that only exists at the passive price is not worth chasing.");
        }

        // Rounded DOWN to the cent: asking for less credit is the marketable direction.
        var credit = Math.Floor(marketable * 100m) / 100m;

        if (credit <= 0m)
        {
            return (null, $"The marketable credit rounds to {credit:F2}; nothing is being paid for this spread.");
        }

        return (credit, null);
    }
}
