using System.Collections.Concurrent;
using System.Globalization;
using IBApi;
using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway.History;
using TradingStuff.IbkrGateway.Pacing;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// Option contract resolution, chains, and quotes against the single TWS socket.
/// </summary>
public sealed class IbkrMarketDataClient(
    IbkrConnection connection,
    PacedSocket socket,
    IOptions<IbkrOptions> options,
    ILogger<IbkrMarketDataClient> logger)
{
    private const string ExpirationFormat = "yyyyMMdd";

    // conId resolution is a round trip and TWS paces requests hard, so results are cached for the
    // process lifetime. Contract definitions do not change intraday.
    private readonly ConcurrentDictionary<OptionContractKey, int> _optionConIds = new();
    private readonly ConcurrentDictionary<string, UnderlyingDefinition> _underlyings = new(StringComparer.OrdinalIgnoreCase);

    private readonly IbkrOptions _options = options.Value;

    private string QuoteSource => _options.MarketDataType == 1 ? "ibkr-live" : "ibkr-delayed";

    // ---- contract resolution --------------------------------------------------------------

    /// <summary>Resolves an option to its IBKR conId, the only unambiguous contract identifier.</summary>
    public async Task<int> ResolveOptionConIdAsync(OptionContract contract, CancellationToken cancellationToken)
    {
        var key = contract.Key();

        if (_optionConIds.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var details = await RequestListAsync<ContractDetails>(
            (requestId, ct) => socket.ReqContractDetailsAsync(requestId, ToIbOption(contract), ct),
            cancellationToken);

        if (details.Count == 0)
        {
            throw new IbkrRequestException(
                IbkrErrorCodes.NoSecurityDefinition,
                $"No IBKR contract matches {Describe(contract)}.");
        }

        if (details.Count > 1)
        {
            // Usually an under-specified contract — a missing TradingClass (SPXW vs SPX) or a
            // multiplier that matches more than one listed series.
            logger.LogWarning(
                "{Count} contracts matched {Contract}; using the first. Consider specifying a trading class.",
                details.Count,
                Describe(contract));
        }

        var conId = details[0].Contract.ConId;
        _optionConIds[key] = conId;
        return conId;
    }

    /// <summary>Identifies the underlying: its conId and whether it is a stock or an index.</summary>
    public sealed record UnderlyingDefinition(int ConId, string SecType, string Exchange);

    /// <summary>
    /// Resolves the underlying's conId and security type, required before requesting a chain.
    /// </summary>
    /// <remarks>
    /// Index underlyings are <c>IND</c> on their listing exchange, not <c>STK</c> on <c>SMART</c> —
    /// SPX, NDX, RUT, VIX and friends resolve to nothing as a stock. Rather than hard-coding a list
    /// of index symbols, this tries the stock definition and falls back to the index one, so any
    /// index works without configuration.
    /// </remarks>
    public async Task<UnderlyingDefinition> ResolveUnderlyingAsync(string underlying, CancellationToken cancellationToken)
    {
        var symbol = underlying.ToUpperInvariant();

        if (_underlyings.TryGetValue(symbol, out var cached))
        {
            return cached;
        }

        var candidates = new (string SecType, string Exchange)[]
        {
            ("STK", "SMART"),
            ("IND", "CBOE"),
            ("IND", "SMART"),
        };

        foreach (var (secType, exchange) in candidates)
        {
            var details = await TryResolveAsync(symbol, secType, exchange, cancellationToken);

            if (details is null || details.Count == 0)
            {
                continue;
            }

            var resolved = new UnderlyingDefinition(details[0].Contract.ConId, secType, exchange);
            _underlyings[symbol] = resolved;

            logger.LogDebug(
                "Resolved underlying {Symbol} as {SecType} on {Exchange} (conId {ConId}).",
                symbol,
                secType,
                exchange,
                resolved.ConId);

            return resolved;
        }

        throw new IbkrRequestException(
            IbkrErrorCodes.NoSecurityDefinition,
            $"No IBKR contract matches underlying {symbol} as a stock or an index.");
    }

    private async Task<IReadOnlyList<ContractDetails>?> TryResolveAsync(
        string symbol,
        string secType,
        string exchange,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RequestListAsync<ContractDetails>(
                (requestId, ct) => socket.ReqContractDetailsAsync(requestId, new IbContract
                {
                    Symbol = symbol,
                    SecType = secType,
                    Exchange = exchange,
                    Currency = "USD",
                }, ct),
                cancellationToken);
        }
        catch (IbkrRequestException ex) when (ex.ErrorCode == IbkrErrorCodes.NoSecurityDefinition)
        {
            // Expected while probing: a stock lookup for an index symbol finds nothing.
            return null;
        }
    }

    // ---- futures family enumeration ----------------------------------------------------------

    /// <summary>
    /// Enumerates every contract IBKR lists for a futures family — expired and currently listed
    /// alike — via <c>reqContractDetails</c> with <c>IncludeExpired</c>. See
    /// <see cref="FuturesContractDefinition"/> for why this exists: a deep intraday backfill cannot
    /// page a <c>CONTFUT</c> into the past, so it must instead walk each individual contract, and
    /// this is how the walker (ResearchService's <c>EsContractWalker</c>) discovers what they are.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResolveOptionConIdAsync"/>, an empty result is not turned into an
    /// exception here: the caller (a periodic scan) treats "nothing back this pass" as "try again
    /// later", not a hard failure, and a family enumeration returning zero rows is not on its own
    /// evidence of a broken symbol/exchange the way an unresolved single option contract is.
    /// </remarks>
    public async Task<IReadOnlyList<FuturesContractDefinition>> GetFuturesFamilyAsync(
        string symbol, string exchange, string currency, CancellationToken cancellationToken)
    {
        var details = await RequestListAsync<ContractDetails>(
            (requestId, ct) => socket.ReqContractDetailsAsync(requestId, new IbContract
            {
                Symbol = symbol.ToUpperInvariant(),
                SecType = "FUT",
                Exchange = exchange,
                Currency = currency,
                IncludeExpired = true,
            }, ct),
            cancellationToken);

        var contracts = new List<FuturesContractDefinition>(details.Count);

        foreach (var detail in details)
        {
            if (FuturesContractExpiry.Resolve(detail) is not { } expiry)
            {
                // Should not happen given IBKR's documented field shapes, but a contract this
                // walker cannot date is one it must not silently mis-plan either.
                logger.LogWarning(
                    "Skipping a {Symbol} futures contract (conId {ConId}): neither RealExpirationDate " +
                    "('{Real}') nor LastTradeDateOrContractMonth ('{Raw}') parsed as a date.",
                    symbol, detail.Contract.ConId, detail.RealExpirationDate, detail.Contract.LastTradeDateOrContractMonth);
                continue;
            }

            contracts.Add(new FuturesContractDefinition(
                detail.Contract.ConId, expiry, detail.Contract.TradingClass, detail.Contract.Exchange, detail.Contract.Currency));
        }

        return contracts;
    }

    // ---- chains -----------------------------------------------------------------------------

    /// <summary>
    /// Returns the contracts IBKR actually lists for one expiration, restricted to a window around spot.
    /// </summary>
    /// <param name="strikeHalfCount">
    /// Window expressed as a COUNT of strikes each side of spot — <c>20</c> means the 41 strikes
    /// nearest spot, NOT ±20% and not ±20 anything else. Ignored when
    /// <paramref name="moneynessHalfWidth"/> is supplied. Null uses <see cref="IbkrOptions.ChainStrikeWindow"/>.
    /// </param>
    /// <param name="moneynessHalfWidth">
    /// Window expressed as a FRACTION of spot — <c>0.20m</c> means <c>spot × [0.80, 1.20]</c>. Wins
    /// over <paramref name="strikeHalfCount"/> when both are given. This exists because a caller
    /// selecting strikes by moneyness cannot express its requirement as a strike count: how far
    /// 41 strikes reaches depends entirely on the local strike increment, which for SPX is 5 points
    /// near the money — so <c>strikeHalfCount: 20</c> covers ±1.3% of a 7,440 spot, and a caller
    /// asking for ±15% silently gets the window's edge instead. Measured live 2026-08-01, SPX at
    /// 7437.63: reaching ±15% needs ~423 strikes each side. Say what you mean instead.
    /// </param>
    /// <remarks>
    /// Two things about the shape of this, both of which cost real data before they were understood.
    /// <para>
    /// <b>The strikes come from <c>reqContractDetails</c>, not <c>reqSecDefOptParams</c>.</b> The
    /// latter returns expirations and strikes as two independent sets whose cross-product is NOT the
    /// listed chain — the strike set is the union across every expiration in the class, and a strike
    /// listed for one expiration is frequently not listed for another. Verified live 2026-08-01:
    /// SPXW 2026-08-06 P 6620 is in the union and resolves to error 200 (no security definition),
    /// while 6625 resolves fine; at 2026-09-14 the near-the-money increment is 25 points, so 6990 is
    /// likewise a phantom. Windowing the union therefore yields contracts that do not exist for
    /// exactly the far-from-the-money nodes a research grid cares about. One
    /// <c>reqContractDetails</c> for the whole expiration returns the real ladder WITH conIds
    /// (476 rows/270 ms … 1004 rows/5.1 s, measured live), which also warms
    /// <see cref="_optionConIds"/> and makes the caller's subsequent per-contract resolution free —
    /// so this costs one paced request and saves several.
    /// </para>
    /// <para>
    /// <b>The window is never silently degraded.</b> See <see cref="OptionChainResult"/>.
    /// </para>
    /// </remarks>
    public async Task<OptionChainResult> GetOptionChainAsync(
        string underlying,
        DateOnly? expiration,
        int? strikeHalfCount,
        CancellationToken cancellationToken,
        string? tradingClass = null,
        decimal? moneynessHalfWidth = null)
    {
        var symbol = underlying.ToUpperInvariant();
        var definition = await ResolveUnderlyingAsync(symbol, cancellationToken);

        var segments = await RequestListAsync<OptionChainSegment>(
            (requestId, ct) => socket.ReqSecDefOptParamsAsync(
                requestId,
                symbol,
                string.Empty,
                definition.SecType,
                definition.ConId,
                ct),
            cancellationToken);

        if (segments.Count == 0)
        {
            return OptionChainResult.NotAvailable($"TWS listed no option chain segments for {symbol}.");
        }

        foreach (var candidate in segments)
        {
            logger.LogDebug(
                "Chain segment for {Underlying}: exchange={Exchange} tradingClass={TradingClass} " +
                "multiplier={Multiplier} expirations={ExpirationCount} strikes={StrikeCount}",
                symbol,
                candidate.Exchange,
                candidate.TradingClass,
                candidate.Multiplier,
                candidate.Expirations.Count,
                candidate.Strikes.Count);
        }

        var segment = SelectChainSegment(segments, symbol, tradingClass);

        logger.LogDebug(
            "Selected chain segment for {Underlying}: exchange={Exchange} tradingClass={TradingClass} " +
            "expirations={ExpirationCount} strikes={StrikeCount}",
            symbol,
            segment.Exchange,
            segment.TradingClass,
            segment.Expirations.Count,
            segment.Strikes.Count);

        var expirations = segment.Expirations
            .Select(value => DateOnly.TryParseExact(value, ExpirationFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ? parsed : (DateOnly?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();

        if (expirations.Length == 0)
        {
            return OptionChainResult.NotAvailable($"TWS listed no parseable expirations for {symbol}.");
        }

        var target = SelectExpiration(expirations, expiration);
        var spot = await TryGetSpotPriceAsync(symbol, definition, cancellationToken);

        if (spot is null)
        {
            // Refuse, loudly, rather than returning the whole strike list and letting it read as a
            // window. See OptionChainResult's remarks: the degraded response was indistinguishable
            // from a healthy one at every caller, which is how it cost a full node grid.
            logger.LogWarning(
                "No spot price for {Underlying}; refusing to return a chain window that is not centred on spot.",
                symbol);

            return OptionChainResult.NotAvailable(
                $"No spot price is available for {symbol}, so no window can be centred on it.", target);
        }

        var listed = await ListExpirationAsync(symbol, target, segment, cancellationToken);

        if (listed.Count == 0)
        {
            return OptionChainResult.NotAvailable(
                $"TWS lists no {segment.TradingClass} contracts for {symbol} expiring {target:yyyy-MM-dd}.", target);
        }

        var strikes = listed.Select(contract => contract.Strike).Distinct().OrderBy(strike => strike).ToArray();

        var selected = moneynessHalfWidth is { } halfWidth && halfWidth > 0m
            ? strikes.Where(strike =>
                strike >= spot.Value * (1m - halfWidth) && strike <= spot.Value * (1m + halfWidth)).ToArray()
            : strikes
                .OrderBy(strike => Math.Abs(strike - spot.Value))
                .Take(((strikeHalfCount ?? _options.ChainStrikeWindow) * 2) + 1)
                .OrderBy(strike => strike)
                .ToArray();

        if (selected.Length == 0)
        {
            return OptionChainResult.NotAvailable(
                $"No listed {segment.TradingClass} strike for {symbol} {target:yyyy-MM-dd} falls inside the " +
                $"requested window around {spot.Value}.", target);
        }

        var keep = selected.ToHashSet();
        var contracts = listed.Where(contract => keep.Contains(contract.Strike)).ToArray();

        logger.LogDebug(
            "Chain window for {Underlying} {TradingClass} {Expiration}: {Contracts} contract(s) over strikes " +
            "{Low}-{High}, spot {Spot}, from {Listed} listed.",
            symbol, segment.TradingClass, target, contracts.Length, selected[0], selected[^1], spot.Value, listed.Count);

        return new OptionChainResult(
            contracts, SpotCentred: true, spot.Value, target, selected[0], selected[^1], Unavailable: null);
    }

    /// <summary>
    /// The real listed ladder for one expiration, straight from <c>reqContractDetails</c>, with each
    /// contract's conId cached on the way past.
    /// </summary>
    private async Task<IReadOnlyList<OptionContract>> ListExpirationAsync(
        string symbol,
        DateOnly expiration,
        OptionChainSegment segment,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContractDetails> details;

        try
        {
            details = await RequestListAsync<ContractDetails>(
                (requestId, ct) => socket.ReqContractDetailsAsync(requestId, new IbContract
                {
                    Symbol = symbol,
                    SecType = "OPT",
                    // SMART, not segment.Exchange: the segment is identified by its trading class
                    // (see SelectChainSegment) and its exchange field is frequently not the one to
                    // route on. Verified live for both SPX and SPXW.
                    Exchange = "SMART",
                    Currency = "USD",
                    LastTradeDateOrContractMonth = expiration.ToString(ExpirationFormat, CultureInfo.InvariantCulture),
                    TradingClass = segment.TradingClass,
                    Multiplier = segment.Multiplier,
                }, ct),
                cancellationToken);
        }
        catch (IbkrRequestException ex) when (ex.ErrorCode == IbkrErrorCodes.NoSecurityDefinition)
        {
            logger.LogWarning(
                "TWS lists no {TradingClass} contracts for {Symbol} expiring {Expiration}.",
                segment.TradingClass, symbol, expiration);
            return [];
        }

        var contracts = new List<OptionContract>(details.Count);

        foreach (var detail in details)
        {
            var ib = detail.Contract;

            if (ib.Strike <= 0d || string.IsNullOrEmpty(ib.Right))
            {
                continue;
            }

            var right = ib.Right.StartsWith('C') || ib.Right.StartsWith('c') ? OptionRight.Call : OptionRight.Put;

            var listedExpiration = DateOnly.TryParseExact(
                ib.LastTradeDateOrContractMonth, ExpirationFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ? parsed : expiration;

            var multiplier = int.TryParse(ib.Multiplier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMultiplier)
                ? parsedMultiplier
                : 100;

            var contract = BuildContract(
                symbol, listedExpiration, (decimal)ib.Strike, right, multiplier,
                string.IsNullOrWhiteSpace(ib.TradingClass) ? segment.TradingClass : ib.TradingClass);

            // Free conId resolution: the caller would otherwise spend one paced reqContractDetails
            // per contract re-asking TWS what it just said.
            _optionConIds[contract.Key()] = ib.ConId;
            contracts.Add(contract);
        }

        return contracts;
    }

    // ---- quotes -----------------------------------------------------------------------------

    public async Task<MarketDataQuoteResponse> GetQuotesAsync(
        IReadOnlyList<OptionContract> contracts,
        CancellationToken cancellationToken)
    {
        var source = QuoteSource;

        // Bounded fan-out: an unbounded WhenAll over a large chain (SPY lists ~978 contracts)
        // would park hundreds of waiters on the line ledger and fail most of them by timeout.
        using var fanOut = new SemaphoreSlim(_options.Pacing.QuoteFanOutLimit);

        var quotes = await Task.WhenAll(contracts.Select(async contract =>
        {
            await fanOut.WaitAsync(cancellationToken);

            try
            {
                return await GetQuoteAsync(contract, source, cancellationToken);
            }
            finally
            {
                fanOut.Release();
            }
        }));

        return new MarketDataQuoteResponse(quotes, DateTimeOffset.UtcNow, source);
    }

    private async Task<QuoteSnapshot> GetQuoteAsync(
        OptionContract contract,
        string source,
        CancellationToken cancellationToken)
    {
        var registry = connection.Registry;
        var tickerId = registry.NextRequestId();
        var request = new QuoteRequest(contract, source);

        registry.Register(tickerId, request);

        LineLease? lease = null;

        try
        {
            // Execution class: transient pre-trade/portfolio quotes may draw on the full line
            // budget, including the reserve that research recording can never touch.
            lease = await socket.ReqMktDataAsync(
                tickerId, ToIbOption(contract), string.Empty, false, false, null,
                LineClass.Execution, cancellationToken);

            try
            {
                return await request.Task
                    .WaitAsync(TimeSpan.FromSeconds(_options.QuoteTimeoutSeconds), cancellationToken);
            }
            catch (TimeoutException)
            {
                // Illiquid series may never publish a complete set. A partial snapshot with the
                // missing fields zeroed beats failing the whole order's quote request.
                logger.LogWarning(
                    "Quote for {Contract} incomplete after {Timeout}s; returning a partial snapshot.",
                    Describe(contract),
                    _options.QuoteTimeoutSeconds);

                request.CompletePartial();
                return await request.Task;
            }
        }
        finally
        {
            // Releases the market-data line whether or not the cancel message lands.
            if (lease is not null)
            {
                await socket.CancelMktDataAsync(tickerId, lease);
            }

            registry.Remove(tickerId);
        }
    }

    private async Task<decimal?> TryGetSpotPriceAsync(
        string underlying,
        UnderlyingDefinition definition,
        CancellationToken cancellationToken)
    {
        var registry = connection.Registry;
        var tickerId = registry.NextRequestId();
        var request = new SpotPriceRequest();

        registry.Register(tickerId, request);

        LineLease? lease = null;

        try
        {
            lease = await socket.ReqMktDataAsync(tickerId, new IbContract
            {
                Symbol = underlying,
                SecType = definition.SecType,
                Exchange = definition.Exchange,
                Currency = "USD",
            }, string.Empty, false, false, null, LineClass.Execution, cancellationToken);

            return await request.Task
                .WaitAsync(TimeSpan.FromSeconds(_options.QuoteTimeoutSeconds), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IbkrRequestException)
        {
            logger.LogWarning(ex, "Could not read a spot price for {Underlying}.", underlying);
            return null;
        }
        finally
        {
            if (lease is not null)
            {
                await socket.CancelMktDataAsync(tickerId, lease);
            }

            registry.Remove(tickerId);
        }
    }

    // ---- plumbing ---------------------------------------------------------------------------

    /// <summary>
    /// Issues a request that accumulates callbacks and completes on its <c>...End</c> callback.
    /// </summary>
    private async Task<IReadOnlyList<T>> RequestListAsync<T>(
        Func<int, CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        var registry = connection.Registry;
        var requestId = registry.NextRequestId();
        var request = new ListRequest<T>();

        registry.Register(requestId, request);

        try
        {
            await send(requestId, cancellationToken);

            return await request.Task
                .WaitAsync(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds), cancellationToken);
        }
        finally
        {
            registry.Remove(requestId);
        }
    }

    /// <summary>
    /// Picks the segment describing the <em>standard</em> option class for the symbol.
    /// </summary>
    /// <remarks>
    /// TWS returns one segment per (exchange, trading class) — for SPY that is 39 of them. Two traps:
    /// <list type="number">
    /// <item>Adjusted option classes produced by corporate actions appear alongside the standard one
    /// under a digit-prefixed trading class (<c>2SPY</c>). They list a handful of strikes, and
    /// treating one as the chain yields a near-empty, untradeable result.</item>
    /// <item>There is frequently <em>no SMART segment for the standard class</em>. SPY's only SMART
    /// row is the adjusted <c>2SPY</c> class, so preferring SMART actively selects the wrong one.
    /// SMART is still the right exchange to route and quote on; it just is not how the correct
    /// segment is identified.</item>
    /// </list>
    /// The reliable signal is trading class equal to the underlying symbol; strike count breaks
    /// any remaining tie.
    /// </remarks>
    internal static OptionChainSegment SelectChainSegment(
        IReadOnlyList<OptionChainSegment> segments,
        string symbol,
        string? tradingClass = null)
    {
        // An explicit trading class wins. This is how you ask for SPXW (PM-settled weeklies and
        // dailies, which trade in global hours) rather than SPX (AM-settled monthlies).
        if (!string.IsNullOrWhiteSpace(tradingClass))
        {
            var requested = segments
                .Where(segment => segment.TradingClass.Equals(tradingClass, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (requested.Length > 0)
            {
                return requested.MaxBy(segment => segment.Strikes.Count)!;
            }
        }

        var standard = segments
            .Where(segment => segment.TradingClass.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var candidates = standard.Length > 0 ? standard : segments;

        // Every exchange lists the same strikes for a given class, so the richest one is the
        // complete picture.
        return candidates.MaxBy(segment => segment.Strikes.Count)!;
    }

    private static DateOnly SelectExpiration(DateOnly[] available, DateOnly? requested)
    {
        if (requested is null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            return available.FirstOrDefault(value => value >= today, available[0]);
        }

        // Requested dates rarely land exactly on a listed expiry, so snap to the nearest listed one.
        return available.MinBy(value => Math.Abs(value.DayNumber - requested.Value.DayNumber));
    }

    private static OptionContract BuildContract(
        string underlying,
        DateOnly expiration,
        decimal strike,
        OptionRight right,
        int multiplier,
        string? tradingClass)
    {
        var rightCode = right == OptionRight.Call ? "C" : "P";
        var series = string.IsNullOrWhiteSpace(tradingClass) ? underlying : tradingClass;
        var symbol = $"{series}{expiration:yyyyMMdd}{rightCode}{strike:0.##}";

        return new OptionContract(
            symbol,
            underlying,
            expiration,
            strike,
            right,
            Multiplier: multiplier,
            TradingClass: tradingClass);
    }

    internal static IbContract ToIbOption(OptionContract contract)
    {
        var ibContract = new IbContract
        {
            Symbol = contract.Underlying.ToUpperInvariant(),
            SecType = "OPT",
            Exchange = contract.Exchange,
            Currency = contract.Currency,
            LastTradeDateOrContractMonth = contract.Expiration.ToString(ExpirationFormat, CultureInfo.InvariantCulture),
            Strike = (double)contract.Strike,
            Right = contract.Right == OptionRight.Call ? "C" : "P",
            Multiplier = contract.Multiplier.ToString(CultureInfo.InvariantCulture),
        };

        // Without this an SPX request matches both SPX and SPXW at the same strike and expiration,
        // and resolution silently picks whichever TWS returns first.
        if (!string.IsNullOrWhiteSpace(contract.TradingClass))
        {
            ibContract.TradingClass = contract.TradingClass;
        }

        return ibContract;
    }

    private static string Describe(OptionContract contract) =>
        $"{contract.Underlying} {contract.Expiration:yyyy-MM-dd} {contract.Strike} {contract.Right}";
}
