using TradingStuff.Contracts;
using TradingStuff.MarketDataService;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The deterministic generator stays registered unconditionally: it is what makes the test suite and
// offline development repeatable, and it is the fallback whenever the IBKR source is not selected.
builder.Services.AddSingleton<DeterministicOptionMarketDataProvider>();

builder.Services.AddHttpClient<IbkrOptionMarketDataProvider>((sp, http) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    ServiceClientConfiguration.ConfigureInternalClient(
        http,
        configuration,
        "IbkrGateway:BaseUrl",
        "http://localhost:5100");
});

builder.Services.AddSingleton<IOptionMarketDataProvider>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var source = configuration["MarketData:Source"];

    return MarketDataSources.UsesIbkrGateway(source)
        ? sp.GetRequiredService<IbkrOptionMarketDataProvider>()
        : sp.GetRequiredService<DeterministicOptionMarketDataProvider>();
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/market-data/options/quotes", async (
        MarketDataQuoteRequest request,
        IOptionMarketDataProvider provider,
        CancellationToken cancellationToken) =>
    {
        if (request.Legs.Count == 0)
        {
            return Results.BadRequest(new { error = "At least one option leg is required." });
        }

        return Results.Ok(await provider.GetQuotesAsync(request, cancellationToken));
    })
    .RequireAuthorization();

app.MapGet("/market-data/options/chains/{underlying}", async (
        string underlying,
        string? expiration,
        int? window,
        string? tradingClass,
        IOptionMarketDataProvider provider,
        CancellationToken cancellationToken) =>
    {
        DateOnly? target = DateOnly.TryParse(expiration, out var parsed) ? parsed : null;

        return Results.Ok(await provider.GetOptionChainAsync(
            underlying, target, window, tradingClass, cancellationToken));
    })
    .RequireAuthorization();

app.MapGet("/market-data/ibkr/status", async (
        IConfiguration configuration,
        IOptionMarketDataProvider provider,
        IbkrOptionMarketDataProvider ibkrProvider,
        CancellationToken cancellationToken) =>
    {
        if (!MarketDataSources.UsesIbkrGateway(configuration["MarketData:Source"]))
        {
            return Results.Ok(new
            {
                required = true,
                mode = provider.Source,
                connected = false,
                note = $"Serving the deterministic paper feed. Set MarketData:Source to " +
                       $"'{MarketDataSources.IbkrDelayed}' or '{MarketDataSources.IbkrLive}' to route " +
                       "through the IBKR gateway.",
            });
        }

        // Report the gateway's real socket state rather than a static placeholder.
        var status = await ibkrProvider.GetGatewayStatusAsync(cancellationToken);

        return Results.Ok(new
        {
            required = true,
            mode = provider.Source,
            connected = status is not null,
            gateway = status,
        });
    })
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();
