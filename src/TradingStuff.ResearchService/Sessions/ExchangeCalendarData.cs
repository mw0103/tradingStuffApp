using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingStuff.ResearchService.Sessions;

/// <summary>How much to trust an operator-entered calendar fact. Mirrors the strings in the JSON.</summary>
public static class CalendarConfidence
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Unverified = "unverified";
}

/// <summary>One statutory/mechanical market holiday rule.</summary>
/// <remarks>
/// Only holidays that are genuinely computable are rules. Everything an exchange *decided* — an
/// unscheduled closure, an early close, the year a holiday entered the calendar — is data. See the
/// notes block at the top of <c>exchange-calendars.json</c>.
/// </remarks>
public sealed record HolidayRule
{
    public string Name { get; init; } = "";

    /// <summary><c>fixed</c> | <c>nthWeekday</c> | <c>lastWeekday</c> | <c>goodFriday</c>.</summary>
    public string Kind { get; init; } = "";

    public int Month { get; init; }
    public int Day { get; init; }
    public DayOfWeek Weekday { get; init; }
    public int Nth { get; init; }

    /// <summary><c>none</c> | <c>nearestWeekday</c> | <c>sundayToMonday</c> | <c>saturdayToFriday</c>.</summary>
    public string Observance { get; init; } = "none";

    /// <summary>First year the rule applies, inclusive. Null means "always".</summary>
    public int? FromYear { get; init; }

    /// <summary>Last year the rule applies, inclusive. Null means "still applies".</summary>
    public int? ToYear { get; init; }

    public string? Note { get; init; }
}

/// <summary>A recurring early close, defined relative to a holiday rule rather than by date.</summary>
public sealed record EarlyCloseRule
{
    public string Name { get; init; } = "";

    /// <summary><c>dayBefore</c> | <c>dayAfter</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The <see cref="HolidayRule.Name"/> this early close hangs off.</summary>
    public string Holiday { get; init; } = "";

    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
    public string? Note { get; init; }
}

/// <summary>A day the venue was closed for a reason no rule can derive.</summary>
public sealed record ClosureEntry
{
    public DateOnly Date { get; init; }
    public string Reason { get; init; } = "";
    public string Confidence { get; init; } = CalendarConfidence.Unverified;
}

/// <summary>A day the venue closed early for a reason no rule can derive.</summary>
public sealed record UnscheduledEarlyClose
{
    public DateOnly Date { get; init; }

    /// <summary>Exchange-local wall-clock close time.</summary>
    public TimeOnly Close { get; init; }

    public string Reason { get; init; } = "";
    public string Confidence { get; init; } = CalendarConfidence.Unverified;
}

/// <summary>Which rule/data sets a venue observes. Several calendars can share one venue.</summary>
public sealed record VenueDefinition
{
    public string HolidayRuleSet { get; init; } = "";
    public string EarlyCloseRuleSet { get; init; } = "";
    public string ClosureSet { get; init; } = "";
    public string UnscheduledEarlyCloseSet { get; init; } = "";
    public string? Note { get; init; }
}

/// <summary>One session row template: a label plus its exchange-local open/close wall-clock times.</summary>
public sealed record SessionDefinition
{
    /// <summary><c>RTH</c> or <c>GTH</c> — the values <c>research.sessions.label</c> permits.</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// Days to add to the trading date to get the calendar date the open's wall clock is read on.
    /// <c>-1</c> for an overnight session that opens the evening before its trading date.
    /// </summary>
    public int OpenDayOffset { get; init; }

    public TimeOnly Open { get; init; }

    public int CloseDayOffset { get; init; }

    public TimeOnly Close { get; init; }

    /// <summary>Exchange-local close on an early-close day. Null when the session has no early close.</summary>
    public TimeOnly? EarlyClose { get; init; }

    /// <summary>
    /// Whether an early close on the trading date shortens THIS session. False for a session that
    /// ends in the morning (Cboe GTH), which an afternoon early close cannot reach.
    /// </summary>
    public bool AppliesEarlyClose { get; init; }

    public string? Note { get; init; }
}

/// <summary>One calendar key's timezone, venue, and session templates.</summary>
public sealed record CalendarDefinition
{
    public string Key { get; init; } = "";
    public string Venue { get; init; } = "";

    /// <summary>IANA id, e.g. <c>America/Chicago</c>. Never a fixed offset.</summary>
    public string TimeZone { get; init; } = "";

    /// <summary>
    /// First trading date this calendar can produce a session for. Sessions before it are not
    /// "unknown", they did not exist — Cboe GTH had no overnight session before the 2022 expansion,
    /// and generating one would manufacture reference data out of nothing.
    /// </summary>
    public DateOnly EffectiveFrom { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<SessionDefinition> Sessions { get; init; } = [];

    /// <summary>
    /// Calendar dates on whose EVENING no overnight session opens — suppresses the session with a
    /// negative <see cref="SessionDefinition.OpenDayOffset"/> whose open falls on one of these dates.
    /// </summary>
    public IReadOnlyList<DateOnly> EveningSessionClosedOn { get; init; } = [];
}

/// <summary>
/// The whole checked-in calendar dataset: <c>exchange-calendars.json</c>, deserialized and indexed.
/// </summary>
/// <remarks>
/// Loaded once from an embedded resource so the generator's inputs ship inside the binary and cannot
/// drift from the code that reads them — the same reasoning that puts the SQL migrations in the
/// assembly. <see cref="GeneratorVersion"/> is written to every <c>research.sessions</c> row.
/// </remarks>
public sealed class ExchangeCalendarSet
{
    private static readonly Lazy<ExchangeCalendarSet> EmbeddedSet = new(LoadEmbedded, isThreadSafe: true);

    private readonly FrozenDictionary<string, CalendarDefinition> _calendars;
    private readonly FrozenDictionary<string, VenueDefinition> _venues;
    private readonly FrozenDictionary<string, IReadOnlyList<HolidayRule>> _holidayRuleSets;
    private readonly FrozenDictionary<string, IReadOnlyList<EarlyCloseRule>> _earlyCloseRuleSets;
    private readonly FrozenDictionary<string, FrozenDictionary<DateOnly, ClosureEntry>> _closureSets;
    private readonly FrozenDictionary<string, FrozenDictionary<DateOnly, UnscheduledEarlyClose>> _earlyCloseSets;

    private ExchangeCalendarSet(CalendarFile file)
    {
        GeneratorVersion = file.GeneratorVersion;
        Revision = file.Revision;
        KnownGoodThrough = file.KnownGoodThrough;

        _calendars = file.Calendars.ToFrozenDictionary(calendar => calendar.Key, StringComparer.Ordinal);
        _venues = file.Venues.ToFrozenDictionary(StringComparer.Ordinal);

        _holidayRuleSets = file.HolidayRuleSets.ToFrozenDictionary(
            entry => entry.Key, entry => (IReadOnlyList<HolidayRule>)entry.Value, StringComparer.Ordinal);
        _earlyCloseRuleSets = file.EarlyCloseRuleSets.ToFrozenDictionary(
            entry => entry.Key, entry => (IReadOnlyList<EarlyCloseRule>)entry.Value, StringComparer.Ordinal);

        // Duplicate dates inside one set would make the winner depend on list order — reject at load
        // time rather than silently keeping the last one.
        _closureSets = file.ClosureSets.ToFrozenDictionary(
            entry => entry.Key,
            entry => entry.Value.ToFrozenDictionary(closure => closure.Date),
            StringComparer.Ordinal);
        _earlyCloseSets = file.EarlyCloseSets.ToFrozenDictionary(
            entry => entry.Key,
            entry => entry.Value.ToFrozenDictionary(early => early.Date),
            StringComparer.Ordinal);
    }

    /// <summary>The version stamped onto every generated session row. Bumped when data or logic changes.</summary>
    public short GeneratorVersion { get; }

    /// <summary>Free-text revision marker for operators reading the JSON.</summary>
    public string Revision { get; }

    /// <summary>
    /// Last trading date whose unscheduled closures and early closes have actually been entered.
    /// Sessions past it are rule projections — correct in structure, but no future closure or
    /// day of mourning is in them yet, because none has been announced.
    /// </summary>
    public DateOnly? KnownGoodThrough { get; }

    /// <summary>Every calendar key in the dataset.</summary>
    public IReadOnlyCollection<string> CalendarKeys => _calendars.Keys;

    /// <summary>The checked-in dataset, parsed once per process.</summary>
    public static ExchangeCalendarSet Embedded => EmbeddedSet.Value;

    public CalendarDefinition Calendar(string key) =>
        _calendars.TryGetValue(key, out var calendar)
            ? calendar
            : throw new ArgumentException(
                $"Unknown calendar '{key}'. Known calendars: {string.Join(", ", _calendars.Keys)}.", nameof(key));

    public bool TryGetCalendar(string key, out CalendarDefinition calendar) =>
        _calendars.TryGetValue(key, out calendar!);

    public VenueDefinition Venue(string name) =>
        _venues.TryGetValue(name, out var venue)
            ? venue
            : throw new InvalidOperationException($"Calendar data references unknown venue '{name}'.");

    public IReadOnlyList<HolidayRule> HolidayRules(string setName) =>
        _holidayRuleSets.TryGetValue(setName, out var rules)
            ? rules
            : throw new InvalidOperationException($"Calendar data references unknown holiday rule set '{setName}'.");

    public IReadOnlyList<EarlyCloseRule> EarlyCloseRules(string setName) =>
        _earlyCloseRuleSets.TryGetValue(setName, out var rules)
            ? rules
            : throw new InvalidOperationException($"Calendar data references unknown early-close rule set '{setName}'.");

    public FrozenDictionary<DateOnly, ClosureEntry> Closures(string setName) =>
        _closureSets.TryGetValue(setName, out var closures)
            ? closures
            : throw new InvalidOperationException($"Calendar data references unknown closure set '{setName}'.");

    public FrozenDictionary<DateOnly, UnscheduledEarlyClose> UnscheduledEarlyCloses(string setName) =>
        _earlyCloseSets.TryGetValue(setName, out var earlyCloses)
            ? earlyCloses
            : throw new InvalidOperationException($"Calendar data references unknown early-close set '{setName}'.");

    /// <summary>Parses a dataset from raw JSON — used by tests that need a hand-built dataset.</summary>
    public static ExchangeCalendarSet Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CalendarFile>(json, SerializerOptions)
                   ?? throw new InvalidOperationException("Exchange calendar JSON deserialized to null.");

        return new ExchangeCalendarSet(file);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Weekdays are written as "Monday", not 1 — the file has to be readable by an operator
        // auditing it against a published exchange calendar.
        Converters = { new JsonStringEnumConverter() },
    };

    private static ExchangeCalendarSet LoadEmbedded()
    {
        const string resource = "TradingStuff.ResearchService.Sessions.exchange-calendars.json";

        using var stream = typeof(ExchangeCalendarSet).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>The on-disk shape of <c>exchange-calendars.json</c>.</summary>
    private sealed record CalendarFile
    {
        public short GeneratorVersion { get; init; }
        public string Revision { get; init; } = "";
        public DateOnly? KnownGoodThrough { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = [];
        public Dictionary<string, HolidayRule[]> HolidayRuleSets { get; init; } = [];
        public Dictionary<string, EarlyCloseRule[]> EarlyCloseRuleSets { get; init; } = [];
        public Dictionary<string, ClosureEntry[]> ClosureSets { get; init; } = [];
        public Dictionary<string, UnscheduledEarlyClose[]> EarlyCloseSets { get; init; } = [];
        public Dictionary<string, VenueDefinition> Venues { get; init; } = [];
        public IReadOnlyList<CalendarDefinition> Calendars { get; init; } = [];
    }
}
