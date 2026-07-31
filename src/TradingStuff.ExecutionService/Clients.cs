using System.Net.Http.Headers;
using System.Net.Http.Json;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public interface IRiskClient
{
    Task<RiskEvaluationResult> EvaluateAsync(RiskEvaluationRequest request, CancellationToken cancellationToken);
}

public interface IMarketDataClient
{
    Task<MarketDataQuoteResponse> GetQuotesAsync(MarketDataQuoteRequest request, CancellationToken cancellationToken);
}

public sealed class HttpRiskClient(HttpClient httpClient) : IRiskClient
{
    public async Task<RiskEvaluationResult> EvaluateAsync(
        RiskEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync("/risk/evaluate-order", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<RiskEvaluationResult>(cancellationToken)
            ?? throw new InvalidOperationException("Risk service returned an empty response.");
    }
}

public sealed class HttpMarketDataClient(HttpClient httpClient) : IMarketDataClient
{
    public async Task<MarketDataQuoteResponse> GetQuotesAsync(
        MarketDataQuoteRequest request,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync("/market-data/options/quotes", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MarketDataQuoteResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Market-data service returned an empty response.");
    }
}

public static class ServiceClientConfiguration
{
    public static void ConfigureInternalClient(HttpClient httpClient, IConfiguration configuration, string baseUrlKey, string fallback)
    {
        httpClient.BaseAddress = new Uri(configuration[baseUrlKey] ?? fallback);
        var token = configuration["Authentication:DevelopmentToken"] ?? "dev-internal-token";
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
