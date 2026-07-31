using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using TradingStuff.Contracts;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Gateway;

/// <summary>An underlying's IBKR identity — mirrors the gateway's internal <c>UnderlyingDefinition</c>.</summary>
public sealed record UnderlyingResolution(int ConId, string SecType, string Exchange);

/// <summary>
/// One futures-family contract, matched by property name against the gateway's own
/// <c>FuturesContractDefinition</c>. See <see cref="IbkrGatewayClient.GetFuturesFamilyAsync"/>.
/// </summary>
public sealed record FuturesContractResolution(
    int ConId, DateOnly LastTradeDateOrContractMonth, string? TradingClass, string Exchange, string Currency);

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

    /// <summary>
    /// Enumerates every contract IBKR lists for a futures family — expired and current alike. The
    /// discovery step <c>EsContractWalker</c> needs before it can walk individual ES quarterlies: a
    /// <c>CONTFUT</c> rejects a past <c>endDateTime</c> (error 10339), so deep intraday history is
    /// only reachable one specific contract at a time.
    /// </summary>
    /// <remarks>
    /// Swallows failure into an empty list rather than throwing, matching
    /// <see cref="ResolveUnderlyingAsync"/> and <see cref="GetChainAsync"/>: the only caller is a
    /// periodic scan for which "nothing back this pass" means "try again next scan", not a fatal
    /// error worth tearing down the walker over.
    /// </remarks>
    public async Task<IReadOnlyList<FuturesContractResolution>> GetFuturesFamilyAsync(
        string symbol, string exchange, string currency, CancellationToken cancellationToken)
    {
        var path = $"/ibkr/futures/{Uri.EscapeDataString(symbol)}/contracts" +
                    $"?exchange={Uri.EscapeDataString(exchange)}&currency={Uri.EscapeDataString(currency)}";

        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not enumerate the {Symbol} futures family: {Message}", symbol, ex.Message);
            return [];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Could not enumerate the {Symbol} futures family: {Status}.", symbol, response.StatusCode);
                return [];
            }

            return await response.Content.ReadFromJsonAsync<IReadOnlyList<FuturesContractResolution>>(cancellationToken) ?? [];
        }
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

    // ---- historical data ------------------------------------------------------------------------
    // The backfill coordinator's only route to TWS history. Every failure mode is classified here
    // rather than at the call site, so the coordinator's state machine reads as a switch over
    // outcomes instead of a pile of status-code checks — and so the one mapping that is easy to get
    // backwards (200 + HasData:false is a confirmed-empty SLICE, not a failed request) lives in
    // exactly one place.

    public async Task<HistoricalBarsResult> GetHistoricalBarsAsync(
        HistoricalBarsRequestDto request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.PostAsJsonAsync("/ibkr/history/bars", request, cancellationToken);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            var (outcome, detail) = ClassifyTransportFailure(ex);
            return new HistoricalBarsResult(outcome, [], null, null, detail);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<HistoricalBarsResponseDto>(cancellationToken);

                if (body is null)
                {
                    return new HistoricalBarsResult(GatewayOutcome.Transient, [], null, null, "The gateway returned an empty body.");
                }

                return body.HasData
                    ? new HistoricalBarsResult(GatewayOutcome.Ok, body.Bars, null, null, null)
                    : new HistoricalBarsResult(GatewayOutcome.Empty, [], null, null, "TWS reported no data for this slice.");
            }

            var (outcome, retryAfter, errorCode, detail) = await ClassifyFailureAsync(response, cancellationToken);
            return new HistoricalBarsResult(outcome, [], retryAfter, errorCode, detail);
        }
    }

    public async Task<HeadTimestampResult> GetHeadTimestampAsync(
        HistoricalContractSpecDto contract, string whatToShow, bool useRth, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.PostAsJsonAsync(
                "/ibkr/history/head-timestamp", new { Contract = contract, WhatToShow = whatToShow, UseRth = useRth },
                cancellationToken);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            var (outcome, detail) = ClassifyTransportFailure(ex);
            return new HeadTimestampResult(outcome, null, null, null, detail);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<HeadTimestampResponseDto>(cancellationToken);

                return body is null
                    ? new HeadTimestampResult(GatewayOutcome.Transient, null, null, null, "The gateway returned an empty body.")
                    : new HeadTimestampResult(GatewayOutcome.Ok, body.HeadTimestamp.ToUniversalTime(), null, null, null);
            }

            var (outcome, retryAfter, errorCode, detail) = await ClassifyFailureAsync(response, cancellationToken);
            return new HeadTimestampResult(outcome, null, retryAfter, errorCode, detail);
        }
    }

    /// <summary>
    /// Whether an exception thrown by the send is a transport failure this classifier owns, rather
    /// than the caller's own cancellation.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than <c>HttpRequestException or TaskCanceledException</c>. The resilience
    /// pipeline this client is built on (see <c>ServiceClientConfiguration.DisableAutomaticRetries</c>)
    /// carries a circuit breaker, and an open circuit throws <see cref="BrokenCircuitException"/> —
    /// which matched neither arm of the original filter, escaped this method entirely, and surfaced
    /// at the coordinator's outermost catch. That path never writes an outcome, so the claimed row
    /// stayed <c>inflight</c> until a reaper turned it into <c>failed</c> with its attempt already
    /// burned: the one failure shape that both loses the slice AND spends its retry budget. Anything
    /// this method fails to recognise now still leaves this class as a classified outcome instead of
    /// a stranded claim, which is the property that was actually missing.
    /// </remarks>
    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop; this is not a failure to classify, and swallowing it would
            // hide a shutdown behind a retryable outcome.
            return false;
        }

        // A TaskCanceledException raised while the CALLER's token is still live is the resilience
        // pipeline's own attempt timeout, which is a transport failure. Any other
        // OperationCanceledException belongs to a cancellation this method has no business claiming.
        return ex is not OperationCanceledException || ex is TaskCanceledException;
    }

    /// <summary>
    /// Splits a transport failure into "provably never reached TWS" and "may have reached TWS".
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point, and it is not cosmetic: the coordinator refunds the
    /// slice's attempt for <see cref="GatewayOutcome.Unreachable"/> and burns it for
    /// <see cref="GatewayOutcome.Transient"/>. <see cref="HttpRequestException.HttpRequestError"/> is
    /// what makes the split precise rather than a guess — a connection refused, an unresolvable
    /// host, or a failed TLS handshake all happen before a single byte reaches the gateway (let
    /// alone TWS), whereas <c>ResponseEnded</c>/<c>InvalidResponse</c>/a client-side timeout all
    /// mean the request was accepted and may well have consumed a paced request slot.
    /// </remarks>
    private static (GatewayOutcome Outcome, string Detail) ClassifyTransportFailure(Exception ex) => ex switch
    {
        BrokenCircuitException => (
            GatewayOutcome.Unreachable,
            $"The gateway circuit is open; the request was not sent. {ex.Message}"),

        HttpRequestException
        {
            HttpRequestError: HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError,
        } => (GatewayOutcome.Unreachable, $"The gateway could not be reached. {ex.Message}"),

        _ => (GatewayOutcome.Transient, ex.Message),
    };

    /// <summary>Maps the gateway's documented history error surface onto <see cref="GatewayOutcome"/>.</summary>
    private static async Task<(GatewayOutcome Outcome, TimeSpan? RetryAfter, int? ErrorCode, string? Detail)> ClassifyFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (errorCode, detail) = await ReadProblemAsync(response, cancellationToken);

        return (int)response.StatusCode switch
        {
            // The pacing governor's backpressure signal. Retry-After is authoritative; the fallback
            // only covers a governor that somehow answered 429 without the header, and erring long
            // is correct because erring short re-triggers the same rejection immediately.
            429 => (GatewayOutcome.Paced, ReadRetryAfter(response) ?? TimeSpan.FromSeconds(60), errorCode, detail),
            400 => (GatewayOutcome.Permanent, null, errorCode, detail),
            503 => (GatewayOutcome.NotConnected, null, errorCode, detail),
            _ => (GatewayOutcome.Transient, null, errorCode, detail), // 502 bad gateway, 504 TWS timeout, anything unforeseen
        };
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>Pulls the gateway's <c>ibkrErrorCode</c> ProblemDetails extension and detail text, if present.</summary>
    private static async Task<(int? ErrorCode, string? Detail)> ReadProblemAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(payload))
            {
                return (null, response.ReasonPhrase);
            }

            using var document = JsonDocument.Parse(payload);

            int? errorCode = document.RootElement.TryGetProperty("ibkrErrorCode", out var code) &&
                             code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : null;

            var detail = document.RootElement.TryGetProperty("detail", out var detailElement)
                ? detailElement.GetString()
                : payload;

            return (errorCode, detail);
        }
        catch (JsonException)
        {
            // A non-ProblemDetails body (a proxy's HTML error page, say) still classifies fine on
            // status code alone; losing the detail text is not worth failing the request over.
            return (null, response.ReasonPhrase);
        }
    }

    private sealed record ResolveContractsRequestDto(IReadOnlyList<OptionContract> Contracts);

    private sealed record ResolvedContractDto(OptionContract Contract, int? ConId, string? Error);

    private sealed record ResolveContractsResponseDto(IReadOnlyList<ResolvedContractDto> Resolved);
}
