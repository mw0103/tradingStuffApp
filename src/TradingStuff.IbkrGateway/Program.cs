using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.History;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Persistence;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.IbkrGateway.Subscriptions;
using TradingStuff.ResearchContracts;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<IbkrOptions>(builder.Configuration.GetSection(IbkrOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IbkrRequestRegistry>();
builder.Services.AddSingleton<IbkrClientWrapper>();
builder.Services.AddSingleton<IbkrConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IbkrConnection>());

// Every outbound socket call flows through the governor via PacedSocket — TWS enforces its pacing
// limits by disconnecting, so nothing is allowed to talk to the wire directly.
builder.Services.AddSingleton<IbkrPacingGovernor>();
builder.Services.AddSingleton<PacedSocket>();
builder.Services.AddSingleton<OrderIdStore>();

builder.Services.AddSingleton<IbkrMarketDataClient>();
builder.Services.AddSingleton<IbkrHistoricalClient>();
builder.Services.AddSingleton<IbkrOrderTracker>();
builder.Services.AddSingleton<IbkrOrderClient>();
builder.Services.AddSingleton<IbkrAccountClient>();

// Recording plane: raw ticks land here append-only; standing subscriptions are leased through
// SubscriptionManager rather than fire-and-forget, so a heartbeat failure or a reconnect has a
// single place that knows what should be subscribed.
builder.Services.AddSingleton<ObservationRecorder>();
builder.Services.AddSingleton<SubscriptionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SubscriptionManager>());

// Reports unhealthy while the socket is down, so Aspire shows the real state instead of "running".
builder.Services.AddHealthChecks()
    .AddCheck<IbkrConnectionHealthCheck>("ibkr-connection");

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/ibkr/status", (IbkrConnection connection) => Results.Ok(connection.GetStatus()))
    .RequireAuthorization();

app.MapGet("/ibkr/pacing", (IbkrPacingGovernor governor) => Results.Ok(governor.GetLineBudget()))
    .RequireAuthorization();

// ---- standing subscriptions ------------------------------------------------------------------
// Leased, not fire-and-forget: a caller acquires a lease, heartbeats it, and either releases it
// explicitly or lets it expire (evicted after 3 missed heartbeats). Every lease survives a
// reconnect via SubscriptionManager's replay — callers never need to notice a disconnect happened.

app.MapPost("/ibkr/subscriptions", async (
        SubscriptionLeaseRequest request,
        SubscriptionManager subscriptions,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await subscriptions.GrantAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (TimeoutException ex)
        {
            // No market-data line available (or the pacing budget is exhausted).
            return Results.Problem(
                title: "Could not grant the subscription lease.",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
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

app.MapGet("/ibkr/subscriptions", (SubscriptionManager subscriptions) => Results.Ok(subscriptions.ActiveLeases()))
    .RequireAuthorization();

app.MapPost("/ibkr/subscriptions/{leaseId:guid}/heartbeat", (Guid leaseId, SubscriptionManager subscriptions) =>
        subscriptions.Heartbeat(leaseId) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization();

app.MapDelete("/ibkr/subscriptions/{leaseId:guid}", async (
        Guid leaseId,
        SubscriptionManager subscriptions,
        CancellationToken cancellationToken) =>
        await subscriptions.ReleaseAsync(leaseId, cancellationToken) ? Results.NoContent() : Results.NotFound())
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

// Underlying resolution as its own endpoint: NodeSelector and RecorderOrchestrator need an
// underlying's conId (to lease a core-underlying tick subscription) without going through the
// option-chain path, which resolves it only as an internal step.
app.MapGet("/ibkr/underlyings/{symbol}/resolve", async (
        string symbol,
        IbkrMarketDataClient client,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await client.ResolveUnderlyingAsync(symbol, cancellationToken));
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR could not resolve the underlying.",
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

// Enumerates a futures family's contracts, expired and current alike — the discovery step a deep
// ES intraday backfill needs before it can walk individual quarterly contracts (a CONTFUT rejects a
// past endDateTime with error 10339; see docs/research/ibkr-data-capability-matrix.md constraint 3
// and ResearchService's EsContractWalker, the only caller today).
app.MapGet("/ibkr/futures/{symbol}/contracts", async (
        string symbol,
        [FromQuery] string? exchange,
        [FromQuery] string? currency,
        IbkrMarketDataClient client,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await client.GetFuturesFamilyAsync(
                symbol, exchange ?? "CME", currency ?? "USD", cancellationToken));
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR could not enumerate the futures family.",
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

// Two mutually exclusive ways to say how wide the window is, named so neither can be mistaken for
// the other: strikeHalfCount is a COUNT of strikes each side, moneynessHalfWidth is a FRACTION of
// spot. The old name was `window`, which is what let a caller pass 20 meaning ±20% and receive the
// 41 strikes nearest spot — ±1.3% of SPX — with no error anywhere.
app.MapGet("/ibkr/options/chains/{underlying}", async (
        string underlying,
        [FromQuery] string? expiration,
        [FromQuery] int? strikeHalfCount,
        [FromQuery] decimal? moneynessHalfWidth,
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
                strikeHalfCount,
                cancellationToken,
                tradingClass,
                moneynessHalfWidth));
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

// ---- historical data ----------------------------------------------------------------------------
// The only surface that reaches reqHistoricalData / reqHeadTimeStamp. Callers are the research
// backfill coordinator, which needs to tell retry-now (429 + Retry-After), retry-later-differently
// (400, permanent), and genuinely-empty (200, HasData=false) apart — see the catch order below.

app.MapPost("/ibkr/history/bars", async (
        HistoricalBarsRequest request,
        IbkrHistoricalClient historical,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Contract?.Symbol) || string.IsNullOrWhiteSpace(request.Contract.SecType))
        {
            return Results.BadRequest(new { error = "A contract symbol and security type are required." });
        }

        if (string.IsNullOrWhiteSpace(request.Duration) ||
            string.IsNullOrWhiteSpace(request.BarSize) ||
            string.IsNullOrWhiteSpace(request.WhatToShow))
        {
            return Results.BadRequest(new { error = "Duration, bar size, and whatToShow are required." });
        }

        try
        {
            return Results.Ok(await historical.GetHistoricalBarsAsync(request, cancellationToken));
        }
        catch (IbkrPacingRejectedException ex)
        {
            // The coordinator's backpressure signal: back off exactly this long and retry the same
            // slice, rather than treating pacing exhaustion as a request failure.
            httpContext.Response.Headers["Retry-After"] =
                Math.Ceiling(ex.RetryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return Results.Problem(
                title: "Historical data pacing budget exhausted.",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR rejected the historical data request.",
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
        catch (TimeoutException ex)
        {
            // TWS accepted the request and never terminated it (no historicalDataEnd, no error).
            // Upstream slowness, not a bug here.
            return Results.Problem(
                title: "TWS did not answer the historical data request in time.",
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    })
    .RequireAuthorization();

app.MapPost("/ibkr/history/head-timestamp", async (
        HeadTimestampQuery request,
        IbkrHistoricalClient historical,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Contract?.Symbol) || string.IsNullOrWhiteSpace(request.Contract.SecType))
        {
            return Results.BadRequest(new { error = "A contract symbol and security type are required." });
        }

        if (string.IsNullOrWhiteSpace(request.WhatToShow))
        {
            return Results.BadRequest(new { error = "whatToShow is required." });
        }

        try
        {
            return Results.Ok(await historical.GetHeadTimestampAsync(request, cancellationToken));
        }
        catch (IbkrPacingRejectedException ex)
        {
            httpContext.Response.Headers["Retry-After"] =
                Math.Ceiling(ex.RetryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return Results.Problem(
                title: "Historical data pacing budget exhausted.",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR rejected the head timestamp request.",
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
        catch (TimeoutException ex)
        {
            return Results.Problem(
                title: "TWS did not answer the head timestamp request in time.",
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    })
    .RequireAuthorization();

// ---- account ----------------------------------------------------------------------------------
// Read-only. This is what feeds the risk engine real buying power, daily P&L, and Greeks instead of
// the stubbed portfolio it used to be given.

app.MapGet("/ibkr/account/portfolio", async (
        [FromQuery] string? accountId,
        IbkrAccountClient accounts,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await accounts.GetPortfolioAsync(accountId, cancellationToken));
        }
        catch (IbkrRequestException ex)
        {
            return Results.Problem(
                title: "IBKR rejected the account request.",
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
        catch (TimeoutException ex)
        {
            // TWS accepted the request and never terminated it. Upstream slowness, not a bug here.
            return Results.Problem(
                title: "TWS did not answer the account request in time.",
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout);
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

app.MapPost("/ibkr/orders/{ibkrOrderId:int}/cancel", async (
        int ibkrOrderId,
        CancelOrderRequest request,
        IbkrOrderClient orders,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return await orders.CancelAsync(ibkrOrderId, request.Reason, cancellationToken) is { } state
                ? Results.Ok(state)
                : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Cancel refused.", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
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
