namespace TradingStuff.ResearchContracts;

/// <summary>
/// Priority tier for a standing market-data subscription. Determines both line-budget eviction
/// order and replay order after a reconnect.
/// </summary>
public enum LeasePriority
{
    /// <summary>
    /// Reserved for the gateway's own transient execution-path quotes (pre-trade, portfolio
    /// Greeks). Not a valid priority for a subscription lease request — those go through
    /// <see cref="Pacing"/>'s line ledger directly, never through the standing-lease API.
    /// </summary>
    ExecutionReserved = 0,

    /// <summary>The registered recording grid: core underlyings and the option node universe.</summary>
    CoreRecording = 1,

    /// <summary>A node mid-swap, held alongside its replacement during the overlap window.</summary>
    Rotation = 2,

    /// <summary>Anything else — exploratory or short-lived.</summary>
    AdHoc = 3,
}

/// <summary>Requests a standing market-data subscription, leased rather than fire-and-forget.</summary>
public sealed record SubscriptionLeaseRequest(
    int ConId,
    LeasePriority Priority,
    bool RecordToDatabase,
    bool IsOption,
    string? GenericTickList,
    int HeartbeatIntervalSeconds,
    string Exchange)
{
    /// <summary>
    /// The exchange to subscribe on. Deliberately has NO default: there is no value that is correct
    /// for every instrument, so every caller is forced to state one.
    /// </summary>
    /// <remarks>
    /// Verified against live paper TWS: <c>reqMktData</c> with only a conId and
    /// <c>Exchange = "SMART"</c> streams fine for options (SPXW: 109 ticks) and for stocks
    /// (SPY: 237 ticks), but is REJECTED with error 200 "No security definition has been found"
    /// for index conIds — SPX and VIX return zero ticks. The same SPX conId on <c>CBOE</c> streams
    /// normally. Omitting the exchange entirely is rejected outright with error 321
    /// ("Please enter exchange"), so there is no universal value: the caller must supply the
    /// instrument's real exchange, which <c>ResolveUnderlyingAsync</c> already returns.
    /// <para>
    /// This is not a hypothetical. Because a failed subscription simply produces no ticks, the
    /// recorder silently recorded nothing for SPX and VIX while every unit test passed — the tests
    /// stub the socket, so nothing but a live connection could have caught it.
    /// </para>
    /// </remarks>
    public string Exchange { get; init; } = Exchange;
}

/// <summary>A granted standing subscription. Expires without a heartbeat within the lease window.</summary>
public sealed record SubscriptionLease(
    Guid LeaseId,
    int ConId,
    LeasePriority Priority,
    bool RecordToDatabase,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt);
