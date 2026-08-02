namespace TradingStuff.ResearchContracts;

/// <summary>
/// Lifecycle of one <c>research.option_chain_requests</c> row (one expiration's worth of chain
/// quotes for a job). Mirrors <see cref="BackfillRequestState"/> deliberately — same six values, same
/// meaning, same reason they are stored as text rather than an enum column.
/// </summary>
public enum OptionChainRequestState
{
    /// <summary>Queued; not yet sent to the vendor.</summary>
    Pending,

    /// <summary>A vendor request is outstanding for this expiration.</summary>
    Inflight,

    /// <summary>The vendor returned one or more quotes; they have been persisted.</summary>
    Succeeded,

    /// <summary>
    /// The vendor reported no data for this expiration over the job's date range. Distinct from
    /// <see cref="Failed"/> — an empty result (e.g. an expiration outside the vendor's coverage, or a
    /// series that never traded) is a normal outcome, not an error.
    /// </summary>
    Empty,

    /// <summary>The request errored in a way worth retrying (transient HTTP failure, Terminal restart).</summary>
    Failed,

    /// <summary>
    /// The request errored in a way retrying cannot fix (a subscription gap, a Terminal API-version
    /// mismatch). The coordinator stops attempting this exact expiration.
    /// </summary>
    Permanent,
}

/// <summary>Known values for <see cref="OptionChainRequestState"/>, spelled as migration 019's CHECK constraint has them.</summary>
public static class OptionChainRequestStates
{
    public const string Pending = "pending";
    public const string Inflight = "inflight";
    public const string Succeeded = "succeeded";
    public const string Empty = "empty";
    public const string Failed = "failed";
    public const string Permanent = "permanent";
}

/// <summary>
/// Known values for <c>research.option_chain_jobs.interval</c>. '1m' is the sizing default
/// (docs/FOLLOWUP.md §4.5); 'tick' is recognised but never planned by
/// <c>OptionChainCoordinator</c> — see that class and <c>OptionChainEndpoints</c> for where choosing
/// it is made to require an explicit, separate confirmation.
/// </summary>
public static class OptionChainIntervals
{
    public const string OneMinute = "1m";
    public const string Tick = "tick";
}

/// <summary>
/// An option-chain ingestion campaign: one row per (underlying, trading class, target date range)
/// an operator has asked the platform to fill in (a row of <c>research.option_chain_jobs</c>).
/// </summary>
/// <param name="Underlying">Canonical underlying, e.g. <c>"SPX"</c>, <c>"VIX"</c>.</param>
/// <param name="TradingClass">
/// The option series: <c>"SPX"</c> (AM-settled monthlies), <c>"SPXW"</c> (PM-settled
/// weeklies/dailies), or <c>"VIX"</c>. Genuinely a different instrument at the same strike and
/// expiration for SPX/SPXW — see <c>TradingStuff.Contracts.OptionContract</c> — never vendor metadata.
/// </param>
/// <param name="Interval">One of <see cref="OptionChainIntervals"/>. Defaults to '1m'.</param>
public sealed record OptionChainJob(
    long JobId,
    string Name,
    string Underlying,
    string TradingClass,
    DateOnly TargetFrom,
    DateOnly TargetTo,
    string Interval,
    int Priority,
    string Status);

/// <summary>
/// One job's progress, derived from <c>research.option_chain_requests</c> — never tracked in memory,
/// for the same "a restart re-derives state from the checkpoint table" reason
/// <see cref="BackfillCheckpoint"/> exists.
/// </summary>
public sealed record OptionChainJobStatus(
    long JobId,
    string Name,
    string Underlying,
    string TradingClass,
    DateOnly TargetFrom,
    DateOnly TargetTo,
    string Interval,
    int Priority,
    string Status,
    int TotalRequests,
    int PendingCount,
    int InflightCount,
    int SucceededCount,
    int EmptyCount,
    int RetryableCount,
    int ExhaustedCount,
    int PermanentCount,
    long QuotesLanded,
    long QuotesReturned,
    double PercentComplete);

/// <summary>What <c>GET /research/options/status</c> answers with.</summary>
public sealed record OptionChainStatusReport(
    bool Enabled, string OwnerId, int MaxAttempts, IReadOnlyList<OptionChainJobStatus> Jobs);
