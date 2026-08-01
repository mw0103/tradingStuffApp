using System.Net.Http.Json;
using TradingStuff.Contracts;
using TradingStuff.ServiceDefaults;

namespace TradingStuff.ExecutionService;

/// <summary>Registration for the internal clients whose requests must not be repeated.</summary>
/// <remarks>
/// Named rather than written inline in <c>Program.cs</c> so that "exactly one attempt" is a property
/// a test can hold: it is invisible in the request path, it holds only until someone registers the
/// client without it, and its absence surfaces as a wrongly rejected order rather than as an error.
/// </remarks>
public static class ExecutionHttpClients
{
    /// <summary>
    /// The risk client. <c>/risk/evaluate-order</c> reads like a query and is not one: an approval
    /// burns the order's client order id in the service's duplicate guard, so a repeated request
    /// meets the record the first one left and comes back rejected with <c>DUPLICATE_ORDER</c> — and
    /// the id stays burned, so resubmitting it is rejected too. A lost response is turned into a
    /// permanent rejection of a perfectly good order by nothing more than a retry.
    /// </summary>
    /// <remarks>
    /// This holds whatever the guard does with approvals: if it is later changed to remember every
    /// evaluation, or to replay a cached decision, a single attempt is still correct — the request
    /// simply stops being one whose repetition has to be reasoned about at all.
    /// <para>
    /// 30 s per attempt, unlike the order path's 60 s, because the evaluation is in-process
    /// arithmetic rather than a broker round trip.
    /// </para>
    /// </remarks>
    public static IHttpClientBuilder AddRiskClient(this IServiceCollection services) =>
        services.AddHttpClient<IRiskClient, HttpRiskClient>((sp, client) =>
        {
            ServiceClientConfiguration.ConfigureInternalClient(
                client,
                sp.GetRequiredService<IConfiguration>(),
                "RiskService:BaseUrl",
                "http://riskservice");
        })
            .DisableAutomaticRetries(TimeSpan.FromSeconds(30));
}

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
