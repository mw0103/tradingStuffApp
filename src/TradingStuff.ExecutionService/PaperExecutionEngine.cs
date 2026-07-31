using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public sealed class PaperExecutionEngine
{
    public PaperExecutionResult Execute(Guid orderId, SubmitOrderRequest order, IReadOnlyList<QuoteSnapshot> quotes)
    {
        var quoteByContract = quotes.ToDictionary(quote => quote.Contract);
        var netDebit = CalculateNetDebit(order, quoteByContract);

        if (!IsExecutable(order, netDebit))
        {
            return new PaperExecutionResult(OrderLifecycleStatus.Submitted, []);
        }

        var fills = order.Legs
            .Select((leg, index) =>
            {
                var quote = quoteByContract[leg.Contract];
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

    private static decimal CalculateNetDebit(
        SubmitOrderRequest order,
        IReadOnlyDictionary<OptionContract, QuoteSnapshot> quoteByContract)
    {
        var netDebit = 0m;

        foreach (var leg in order.Legs)
        {
            var quote = quoteByContract[leg.Contract];
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
