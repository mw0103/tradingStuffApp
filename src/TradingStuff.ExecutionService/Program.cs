using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<OrderRequestValidator>();
builder.Services.AddSingleton<PaperExecutionEngine>();
builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
builder.Services.AddSingleton<DevelopmentPortfolioProvider>();
builder.Services.AddSingleton<InMemoryExecutionEventPublisher>();
builder.Services.AddSingleton<IExecutionEventPublisher>(sp => sp.GetRequiredService<InMemoryExecutionEventPublisher>());
builder.Services.AddSingleton<IPublishedExecutionEventStore>(sp => sp.GetRequiredService<InMemoryExecutionEventPublisher>());
builder.Services.AddScoped<ExecutionWorkflow>();

builder.Services.AddHttpClient<IRiskClient, HttpRiskClient>((sp, client) =>
{
    ServiceClientConfiguration.ConfigureInternalClient(
        client,
        sp.GetRequiredService<IConfiguration>(),
        "RiskService:BaseUrl",
        "http://riskservice");
});

builder.Services.AddHttpClient<IMarketDataClient, HttpMarketDataClient>((sp, client) =>
{
    ServiceClientConfiguration.ConfigureInternalClient(
        client,
        sp.GetRequiredService<IConfiguration>(),
        "MarketDataService:BaseUrl",
        "http://marketdataservice");
});

builder.Services.AddHttpClient<IbkrOrderRouter>((sp, client) =>
{
    ServiceClientConfiguration.ConfigureInternalClient(
        client,
        sp.GetRequiredService<IConfiguration>(),
        "IbkrGateway:BaseUrl",
        "http://ibkrgateway");
})
    // Order placement is not idempotent. The default resilience handler retries on a 10s per-attempt
    // timeout, and a combo that rests longer than that would be transmitted to the broker twice.
    // 60s comfortably exceeds the gateway's 20s order settle timeout.
    .DisableAutomaticRetries(TimeSpan.FromSeconds(60));

builder.Services.AddHttpClient<IbkrPortfolioProvider>((sp, client) =>
{
    ServiceClientConfiguration.ConfigureInternalClient(
        client,
        sp.GetRequiredService<IConfiguration>(),
        "IbkrGateway:BaseUrl",
        "http://ibkrgateway");
});

// Fixed development figures unless real account data is explicitly opted into. Routing orders to
// IBKR while leaving this on 'development' evaluates real orders against fabricated buying power,
// so the two settings belong together.
builder.Services.AddSingleton<IPortfolioProvider>(sp =>
{
    var source = sp.GetRequiredService<IConfiguration>()["Portfolio:Source"];

    if (!PortfolioSources.UsesIbkr(source))
    {
        return sp.GetRequiredService<DevelopmentPortfolioProvider>();
    }

    sp.GetRequiredService<ILogger<Program>>().LogInformation(
        "Portfolio:Source is '{Source}': risk inputs come from the real IBKR account.",
        PortfolioSources.Ibkr);

    return sp.GetRequiredService<IbkrPortfolioProvider>();
});

builder.Services.AddSingleton<PaperOrderRouter>();

// Simulated fills unless real routing is explicitly opted into. An unrecognised value stays on
// paper rather than silently sending orders to a broker.
builder.Services.AddSingleton<IOrderRouter>(sp =>
{
    var router = sp.GetRequiredService<IConfiguration>()["Execution:Router"];

    if (!OrderRouters.UsesIbkr(router))
    {
        return sp.GetRequiredService<PaperOrderRouter>();
    }

    sp.GetRequiredService<ILogger<Program>>().LogWarning(
        "Execution:Router is '{Router}': approved orders will be sent to IBKR through the gateway.",
        OrderRouters.Ibkr);

    return sp.GetRequiredService<IbkrOrderRouter>();
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/orders", async (
        SubmitOrderRequest request,
        ExecutionWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var response = await workflow.SubmitAsync(request, cancellationToken);
            return Results.Created($"/orders/{response.OrderId}", response);
        }
        catch (OrderValidationException exception)
        {
            return Results.BadRequest(new { errors = exception.Errors });
        }
        catch (PortfolioUnavailableException exception)
        {
            // No order is placed. Approving one against a portfolio that could not be read would be
            // the same failure as approving it against fabricated numbers.
            return Results.Problem(
                title: "Portfolio data is unavailable, so the order was not evaluated.",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireAuthorization();

app.MapGet("/orders", async (IOrderRepository repository, CancellationToken cancellationToken) =>
        Results.Ok(await repository.ListAsync(cancellationToken)))
    .RequireAuthorization();

app.MapGet("/orders/{orderId:guid}", async (
        Guid orderId,
        ExecutionWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        var order = await workflow.GetAsync(orderId, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order);
    })
    .RequireAuthorization();

app.MapGet("/orders/{orderId:guid}/events", async (
        Guid orderId,
        ExecutionWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        var order = await workflow.GetAsync(orderId, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order.Events);
    })
    .RequireAuthorization();

app.MapPost("/orders/{orderId:guid}/cancel", async (
        Guid orderId,
        CancelOrderRequest request,
        ExecutionWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        var order = await workflow.CancelAsync(orderId, request, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order);
    })
    .RequireAuthorization();

app.MapPost("/orders/{orderId:guid}/replace", async (
        Guid orderId,
        ReplaceOrderRequest request,
        ExecutionWorkflow workflow,
        CancellationToken cancellationToken) =>
    {
        var order = await workflow.ReplaceAsync(orderId, request, cancellationToken);
        return order is null ? Results.NotFound() : Results.Ok(order);
    })
    .RequireAuthorization();

app.MapGet("/events/published", (IPublishedExecutionEventStore eventStore) => Results.Ok(eventStore.List()))
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();
