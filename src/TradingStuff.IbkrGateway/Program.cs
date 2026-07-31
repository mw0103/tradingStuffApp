using Microsoft.AspNetCore.Mvc;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<IbkrOptions>(builder.Configuration.GetSection(IbkrOptions.SectionName));

builder.Services.AddSingleton<IbkrRequestRegistry>();
builder.Services.AddSingleton<IbkrClientWrapper>();
builder.Services.AddSingleton<IbkrConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IbkrConnection>());
builder.Services.AddSingleton<IbkrMarketDataClient>();
builder.Services.AddSingleton<IbkrOrderTracker>();
builder.Services.AddSingleton<IbkrOrderClient>();

// Reports unhealthy while the socket is down, so Aspire shows the real state instead of "running".
builder.Services.AddHealthChecks()
    .AddCheck<IbkrConnectionHealthCheck>("ibkr-connection");

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/ibkr/status", (IbkrConnection connection) => Results.Ok(connection.GetStatus()))
    .RequireAuthorization();

app.MapPost("/ibkr/contracts/resolve", async (
        ResolveContractsRequest request,
        IbkrMarketDataClient client,
        CancellationToken cancellationToken) =>
    {
        if (request.Contracts.Count == 0)
        {
            return Results.BadRequest(new { error = "At least one contract is required." });
        }

        var resolved = new List<ResolvedContract>(request.Contracts.Count);

        foreach (var contract in request.Contracts)
        {
            try
            {
                var conId = await client.ResolveOptionConIdAsync(contract, cancellationToken);
                resolved.Add(new ResolvedContract(contract, conId, null));
            }
            catch (IbkrRequestException ex)
            {
                // One unresolvable leg should not hide the others; report per contract.
                resolved.Add(new ResolvedContract(contract, null, ex.Message));
            }
        }

        return Results.Ok(new ResolveContractsResponse(resolved));
    })
    .RequireAuthorization();

app.MapGet("/ibkr/options/chains/{underlying}", async (
        string underlying,
        [FromQuery] string? expiration,
        [FromQuery] int? window,
        [FromQuery] string? tradingClass,
        IbkrMarketDataClient client,
        CancellationToken cancellationToken) =>
    {
        DateOnly? target = DateOnly.TryParse(expiration, out var parsed) ? parsed : null;

        try
        {
            return Results.Ok(await client.GetOptionChainAsync(
                underlying,
                target,
                window,
                cancellationToken,
                tradingClass));
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR rejected the option chain request.",
                detail: ex.Message,
                statusCode: ex.IsPermanent ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?> { ["ibkrErrorCode"] = ex.ErrorCode });
        }
        catch (IbkrConnectionException ex)
        {
            return Results.Problem(
                title: "Not connected to TWS.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireAuthorization();

app.MapPost("/ibkr/options/quotes", async (
        IbkrQuoteRequest request,
        IbkrMarketDataClient client,
        CancellationToken cancellationToken) =>
    {
        if (request.Contracts.Count == 0)
        {
            return Results.BadRequest(new { error = "At least one contract is required." });
        }

        try
        {
            return Results.Ok(await client.GetQuotesAsync(request.Contracts, cancellationToken));
        }
        catch (IbkrRequestException ex)
        {
            // A broker rejection is not a bug in this service; report it as an upstream failure with
            // the TWS code intact rather than an opaque 500.
            return Results.Problem(
                title: "IBKR rejected the market data request.",
                detail: ex.Message,
                statusCode: ex.IsPermanent ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?> { ["ibkrErrorCode"] = ex.ErrorCode });
        }
        catch (IbkrConnectionException ex)
        {
            return Results.Problem(
                title: "Not connected to TWS.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireAuthorization();

// ---- orders -----------------------------------------------------------------------------------
// The only order-placing surface in the system. Gated by IbkrConnection.EnsureTradingPermitted().

app.MapPost("/ibkr/orders", async (
        PlaceOrderRequest request,
        IbkrOrderClient orders,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var state = await orders.PlaceAsync(request.InternalOrderId, request.Order, cancellationToken);

            return Results.Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            // Trading gate refused, or a leg had no resolvable conId.
            return Results.Problem(
                title: "Order was not placed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR rejected the order.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["ibkrErrorCode"] = ex.ErrorCode });
        }
        catch (IbkrConnectionException ex)
        {
            return Results.Problem(
                title: "Not connected to TWS.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireAuthorization();

app.MapGet("/ibkr/orders", (IbkrOrderClient orders) => Results.Ok(orders.All()))
    .RequireAuthorization();

// Reconciliation: what TWS itself still considers open, including orders this process never placed.
app.MapGet("/ibkr/orders/open", async (IbkrOrderClient orders, CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await orders.GetOpenOrdersAsync(cancellationToken));
        }
        catch (IbkrConnectionException ex)
        {
            return Results.Problem(
                title: "Not connected to TWS.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireAuthorization();

app.MapGet("/ibkr/orders/{ibkrOrderId:int}", (int ibkrOrderId, IbkrOrderClient orders) =>
        orders.Get(ibkrOrderId) is { } state ? Results.Ok(state) : Results.NotFound())
    .RequireAuthorization();

app.MapPost("/ibkr/orders/{ibkrOrderId:int}/cancel", (
        int ibkrOrderId,
        CancelOrderRequest request,
        IbkrOrderClient orders) =>
    {
        try
        {
            return orders.Cancel(ibkrOrderId, request.Reason) is { } state
                ? Results.Ok(state)
                : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Cancel refused.", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    })
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Contracts to resolve to IBKR conIds.</summary>
public sealed record ResolveContractsRequest(IReadOnlyList<OptionContract> Contracts);

public sealed record ResolvedContract(OptionContract Contract, int? ConId, string? Error);

public sealed record ResolveContractsResponse(IReadOnlyList<ResolvedContract> Resolved);

/// <summary>Contracts to quote. Unlike the market-data service this takes bare contracts, not legs.</summary>
public sealed record IbkrQuoteRequest(IReadOnlyList<OptionContract> Contracts);

/// <summary>
/// An order to route to IBKR. <paramref name="InternalOrderId"/> is the caller's own order id, so
/// broker fills come back already correlated to it.
/// </summary>
public sealed record PlaceOrderRequest(Guid InternalOrderId, SubmitOrderRequest Order);
