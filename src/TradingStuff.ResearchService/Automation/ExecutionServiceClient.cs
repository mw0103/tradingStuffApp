using System.Net.Http.Json;
using TradingStuff.Contracts;

namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// ResearchService's only route to an order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The research plane does not place orders; it asks the service that owns them to.</b> This
/// client posts to ExecutionService's existing <c>POST /orders</c>, which runs the whole spine —
/// validate, quote, portfolio, risk, route, persist, publish. Nothing here reaches the IBKR gateway,
/// and nothing here goes near <c>placeOrder</c>: that call site stays the single one in
/// <c>PacedSocket</c>, two processes away, reached only through the router ExecutionService
/// resolved. Automation adds a caller to an existing surface, not a second path to the broker.
/// </para>
/// <para>
/// <b>Retries are stripped at registration</b> (<c>ServiceClientConfiguration.DisableAutomaticRetries</c>),
/// for the reason recorded on that method: the standard resilience handler retries on its per-attempt
/// timeout, and an order that rests longer than that reaches the broker twice under two broker order
/// ids while the caller sees only the last attempt's outcome. That happened on 2026-07-31 with a
/// resting SPXW combo. An automated caller makes it worse, not better — nobody is watching.
/// </para>
/// </remarks>
public sealed class ExecutionServiceClient(HttpClient httpClient)
{
    /// <summary>Which router and portfolio provider ExecutionService actually resolved, plus its quote-source string.</summary>
    public async Task<(string Router, string PortfolioSource, string? MarketDataSourceConfigured)> GetResolvedConfigurationAsync(
        CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync("/execution/configuration", cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ExecutionConfigurationDto>(cancellationToken)
                   ?? throw new HttpRequestException("ExecutionService returned an empty configuration body.");

        if (string.IsNullOrWhiteSpace(body.Router) || string.IsNullOrWhiteSpace(body.PortfolioSource))
        {
            throw new HttpRequestException(
                "ExecutionService did not report a resolved router and portfolio provider.");
        }

        return (body.Router, body.PortfolioSource, body.MarketDataSourceConfigured);
    }

    public async Task<SubmitOrderResponse> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync("/orders", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);

            // Deliberately an exception rather than a null or a synthesised "rejected" result. A 502
            // from this endpoint means the order WAS routed and no outcome came back — it may be live
            // at the venue — and a caller that treats a failure as "nothing happened" would record
            // exactly the wrong thing in the decision log. The caller catches this and writes
            // outcome-unknown.
            throw new OrderSubmissionFailedException(
                $"ExecutionService returned {(int)response.StatusCode}: {problem}",
                (int)response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<SubmitOrderResponse>(cancellationToken)
               ?? throw new OrderSubmissionFailedException(
                   "ExecutionService returned 2xx with an empty body, so the order's outcome is unknown.", 200);
    }

    private sealed record ExecutionConfigurationDto(string? Router, string? PortfolioSource, string? MarketDataSourceConfigured);
}

/// <summary>
/// The quote provider MarketDataService actually resolved, and the quotes it serves.
/// </summary>
/// <remarks>
/// Separate from <see cref="ExecutionServiceClient"/> because the fact lives in a separate process,
/// and that is the entire point. ExecutionService can report the <c>MarketData:Source</c> string it
/// was configured with; only MarketDataService can report which provider that string actually
/// resolved to. On 2026-08-01 those two disagreed — <c>"ibkr"</c> is not one of the recognised values,
/// so the deterministic generator was serving while every configuration file read like an opt-in —
/// and a vertical was approved against invented quotes. Automation asks the process that knows.
/// </remarks>
public sealed class MarketDataServiceClient(HttpClient httpClient)
{
    public async Task<string> GetResolvedSourceAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync("/market-data/ibkr/status", cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MarketDataStatusDto>(cancellationToken)
                   ?? throw new HttpRequestException("MarketDataService returned an empty status body.");

        return string.IsNullOrWhiteSpace(body.Mode)
            ? throw new HttpRequestException(
                "MarketDataService reported no resolved quote provider, so what would price an order is unknown.")
            : body.Mode;
    }

    /// <summary>
    /// Quotes for the legs of a planned order, from the same service that will price it for risk.
    /// </summary>
    /// <remarks>
    /// The same service on purpose. Pricing the limit here from one feed while ExecutionService
    /// prices the risk check from another would make the two disagree by construction — and the one
    /// that gets to be wrong silently is whichever the operator is not looking at.
    /// </remarks>
    public async Task<MarketDataQuoteResponse> GetQuotesAsync(
        IReadOnlyList<OrderLegRequest> legs, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/market-data/options/quotes", new MarketDataQuoteRequest(legs), cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MarketDataQuoteResponse>(cancellationToken)
               ?? throw new HttpRequestException("MarketDataService returned an empty quote body.");
    }

    private sealed record MarketDataStatusDto(bool Required, string? Mode, bool Connected);
}

/// <summary>
/// The order was handed to ExecutionService and no usable outcome came back.
/// </summary>
/// <remarks>
/// Never to be read as "no order was placed". ExecutionService persists the order record BEFORE
/// routing, so a failure here has a record on the other side to reconcile against; a 502 in
/// particular means it was routed and the outcome is unestablished.
/// </remarks>
public sealed class OrderSubmissionFailedException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
