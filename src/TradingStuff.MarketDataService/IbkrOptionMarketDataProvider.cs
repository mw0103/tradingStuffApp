using System.Net.Http.Json;
using System.Text.Json;
using TradingStuff.Contracts;

namespace TradingStuff.MarketDataService;

/// <summary>
/// Serves quotes and chains from the IBKR gateway service.
/// </summary>
/// <remarks>
/// This is an HTTP hop, not a TWS client. Exactly one process in the mesh holds the TWS socket,
/// because a TWS connection is stateful and single-owner per client id; a second connection from
/// here would give the account two independent request/order id sequences.
/// </remarks>
public sealed class IbkrOptionMarketDataProvider(
    HttpClient httpClient,
    ILogger<IbkrOptionMarketDataProvider> logger) : IOptionMarketDataProvider
{
    public string Source => "ibkr-gateway";

    public async Task<MarketDataQuoteResponse> GetQuotesAsync(
        MarketDataQuoteRequest request,
        CancellationToken cancellationToken)
    {
        // Legs carry side and quantity; quoting needs neither. Distinct because a strategy can name
        // the same contract twice and one subscription per contract is enough.
        var contracts = request.Legs
            .Select(leg => leg.Contract)
            .DistinctBy(contract => contract.Key())
            .ToArray();

        var response = await httpClient.PostAsJsonAsync(
            "/ibkr/options/quotes",
            new IbkrQuoteRequestBody(contracts),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var quotes = await response.Content.ReadFromJsonAsync<MarketDataQuoteResponse>(cancellationToken)
                     ?? throw new InvalidOperationException("IBKR gateway returned an empty quote response.");

        logger.LogDebug("Received {Count} quote(s) from the IBKR gateway.", quotes.Quotes.Count);

        return quotes;
    }

    public async Task<IReadOnlyList<OptionContract>> GetOptionChainAsync(
        string underlying,
        DateOnly? expiration,
        int? strikeWindow,
        string? tradingClass,
        CancellationToken cancellationToken)
    {
        // Forward every selector. Dropping tradingClass here silently reduces SPX to the AM-settled
        // monthly series and makes SPXW unreachable through this service.
        var query = new List<string>(3);

        if (expiration is { } value)
        {
            query.Add($"expiration={value:yyyy-MM-dd}");
        }

        if (strikeWindow is { } window)
        {
            query.Add($"window={window}");
        }

        if (!string.IsNullOrWhiteSpace(tradingClass))
        {
            query.Add($"tradingClass={Uri.EscapeDataString(tradingClass)}");
        }

        var path = $"/ibkr/options/chains/{Uri.EscapeDataString(underlying)}";

        if (query.Count > 0)
        {
            path += "?" + string.Join('&', query);
        }

        return await httpClient.GetFromJsonAsync<IReadOnlyList<OptionContract>>(path, cancellationToken)
               ?? [];
    }

    /// <summary>The gateway's own view of its TWS socket, for the status endpoint.</summary>
    public async Task<JsonElement?> GetGatewayStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<JsonElement>("/ibkr/status", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Could not reach the IBKR gateway for status.");
            return null;
        }
    }

    private sealed record IbkrQuoteRequestBody(IReadOnlyList<OptionContract> Contracts);
}
