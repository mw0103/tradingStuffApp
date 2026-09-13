using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Stats;

// The shapes the compute verb produces. The event table is the audit record — one row for every
// event that reached this verb, carrying the gate it stopped at or its sample membership, so that an
// event contributing nothing to the statistics is still visible as a row with a reason. A row that
// is not written cannot be counted (docs/LESSONS.md §3, "absence renders as health").

/// <summary>
/// One row of <c>c1_event_table.csv</c>. Every value the memo is computed from appears here, whether
/// or not the event reached the statistics, so the memo can be re-derived from this file by hand.
/// </summary>
/// <param name="ClusterKey">The bootstrap cluster: the earnings week, or <c>unknown-week:{event_id}</c> when the timing step recorded none — a singleton, counted and reported.</param>
/// <param name="PriceQa">ok | quarantined | unknown | not_reached.</param>
/// <param name="V0TwoSignalWouldQuarantine">What the RETIRED v0 two-signal rule would have decided (<see cref="V0TwoSignalRule"/>). POST-HOC DIAGNOSTIC: it never affects inclusion. Empty for an event that never reached gate 09, or whose two moves are not both recorded.</param>
/// <param name="SpotSource">feed | parity | none — which spot the implied move was divided by.</param>
/// <param name="StraddleMidPreEntry">The straddle mid at the PRE-ENTRY snapshot, carried so <paramref name="ImCollapseRatio"/> can be recomputed from this file by hand.</param>
/// <param name="SpotParityPreEntry">The parity spot at the PRE-ENTRY snapshot, carried for the same reason.</param>
/// <param name="ImCollapseRatio">IM at entry / IM at pre-entry (<c>TimingQa.ImCollapseDiagnostic</c>). Diagnostic only — it gates nothing. Null when any of the four inputs is missing or non-positive.</param>
/// <param name="StopGate">The gate that removed the event; empty when it survived every gate.</param>
/// <param name="Membership">primary | secondary | empty. The primary (tradable) sample is a SUBSET of the secondary (all-quotable) sample; <paramref name="InPrimary"/> and <paramref name="InSecondary"/> are the authoritative flags and a primary event has both set.</param>
public sealed record C1EventRow(
    string EventId,
    string Symbol,
    long Cik,
    DateOnly FilingDate,
    DateTime AcceptanceEt,
    string TimingClass,
    DateOnly? PrintDate,
    DateOnly? EntryDate,
    DateOnly? ExitDate,
    string? EarningsWeek,
    string ClusterKey,
    string ClosesStatus,
    decimal? ClosePreEntry,
    decimal? CloseEntry,
    decimal? CloseExit,
    decimal? MedianAbsReturn20,
    string PriceQa,
    string? PriceQaReason,
    decimal? PriceQaPreEntryMove,
    decimal? PriceQaEventMove,
    bool? V0TwoSignalWouldQuarantine,
    string? Root,
    DateOnly? Expiration,
    int? DteCalendarDays,
    string? SnapshotKind,
    string? SnapshotTimeEt,
    decimal? SpotFeedEntry,
    decimal? SpotParityEntry,
    string SpotSource,
    decimal? SpotForIm,
    decimal? ParityCloseDeviation,
    decimal? AtmStrike,
    decimal? CallBidEntry,
    decimal? PutBidEntry,
    decimal? StraddleMidEntry,
    decimal? CombinedSpreadEntry,
    decimal? SpreadFraction,
    decimal? StraddleMidPreEntry,
    decimal? SpotParityPreEntry,
    decimal? ImCollapseRatio,
    decimal? StraddleMidExit,
    decimal? StraddleReturn,
    decimal? RealizedMove,
    decimal? ImpliedMove,
    decimal? Ratio,
    bool? RfLessThanIm,
    decimal? SharesOutstanding,
    DateOnly? SharesAsOfFiled,
    decimal? MarketCap,
    string? MarketCapQuintilePrimary,
    string? MarketCapQuintileSecondary,
    string? SpreadQuintilePrimary,
    string? SpreadQuintileSecondary,
    string DteBucket,
    string TimingBucket,
    string StopGate,
    string Membership,
    bool InPrimary,
    bool InSecondary,
    bool RatioMeasurable,
    bool ZeroRealizedMove,
    string? Note);

/// <summary>One event as the statistics see it: the ratio, the strict comparison, and the split keys.</summary>
/// <param name="RfLessThanIm">RF strictly below IM. Compared on RF and IM directly, never on the stored ratio.</param>
/// <param name="RfEqualsIm">RF exactly equal to IM. A tie counts as NOT less, and is reported.</param>
public sealed record SampleEvent(
    string EventId,
    string ClusterKey,
    decimal Ratio,
    bool RfLessThanIm,
    bool RfEqualsIm,
    decimal? MarketCap,
    decimal? SpreadFraction,
    int? DteCalendarDays,
    string TimingClass,
    decimal? StraddleReturn);

/// <summary>A named count. Used wherever the memo reports a tally that must not be summarised away.</summary>
public sealed record Tally(string Key, int Count);

/// <summary>One bucket of a pre-registered split.</summary>
public sealed record SplitBucket(string Label, string? Range, IReadOnlyList<SampleEvent> Events);

/// <summary>A split as a set of buckets, in the order the memo prints them.</summary>
public sealed record SplitDefinition(string Key, string Title, string Rule, IReadOnlyList<SplitBucket> Buckets);

/// <summary>The secondary log statistics, with the excluded zero-RF events counted rather than dropped.</summary>
public sealed record LogStatistics(
    int Considered,
    int ZeroRealizedExcluded,
    int Used,
    int TrimPerTail,
    double? MeanLog,
    double? TrimmedMeanLog,
    string? Note);

/// <summary>The straddle hold-through return at mids. Diagnostic only; never an input to the verdict.</summary>
public sealed record StraddleDiagnostic(
    int Members,
    int WithReturn,
    int NullExit,
    decimal? Median,
    decimal? Mean,
    decimal? P10,
    decimal? P25,
    decimal? P75,
    decimal? P90);

/// <summary>Nearest-rank quantiles of a measured series, for the memo's limitations section.</summary>
public sealed record QuantileSummary(int Count, decimal? P50, decimal? P90, decimal? P95, decimal? P99, decimal? Max);

/// <summary>One bucket's readout inside a split.</summary>
public sealed record SplitBucketReport(
    string Label,
    string? Range,
    int Members,
    int Clusters,
    decimal? Mean,
    BootstrapInterval? MeanInterval,
    decimal? Median,
    BootstrapInterval? MedianInterval,
    decimal? ProportionLess,
    BootstrapInterval? ProportionInterval);

/// <summary>One split's readout for one sample.</summary>
public sealed record SplitReport(string Key, string Title, string Rule, IReadOnlyList<SplitBucketReport> Buckets);

/// <summary>Everything the memo says about one sample. <c>Mean</c> is the v2 primary statistic; <c>Median</c> and <c>ProportionLess</c> are the demoted descriptive readouts.</summary>
public sealed record SampleReport(
    string Key,
    string Title,
    string Definition,
    int Members,
    int Measurable,
    IReadOnlyList<string> UnmeasurableEvents,
    int Clusters,
    int SingletonUnknownWeeks,
    decimal? Mean,
    decimal? Median,
    decimal? ProportionLess,
    int LessCount,
    int TieCount,
    BootstrapResult? Bootstrap,
    LogStatistics Log,
    StraddleDiagnostic Straddle,
    IReadOnlyList<Tally> SpotSources,
    IReadOnlyList<SplitReport> Splits);

/// <summary>
/// The two quarantine rates, each against ITS OWN denominator, with the parts kept visible so the
/// memo never shows a bare ratio.
///
/// Gate 09 decides only on the events that reached it, which is a strictly smaller set than gate 07
/// considered (gates 07 and 08 come first). Dividing both numerators by the gate-07 denominator
/// understates the price-QA rate by exactly the events it never saw, so the two are reported apart.
/// <paramref name="CombinedRate"/> is kept for continuity and labelled in the memo as the share of
/// the gate-07 denominator that either quarantine removed — a coverage headline, not a rate.
/// </summary>
public sealed record QuarantineRate(
    bool TimingComputable,
    int TimingConsidered,
    int TimingQuarantined,
    decimal? TimingRate,
    bool PriceQaComputable,
    int PriceQaConsidered,
    int PriceQaQuarantined,
    decimal? PriceQaRate,
    decimal? CombinedRate,
    string? Note);

/// <summary>
/// How often the registered gate-09 decision and the options-side IM-collapse diagnostic agree, over
/// the events gate 09 actually decided on. A 2 x 2 with the not-computable column kept, so the rows
/// sum to the events considered rather than quietly shedding the events the diagnostic could not
/// measure (docs/LESSONS.md §3).
/// </summary>
/// <param name="Threshold">The reference below which the diagnostic reads as a collapse. Diagnostic only; no gate reads it.</param>
public sealed record ImCollapseAgreement(
    int Considered,
    decimal Threshold,
    int QuarantinedCollapsed,
    int QuarantinedNotCollapsed,
    int QuarantinedNotComputable,
    int KeptCollapsed,
    int KeptNotCollapsed,
    int KeptNotComputable)
{
    public int Quarantined => QuarantinedCollapsed + QuarantinedNotCollapsed + QuarantinedNotComputable;

    public int Kept => KeptCollapsed + KeptNotCollapsed + KeptNotComputable;

    public int NotComputable => QuarantinedNotComputable + KeptNotComputable;
}

/// <summary>One input table's provenance line.</summary>
public sealed record InputFile(string Name, int Rows, string Sha256);

/// <summary>The measured limitations: what this run observed, as opposed to the registered ones.</summary>
/// <param name="PriceSources">A tally of <c>closes.source</c> over the events entering compute, so the memo names where the official closes came from rather than leaving the reader to assume.</param>
/// <param name="ImCollapse">The gate-09 / IM-collapse agreement cross-tab.</param>
public sealed record MeasuredContext(
    IReadOnlyList<Tally> SnapshotKinds,
    IReadOnlyList<Tally> SnapshotTimes,
    IReadOnlyList<Tally> PriceSources,
    QuantileSummary ParityCloseDeviation,
    IReadOnlyList<string> TimingGateNotes,
    IReadOnlyList<Tally> TimingQuarantineReasons,
    IReadOnlyList<Tally> OrphanRows,
    ImCollapseAgreement ImCollapse,
    int UniverseRows,
    int UniverseEligible);

/// <summary>The whole memo, assembled before a line of markdown is written.</summary>
/// <param name="EventsInWindowAndKept">Rows of <c>events.csv</c> that are BOTH in window and kept after dedup — the same predicate the timing step considers at gate 07, so the two denominators are the same set.</param>
/// <param name="EventsQuarantinedByTimingStep">Events whose <c>event_timing.csv</c> row says quarantined. These ARE the gate-07 removals and are tallied there, never again here.</param>
/// <param name="EventsWithNoTimingRow">Events with no row in <c>event_timing.csv</c> at all. A pipeline discontinuity, warned about and reported separately: folding it into the gate-07 removal count would render a missing file as a gate decision (docs/STATE.md, class (c)).</param>
public sealed record C1Report(
    C1Verdict Verdict,
    StraddleCrossCheck StraddleCrossCheck,
    CoverageReport Coverage,
    V0TwoSignalDiagnostic V0TwoSignal,
    SampleReport Primary,
    SampleReport Secondary,
    IReadOnlyList<GateCountRow> ExclusionTable,
    IReadOnlyList<string> GatesWithoutCounts,
    IReadOnlyList<GateCountRow> UnrecognisedGateRows,
    QuarantineRate Quarantine,
    IReadOnlyList<InputFile> Inputs,
    MeasuredContext Measured,
    int EventsInWindowAndKept,
    int EventsEnteringCompute,
    int EventsQuarantinedByTimingStep,
    int EventsWithNoTimingRow,
    int Replications,
    int Seed,
    string PriceQaDescription,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The v2 primary criterion's own description, printed in the memo beside the verdict so the rule
/// and its execution are read together. A constant rather than prose so a test can compare it
/// against <see cref="C1Verdict"/>.
/// </summary>
public static class C1Criterion
{
    public const string Rule =
        "PASS = mean(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for mean(RF/IM) excludes 1. FAIL = otherwise.";
}
