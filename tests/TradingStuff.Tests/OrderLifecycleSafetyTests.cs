using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.RiskService;

namespace TradingStuff.Tests;

/// <summary>
/// The order lifecycle's honesty properties: what the record claims about the broker has to be what
/// the broker said.
/// </summary>
/// <remarks>
/// Every test here was written against the defect it names and confirmed to fail with the fix
/// reverted, so it distinguishes a regression test from a restatement of the current code.
/// </remarks>
public sealed class OrderLifecycleSafetyTests
{
    // ---- cancel reaches the venue ---------------------------------------------------------------

    [Fact]
    public async Task Cancel_asks_the_venue_and_hands_it_the_brokers_own_order_id()
    {
        // Regression: CancelAsync loaded the order, set Status = Cancelled, and saved. Nothing was
        // ever sent anywhere, so with Execution:Router=ibkr the endpoint reported a dead order that
        // was still working at TWS and could still fill.
        var router = new RecordingOrderRouter(
            new RoutedOrderResult(OrderLifecycleStatus.Submitted, [], BrokerReference: "42"));
        var context = WorkflowContext.Create(router);
        var response = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Limit, 0.10m), CancellationToken.None);

        await context.Workflow.CancelAsync(response.OrderId, new CancelOrderRequest("operator"), CancellationToken.None);

        Assert.Equal(1, router.CancelCount);
        Assert.Equal(response.OrderId, router.LastCancel?.OrderId);
        Assert.Equal("42", router.LastCancel?.BrokerReference);
        Assert.Equal("operator", router.LastCancel?.Reason);
    }

    [Fact]
    public async Task A_cancel_the_broker_has_not_confirmed_is_not_recorded_as_cancelled()
    {
        // TWS confirms cancellation asynchronously, so the ordinary answer to a cancel request is
        // PendingCancel — which the gateway maps to Submitted precisely because such an order can
        // still fill. Recording Cancelled here would recreate the defect one layer up.
        var router = new RecordingOrderRouter(
            new RoutedOrderResult(OrderLifecycleStatus.Submitted, [], BrokerReference: "42"),
            new CancelOrderResult(Acknowledged: true, OrderLifecycleStatus.Submitted, [], "IBKR order 42 reports PendingCancel."));
        var context = WorkflowContext.Create(router);
        var response = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Limit, 0.10m), CancellationToken.None);

        var cancelled = await context.Workflow.CancelAsync(response.OrderId, new CancelOrderRequest("operator"), CancellationToken.None);

        Assert.Equal(OrderLifecycleStatus.Submitted, cancelled!.Status);
        Assert.Equal(OrderLifecycleStatus.Submitted, (await context.Repository.GetAsync(response.OrderId, CancellationToken.None))!.Status);
        Assert.Contains(cancelled.Events, @event => @event.Message.Contains("PendingCancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_order_that_filled_before_the_cancel_landed_is_recorded_as_filled()
    {
        var orderId = Guid.NewGuid();
        var brokerFills = new[]
        {
            new FillReport(Guid.NewGuid(), orderId, 0, 1, 2.05m, FillLiquidity.BrokerReported, DateTimeOffset.UtcNow),
            new FillReport(Guid.NewGuid(), orderId, 1, 1, 0.95m, FillLiquidity.BrokerReported, DateTimeOffset.UtcNow)
        };

        var router = new RecordingOrderRouter(
            new RoutedOrderResult(OrderLifecycleStatus.Submitted, [], BrokerReference: "42"),
            new CancelOrderResult(Acknowledged: true, OrderLifecycleStatus.Filled, brokerFills, "IBKR order 42 reports Filled."));
        var context = WorkflowContext.Create(router);
        var response = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Limit, 0.10m), CancellationToken.None);

        var cancelled = await context.Workflow.CancelAsync(response.OrderId, new CancelOrderRequest("too late"), CancellationToken.None);

        // The venue is the authority on its own order. Asking to cancel does not make it cancelled.
        Assert.Equal(OrderLifecycleStatus.Filled, cancelled!.Status);
        Assert.Equal(2, cancelled.Fills.Count);
    }

    [Fact]
    public async Task A_cancel_that_never_reached_the_venue_fails_loudly_and_changes_nothing()
    {
        var router = new RecordingOrderRouter(
            new RoutedOrderResult(OrderLifecycleStatus.Submitted, [], BrokerReference: "42"),
            new CancelOrderResult(Acknowledged: false, OrderLifecycleStatus.Submitted, [], "gateway unreachable"));
        var context = WorkflowContext.Create(router);
        var response = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Limit, 0.10m), CancellationToken.None);

        await Assert.ThrowsAsync<OrderCancelFailedException>(
            () => context.Workflow.CancelAsync(response.OrderId, new CancelOrderRequest("operator"), CancellationToken.None));

        var persisted = await context.Repository.GetAsync(response.OrderId, CancellationToken.None);

        // Still working, and recorded as still working — plus the failed attempt, so the audit trail
        // shows that someone tried.
        Assert.Equal(OrderLifecycleStatus.Submitted, persisted!.Status);
        Assert.Contains(persisted.Events, @event => @event.Message.Contains("gateway unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_ibkr_router_posts_the_cancel_to_the_gateways_own_endpoint()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(new
        {
            IbkrOrderId = 42,
            PermId = 7L,
            Status = OrderLifecycleStatus.Submitted,
            RawStatus = "PendingCancel",
            Fills = Array.Empty<FillReport>(),
            Message = (string?)null
        }));

        var router = new IbkrOrderRouter(
            new HttpClient(handler) { BaseAddress = new Uri("http://gateway") },
            ExecutionTestDoubles.Logger<IbkrOrderRouter>());

        var result = await router.CancelAsync(Guid.NewGuid(), "42", "operator", CancellationToken.None);

        Assert.Equal("/ibkr/orders/42/cancel", handler.LastRequestPath);
        Assert.True(result.Acknowledged);
        Assert.Equal(OrderLifecycleStatus.Submitted, result.Status);
        Assert.Contains("PendingCancel", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ibkr_router_refuses_to_claim_a_cancel_it_could_not_send()
    {
        // No broker id on record does not mean nothing is at the broker: the placement may have been
        // transmitted and its response lost. "I could not ask" must not render as "it is cancelled".
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var router = new IbkrOrderRouter(
            new HttpClient(handler) { BaseAddress = new Uri("http://gateway") },
            ExecutionTestDoubles.Logger<IbkrOrderRouter>());

        var result = await router.CancelAsync(Guid.NewGuid(), brokerReference: null, "operator", CancellationToken.None);

        Assert.False(result.Acknowledged);
        Assert.Null(handler.LastRequestPath);
        Assert.NotEqual(OrderLifecycleStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task The_ibkr_router_refuses_to_claim_a_cancel_the_gateway_rejected()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Not connected to TWS.")
        });

        var router = new IbkrOrderRouter(
            new HttpClient(handler) { BaseAddress = new Uri("http://gateway") },
            ExecutionTestDoubles.Logger<IbkrOrderRouter>());

        var result = await router.CancelAsync(Guid.NewGuid(), "42", "operator", CancellationToken.None);

        Assert.False(result.Acknowledged);
        Assert.NotEqual(OrderLifecycleStatus.Cancelled, result.Status);
    }

    // ---- replace is refused rather than faked ---------------------------------------------------

    [Fact]
    public async Task Replace_is_refused_for_a_venue_that_cannot_be_modified_and_the_record_is_untouched()
    {
        // Regression: the replace endpoint rewrote the recorded limit price of an order resting at
        // the broker at the old price, producing a record indistinguishable from a replace that
        // worked. An honest 409 is the v1 answer; the gateway has no modify endpoint to wire to.
        var router = new RecordingOrderRouter(
            new RoutedOrderResult(OrderLifecycleStatus.Submitted, [], BrokerReference: "42"),
            supportsReplace: false);
        var context = WorkflowContext.Create(router);
        var response = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Limit, 0.10m), CancellationToken.None);

        await Assert.ThrowsAsync<ReplaceNotSupportedException>(
            () => context.Workflow.ReplaceAsync(response.OrderId, new ReplaceOrderRequest(9.99m, null, null), CancellationToken.None));

        var persisted = await context.Repository.GetAsync(response.OrderId, CancellationToken.None);

        Assert.Equal(0.10m, persisted!.Request.LimitPrice);
        Assert.Equal(OrderLifecycleStatus.Submitted, persisted.Status);
        Assert.DoesNotContain(persisted.Events, @event => @event.Status == OrderLifecycleStatus.ReplaceRequested);
    }

    [Fact]
    public void Only_the_simulated_venue_claims_it_can_replace_an_order()
    {
        Assert.True(new PaperOrderRouter(new PaperExecutionEngine()).SupportsReplace);
        Assert.False(new IbkrOrderRouter(new HttpClient(), ExecutionTestDoubles.Logger<IbkrOrderRouter>()).SupportsReplace);
    }

    // ---- the record exists before the venue can ------------------------------------------------

    [Fact]
    public async Task The_order_record_exists_before_the_router_is_called()
    {
        // Regression: nothing was persisted until RouteAsync returned, so a crash between the gateway
        // accepting an order and the save left a live broker order with no record at all — no id, no
        // audit trail, absent from /orders, and nothing for a retry to key on.
        ExecutionOrder? visibleDuringRouting = null;

        var router = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Filled, []));
        var context = WorkflowContext.Create(router);

        router.OnRoute = async orderId =>
            visibleDuringRouting = await context.Repository.GetAsync(orderId, CancellationToken.None);

        await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Market), CancellationToken.None);

        Assert.NotNull(visibleDuringRouting);
        Assert.Equal(OrderLifecycleStatus.Submitted, visibleDuringRouting.Status);
    }

    [Fact]
    public async Task A_router_that_throws_leaves_a_reconcilable_record_rather_than_none()
    {
        var context = WorkflowContext.Create(new ThrowingOrderRouter());

        var failure = await Assert.ThrowsAsync<OrderRoutingFailedException>(
            () => context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Market), CancellationToken.None));

        var persisted = await context.Repository.GetAsync(failure.OrderId, CancellationToken.None);

        Assert.NotNull(persisted);

        // Submitted, not Failed: a transport failure here is ambiguous, and Failed would assert the
        // one thing nobody knows — that the venue has nothing.
        Assert.Equal(OrderLifecycleStatus.Submitted, persisted.Status);
        Assert.Contains(persisted.Events, @event => @event.Message.Contains("outcome unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_brokers_order_id_is_recorded_so_the_order_stays_cancellable()
    {
        var router = new RecordingOrderRouter(
            new RoutedOrderResult(OrderLifecycleStatus.Submitted, [], BrokerReference: "42"));
        var context = WorkflowContext.Create(router);

        var response = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Limit, 0.10m), CancellationToken.None);

        Assert.Equal("42", await context.Repository.GetBrokerReferenceAsync(response.OrderId, CancellationToken.None));
    }

    // ---- retries carry the id the gateway deduplicates on ---------------------------------------

    [Fact]
    public async Task A_resubmission_reuses_the_internal_order_id_instead_of_minting_a_new_one()
    {
        // Regression: the internal order id was a fresh Guid per submit attempt, so the gateway's
        // refusal to transmit an internal id twice — the last thing between a retried POST /orders
        // and two live broker orders — had nothing stable to recognise.
        var router = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Filled, []));
        var context = WorkflowContext.Create(router);
        var order = SampleOrders.VerticalSpread(OrderType.Market);

        var first = await context.Workflow.SubmitAsync(order, CancellationToken.None);
        var second = await context.Workflow.SubmitAsync(order, CancellationToken.None);

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(1, router.RouteCount);
        Assert.Equal(OrderLifecycleStatus.Filled, second.Status);
    }

    [Fact]
    public async Task A_resubmission_after_the_record_is_lost_still_carries_the_id_the_gateway_knows()
    {
        // The case the order repository cannot help with, and the reason the id is derived rather
        // than remembered: ExecutionService restarts with an empty in-memory store, the operator
        // retries, and the only thing that can still recognise the order is the gateway's persisted
        // internal→broker map — keyed on the id sent to it.
        var order = SampleOrders.VerticalSpread(OrderType.Market);

        var beforeRestart = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Filled, []));
        await WorkflowContext.Create(beforeRestart).Workflow.SubmitAsync(order, CancellationToken.None);

        var afterRestart = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Filled, []));
        await WorkflowContext.Create(afterRestart).Workflow.SubmitAsync(order, CancellationToken.None);

        Assert.Equal(beforeRestart.RoutedOrderIds[0], afterRestart.RoutedOrderIds[0]);
    }

    [Fact]
    public async Task An_unsettled_order_is_re_routed_under_the_same_id_without_re_running_risk()
    {
        // Re-routing is how the venue gets asked what it actually holds. Re-running risk is not:
        // the risk service remembers approvals by client order id, so the replay would come back
        // DUPLICATE_ORDER and stamp RiskRejected on an order that may be live at the broker.
        var router = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Submitted, []));
        var context = WorkflowContext.Create(router);
        var order = SampleOrders.VerticalSpread(OrderType.Limit, 0.10m);

        var first = await context.Workflow.SubmitAsync(order, CancellationToken.None);
        var second = await context.Workflow.SubmitAsync(order, CancellationToken.None);

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(2, router.RouteCount);
        Assert.Equal(router.RoutedOrderIds[0], router.RoutedOrderIds[1]);
        Assert.Equal(1, context.RiskClient.EvaluationCount);
    }

    [Fact]
    public async Task Distinct_client_order_ids_stay_distinct_orders()
    {
        var router = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Filled, []));
        var context = WorkflowContext.Create(router);

        var first = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Market), CancellationToken.None);
        var second = await context.Workflow.SubmitAsync(SampleOrders.VerticalSpread(OrderType.Market), CancellationToken.None);

        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal(2, router.RouteCount);
    }

    [Fact]
    public async Task An_order_with_no_client_order_id_gets_no_idempotency_and_that_is_the_callers_choice()
    {
        var router = new RecordingOrderRouter(new RoutedOrderResult(OrderLifecycleStatus.Filled, []));
        var context = WorkflowContext.Create(router);
        var order = SampleOrders.VerticalSpread(OrderType.Market) with { ClientOrderId = null };

        var first = await context.Workflow.SubmitAsync(order, CancellationToken.None);
        var second = await context.Workflow.SubmitAsync(order, CancellationToken.None);

        // Documented, not endorsed: with no idempotency key there is nothing to recognise, which is
        // why the workflow logs a warning for anything routed to a broker without one.
        Assert.NotEqual(first.OrderId, second.OrderId);
    }

    // ---- an unusable quote is not a free fill ---------------------------------------------------

    [Fact]
    public void A_buy_leg_with_no_offer_does_not_fill_at_zero()
    {
        // Regression: a market order was unconditionally executable and a buy filled at the ask, so
        // an ask of 0 — what an option with no book quotes, which is every SPY option pre-market —
        // recorded a real fill at $0.00.
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Market);
        var quotes = SampleOrders.Quotes(order)
            .Select((quote, index) => index == 0 ? quote with { Bid = 0m, Ask = 0m } : quote)
            .ToArray();

        var result = engine.Execute(Guid.NewGuid(), order, quotes);

        Assert.Equal(OrderLifecycleStatus.Failed, result.Status);
        Assert.Empty(result.Fills);
    }

    [Fact]
    public void A_sell_leg_with_no_bid_does_not_fill_at_zero()
    {
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.VerticalSpread(OrderType.Market);
        var quotes = SampleOrders.Quotes(order)
            .Select((quote, index) => index == 1 ? quote with { Bid = 0m } : quote)
            .ToArray();

        var result = engine.Execute(Guid.NewGuid(), order, quotes);

        Assert.Equal(OrderLifecycleStatus.Failed, result.Status);
        Assert.Empty(result.Fills);
    }

    [Fact]
    public void A_one_sided_market_still_fills_the_side_that_has_a_price()
    {
        // The check is per leg and per side, not "is this quote usable": a bid of 0.95 with no offer
        // can be sold into and cannot be bought from, and refusing both would fail closed on orders
        // a real venue would accept.
        var engine = new PaperExecutionEngine();
        var order = SampleOrders.ShortStrangle();
        var quotes = SampleOrders.Quotes(order).Select(quote => quote with { Ask = 0m }).ToArray();

        var result = engine.Execute(Guid.NewGuid(), order, quotes);

        Assert.Equal(OrderLifecycleStatus.Filled, result.Status);
        Assert.All(result.Fills, fill => Assert.Equal(0.95m, fill.Price));
    }

    // ---- the two settings that must agree -------------------------------------------------------

    [Theory]
    [InlineData("ibkr", "development")]
    [InlineData("ibkr", null)]
    [InlineData("ibkr", "ibrk")]
    [InlineData("IBKR", "")]
    public void Transmitting_real_orders_against_a_fabricated_portfolio_refuses_to_start(string? router, string? portfolioSource)
    {
        // Each setting degrades safely on its own and the pair does not: a typo in Portfolio:Source
        // leaves real orders being checked against a fixed buying power and a flat day, where
        // MAX_DAILY_LOSS cannot fire at all.
        var configuration = Configuration(router, portfolioSource);

        var failure = Assert.Throws<InvalidOperationException>(
            () => ExecutionSafetyConfiguration.EnsureRouterAndPortfolioAgree(configuration));

        Assert.Contains("Portfolio__Source=ibkr", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ibkr", "ibkr")]
    [InlineData("paper", "development")]
    [InlineData(null, null)]
    [InlineData("typo", "development")]
    public void Combinations_that_cannot_transmit_a_mischecked_order_start(string? router, string? portfolioSource) =>
        ExecutionSafetyConfiguration.EnsureRouterAndPortfolioAgree(Configuration(router, portfolioSource));

    // ---- risk evaluation is not a repeatable request --------------------------------------------

    [Fact]
    public async Task The_risk_client_makes_exactly_one_attempt()
    {
        // The standard resilience handler retries a 503, and /risk/evaluate-order is not repeatable:
        // an approval burns the client order id, so the retry meets the record the first attempt left
        // and an approved order comes back rejected with DUPLICATE_ORDER.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration(null, null));

        // AddServiceDefaults applies the standard resilience handler — which retries a 503 — to every
        // client in the host. Without this line the test passes whether or not the risk client opts
        // out of it, because there is nothing to opt out of: the first negative control run caught
        // exactly that, and the assertion below was measuring nothing.
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddRiskClient().ConfigurePrimaryHttpMessageHandler(() => handler);

        using var provider = services.BuildServiceProvider();
        var order = SampleOrders.VerticalSpread(OrderType.Market);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.GetRequiredService<IRiskClient>().EvaluateAsync(
            new RiskEvaluationRequest(order, SampleOrders.Portfolio(order.AccountId), SampleOrders.Quotes(order), DateTimeOffset.UtcNow),
            CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    private static IConfiguration Configuration(string? router, string? portfolioSource) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:Router"] = router,
                ["Portfolio:Source"] = portfolioSource,
                ["RiskService:BaseUrl"] = "http://riskservice"
            })
            .Build();

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
        };
}

/// <summary>An <see cref="ExecutionWorkflow"/> wired to test doubles, with the doubles kept reachable.</summary>
internal sealed record WorkflowContext(
    ExecutionWorkflow Workflow,
    InMemoryOrderRepository Repository,
    InMemoryExecutionEventPublisher Publisher,
    FakeRiskClient RiskClient)
{
    public static WorkflowContext Create(IOrderRouter router)
    {
        var repository = new InMemoryOrderRepository();
        var publisher = new InMemoryExecutionEventPublisher(ExecutionTestDoubles.Logger<InMemoryExecutionEventPublisher>());
        var riskClient = new FakeRiskClient(new PortfolioRiskEvaluator(new RiskLimits(
            MaxLossPerOrder: 10_000m,
            MaxBuyingPowerUsage: 10_000m,
            MaxContractsPerOrder: 20,
            MaxDailyLoss: 1_000m,
            MaxAbsGreeks: new GreeksVector(1_000m, 1_000m, 1_000m, 1_000m))));

        // Quotes are generated per request so a resubmission of the same order is quoted the same way
        // the first submission was.
        var workflow = new ExecutionWorkflow(
            new OrderRequestValidator(),
            new QuotingMarketDataClient(),
            riskClient,
            new FakePortfolioProvider(SampleOrders.Portfolio("DU1234567")),
            router,
            repository,
            publisher,
            ExecutionTestDoubles.Logger<ExecutionWorkflow>());

        return new WorkflowContext(workflow, repository, publisher, riskClient);
    }
}

internal static class ExecutionTestDoubles
{
    public static ILogger<T> Logger<T>() => LoggerFactory.Create(_ => { }).CreateLogger<T>();
}

internal sealed class FakeMarketDataClient(MarketDataQuoteResponse response) : IMarketDataClient
{
    public Task<MarketDataQuoteResponse> GetQuotesAsync(
        MarketDataQuoteRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(response);
}

/// <summary>Quotes whatever it is asked for, the way the deterministic provider does.</summary>
internal sealed class QuotingMarketDataClient : IMarketDataClient
{
    public Task<MarketDataQuoteResponse> GetQuotesAsync(
        MarketDataQuoteRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new MarketDataQuoteResponse(
            SampleOrders.QuotesForLegs(request.Legs),
            DateTimeOffset.UtcNow,
            "test-quotes"));
}

internal sealed class FakeRiskClient(PortfolioRiskEvaluator evaluator) : IRiskClient
{
    public int EvaluationCount { get; private set; }

    public Task<RiskEvaluationResult> EvaluateAsync(
        RiskEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        EvaluationCount++;

        return Task.FromResult(evaluator.Evaluate(request));
    }
}

internal sealed class FakePortfolioProvider(PortfolioSnapshot portfolio) : IPortfolioProvider
{
    public Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken) =>
        Task.FromResult(portfolio);
}

/// <summary>A venue that records what it was asked to do and answers however the test needs.</summary>
internal sealed class RecordingOrderRouter(
    RoutedOrderResult routeResult,
    CancelOrderResult? cancelResult = null,
    bool supportsReplace = true) : IOrderRouter
{
    private readonly List<Guid> _routedOrderIds = [];

    public string Name => "recording";

    public bool SupportsReplace { get; } = supportsReplace;

    public IReadOnlyList<Guid> RoutedOrderIds => _routedOrderIds;

    public int RouteCount => _routedOrderIds.Count;

    public int CancelCount { get; private set; }

    public (Guid OrderId, string? BrokerReference, string Reason)? LastCancel { get; private set; }

    /// <summary>Runs while the venue "holds" the order, so a test can observe the crash window.</summary>
    public Func<Guid, Task>? OnRoute { get; set; }

    public async Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken)
    {
        _routedOrderIds.Add(orderId);

        if (OnRoute is { } onRoute)
        {
            await onRoute(orderId);
        }

        return routeResult;
    }

    public Task<CancelOrderResult> CancelAsync(
        Guid orderId,
        string? brokerReference,
        string reason,
        CancellationToken cancellationToken)
    {
        CancelCount++;
        LastCancel = (orderId, brokerReference, reason);

        return Task.FromResult(cancelResult
                               ?? new CancelOrderResult(true, OrderLifecycleStatus.Cancelled, [], "cancelled"));
    }
}

/// <summary>A venue whose transport fails, leaving the order's outcome unestablished.</summary>
internal sealed class ThrowingOrderRouter : IOrderRouter
{
    public string Name => "throwing";

    public bool SupportsReplace => true;

    public Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("the gateway went away mid-request");

    public Task<CancelOrderResult> CancelAsync(
        Guid orderId,
        string? brokerReference,
        string reason,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("the gateway went away mid-request");
}

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    public string? LastRequestPath { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestPath = request.RequestUri?.AbsolutePath;

        return Task.FromResult(respond(request));
    }
}
