using System.Collections.Concurrent;
using System.Globalization;
using IBApi;
using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
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

    // ---- chains -----------------------------------------------------------------------------

    /// <summary>
    /// Returns a strike window of the chain for one expiration, centred on spot.
    /// </summary>
    /// <remarks>
    /// TWS returns expirations and strikes as two independent sets, not a validated cross-product —
    /// not every (expiry, strike) pair is actually listed. Contracts returned here are therefore
    /// candidates; resolving one to a conId is what proves it exists.
    /// </remarks>
    public async Task<IReadOnlyList<OptionContract>> GetOptionChainAsync(
        string underlying,
        DateOnly? expiration,
        int? strikeWindow,
        CancellationToken cancellationToken,
        string? tradingClass = null)
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
            return [];
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
            return [];
        }

        var target = SelectExpiration(expirations, expiration);
        var spot = await TryGetSpotPriceAsync(symbol, definition, cancellationToken);
        var window = strikeWindow ?? _options.ChainStrikeWindow;

        var strikes = segment.Strikes
            .Where(strike => strike > 0d)
            .Select(strike => (decimal)strike)
            .Distinct()
            .OrderBy(strike => strike)
            .ToArray();

        var selected = spot is null
            ? strikes
            : strikes
                .OrderBy(strike => Math.Abs(strike - spot.Value))
                .Take((window * 2) + 1)
                .OrderBy(strike => strike)
                .ToArray();

        if (spot is null)
        {
            logger.LogWarning(
                "No spot price for {Underlying}; returning the full strike list rather than a window.",
                symbol);
        }

        var multiplier = int.TryParse(segment.Multiplier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMultiplier)
            ? parsedMultiplier
            : 100;

        return selected
            .SelectMany(strike => new[]
            {
                BuildContract(symbol, target, strike, OptionRight.Call, multiplier, segment.TradingClass),
                BuildContract(symbol, target, strike, OptionRight.Put, multiplier, segment.TradingClass),
            })
            .ToArray();
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
