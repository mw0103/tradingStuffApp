using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public sealed class PaperExecutionEngine
{
    public PaperExecutionResult Execute(Guid orderId, SubmitOrderRequest order, IReadOnlyList<QuoteSnapshot> quotes)
    {
        // Keyed on contract identity rather than the whole OptionContract record: a quote returned by
        // a broker-backed provider carries fields the inbound request leg does not, and record
        // equality would then miss every lookup. Last quote wins if a provider sends duplicates.
        var quoteByContract = quotes
            .GroupBy(quote => quote.Contract.Key())
            .ToDictionary(group => group.Key, group => group.Last());

        if (!TryResolveLegQuotes(order, quoteByContract, out var legQuotes))
        {
            // A leg we have no quote for cannot be priced, so it cannot be filled.
            return new PaperExecutionResult(OrderLifecycleStatus.Failed, []);
        }

        if (!TryResolveLegPrices(order, legQuotes, out var legPrices))
        {
            // Same reason as an absent quote: a leg that cannot be priced cannot be filled.
            return new PaperExecutionResult(OrderLifecycleStatus.Failed, []);
        }

        var netDebit = CalculateNetDebit(order, legPrices);

        if (!IsExecutable(order, netDebit))
        {
            return new PaperExecutionResult(OrderLifecycleStatus.Submitted, []);
        }

        var fills = order.Legs
            .Select((leg, index) => new FillReport(
                Guid.NewGuid(),
                orderId,
                index,
                leg.Quantity,
                legPrices[index],
                FillLiquidity.Simulated,
                DateTimeOffset.UtcNow))
            .ToArray();

        return new PaperExecutionResult(OrderLifecycleStatus.Filled, fills);
    }

    /// <summary>Resolves one quote per leg, positionally. Fails closed if any leg is unquoted.</summary>
    private static bool TryResolveLegQuotes(
        SubmitOrderRequest order,
        IReadOnlyDictionary<OptionContractKey, QuoteSnapshot> quoteByContract,
        out QuoteSnapshot[] legQuotes)
    {
        var resolved = new QuoteSnapshot[order.Legs.Count];

        for (var index = 0; index < order.Legs.Count; index++)
        {
            if (!quoteByContract.TryGetValue(order.Legs[index].Contract.Key(), out var quote))
            {
                legQuotes = [];
                return false;
            }

            resolved[index] = quote;
        }

        legQuotes = resolved;
        return true;
    }

    /// <summary>
    /// The price each leg would trade at: the ask for a buy, the bid for a sell. Fails closed when
    /// the side being traded has no usable price.
    /// </summary>
    /// <remarks>
    /// A non-positive price on the side of the book we would hit is not a price — it is the absence
    /// of one. An option with no offer quotes an ask of 0, and pairing that with
    /// <see cref="IsExecutable"/>'s "a market order always executes" produces a recorded fill at
    /// $0.00: a free long position, and one that a real venue would never have given. SPY options
    /// outside regular hours quote exactly this way (bid/ask 0 with no book at all), so it is the
    /// ordinary pre-market case rather than an exotic one.
    /// <para>
    /// Both sides are checked per leg rather than as a whole-quote "is this quote usable" test,
    /// because a one-sided market is genuinely tradable in one direction: a bid of 1.20 against an
    /// ask of 0 can be sold into and cannot be bought from.
    /// </para>
    /// </remarks>
    private static bool TryResolveLegPrices(
        SubmitOrderRequest order,
        QuoteSnapshot[] legQuotes,
        out decimal[] legPrices)
    {
        var resolved = new decimal[order.Legs.Count];

        for (var index = 0; index < order.Legs.Count; index++)
        {
            var price = order.Legs[index].Side == OrderSide.Buy ? legQuotes[index].Ask : legQuotes[index].Bid;

            if (price <= 0m)
            {
                legPrices = [];
                return false;
            }

            resolved[index] = price;
        }

        legPrices = resolved;
        return true;
    }

    private static bool IsExecutable(SubmitOrderRequest order, decimal netDebit) =>
        order.OrderType switch
        {
            OrderType.Market => true,
            OrderType.Limit => order.LimitPrice is { } limitPrice && netDebit <= limitPrice,
            OrderType.Stop => order.StopPrice is { } stopPrice && Math.Abs(netDebit) >= stopPrice,
            OrderType.StopLimit => order.StopPrice is { } stopPrice &&
                                   order.LimitPrice is { } limitPrice &&
                                   Math.Abs(netDebit) >= stopPrice &&
                                   netDebit <= limitPrice,
            _ => false
        };

    /// <summary>
    /// Net cost of the order at <paramref name="legPrices"/> — the same prices the fills are written
    /// at, so what the order is judged executable at and what it fills at cannot diverge.
    /// </summary>
    private static decimal CalculateNetDebit(SubmitOrderRequest order, decimal[] legPrices)
    {
        var netDebit = 0m;

        for (var index = 0; index < order.Legs.Count; index++)
        {
            var leg = order.Legs[index];
            var signedPrice = leg.Side == OrderSide.Buy ? legPrices[index] : -legPrices[index];
            netDebit += signedPrice * leg.Quantity;
        }

        return netDebit;
    }
}

public sealed record PaperExecutionResult(
    OrderLifecycleStatus Status,
    IReadOnlyList<FillReport> Fills);
