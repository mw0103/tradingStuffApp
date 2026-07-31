using TradingStuff.Contracts;
using TradingStuff.ExecutionService;

namespace TradingStuff.Tests;

public sealed class PaperExecutionEngineTests
{
    [Fact]
    public void Fills_when_quotes_carry_broker_enriched_contracts()
    {
        // Regression: quotes were correlated to legs on the whole OptionContract record, so record
        // equality covered every property. A broker-backed provider echoes a contract carrying
        // fields the inbound leg does not (here a canonical Symbol and an explicit exchange), which
        // made every lookup miss and threw KeyNotFoundException mid-order.
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Market);

        var enrichedQuotes = SampleOrders.Quotes(order)
            .Select(quote => quote with
            {
                Contract = quote.Contract with
                {
                    Symbol = $"IBKR:{quote.Contract.Underlying}:{quote.Contract.Strike}",
                    Exchange = "CBOE",
                },
            })
            .ToArray();

        var result = engine.Execute(Guid.NewGuid(), order, enrichedQuotes);

        Assert.Equal(OrderLifecycleStatus.Filled, result.Status);
        Assert.Equal(2, result.Fills.Count);
    }

    [Fact]
    public void Matches_quotes_regardless_of_underlying_casing()
    {
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Market);

        var quotes = SampleOrders.Quotes(order)
            .Select(quote => quote with
            {
                Contract = quote.Contract with { Underlying = quote.Contract.Underlying.ToLowerInvariant() },
            })
            .ToArray();

        var result = engine.Execute(Guid.NewGuid(), order, quotes);

        Assert.Equal(OrderLifecycleStatus.Filled, result.Status);
    }

    [Fact]
    public void Fails_closed_when_a_leg_has_no_quote()
    {
        // Previously an unquoted leg threw KeyNotFoundException out of the workflow. An order that
        // cannot be priced must fail as an order, not as an unhandled exception.
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Market);
        var onlyFirstLeg = SampleOrders.Quotes(order).Take(1).ToArray();

        var result = engine.Execute(Guid.NewGuid(), order, onlyFirstLeg);

        Assert.Equal(OrderLifecycleStatus.Failed, result.Status);
        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Does_not_fill_a_limit_order_priced_through_the_market()
    {
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Limit, limitPrice: 0.10m);

        var result = engine.Execute(Guid.NewGuid(), order, SampleOrders.Quotes(order));

        Assert.Equal(OrderLifecycleStatus.Submitted, result.Status);
        Assert.Empty(result.Fills);
    }
}
