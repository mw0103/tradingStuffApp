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

        var netDebit = CalculateNetDebit(order, legQuotes);

        if (!IsExecutable(order, netDebit))
        {
            return new PaperExecutionResult(OrderLifecycleStatus.Submitted, []);
        }

        var fills = order.Legs
            .Select((leg, index) =>
            {
                var quote = legQuotes[index];
                var price = leg.Side == OrderSide.Buy ? quote.Ask : quote.Bid;

                return new FillReport(
                    Guid.NewGuid(),
                    orderId,
                    index,
                    leg.Quantity,
                    price,
                    FillLiquidity.Simulated,
                    DateTimeOffset.UtcNow);
            })
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

    private static decimal CalculateNetDebit(SubmitOrderRequest order, QuoteSnapshot[] legQuotes)
    {
        var netDebit = 0m;

        for (var index = 0; index < order.Legs.Count; index++)
        {
            var leg = order.Legs[index];
            var quote = legQuotes[index];
            var fillPrice = leg.Side == OrderSide.Buy ? quote.Ask : quote.Bid;
            var signedPrice = leg.Side == OrderSide.Buy ? fillPrice : -fillPrice;
            netDebit += signedPrice * leg.Quantity;
        }

        return netDebit;
    }
}

public sealed record PaperExecutionResult(
    OrderLifecycleStatus Status,
    IReadOnlyList<FillReport> Fills);
