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
/// <param name="SpotSource">feed | parity | none — which spot the implied move was divided by.</param>
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
    decimal? Median,
    BootstrapInterval? MedianInterval,
    decimal? ProportionLess,
    BootstrapInterval? ProportionInterval);

/// <summary>One split's readout for one sample.</summary>
public sealed record SplitReport(string Key, string Title, string Rule, IReadOnlyList<SplitBucketReport> Buckets);

/// <summary>Everything the memo says about one sample.</summary>
public sealed record SampleReport(
    string Key,
    string Title,
    string Definition,
    int Members,
    int Measurable,
    IReadOnlyList<string> UnmeasurableEvents,
    int Clusters,
    int SingletonUnknownWeeks,
    decimal? Median,
    decimal? ProportionLess,
    int LessCount,
    int TieCount,
    BootstrapResult? Bootstrap,
    LogStatistics Log,
    StraddleDiagnostic Straddle,
    IReadOnlyList<Tally> SpotSources,
    IReadOnlyList<SplitReport> Splits);

/// <summary>The quarantine rate, with its parts kept visible so the memo never shows a bare ratio.</summary>
public sealed record QuarantineRate(
    bool Computable,
    int TimingConsidered,
    int TimingQuarantined,
    int PriceQaQuarantined,
    decimal? Rate,
    string? Note);

/// <summary>One input table's provenance line.</summary>
public sealed record InputFile(string Name, int Rows, string Sha256);

/// <summary>The measured limitations: what this run observed, as opposed to the registered ones.</summary>
public sealed record MeasuredContext(
    IReadOnlyList<Tally> SnapshotKinds,
    IReadOnlyList<Tally> SnapshotTimes,
    QuantileSummary ParityCloseDeviation,
    IReadOnlyList<string> TimingGateNotes,
    IReadOnlyList<Tally> TimingQuarantineReasons,
    IReadOnlyList<Tally> OrphanRows,
    int UniverseRows,
    int UniverseEligible);

/// <summary>The whole memo, assembled before a line of markdown is written.</summary>
public sealed record C1Report(
    C1Verdict Verdict,
    SampleReport Primary,
    SampleReport Secondary,
    IReadOnlyList<GateCountRow> ExclusionTable,
    IReadOnlyList<string> GatesWithoutCounts,
    IReadOnlyList<GateCountRow> UnrecognisedGateRows,
    QuarantineRate Quarantine,
    IReadOnlyList<InputFile> Inputs,
    MeasuredContext Measured,
    int EventsKeptAfterDedup,
    int EventsEnteringCompute,
    int EventsStoppedBeforeCompute,
    int Replications,
    int Seed,
    bool PriceQaApplied,
    string PriceQaDescription,
    bool SharesAsOfApplied,
    IReadOnlyList<string> Warnings);
