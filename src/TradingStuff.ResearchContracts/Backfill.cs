namespace TradingStuff.ResearchContracts;

/// <summary>
/// Lifecycle of one concrete TWS historical-data request slice (a row of
/// <c>research.backfill_requests</c>). Stored as text in Postgres (a CHECK constraint enumerates the
/// same six values) rather than an integer, so an ad-hoc query against the database reads directly
/// without a lookup table — the same reasoning migration 003 applies to
/// <see cref="NodeAssignmentReasons"/> and <see cref="GapBasis"/> below.
/// </summary>
public enum BackfillRequestState
{
    /// <summary>Queued; not yet sent to TWS.</summary>
    Pending,

    /// <summary>A <c>reqHistoricalData</c> call is outstanding for this slice.</summary>
    Inflight,

    /// <summary>TWS returned one or more bars; they have been persisted to <c>research.bars</c>.</summary>
    Succeeded,

    /// <summary>
    /// TWS returned zero bars for a range that is not a known session gap (e.g. before an
    /// instrument's actual head timestamp, or a genuine market-closed stretch). Distinct from
    /// <see cref="Failed"/>: an empty result is a normal outcome, not an error, and must not be
    /// retried the same way a transient failure is.
    /// </summary>
    Empty,

    /// <summary>
    /// The request errored in a way worth retrying (pacing violation, transient disconnect). See
    /// <c>attempts</c>/<c>error_code</c>/<c>error_message</c> on the row.
    /// </summary>
    Failed,

    /// <summary>
    /// The request errored in a way retrying cannot fix (e.g. the contract is not entitled, or the
    /// parameters are invalid) and the coordinator must stop attempting this exact slice.
    /// </summary>
    Permanent,
}

/// <summary>
/// A backfill campaign: one row per (instrument, whatToShow, barSize, useRth, target range) the
/// operator has asked the platform to fill in (a row of <c>research.backfill_jobs</c>). Declares
/// intent; <see cref="BackfillSlice"/> and the persisted request rows it maps to are where the
/// actual work happens.
/// </summary>
/// <param name="ConId">
/// NULL for a job that walks multiple contracts over the target range (e.g. ES, which must roll
/// across expired futures contracts — see docs/research/ibkr-data-capability-matrix.md on CONTFUT
/// rejecting a past endDateTime).
/// </param>
public sealed record BackfillJob(
    long JobId,
    string Name,
    short InstrumentId,
    int? ConId,
    string WhatToShow,
    string BarSize,
    bool UseRth,
    DateTimeOffset TargetFrom,
    DateTimeOffset TargetTo,
    int Priority,
    string Status);

/// <summary>
/// The concrete parameters of one <c>reqHistoricalData</c> call — everything TWS needs to identify
/// and answer this exact slice. A <see cref="BackfillSlice"/> maps 1:1 onto the idempotency key of
/// <c>research.backfill_requests</c> (job_id, con_id, end_time_utc, duration, what_to_show, bar_size,
/// use_rth): re-deriving and re-issuing the identical slice must be safe, because the checkpoint
/// table is exactly what makes that a no-op rather than a duplicate request.
/// </summary>
/// <param name="EndTimeUtc">
/// NULL means "now" — an open-ended, forward-anchored request (a live top-up) rather than a fixed
/// historical boundary. See the idempotency-key comment in migration 004 for why this column allows
/// NULL and what that implies for the uniqueness constraint.
/// </param>
/// <param name="Duration">TWS duration string, e.g. <c>"1 D"</c>, <c>"2 W"</c>.</param>
public sealed record BackfillSlice(
    long JobId,
    int ConId,
    DateTimeOffset? EndTimeUtc,
    string Duration,
    string WhatToShow,
    string BarSize,
    bool UseRth);

/// <summary>
/// A job's progress, derived by querying <c>research.backfill_requests</c> rather than tracked in
/// memory — the same "restart re-derives state from the checkpoint table" principle the requests
/// table itself exists for.
/// </summary>
public sealed record BackfillCheckpoint(
    long JobId,
    int CompletedCount,
    int RemainingCount,
    int FailedCount,
    /// <summary>
    /// The earliest (oldest) instant successfully backfilled so far. Jobs walk backward from
    /// recent history toward <see cref="BackfillJob.TargetFrom"/>, so this is how far back the job
    /// has actually reached — not the same as <c>TargetFrom</c> until the job completes. NULL before
    /// any slice has succeeded.
    /// </summary>
    DateTimeOffset? LowWaterMarkUtc);

/// <summary>
/// A range with no landed bars, and why it is believed to be a gap rather than simply unattempted.
/// </summary>
/// <param name="Basis">
/// Explains WHY this range counts as a gap — see <see cref="GapBasis"/> for the known values.
/// Distinguishing "no_bars" (TWS returned nothing here) from "session_expected" (a session row from
/// <c>research.sessions</c> says trading should have happened here) matters because the latter is a
/// stronger signal something is actually missing, while the former can be a legitimate closed
/// market or a request that has simply not been attempted yet.
/// </param>
public sealed record GapRange(DateTimeOffset From, DateTimeOffset To, string Basis);

/// <summary>Known values for <see cref="GapRange.Basis"/>. Stored as free text; not exhaustive.</summary>
public static class GapBasis
{
    /// <summary>A backfill request covering this range came back with zero bars.</summary>
    public const string NoBars = "no_bars";

    /// <summary>A <c>research.sessions</c> row says a session covered this range, but no bars land in it.</summary>
    public const string SessionExpected = "session_expected";
}
