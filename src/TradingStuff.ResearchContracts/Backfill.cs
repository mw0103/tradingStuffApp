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
/// rejecting a past endDateTime). A job with a NULL conId supplies its own request rows (its walker
/// knows the per-contract conIds); the coordinator drains them but does not plan them.
/// </param>
/// <param name="Kind">
/// <c>"historical"</c> (walk a fixed <paramref name="TargetFrom"/>..<paramref name="TargetTo"/>
/// backward, once) or <c>"topup"</c> (re-anchor forward to the current bucket every run, never
/// finished). See <see cref="BackfillJobKinds"/>.
/// </param>
/// <param name="SliceDuration">
/// Overrides the TWS duration string the planner would otherwise derive from
/// <paramref name="BarSize"/>. NULL means "derive it". This is a persisted job column rather than
/// configuration on purpose: slice boundaries must be a pure function of the job row, or an ambient
/// config change silently re-plans the job into a second, overlapping set of request rows that the
/// idempotency key cannot collapse.
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
    string Status,
    string Kind = BackfillJobKinds.Historical,
    string? SliceDuration = null);

/// <summary>Known values for <see cref="BackfillJob.Kind"/>; mirrors migration 005's CHECK constraint.</summary>
public static class BackfillJobKinds
{
    /// <summary>Walks a fixed target range backward, exactly once, and then completes.</summary>
    public const string Historical = "historical";

    /// <summary>
    /// Re-anchors forward to the current bucket on every run and never completes. Its slices carry a
    /// concrete bucket-floored <c>end_time_utc</c>, never NULL — see migration 005 for why.
    /// </summary>
    public const string TopUp = "topup";
}

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
/// A span of a backfill job's OWN declared range where the bars the session calendar says should
/// exist do not fully match what has landed in <c>research.bars</c>. Produced by the gap detector
/// (package 2f) by comparing expected sessions × expected bars against landed ones; this record
/// itself has no opinion about severity, only about WHY the shortfall exists — see
/// <see cref="GapBasis"/>.
/// </summary>
/// <param name="Basis">
/// Explains WHY this range is reported — see <see cref="GapBasis"/> for the known values. Ranges from
/// entirely benign (<see cref="GapBasis.Pending"/>: the coordinator has not gotten here yet) to the
/// one basis that names an actual defect (<see cref="GapBasis.SucceededButAbsent"/>: the checkpoint
/// says this range succeeded, and <c>research.bars</c> disagrees). A caller that wants "is this
/// backfill actually complete" rather than "what does every range's checkpoint say" should treat
/// anything other than <see cref="GapBasis.Empty"/> and <see cref="GapBasis.Permanent"/> as unresolved.
/// </param>
public sealed record GapRange(DateTimeOffset From, DateTimeOffset To, string Basis);

/// <summary>
/// Known values for <see cref="GapRange.Basis"/>. Stored as free text rather than an enum — the same
/// reasoning <see cref="BackfillRequestState"/>'s doc comment gives: an ad-hoc query against a gap
/// report reads directly, and the set is explicitly "known values", not exhaustive.
/// </summary>
/// <remarks>
/// Every basis but <see cref="NotRequested"/> names the state of the <c>research.backfill_requests</c>
/// row(s) that nominally cover the range; <see cref="NotRequested"/> is for when no row covers it at
/// all, and <see cref="SucceededButAbsent"/> is not a request state but a comparison BETWEEN a
/// <see cref="BackfillRequestState.Succeeded"/> row and the bars that should have landed under it. When
/// more than one covering row applies to the same range — rare; only a historical job's newest slice
/// is allowed to overlap its neighbour — the most alarming basis wins rather than an arbitrary one.
/// </remarks>
public static class GapBasis
{
    /// <summary>
    /// No <c>research.backfill_requests</c> row's nominal window covers this range at all — the
    /// coordinator has not reached it yet, or the job's planner never ran. Per the absent-row
    /// discipline this whole report exists to apply, a job with ZERO request rows must still produce
    /// this basis across its entire window rather than an empty, falsely-clean gap list.
    /// </summary>
    public const string NotRequested = "not_requested";

    /// <summary>Covered by a request row still <see cref="BackfillRequestState.Pending"/>.</summary>
    public const string Pending = "pending";

    /// <summary>Covered by a request row currently <see cref="BackfillRequestState.Inflight"/>.</summary>
    public const string Inflight = "inflight";

    /// <summary>
    /// Covered by a request row that is <see cref="BackfillRequestState.Failed"/> but has not yet
    /// exhausted its retry budget — distinct from <see cref="Exhausted"/>, which has.
    /// </summary>
    public const string Retrying = "retrying";

    /// <summary>
    /// Covered only by request row(s) that failed and hit the attempt cap. Unlike <see cref="Empty"/>
    /// and <see cref="Permanent"/> this is NOT explained: the coordinator gave up without a confirmed
    /// reason the data does not exist, and the range needs a human decision (raise the attempt cap,
    /// investigate the recorded error, or accept the loss).
    /// </summary>
    public const string Exhausted = "exhausted";

    /// <summary>
    /// Covered by a request row TWS confirmed has no data (<see cref="BackfillRequestState.Empty"/>).
    /// Explained, not missing — a genuine market-closed stretch or a range before the instrument's
    /// real data floor.
    /// </summary>
    public const string Empty = "empty";

    /// <summary>
    /// Covered by a request row TWS rejected in a way retrying cannot fix
    /// (<see cref="BackfillRequestState.Permanent"/>). Explained, not missing.
    /// </summary>
    public const string Permanent = "permanent";

    /// <summary>
    /// The alarming case: a request row says <see cref="BackfillRequestState.Succeeded"/> for this
    /// range, and <c>research.bars</c> has no (or an incomplete count of) matching rows anyway. Every
    /// other basis describes an EXPECTED shortfall; this one means the checkpoint and the data
    /// disagree — the write path that is supposed to make "succeeded" and "bars landed" atomic did
    /// not, for a request this detector can name.
    /// </summary>
    public const string SucceededButAbsent = "succeeded_but_absent";
}
