using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using TradingStuff.Contracts;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Gateway;

/// <summary>An underlying's IBKR identity — mirrors the gateway's internal <c>UnderlyingDefinition</c>.</summary>
public sealed record UnderlyingResolution(int ConId, string SecType, string Exchange);

/// <summary>
/// The gateway's socket status, matched by property name against its own status DTO. Only the fields
/// a caller needs to decide whether the broker is usable — deliberately not the whole payload, and
/// deliberately no account numbers beyond what <c>ManagedAccounts</c> already exposes internally.
/// </summary>
public sealed record GatewayStatus(
    bool Connected,
    bool TradingPermitted,
    string? TradingBlockedReason,
    IReadOnlyList<string> ManagedAccounts,
    int? MarketDataType);

/// <summary>
/// One futures-family contract, matched by property name against the gateway's own
/// <c>FuturesContractDefinition</c>. See <see cref="IbkrGatewayClient.GetFuturesFamilyAsync"/>.
/// </summary>
public sealed record FuturesContractResolution(
    int ConId, DateOnly LastTradeDateOrContractMonth, string? TradingClass, string Exchange, string Currency);

/// <summary>One account-summary tag as TWS reported it, matched by name against the gateway's DTO.</summary>
public sealed record AccountSummaryTagRead(string Tag, string Value, string Currency);

/// <summary>Every summary tag the gateway's open account stream is currently carrying.</summary>
public sealed record AccountSummaryRead(
    string Account, DateTimeOffset CapturedAt, IReadOnlyList<AccountSummaryTagRead> Tags);

/// <summary>One open position exactly as <c>reqPositionsMulti</c> reported it. No Greeks, no marks.</summary>
public sealed record AccountPositionRead(
    int ConId,
    string Symbol,
    string SecType,
    DateOnly? Expiration,
    decimal? Strike,
    string? Right,
    string? TradingClass,
    string? Currency,
    string? Multiplier,
    string? LocalSymbol,
    decimal Quantity,
    decimal? AverageCost);

/// <summary>Every open position the gateway's account stream is currently carrying.</summary>
public sealed record AccountPositionsRead(
    string Account, DateTimeOffset CapturedAt, IReadOnlyList<AccountPositionRead> Positions);

/// <summary>One execution report as TWS delivered it, plus its commission if one arrived in time.</summary>
public sealed record AccountExecutionRead(
    string ExecId,
    long PermId,
    int OrderId,
    int ClientId,
    string Account,
    int ConId,
    string Symbol,
    string SecType,
    DateOnly? Expiration,
    decimal? Strike,
    string? Right,
    string? TradingClass,
    int? Multiplier,
    string? Exchange,
    string Side,
    decimal Quantity,
    decimal Price,
    string ExecutedAtRaw,
    DateTimeOffset? ExecutedAt,
    decimal? Commission,
    string? CommissionCurrency,
    decimal? RealizedPnL);

/// <summary>The answer to one executions pull, and how much of it the commission callback did not cover.</summary>
public sealed record AccountExecutionsRead(
    string Account,
    DateTimeOffset CapturedAt,
    DateTimeOffset SinceUtc,
    IReadOnlyList<AccountExecutionRead> Executions,
    int CommissionsMissing);

/// <summary>
/// Stable short names for why a gateway read could not be made. Recorded verbatim in the capture
/// tables, so they are constants rather than message text: an operator counting refusals by reason
/// must not have that count split by an exception string that gained a port number.
/// </summary>
public static class GatewayRefusalKinds
{
    /// <summary>The request never reached the gateway process (connection refused, open circuit, DNS).</summary>
    public const string GatewayUnreachable = "gateway-unreachable";

    /// <summary>The gateway answered, and said its TWS socket is down (503).</summary>
    public const string BrokerNotConnected = "broker-not-connected";

    /// <summary>The gateway or TWS refused the request itself (a rejection, a timeout, a bad account).</summary>
    public const string GatewayRefused = "gateway-refused";
}

/// <summary>
/// A gateway read that could not be made, carrying the stable reason name the capture layer records.
/// </summary>
/// <remarks>
/// Thrown rather than folded into a null the way <see cref="IbkrGatewayClient.ResolveUnderlyingAsync"/>
/// does, because the caller's whole job is to write down WHY the read failed. A null would make an
/// unreachable gateway indistinguishable from an account holding nothing, which is the exact
/// absence-reads-as-health failure the capture tables exist to prevent.
/// </remarks>
public sealed class GatewayReadException(string refusalKind, string message) : Exception(message)
{
    public string RefusalKind { get; } = refusalKind;
}

/// <summary>
/// Thin HTTP client for the parts of the IBKR gateway that recorder orchestration needs: underlying
/// resolution, contract resolution, and standing-subscription leases. Option chains are fetched
/// through <see cref="OptionChainClient"/> instead — see that type for why it is a separate client.
/// </summary>
/// <remarks>
/// ResearchService talks to the gateway over HTTP, never via a project reference to it — the two
/// are separate processes by design (the gateway is the sole TWS socket owner). Request/response
/// shapes are matched by property name against the gateway's minimal-API DTOs rather than shared
/// types, the same pattern <c>IbkrOptionMarketDataProvider</c> already uses in MarketDataService.
/// </remarks>
public sealed class IbkrGatewayClient(HttpClient httpClient, ILogger<IbkrGatewayClient> logger)
{
    /// <summary>
    /// The gateway's view of the TWS socket.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning null on failure, unlike the read-only lookups below. Its one
    /// caller is the automation arming check, and there "the gateway did not answer" must reach that
    /// check as an error it refuses on — folding it into a null would make an unreachable gateway
    /// indistinguishable from a well-formed answer with nothing in it.
    /// </remarks>
    public async Task<GatewayStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync("/ibkr/status", cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<GatewayStatus>(cancellationToken)
               ?? throw new HttpRequestException("The IBKR gateway returned an empty status body.");
    }

    /// <summary>
    /// The account's open positions, as the broker reports them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-only, and the same endpoint ExecutionService's <c>IbkrPortfolioProvider</c> reads for risk
    /// — deliberately, so "what is open" means the same thing to the component that decides to close a
    /// position and to the component that prices the closing order's risk. A second source would come
    /// apart exactly when it mattered.
    /// </para>
    /// <para>
    /// Throws rather than returning null, matching <see cref="GetStatusAsync"/> and for the same
    /// reason: its caller is the exit branch, and "the account could not be read" must reach it as an
    /// error it records and refuses on. Folded into an empty list it would read as a FLAT account —
    /// which would both skip a due exit and unblock an entry, in one silent step.
    /// </para>
    /// </remarks>
    public async Task<PortfolioSnapshot> GetPortfolioAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync("/ibkr/account/portfolio", cancellationToken);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GatewayPortfolioResponse>(cancellationToken)
                   ?? throw new HttpRequestException("The IBKR gateway returned an empty portfolio body.");

        return body.Portfolio
               ?? throw new HttpRequestException("The IBKR gateway returned a portfolio body with no portfolio in it.");
    }

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
    /// <see cref="ResolveUnderlyingAsync"/>: the only caller is a periodic scan for which "nothing
    /// back this pass" means "try again next scan", not a fatal error worth tearing down the walker
    /// over.
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

    // ---- raw account capture ---------------------------------------------------------------------
    // The paper capture layer's three reads. All read-only, all on the gateway's /ibkr/account/*
    // surface, and all classified into a named refusal rather than a null: a capture pass that
    // cannot read the broker has to record WHY, and "no answer" and "an empty account" must never
    // arrive here looking the same.

    public Task<AccountSummaryRead> GetAccountSummaryAsync(string? accountId, CancellationToken cancellationToken) =>
        ReadAccountAsync<AccountSummaryRead>(
            $"/ibkr/account/summary{AccountQuery(accountId)}", "account summary", cancellationToken);

    public Task<AccountPositionsRead> GetPositionsAsync(string? accountId, CancellationToken cancellationToken) =>
        ReadAccountAsync<AccountPositionsRead>(
            $"/ibkr/account/positions{AccountQuery(accountId)}", "positions", cancellationToken);

    /// <summary>
    /// The account's executions since <paramref name="sinceUtc"/>. The bound is explicit because the
    /// gateway requires it — see its endpoint's remarks.
    /// </summary>
    public Task<AccountExecutionsRead> GetExecutionsAsync(
        string? accountId, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var since = Uri.EscapeDataString(sinceUtc.ToUniversalTime().ToString("O"));
        var account = accountId is { Length: > 0 } id ? $"&accountId={Uri.EscapeDataString(id)}" : string.Empty;

        return ReadAccountAsync<AccountExecutionsRead>(
            $"/ibkr/account/executions?since={since}{account}", "executions", cancellationToken);
    }

    private static string AccountQuery(string? accountId) =>
        accountId is { Length: > 0 } id ? $"?accountId={Uri.EscapeDataString(id)}" : string.Empty;

    private async Task<T> ReadAccountAsync<T>(string path, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync(path, cancellationToken);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            // Reuses the send-side classifier the backfill path already depends on, so an open
            // circuit and a refused connection land as unreachable rather than escaping unclassified.
            var (outcome, detail) = ClassifyTransportFailure(ex);

            throw new GatewayReadException(
                outcome == GatewayOutcome.Unreachable
                    ? GatewayRefusalKinds.GatewayUnreachable
                    : GatewayRefusalKinds.GatewayRefused,
                $"Could not read {what} from the IBKR gateway. {detail}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var (_, detail) = await ReadProblemAsync(response, cancellationToken);

                throw new GatewayReadException(
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                        ? GatewayRefusalKinds.BrokerNotConnected
                        : GatewayRefusalKinds.GatewayRefused,
                    $"The IBKR gateway refused the {what} read ({(int)response.StatusCode}). {detail}");
            }

            T? body;

            try
            {
                body = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            }
            catch (Exception ex) when (IsBodyReadFailure(ex, cancellationToken))
            {
                throw new GatewayReadException(
                    GatewayRefusalKinds.GatewayRefused,
                    $"The IBKR gateway's {what} response body could not be read. {ex.Message}");
            }

            return body ?? throw new GatewayReadException(
                GatewayRefusalKinds.GatewayRefused,
                $"The IBKR gateway returned an empty {what} body.");
        }
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
                HistoricalBarsResponseDto? body;

                try
                {
                    body = await response.Content.ReadFromJsonAsync<HistoricalBarsResponseDto>(cancellationToken);
                }
                catch (Exception ex) when (IsBodyReadFailure(ex, cancellationToken))
                {
                    // Transient, not Unreachable: the gateway answered 200, so the request reached
                    // TWS and may well have consumed a paced request slot. Classifying rather than
                    // throwing is the point — see IsBodyReadFailure.
                    return new HistoricalBarsResult(
                        GatewayOutcome.Transient, [], null, null,
                        $"The gateway's 200 response body could not be read. {ex.Message}");
                }

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
                HeadTimestampResponseDto? body;

                try
                {
                    body = await response.Content.ReadFromJsonAsync<HeadTimestampResponseDto>(cancellationToken);
                }
                catch (Exception ex) when (IsBodyReadFailure(ex, cancellationToken))
                {
                    return new HeadTimestampResult(
                        GatewayOutcome.Transient, null, null, null,
                        $"The gateway's 200 response body could not be read. {ex.Message}");
                }

                return body is null
                    ? new HeadTimestampResult(GatewayOutcome.Transient, null, null, null, "The gateway returned an empty body.")
                    : new HeadTimestampResult(GatewayOutcome.Ok, body.HeadTimestamp.ToUniversalTime(), null, null, null);
            }

            var (outcome, retryAfter, errorCode, detail) = await ClassifyFailureAsync(response, cancellationToken);
            return new HeadTimestampResult(outcome, null, retryAfter, errorCode, detail);
        }
    }

    /// <summary>
    /// Whether an exception thrown by <b>the send</b> is a transport failure this classifier owns,
    /// rather than the caller's own cancellation.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than <c>HttpRequestException or TaskCanceledException</c>. The resilience
    /// pipeline this client is built on (see <c>ServiceClientConfiguration.DisableAutomaticRetries</c>)
    /// carries a circuit breaker, and an open circuit throws <see cref="BrokenCircuitException"/> —
    /// which matched neither arm of the original filter, escaped this method entirely, and surfaced
    /// at the coordinator's outermost catch. That path never writes an outcome, so the claimed row
    /// stayed <c>inflight</c> until a reaper turned it into <c>failed</c> with its attempt already
    /// burned: the one failure shape that both loses the slice AND spends its retry budget.
    /// <para>
    /// <b>This covers the send and nothing else, and the earlier claim that it closed the whole
    /// stranded-claim class was wrong.</b> Reading the response body is a second network operation
    /// that throws its own exceptions (a connection reset part-way through a 200, a truncated JSON
    /// document) — <see cref="IsBodyReadFailure"/> is what classifies those. The bookkeeping AFTER
    /// this client returns is a third region again, and it is guarded where it lives, by
    /// <c>BackfillCoordinator.ExecuteSliceAsync</c>. Whoever widens one of the three should not
    /// assume the other two came with it.
    /// </para>
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
    /// Whether an exception thrown while reading an already-received response body is one this
    /// classifier owns.
    /// </summary>
    /// <remarks>
    /// The status line can arrive and the body still fail: a reset connection mid-stream surfaces as
    /// <see cref="HttpRequestException"/> or <see cref="IOException"/>, a truncated document as
    /// <see cref="JsonException"/>. None of those reached the send's catch — it had already
    /// completed — so they escaped to the coordinator, which is the stranded-claim path this client's
    /// classification exists to keep closed. Everything here maps to
    /// <see cref="GatewayOutcome.Transient"/> rather than <see cref="GatewayOutcome.Unreachable"/>:
    /// a response was produced, so TWS was reached and a paced slot was spent.
    /// </remarks>
    private static bool IsBodyReadFailure(Exception ex, CancellationToken cancellationToken) =>
        // The same predicate as IsTransportFailure — anything that is not the caller's own
        // cancellation — shared rather than restated so widening one cannot leave the other behind.
        // What differs is the REGION it guards and therefore the outcome the call site returns.
        IsTransportFailure(ex, cancellationToken);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A non-ProblemDetails body (a proxy's HTML error page, say) still classifies fine on
            // status code alone; losing the detail text is not worth failing the request over.
            // Widened from JsonException for the reason the whole class of fix here shares: the READ
            // can fail as well as the parse, and an IOException escaping this method would take the
            // already-decided status-code classification with it and strand the caller's claim.
            return (null, response.ReasonPhrase);
        }
    }

    /// <summary>
    /// Mirror of the gateway's portfolio response, matched by property name like every other DTO here.
    /// </summary>
    /// <remarks>
    /// Only the portfolio itself is projected. The gateway's completeness flags (daily P&amp;L
    /// availability, Greek coverage) are risk inputs and are already acted on by
    /// <c>IbkrPortfolioProvider</c>; the exit branch reads positions and nothing else, and mirroring
    /// fields it does not use would invite someone to start using them here instead of there.
    /// </remarks>
    private sealed record GatewayPortfolioResponse(PortfolioSnapshot? Portfolio);

    private sealed record ResolveContractsRequestDto(IReadOnlyList<OptionContract> Contracts);

    private sealed record ResolvedContractDto(OptionContract Contract, int? ConId, string? Error);

    private sealed record ResolveContractsResponseDto(IReadOnlyList<ResolvedContractDto> Resolved);
}
