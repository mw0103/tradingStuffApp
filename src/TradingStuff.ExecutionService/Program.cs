using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<OrderRequestValidator>();
builder.Services.AddSingleton<PaperExecutionEngine>();
builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
builder.Services.AddSingleton<IPortfolioProvider, DevelopmentPortfolioProvider>();
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
