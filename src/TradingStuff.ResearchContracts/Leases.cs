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
    int HeartbeatIntervalSeconds);

/// <summary>A granted standing subscription. Expires without a heartbeat within the lease window.</summary>
public sealed record SubscriptionLease(
    Guid LeaseId,
    int ConId,
    LeasePriority Priority,
    bool RecordToDatabase,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt);
