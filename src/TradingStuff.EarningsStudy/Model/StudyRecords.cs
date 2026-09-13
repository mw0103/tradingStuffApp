namespace TradingStuff.EarningsStudy.Model;

// The tables the verbs exchange. Column names are the positional parameter names in snake_case
// (see Csv/CsvFile.cs); a nullable parameter is an empty cell. Money and prices are decimal without
// exception. Every row carries a status or gate so that an absent value is recorded as a reason,
// never as a missing row — the exclusion table is built from these, and a row that is not there
// cannot be counted (docs/STATE.md, class (c)).

/// <summary>One row per symbol in the frozen CBOE optionable directory, after the name-level gates.</summary>
public sealed record UniverseRow(
    string Symbol,
    string CboeName,
    long? Cik,
    string? SecTicker,
    string? SecName,
    string? Exchange,
    bool Eligible,
    string? ExclusionGate,
    string? Note);

/// <summary>One row per 8-K Item 2.02 filing found for an eligible name, before and after dedup.</summary>
public sealed record EventRow(
    string EventId,
    long Cik,
    string Symbol,
    string AccessionNumber,
    string Form,
    string Items,
    DateOnly FilingDate,
    DateOnly? ReportDate,
    DateTime AcceptanceEt,
    bool InWindow,
    bool KeptAfterDedup,
    string? DedupNote);

/// <summary>Every dei:EntityCommonStockSharesOutstanding fact for a CIK; the as-of pick happens per event downstream.</summary>
public sealed record SharesFactRow(
    long Cik,
    DateOnly PeriodEnd,
    decimal Value,
    DateOnly Filed,
    string Form,
    string AccessionNumber,
    string? Frame);

/// <summary>Timing classification and the calendar-resolved measurement dates for one event.</summary>
public sealed record EventTimingRow(
    string EventId,
    string TimingClass,
    DateOnly PrintDate,
    DateOnly? PreEntryDate,
    DateOnly? EntryDate,
    DateOnly? ExitDate,
    string? EarningsWeek,
    bool Quarantined,
    string? QuarantineReason);

/// <summary>Option-side measures for one event at the entry, pre-entry and exit snapshots.</summary>
public sealed record OptionMeasuresRow(
    string EventId,
    string Root,
    DateOnly? Expiration,
    int? DteCalendarDays,
    string SnapshotKind,
    string? SnapshotTimeEt,
    decimal? SpotParityEntry,
    decimal? SpotFeedEntry,
    decimal? AtmStrike,
    decimal? CallBidEntry,
    decimal? CallAskEntry,
    decimal? PutBidEntry,
    decimal? PutAskEntry,
    decimal? StraddleMidEntry,
    decimal? CombinedSpreadEntry,
    decimal? StraddleMidPreEntry,
    decimal? SpotParityPreEntry,
    decimal? StraddleMidExit,
    decimal? SpotParityExit,
    string FetchStatus,
    string? Note);

/// <summary>Official closes around one event, plus the trailing daily-move scale the timing QA reads.</summary>
public sealed record ClosesRow(
    string EventId,
    string Symbol,
    decimal? ClosePreEntry,
    decimal? CloseEntry,
    decimal? CloseExit,
    decimal? MedianAbsReturn20,
    string Status,
    string Source,
    string? Note);

/// <summary>One gate's tally, in the order it was applied. Appended by each verb.</summary>
public sealed record GateCountRow(
    string Step,
    int Order,
    string Gate,
    int Considered,
    int Removed,
    int Remaining,
    string? Note);
