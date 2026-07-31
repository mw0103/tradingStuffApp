using System.Net.Http.Json;
using TradingStuff.Contracts;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Gateway;

/// <summary>An underlying's IBKR identity — mirrors the gateway's internal <c>UnderlyingDefinition</c>.</summary>
public sealed record UnderlyingResolution(int ConId, string SecType, string Exchange);

/// <summary>
/// Thin HTTP client for the parts of the IBKR gateway that recorder orchestration needs: underlying
/// resolution, option chains, contract resolution, and standing-subscription leases.
/// </summary>
/// <remarks>
/// ResearchService talks to the gateway over HTTP, never via a project reference to it — the two
/// are separate processes by design (the gateway is the sole TWS socket owner). Request/response
/// shapes are matched by property name against the gateway's minimal-API DTOs rather than shared
/// types, the same pattern <c>IbkrOptionMarketDataProvider</c> already uses in MarketDataService.
/// </remarks>
public sealed class IbkrGatewayClient(HttpClient httpClient, ILogger<IbkrGatewayClient> logger)
{
    public async Task<UnderlyingResolution?> ResolveUnderlyingAsync(string symbol, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(
            $"/ibkr/underlyings/{Uri.EscapeDataString(symbol)}/resolve", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Could not resolve underlying {Symbol}: {Status}.", symbol, response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<UnderlyingResolution>(cancellationToken);
    }

    public async Task<IReadOnlyList<OptionContract>> GetChainAsync(
        string underlying, DateOnly expiration, string tradingClass, int window, CancellationToken cancellationToken)
    {
        var path = $"/ibkr/options/chains/{Uri.EscapeDataString(underlying)}" +
                    $"?expiration={expiration:yyyy-MM-dd}&window={window}&tradingClass={Uri.EscapeDataString(tradingClass)}";

        var response = await httpClient.GetAsync(path, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Chain request failed for {Underlying}/{TradingClass} near {Expiration}: {Status}.",
                underlying, tradingClass, expiration, response.StatusCode);
            return [];
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<OptionContract>>(cancellationToken) ?? [];
    }

    /// <summary>Resolves each contract to its conId; contracts the broker could not match are simply absent from the result.</summary>
    public async Task<IReadOnlyDictionary<OptionContractKey, int>> ResolveContractsAsync(
        IReadOnlyList<OptionContract> contracts, CancellationToken cancellationToken)
    {
        if (contracts.Count == 0)
        {
            return new Dictionary<OptionContractKey, int>();
        }

        var response = await httpClient.PostAsJsonAsync(
            "/ibkr/contracts/resolve", new ResolveContractsRequestDto(contracts), cancellationToken);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ResolveContractsResponseDto>(cancellationToken);
        var resolved = new Dictionary<OptionContractKey, int>();

        foreach (var entry in body?.Resolved ?? [])
        {
            if (entry.ConId is { } conId)
            {
                resolved[entry.Contract.Key()] = conId;
            }
            else
            {
                logger.LogDebug("Could not resolve {Contract}: {Error}", entry.Contract, entry.Error);
            }
        }

        return resolved;
    }

    public async Task<SubscriptionLease?> GrantSubscriptionAsync(
        SubscriptionLeaseRequest request, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync("/ibkr/subscriptions", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Could not grant a subscription lease for conId {ConId}: {Status}.", request.ConId, response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<SubscriptionLease>(cancellationToken);
    }

    public async Task<bool> HeartbeatAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsync($"/ibkr/subscriptions/{leaseId}/heartbeat", null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ReleaseSubscriptionAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var response = await httpClient.DeleteAsync($"/ibkr/subscriptions/{leaseId}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private sealed record ResolveContractsRequestDto(IReadOnlyList<OptionContract> Contracts);

    private sealed record ResolvedContractDto(OptionContract Contract, int? ConId, string? Error);

    private sealed record ResolveContractsResponseDto(IReadOnlyList<ResolvedContractDto> Resolved);
}
