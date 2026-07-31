using Microsoft.Extensions.Logging;
using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.RiskService;

namespace TradingStuff.Tests;

public sealed class TradingWorkflowTests
{
    [Fact]
    public void Validator_accepts_vertical_spread()
    {
        var validator = new OrderRequestValidator();
        var order = SampleOrders.VerticalSpread(OrderType.Limit, limitPrice: 1.50m);

        var errors = validator.Validate(order);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validator_rejects_invalid_straddle_shape()
    {
        var validator = new OrderRequestValidator();
        var order = SampleOrders.VerticalSpread(OrderType.Market) with
        {
            Strategy = StrategyKind.Straddle
        };

        var errors = validator.Validate(order);

        Assert.Contains(errors, error => error.Contains("Straddles require", StringComparison.Ordinal));
    }

    [Fact]
    public void Risk_evaluator_rejects_projected_delta_breach()
    {
        var evaluator = new PortfolioRiskEvaluator(new RiskLimits(
            MaxLossPerOrder: 10_000m,
            MaxBuyingPowerUsage: 10_000m,
            MaxContractsPerOrder: 20,
            MaxDailyLoss: 1_000m,
            MaxAbsGreeks: new GreeksVector(5m, 100m, 100m, 100m)));

        var order = SampleOrders.VerticalSpread(OrderType.Market);
        var request = new RiskEvaluationRequest(
            order,
            SampleOrders.Portfolio(order.AccountId),
            SampleOrders.Quotes(order),
            DateTimeOffset.UtcNow);

        var result = evaluator.Evaluate(request);

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == "MAX_DELTA");
    }

    [Fact]
    public void Risk_evaluator_rejects_short_strangle()
    {
        var evaluator = new PortfolioRiskEvaluator(RiskLimits.DevelopmentDefaults);
        var order = SampleOrders.ShortStrangle();
        var request = new RiskEvaluationRequest(
            order,
            SampleOrders.Portfolio(order.AccountId),
            SampleOrders.Quotes(order),
            DateTimeOffset.UtcNow);

        var result = evaluator.Evaluate(request);

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == "UNCOVERED_SHORT_VOLATILITY_SPREAD");
    }

    [Fact]
    public void Paper_engine_fills_market_order()
    {
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Market);

        var result = engine.Execute(Guid.NewGuid(), order, SampleOrders.Quotes(order));

        Assert.Equal(OrderLifecycleStatus.Filled, result.Status);
        Assert.Equal(2, result.Fills.Count);
    }

    [Fact]
    public async Task Execution_workflow_completes_end_to_end_paper_trade()
    {
        var order = SampleOrders.VerticalSpread(OrderType.Market);
        var quoteResponse = new MarketDataQuoteResponse(
            SampleOrders.Quotes(order),
            DateTimeOffset.UtcNow,
            "test-quotes");

        var repository = new InMemoryOrderRepository();
        var publisher = new InMemoryExecutionEventPublisher(
            LoggerFactory.Create(_ => { }).CreateLogger<InMemoryExecutionEventPublisher>());

        var workflow = new ExecutionWorkflow(
            new OrderRequestValidator(),
            new FakeMarketDataClient(quoteResponse),
            new FakeRiskClient(new PortfolioRiskEvaluator(new RiskLimits(
                MaxLossPerOrder: 10_000m,
                MaxBuyingPowerUsage: 10_000m,
                MaxContractsPerOrder: 20,
                MaxDailyLoss: 1_000m,
                MaxAbsGreeks: new GreeksVector(1_000m, 1_000m, 1_000m, 1_000m)))),
            new FakePortfolioProvider(SampleOrders.Portfolio(order.AccountId)),
            new PaperExecutionEngine(),
            repository,
            publisher);

        var response = await workflow.SubmitAsync(order, CancellationToken.None);
        var persisted = await repository.GetAsync(response.OrderId, CancellationToken.None);

        Assert.Equal(OrderLifecycleStatus.Filled, response.Status);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted.Fills.Count);
        Assert.Contains(persisted.Events, @event => @event.Status == OrderLifecycleStatus.RiskApproved);
        Assert.Contains(publisher.List(), @event => @event.Name == nameof(OrderLifecycleStatus.Filled));
    }

    private sealed class FakeMarketDataClient(MarketDataQuoteResponse response) : IMarketDataClient
    {
        public Task<MarketDataQuoteResponse> GetQuotesAsync(
            MarketDataQuoteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class FakeRiskClient(PortfolioRiskEvaluator evaluator) : IRiskClient
    {
        public Task<RiskEvaluationResult> EvaluateAsync(
            RiskEvaluationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(evaluator.Evaluate(request));
    }

    private sealed class FakePortfolioProvider(PortfolioSnapshot portfolio) : IPortfolioProvider
    {
        public Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(portfolio);
    }
}

internal static class SampleOrders
{
    public static SubmitOrderRequest VerticalSpread(OrderType orderType, decimal? limitPrice = null)
    {
        var expiration = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(35));
        var longCall = new OptionContract("XYZ20260821C100", "XYZ", expiration, 100m, OptionRight.Call);
        var shortCall = new OptionContract("XYZ20260821C105", "XYZ", expiration, 105m, OptionRight.Call);

        return new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Vertical,
            orderType,
            TimeInForce.Day,
            [
                new OrderLegRequest(longCall, OrderSide.Buy, 1, PositionEffect.Open),
                new OrderLegRequest(shortCall, OrderSide.Sell, 1, PositionEffect.Open)
            ],
            LimitPrice: limitPrice,
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "test");
    }

    public static SubmitOrderRequest ShortStrangle()
    {
        var expiration = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(35));
        var shortCall = new OptionContract("XYZ20260821C110", "XYZ", expiration, 110m, OptionRight.Call);
        var shortPut = new OptionContract("XYZ20260821P90", "XYZ", expiration, 90m, OptionRight.Put);

        return new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Strangle,
            OrderType.Market,
            TimeInForce.Day,
            [
                new OrderLegRequest(shortCall, OrderSide.Sell, 1, PositionEffect.Open),
                new OrderLegRequest(shortPut, OrderSide.Sell, 1, PositionEffect.Open)
            ],
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "test");
    }

    public static PortfolioSnapshot Portfolio(string accountId) =>
        new(accountId, 50_000m, 0m, GreeksVector.Zero, []);

    public static IReadOnlyList<QuoteSnapshot> Quotes(SubmitOrderRequest order)
    {
        return order.Legs.Select((leg, index) =>
        {
            var isBuy = leg.Side == OrderSide.Buy;
            var absoluteDelta = index == 0 ? 0.80m : 0.20m;
            var delta = leg.Contract.Right == OptionRight.Call ? absoluteDelta : -absoluteDelta;

            return new QuoteSnapshot(
                Guid.NewGuid(),
                leg.Contract,
                isBuy ? 1.95m : 0.95m,
                isBuy ? 2.05m : 1.05m,
                isBuy ? 2.00m : 1.00m,
                new OptionGreeks(delta, 0.02m + index, -0.03m, 0.09m),
                DateTimeOffset.UtcNow,
                "test");
        }).ToArray();
    }
}
