using TradingStuff.Contracts;
using TradingStuff.MarketDataService;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton<DeterministicOptionMarketDataProvider>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/market-data/options/quotes", (
        MarketDataQuoteRequest request,
        DeterministicOptionMarketDataProvider provider) =>
    {
        if (request.Legs.Count == 0)
        {
            return Results.BadRequest(new { error = "At least one option leg is required." });
        }

        return Results.Ok(provider.GetQuotes(request));
    })
    .RequireAuthorization();

app.MapGet("/market-data/options/chains/{underlying}", (
        string underlying,
        string? expiration,
        DeterministicOptionMarketDataProvider provider) =>
    {
        var targetExpiration = DateOnly.TryParse(expiration, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30));

        return Results.Ok(provider.GetOptionChain(underlying, targetExpiration));
    })
    .RequireAuthorization();

app.MapGet("/market-data/ibkr/status", (IConfiguration configuration) => Results.Ok(new
    {
        required = true,
        mode = configuration["MarketData:Source"] ?? "ibkr-deterministic-paper-feed",
        gatewayUrl = configuration["IBKR:GatewayUrl"] ?? "http://localhost:5000",
        note = "IBKR Gateway is modeled as a required external Aspire dependency; this v1 provider keeps quote generation deterministic for paper tests."
    }))
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();
