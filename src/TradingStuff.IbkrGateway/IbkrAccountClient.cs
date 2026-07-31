using System.Globalization;
using IBApi;
using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// A portfolio read, plus what the read could not establish.
/// </summary>
/// <remarks>
/// The flags are not decoration. A risk check fed a silently-defaulted input is worse than no check
/// at all: <c>MAX_DAILY_LOSS</c> cannot fire when daily P&amp;L defaults to zero, and the Greek
/// limits under-report when a position's Greeks are missing or when the account holds something this
/// options-only contract model cannot represent. Callers must be able to see that.
/// </remarks>
public sealed record IbkrPortfolioSnapshot(
    PortfolioSnapshot Portfolio,
    DateTimeOffset CapturedAt,
    bool DailyPnLAvailable,
    bool GreeksComplete,
    int OptionPositionCount,
    int NonOptionPositionCount);

/// <summary>
/// Read-only account summary, positions, and P&amp;L against the single TWS socket.
/// </summary>
/// <remarks>
/// Nothing here places, modifies, or cancels an order — every request is a subscription that is read
/// once and cancelled. Its purpose is to replace the fabricated portfolio the risk engine was being
/// fed with the real state of the account orders are actually routed to.
/// </remarks>
public sealed class IbkrAccountClient(
    IbkrConnection connection,
    Pacing.PacedSocket socket,
    IbkrMarketDataClient marketData,
    IOptions<IbkrOptions> options,
    ILogger<IbkrAccountClient> logger)
{
    private const string ExpirationFormat = "yyyyMMdd";

    /// <summary>
    /// Tags requested from <c>reqAccountSummary</c>. All are balances in the account's base currency.
    /// </summary>
    private const string SummaryTags = "NetLiquidation,BuyingPower,AvailableFunds,ExcessLiquidity,GrossPositionValue";

    /// <summary>Buying power, in preference order — not every account type reports every tag.</summary>
    private static readonly string[] BuyingPowerTags = ["BuyingPower", "AvailableFunds", "ExcessLiquidity"];

    private readonly IbkrOptions _options = options.Value;

    // Serialises refreshes rather than merely deduplicating them: every option position costs a
    // market data line to quote, and lines are capped per account (100 by default). Concurrent order
    // submissions must not each open their own fan-out.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Account and snapshot together, so a cache read cannot see one without the other.</summary>
    private sealed record CachedPortfolio(string Account, IbkrPortfolioSnapshot Snapshot);

    private CachedPortfolio? _cached;

    /// <summary>
    /// The account's real buying power, daily P&amp;L, positions, and aggregate Greeks.
    /// </summary>
    /// <param name="accountId">
    /// Optional. Must name a TWS-managed account when supplied; omit it to read the account this
    /// gateway trades.
    /// </param>
    public async Task<IbkrPortfolioSnapshot> GetPortfolioAsync(string? accountId, CancellationToken cancellationToken)
    {
        var account = SelectAccount(connection.GetStatus().ManagedAccounts, accountId, _options.AccountId);
        var ttl = TimeSpan.FromSeconds(_options.PortfolioCacheSeconds);

        if (TryGetCached(account, ttl) is { } fresh)
        {
            return fresh;
        }

        await _refreshGate.WaitAsync(cancellationToken);

        try
        {
            // Re-check under the gate: several orders submitted together would otherwise each refresh
            // after queueing behind the first.
            if (TryGetCached(account, ttl) is { } settled)
            {
                return settled;
            }

            var snapshot = await ReadPortfolioAsync(account, cancellationToken);

            _cached = new CachedPortfolio(account, snapshot);

            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private IbkrPortfolioSnapshot? TryGetCached(string account, TimeSpan ttl)
    {
        var cached = _cached;

        if (cached is null || !string.Equals(cached.Account, account, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DateTimeOffset.UtcNow - cached.Snapshot.CapturedAt < ttl ? cached.Snapshot : null;
    }

    private async Task<IbkrPortfolioSnapshot> ReadPortfolioAsync(string account, CancellationToken cancellationToken)
    {
        // No round trip: the streams are already open and TWS has been pushing into them.
        var feed = await EnsureFeedAsync(account, cancellationToken);

        var summary = feed.Summary.Values;
        var positionRows = feed.Positions.Rows;

        var pnl = ReadableDailyPnL(feed.Pnl);

        var buyingPower = ReadDecimal(summary, account, BuyingPowerTags);

        if (buyingPower is null)
        {
            throw new IbkrRequestException(
                IbkrErrorCodes.NoSecurityDefinition,
                $"TWS returned no buying-power tag ({string.Join(", ", BuyingPowerTags)}) for the requested account.");
        }

        var options = new List<(OptionContract Contract, decimal Quantity, double AverageCost)>();
        var nonOptionCount = 0;

        foreach (var row in positionRows)
        {
            // A closed position lingers as a zero-quantity row until TWS drops it.
            if (row.Position == 0m)
            {
                continue;
            }

            if (TryToOptionContract(row.Contract) is { } contract)
            {
                options.Add((contract, row.Position, row.AverageCost));
            }
            else
            {
                nonOptionCount++;
            }
        }

        if (nonOptionCount > 0)
        {
            // Not a silent omission: PositionSnapshot carries an OptionContract, so equities and
            // futures in the account have no representation and their delta goes uncounted.
            logger.LogWarning(
                "{Count} non-option position(s) are excluded from the portfolio snapshot; " +
                "their exposure is not reflected in the Greek limits.",
                nonOptionCount);
        }

        var (positions, greeksComplete) = await BuildPositionsAsync(options, cancellationToken);

        var portfolio = new PortfolioSnapshot(
            account,
            buyingPower.Value,
            pnl?.DailyPnL ?? 0m,
            positions.Aggregate(GreeksVector.Zero, (total, position) => total + position.GreeksExposure),
            positions);

        logger.LogInformation(
            "Read portfolio: {OptionPositions} option position(s), {NonOptionPositions} excluded, " +
            "daily P&L {PnLState}, Greeks {GreeksState}.",
            positions.Count,
            nonOptionCount,
            pnl is null ? "unavailable" : "available",
            greeksComplete ? "complete" : "partial");

        return new IbkrPortfolioSnapshot(
            portfolio,
            DateTimeOffset.UtcNow,
            pnl is not null,
            greeksComplete,
            positions.Count,
            nonOptionCount);
    }

    private async Task<(IReadOnlyList<PositionSnapshot> Positions, bool GreeksComplete)> BuildPositionsAsync(
        IReadOnlyList<(OptionContract Contract, decimal Quantity, double AverageCost)> holdings,
        CancellationToken cancellationToken)
    {
        if (holdings.Count == 0)
        {
            return ([], true);
        }

        var greeksByContract = new Dictionary<OptionContractKey, OptionGreeks>();
        var greeksComplete = true;

        if (!_options.IncludePositionGreeks)
        {
            greeksComplete = false;
        }
        else if (holdings.Count > _options.MaxPositionsQuoted)
        {
            // Quoting past the market data line cap would fail the whole read, and a truncated set of
            // Greeks reported as complete is worse than an honest partial.
            logger.LogWarning(
                "{Count} option positions exceed the {Max}-position quote cap; Greeks are not being read.",
                holdings.Count,
                _options.MaxPositionsQuoted);

            greeksComplete = false;
        }
        else
        {
            try
            {
                var quotes = await marketData.GetQuotesAsync(
                    [.. holdings.Select(holding => holding.Contract)],
                    cancellationToken);

                foreach (var quote in quotes.Quotes)
                {
                    // A quote that settled on a timeout carries zeroed Greeks rather than none at
                    // all, and the source string is what says which happened. The all-zero check
                    // stays as a belt-and-braces second reading for any quote source that does not
                    // carry the marker.
                    if (QuoteRequest.IsPartial(quote) ||
                        quote.Greeks is { Delta: 0m, Gamma: 0m, Theta: 0m, Vega: 0m })
                    {
                        continue;
                    }

                    greeksByContract[quote.Contract.Key()] = quote.Greeks;
                }
            }
            catch (Exception ex) when (ex is IbkrRequestException or TimeoutException)
            {
                // One unquotable holding — an expiring series, a contract with no book — must not
                // cost the caller its buying power and positions, which are real and were read
                // successfully. Greeks degrade to incomplete and the flag says so.
                logger.LogWarning(ex, "Could not quote open positions for Greeks.");

                greeksComplete = false;
            }
        }

        var positions = new List<PositionSnapshot>(holdings.Count);

        foreach (var (contract, quantity, averageCost) in holdings)
        {
            greeksByContract.TryGetValue(contract.Key(), out var greeks);

            if (greeks is null && _options.IncludePositionGreeks)
            {
                greeksComplete = false;
            }

            positions.Add(ToPositionSnapshot(contract, quantity, averageCost, greeks));
        }

        return (positions, greeksComplete);
    }

    /// <summary>
    /// The account P&amp;L to report, or null when there is no live stream to report it from.
    /// </summary>
    /// <remarks>
    /// A faulted <see cref="PnLSubscription"/> keeps returning whatever it last received — the
    /// registry drops the entry on the TWS error that killed it, so nothing overwrites the value
    /// again. Serving that is worse than serving nothing: <c>MAX_DAILY_LOSS</c> would evaluate a
    /// figure frozen at the moment TWS stopped talking, while <c>DailyPnLAvailable</c> claimed it
    /// was current. Absence at least fails the check honestly and shows up in the flag.
    /// </remarks>
    internal static AccountPnL? ReadableDailyPnL(PnLSubscription? pnl) =>
        pnl is { Failure: null } ? pnl.Latest : null;

    // ---- the connection-scoped feed -------------------------------------------------------------

    /// <summary>The three open streams, the request ids they were issued under, and their connection.</summary>
    private sealed record AccountFeed(
        DateTimeOffset ConnectedAt,
        string Account,
        int SummaryRequestId,
        int PositionsRequestId,
        AccountSummarySubscription Summary,
        PositionsSubscription Positions,
        int? PnlRequestId,
        PnLSubscription? Pnl);

    /// <summary>
    /// How long to leave a P&amp;L stream alone after a failed open before trying again.
    /// </summary>
    /// <remarks>
    /// Retrying on every portfolio refresh would put a <c>PnLTimeoutSeconds</c> wait on the order
    /// path for as long as TWS refuses to serve P&amp;L, which is the wrong trade for a figure that
    /// is already reported as unavailable. Long enough not to matter, short enough that a transient
    /// fault does not disable <c>MAX_DAILY_LOSS</c> for the rest of the connection.
    /// </remarks>
    private static readonly TimeSpan PnLRecoveryBackoff = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _feedGate = new(1, 1);
    private AccountFeed? _feed;
    private DateTimeOffset _pnlRetryNotBefore = DateTimeOffset.MinValue;

    // The request ids the three account streams are ALWAYS issued under, allocated once and reused
    // for the life of the process. See RebuildFeedAsync for why they must not be fresh per rebuild.
    private bool _streamIdsAllocated;
    private int _summaryRequestId;
    private int _positionsRequestId;
    private int _pnlRequestId;

    /// <summary>
    /// Returns the open streams for this account, opening them if this connection has none.
    /// </summary>
    /// <remarks>
    /// A subscription belongs to the connection that opened it, so the feed is keyed on the
    /// connection's <see cref="IbkrConnectionStatus.ConnectedAt"/>. After a reconnect that value
    /// changes and the feed is rebuilt — serving values frozen at the moment of a disconnect would be
    /// exactly the stale-risk-input problem this whole stage exists to fix.
    /// </remarks>
    private async Task<AccountFeed> EnsureFeedAsync(string account, CancellationToken cancellationToken)
    {
        var connectedAt = connection.GetStatus().ConnectedAt
                          ?? throw new IbkrConnectionException("Not connected to TWS.");

        if (IsUsable(_feed, account, connectedAt) is { } live && !ShouldRetryPnL(live))
        {
            return live;
        }

        await _feedGate.WaitAsync(cancellationToken);

        try
        {
            // Re-checked under the gate: concurrent readers would otherwise each rebuild after
            // queueing behind the first, and a rebuild is the expensive, TWS-limited path.
            if (IsUsable(_feed, account, connectedAt) is { } settled)
            {
                if (!ShouldRetryPnL(settled))
                {
                    return settled;
                }

                var recovered = await ReopenPnLAsync(settled, cancellationToken);
                _feed = recovered;

                return recovered;
            }

            var feed = await RebuildFeedAsync(account, connectedAt, cancellationToken);
            _feed = feed;

            return feed;
        }
        finally
        {
            _feedGate.Release();
        }
    }

    /// <summary>
    /// The feed, if it belongs to this connection and account and neither structural stream died.
    /// </summary>
    /// <remarks>
    /// A dead P&amp;L stream deliberately does NOT invalidate the feed. It is optional by design
    /// (<see cref="TryOpenPnLAsync"/> degrades to unavailable), and tearing down a healthy summary
    /// and positions feed to recover it would spend one of TWS's two account-summary request slots
    /// per attempt. It is handled by re-opening that one stream instead — and until it is,
    /// <see cref="ReadPortfolioAsync"/> reports P&amp;L as unavailable rather than serving the last
    /// value it received before the fault.
    /// </remarks>
    private static AccountFeed? IsUsable(AccountFeed? feed, string account, DateTimeOffset connectedAt) =>
        feed is not null
        && feed.ConnectedAt == connectedAt
        && string.Equals(feed.Account, account, StringComparison.OrdinalIgnoreCase)
        && feed.Summary.Failure is null
        && feed.Positions.Failure is null
            ? feed
            : null;

    /// <summary>True when this feed has no live P&amp;L and enough time has passed to try again.</summary>
    private bool ShouldRetryPnL(AccountFeed feed) =>
        feed.Pnl is null or { Failure: not null } && DateTimeOffset.UtcNow >= _pnlRetryNotBefore;

    /// <summary>
    /// Replaces the feed: the previous streams are desubscribed and deregistered first, then
    /// re-issued under the SAME request ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter, and the second is the surprising one. Measured against TWS 223 on the
    /// paper account: TWS permits exactly <b>two</b> account-summary requests per API client and
    /// counts <em>distinct request ids</em>, not live subscriptions. Issuing a third id — the second
    /// rebuild of a session — fails with error 322 ("Maximum number of account summary requests
    /// exceeded; desubscribe to previous request first") and every later id fails the same way, so
    /// portfolio reads stay broken for the rest of the connection and the risk engine loses its
    /// inputs. <c>cancelAccountSummary</c> does <em>not</em> free the slot: five cycles of
    /// cancel-then-subscribe-with-a-fresh-id reproduced 322 on the third exactly as the leaking
    /// version did. Re-issuing on an id TWS has already seen does work, indefinitely — five
    /// rebuild cycles of all three streams, no errors.
    /// </para>
    /// <para>
    /// The cancel is still sent, because it is what stops TWS pushing into a subscription nothing
    /// reads, and it is sent BEFORE the new registration so a late error for the old stream cannot
    /// fault the replacement registered under the same id. It is skipped when the previous feed
    /// belongs to a connection that no longer exists — that socket took its subscriptions with it,
    /// and cancelling a request the current session never made only draws an error back.
    /// </para>
    /// </remarks>
    private async Task<AccountFeed> RebuildFeedAsync(
        string account,
        DateTimeOffset connectedAt,
        CancellationToken cancellationToken)
    {
        var registry = connection.Registry;
        EnsureStreamRequestIds();

        // Cleared before the teardown, so a failure part-way through can never leave a reference to
        // a feed whose streams have already been cancelled.
        var previous = _feed;
        _feed = null;

        if (previous is not null)
        {
            registry.Remove(previous.SummaryRequestId);
            registry.Remove(previous.PositionsRequestId);

            if (previous.PnlRequestId is { } previousPnlId)
            {
                registry.Remove(previousPnlId);
            }

            if (previous.ConnectedAt == connectedAt)
            {
                logger.LogInformation(
                    "Rebuilding the account feed on the live connection; desubscribing the previous streams.");

                await socket.CancelAccountStreamsAsync(
                    previous.SummaryRequestId, previous.PositionsRequestId, previous.PnlRequestId);
            }
        }

        return await OpenFeedAsync(account, connectedAt, cancellationToken);
    }

    /// <summary>
    /// The request ids the three account streams are issued under, allocated on first use and never
    /// reallocated. Call under <see cref="_feedGate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocation is deliberately lazy rather than done in the constructor: ids come from the
    /// sequence shared with order ids, and one allocated before TWS's <c>nextValidId</c> has raised
    /// that sequence would sit inside the range TWS's own order ids already occupy, making error
    /// routing ambiguous.
    /// </para>
    /// <para>
    /// Internal rather than private so a test can assert the ids do not move between rebuilds. That
    /// is the property TWS's two-request account-summary cap depends on (see
    /// <see cref="RebuildFeedAsync"/>), and it is otherwise unobservable from outside the gateway
    /// without a live socket — which is exactly how the leak it replaces went unnoticed.
    /// </para>
    /// </remarks>
    internal (int Summary, int Positions, int Pnl) EnsureStreamRequestIds()
    {
        if (!_streamIdsAllocated)
        {
            var registry = connection.Registry;

            _summaryRequestId = registry.NextRequestId();
            _positionsRequestId = registry.NextRequestId();
            _pnlRequestId = registry.NextRequestId();
            _streamIdsAllocated = true;
        }

        return (_summaryRequestId, _positionsRequestId, _pnlRequestId);
    }

    private async Task<AccountFeed> OpenFeedAsync(
        string account,
        DateTimeOffset connectedAt,
        CancellationToken cancellationToken)
    {
        var registry = connection.Registry;
        var timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);

        var summary = new AccountSummarySubscription();
        var positions = new PositionsSubscription();

        registry.Register(_summaryRequestId, summary);
        registry.Register(_positionsRequestId, positions);

        try
        {
            // "All" rather than the account id: TWS rejects a group naming a single account unless it
            // has been defined as an account group in TWS itself. Rows are filtered by account on the
            // way out.
            await socket.ReqAccountSummaryAsync(_summaryRequestId, "All", SummaryTags, cancellationToken);
            await socket.ReqPositionsMultiAsync(_positionsRequestId, account, string.Empty, cancellationToken);

            await summary.InitialDelivery.WaitAsync(timeout, cancellationToken);
            await positions.InitialDelivery.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            // A half-open feed must not be left behind. The registry entries would swallow every
            // later push, and the TWS-side subscriptions would keep running against a client that
            // has forgotten them.
            registry.Remove(_summaryRequestId);
            registry.Remove(_positionsRequestId);

            await socket.CancelAccountStreamsAsync(_summaryRequestId, _positionsRequestId, pnlId: null);

            throw;
        }

        var pnl = await TryOpenPnLAsync(registry, account, cancellationToken);

        logger.LogInformation(
            "Opened account streams for the connection established at {ConnectedAt} " +
            "(summary {SummaryId}, positions {PositionsId}, P&L {PnLState}).",
            connectedAt,
            _summaryRequestId,
            _positionsRequestId,
            pnl is null ? "unavailable" : "open");

        return new AccountFeed(
            connectedAt,
            account,
            _summaryRequestId,
            _positionsRequestId,
            summary,
            positions,
            pnl is null ? null : _pnlRequestId,
            pnl);
    }

    /// <summary>
    /// Replaces a missing or faulted P&amp;L stream on an otherwise healthy feed.
    /// </summary>
    /// <remarks>
    /// Without this, one TWS error against the P&amp;L request id disables <c>MAX_DAILY_LOSS</c> for
    /// the rest of the connection — the subscription faults, the registry drops it, and nothing ever
    /// re-opens it. Rate-limited by <see cref="PnLRecoveryBackoff"/> so a permanently unavailable
    /// P&amp;L does not add its timeout to every portfolio read.
    /// </remarks>
    private async Task<AccountFeed> ReopenPnLAsync(AccountFeed feed, CancellationToken cancellationToken)
    {
        var registry = connection.Registry;

        // Only the P&L stream is touched — the summary and positions streams are healthy, and a
        // rebuild of those is the expensive path this method exists to avoid. The cancel goes out
        // even when the last open failed by timeout: TWS may hold a subscription that never
        // delivered, and re-issuing on top of it is what the request-id reuse depends on.
        registry.Remove(_pnlRequestId);
        await socket.CancelAccountStreamsAsync(summaryId: null, positionsId: null, pnlId: _pnlRequestId);

        var pnl = await TryOpenPnLAsync(registry, feed.Account, cancellationToken);

        if (pnl is not null)
        {
            logger.LogInformation("Re-opened the account P&L stream; MAX_DAILY_LOSS has a live figure again.");
        }

        return feed with { PnlRequestId = pnl is null ? null : _pnlRequestId, Pnl = pnl };
    }

    private async Task<PnLSubscription?> TryOpenPnLAsync(
        IbkrRequestRegistry registry,
        string account,
        CancellationToken cancellationToken)
    {
        var pnl = new PnLSubscription();
        registry.Register(_pnlRequestId, pnl);

        try
        {
            await socket.ReqPnLAsync(_pnlRequestId, account, string.Empty, cancellationToken);

            await pnl.InitialDelivery
                .WaitAsync(TimeSpan.FromSeconds(_options.PnLTimeoutSeconds), cancellationToken);

            return pnl;
        }
        catch (Exception ex) when (ex is TimeoutException or IbkrRequestException)
        {
            // Reported as unavailable rather than defaulted to zero. A missing daily P&L silently
            // read as flat disables the MAX_DAILY_LOSS check without anyone noticing.
            logger.LogWarning(
                ex,
                "Daily P&L is unavailable; the MAX_DAILY_LOSS risk check cannot fire on this snapshot.");

            registry.Remove(_pnlRequestId);
            _pnlRetryNotBefore = DateTimeOffset.UtcNow + PnLRecoveryBackoff;

            return null;
        }
    }

    // ---- mapping ------------------------------------------------------------------------------

    /// <summary>
    /// Picks the account to read.
    /// </summary>
    /// <remarks>
    /// An explicit request must name a managed account — quietly substituting a different one would
    /// evaluate risk against a portfolio the order never touches. With nothing requested, the
    /// configured account wins, then a sole managed account; several managed accounts and no
    /// configuration is ambiguous and is an error rather than a guess.
    /// </remarks>
    internal static string SelectAccount(IReadOnlyList<string> managed, string? requested, string? configured)
    {
        if (managed.Count == 0)
        {
            throw new IbkrConnectionException("TWS has not reported any managed accounts yet.");
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            return managed.FirstOrDefault(account => account.Equals(requested, StringComparison.OrdinalIgnoreCase))
                   ?? throw new IbkrRequestException(
                       IbkrErrorCodes.NoSecurityDefinition,
                       "The requested account is not one of the accounts this TWS session manages.");
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return managed.FirstOrDefault(account => account.Equals(configured, StringComparison.OrdinalIgnoreCase))
                   ?? throw new IbkrRequestException(
                       IbkrErrorCodes.NoSecurityDefinition,
                       "IBKR:AccountId is not one of the accounts this TWS session manages.");
        }

        return managed.Count == 1
            ? managed[0]
            : throw new IbkrRequestException(
                IbkrErrorCodes.NoSecurityDefinition,
                $"TWS manages {managed.Count} accounts; set IBKR:AccountId to choose one.");
    }

    /// <summary>
    /// Reads the first parseable value for any of <paramref name="tags"/>, in preference order.
    /// </summary>
    /// <remarks>
    /// Summary values arrive as strings tagged with a currency. USD rows win when the account reports
    /// more than one, since every limit in <see cref="RiskLimits"/> is denominated in the base
    /// currency of a US options account.
    /// </remarks>
    internal static decimal? ReadDecimal(
        IReadOnlyList<AccountSummaryValue> rows,
        string account,
        params string[] tags)
    {
        foreach (var tag in tags)
        {
            var matches = rows
                .Where(row =>
                    row.Account.Equals(account, StringComparison.OrdinalIgnoreCase) &&
                    row.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var match in matches)
            {
                if (decimal.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Converts an IBKR position contract to the broker-neutral option contract, or null when the
    /// position is not an option this model can represent.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="IbkrMarketDataClient.ToIbOption"/>, with two differences that only
    /// show up on position rows: the exchange comes back empty (positions are held against a listing,
    /// not a route) and the strike is a <c>double</c> that has to be narrowed at this boundary.
    /// </remarks>
    internal static OptionContract? TryToOptionContract(IbContract contract)
    {
        if (!string.Equals(contract.SecType, "OPT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(
                contract.LastTradeDateOrContractMonth,
                ExpirationFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var expiration))
        {
            return null;
        }

        var right = contract.Right?.ToUpperInvariant() switch
        {
            "C" or "CALL" => OptionRight.Call,
            "P" or "PUT" => OptionRight.Put,
            _ => (OptionRight?)null,
        };

        if (right is null || string.IsNullOrWhiteSpace(contract.Symbol))
        {
            return null;
        }

        var multiplier = int.TryParse(
            contract.Multiplier,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedMultiplier)
            ? parsedMultiplier
            : 100;

        var underlying = contract.Symbol.ToUpperInvariant();
        var strike = (decimal)contract.Strike;
        var tradingClass = string.IsNullOrWhiteSpace(contract.TradingClass) ? null : contract.TradingClass;
        var series = tradingClass ?? underlying;
        var rightCode = right.Value == OptionRight.Call ? "C" : "P";

        return new OptionContract(
            $"{series}{expiration:yyyyMMdd}{rightCode}{strike:0.##}",
            underlying,
            expiration,
            strike,
            right.Value,
            // SMART is the route to quote on; the position row's own exchange is frequently empty.
            Exchange: string.IsNullOrWhiteSpace(contract.Exchange) ? "SMART" : contract.Exchange,
            Currency: string.IsNullOrWhiteSpace(contract.Currency) ? "USD" : contract.Currency,
            Multiplier: multiplier,
            TradingClass: tradingClass);
    }

    /// <summary>
    /// Builds a position snapshot, converting IBKR's per-contract conventions to per-share ones.
    /// </summary>
    /// <remarks>
    /// Two conversions, both easy to get wrong:
    /// <list type="bullet">
    /// <item>IBKR's <c>avgCost</c> for an option is the cost of one <em>contract</em>, so it already
    /// includes the multiplier. Every other price in the system is per share.</item>
    /// <item>Exposure scales by quantity <em>and</em> multiplier, and the quantity is signed — a short
    /// position flips the sign of every Greek. This matches how
    /// <c>PortfolioRiskEvaluator.CalculateExposureDelta</c> scales the incoming order, so the two are
    /// summable.</item>
    /// </list>
    /// </remarks>
    internal static PositionSnapshot ToPositionSnapshot(
        OptionContract contract,
        decimal quantity,
        double averageCost,
        OptionGreeks? greeks)
    {
        var multiplier = contract.Multiplier == 0 ? 1 : contract.Multiplier;
        var averagePrice = QuoteRequest.TryConvertSigned(averageCost, out var cost) ? cost / multiplier : 0m;
        var scale = quantity * multiplier;

        var exposure = greeks is null
            ? GreeksVector.Zero
            : new GreeksVector(
                greeks.Delta * scale,
                greeks.Gamma * scale,
                greeks.Theta * scale,
                greeks.Vega * scale);

        return new PositionSnapshot(contract, (int)decimal.Truncate(quantity), averagePrice, exposure);
    }
}
