using System.Collections.Concurrent;
using System.Collections.Frozen;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Sessions;

/// <summary>Which end of a session a wall-clock time is being resolved for.</summary>
/// <remarks>
/// The distinction only matters when the local time is ambiguous — see
/// <see cref="SessionGenerator.ToUtc"/> for the widening rule and why it is the safe direction.
/// </remarks>
public enum SessionBoundary
{
    Open,
    Close,
}

/// <summary>
/// Turns <c>exchange-calendars.json</c> into concrete <see cref="TradingSession"/> rows. Pure and
/// deterministic: same data + same range ⇒ byte-identical sessions, no clock, no database.
/// </summary>
/// <remarks>
/// <para>
/// This is a GROUND-TRUTH MANUFACTURER. Coverage denominators, gap detection, and (transitively) the
/// Phase 4 leakage firewall's feature cutoffs are all expressed in the sessions and trading dates
/// this class produces, and none of them can detect that a boundary is wrong — a wrong session is
/// simply believed. Everything here is therefore deliberately boring: holidays that are statutory
/// formulas are computed, and everything an exchange *decided* is read from checked-in data. That
/// includes the direction people forget — a venue trading a SHORTENED session on a day the holiday
/// rules call it closed. CME equity index does exactly that on nearly every US holiday, and modelling
/// those days as closures deleted a 1,140-minute session and pushed every observation inside it onto
/// the following trading date. See <c>partialSessionSets</c> in the JSON.
/// </para>
/// <para>
/// <b>DST doctrine.</b> <see cref="TimeZoneInfo"/> does the conversion; this class only decides what
/// an ambiguous or nonexistent local wall-clock boundary MEANS. Both cases resolve in the direction
/// that makes the session wider, never narrower, because a session boundary is the containment
/// predicate for real recorded observations: if a clock artifact shrinks a session, real ticks fall
/// outside every session and become invisible to coverage and to as-of feature reads. A slightly
/// over-wide session, by contrast, shows up as an explainable low-coverage minute. Concretely —
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Ambiguous</b> (the hour repeated at the autumn fall-back): an OPEN takes the first occurrence
/// (the larger, still-DST offset ⇒ earlier UTC), a CLOSE takes the second (the standard-time offset
/// ⇒ later UTC). The session therefore spans the whole repeated hour.
/// </item>
/// <item>
/// <b>Invalid</b> (the hour skipped at the spring-forward): both ends resolve to the offset
/// transition instant itself — the single UTC instant at which the exchange's clock jumps across the
/// requested wall time. There is no earlier/later choice to make: the requested time never occurs,
/// and the discontinuity is the only instant that is genuinely "at" it. Shifting forward by the DST
/// delta instead would invent a boundary an hour away from where the operator wrote it.
/// </item>
/// </list>
/// <para>
/// No session boundary in the shipped calendars currently lands in either window — US transitions
/// happen at 02:00 local on a Sunday, when every venue here is shut — so in practice DST only shifts
/// consecutive sessions by an hour in UTC. The policy exists and is tested anyway, because the next
/// calendar added (a non-US venue, a maintenance-window boundary) may not be so lucky, and a silent
/// exception at that point is exactly the failure this component cannot afford.
/// </para>
/// </remarks>
public sealed class SessionGenerator(ExchangeCalendarSet data)
{
    /// <summary>
    /// Stamped onto every <c>research.sessions</c> row. <b>Bump on ANY change to generation logic or
    /// to <c>exchange-calendars.json</c></b> — a regenerated calendar must be distinguishable from a
    /// stale one. A unit test asserts this equals the JSON's own <c>generatorVersion</c>, so the two
    /// cannot drift.
    /// </summary>
    public const short GeneratorVersion = 3;

    /// <summary>Sessions generated but not yet persisted carry this id.</summary>
    public const long UnpersistedSessionId = 0;

    private readonly ConcurrentDictionary<(string Calendar, int Year), TradingSession[]> _byYear = new();
    private readonly ConcurrentDictionary<(string Venue, int Year), YearRules> _rulesByYear = new();

    /// <summary>The dataset these sessions come from.</summary>
    public ExchangeCalendarSet Data { get; } = data;

    public SessionGenerator() : this(ExchangeCalendarSet.Embedded)
    {
    }

    /// <summary>
    /// Every session on <paramref name="calendar"/> with a trading date in
    /// [<paramref name="from"/>, <paramref name="to"/>], ordered by trading date then open.
    /// Dates before the calendar's <see cref="CalendarDefinition.EffectiveFrom"/> yield nothing —
    /// those sessions did not exist, and inventing them would manufacture reference data.
    /// </summary>
    public IReadOnlyList<TradingSession> Generate(string calendar, DateOnly from, DateOnly to)
    {
        var definition = Data.Calendar(calendar);

        if (from < definition.EffectiveFrom)
        {
            from = definition.EffectiveFrom;
        }

        if (to < from)
        {
            return [];
        }

        var sessions = new List<TradingSession>();

        for (var year = from.Year; year <= to.Year; year++)
        {
            foreach (var session in ForYear(calendar, year))
            {
                if (session.TradingDate >= from && session.TradingDate <= to)
                {
                    sessions.Add(session);
                }
            }
        }

        return sessions;
    }

    /// <summary>Whether <paramref name="date"/> is a trading date on <paramref name="calendar"/>'s venue.</summary>
    public bool IsTradingDay(string calendar, DateOnly date)
    {
        var definition = Data.Calendar(calendar);

        return date >= definition.EffectiveFrom && Rules(definition.Venue, date.Year).IsTradingDay(date);
    }

    /// <summary>Why the venue was closed on <paramref name="date"/>, or null if it was not an unscheduled closure.</summary>
    public ClosureEntry? UnscheduledClosure(string calendar, DateOnly date)
    {
        var definition = Data.Calendar(calendar);

        return Rules(definition.Venue, date.Year).Closures.TryGetValue(date, out var closure) ? closure : null;
    }

    /// <summary>
    /// The shortened session the venue traded on <paramref name="date"/> despite its rules calling the
    /// day closed, or null if <paramref name="date"/> is an ordinary trading day or a real closure.
    /// </summary>
    public PartialSessionEntry? PartialSession(string calendar, DateOnly date)
    {
        var definition = Data.Calendar(calendar);

        return date >= definition.EffectiveFrom ? Rules(definition.Venue, date.Year).PartialSessionOn(date) : null;
    }

    private TradingSession[] ForYear(string calendar, int year) =>
        _byYear.GetOrAdd((calendar, year), key => Build(key.Calendar, key.Year));

    private TradingSession[] Build(string calendar, int year)
    {
        var definition = Data.Calendar(calendar);
        var rules = Rules(definition.Venue, year);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZone);
        var eveningsClosed = definition.EveningSessionClosedOn.ToFrozenSet();

        var sessions = new List<TradingSession>();
        var last = new DateOnly(year, 12, 31);

        for (var date = new DateOnly(year, 1, 1); date <= last; date = date.AddDays(1))
        {
            if (date < definition.EffectiveFrom || !rules.IsTradingDay(date))
            {
                continue;
            }

            var early = rules.EarlyCloseOn(date);

            // A partial session is a date the rules call closed on which the venue in fact traded a
            // shortened session — nearly every US holiday, for CME equity index. It overrides both
            // the normal close and the early-close rules, and it names which sessions ran: on a
            // shortened Globex holiday there is no 08:30-15:15 regular session at all, and a Good
            // Friday closing at 08:15 CT would otherwise generate an RTH row of negative length.
            var partial = rules.PartialSessionOn(date);
            var emitted = 0;

            foreach (var template in definition.Sessions)
            {
                if (!template.AppliesOn(date))
                {
                    continue;
                }

                if (partial is not null && !partial.Sessions.Contains(template.Label, StringComparer.Ordinal))
                {
                    continue;
                }

                var openLocalDate = date.AddDays(template.OpenDayOffset);

                // An overnight session is suppressed when the venue does not open that evening at
                // all. Note the default is the opposite: Globex reopens at 17:00 CT even on a
                // holiday evening, so the session belonging to the NEXT trading date exists.
                if (template.OpenDayOffset < 0 && eveningsClosed.Contains(openLocalDate))
                {
                    continue;
                }

                var isHalfDay = partial is not null || (early is not null && template.AppliesEarlyClose);
                var closeTime = template.Close;

                if (partial is not null)
                {
                    closeTime = partial.Close;
                }
                else if (isHalfDay)
                {
                    closeTime = early!.Value.Time
                                ?? template.EarlyClose
                                ?? throw new InvalidOperationException(
                                    $"Calendar '{calendar}' session '{template.Label}' takes early closes but " +
                                    $"defines no earlyClose time, and {date:yyyy-MM-dd} has no explicit override.");
                }

                var openUtc = ToUtc(openLocalDate.ToDateTime(template.Open), timeZone, SessionBoundary.Open);
                var closeUtc = ToUtc(
                    date.AddDays(template.CloseDayOffset).ToDateTime(closeTime), timeZone, SessionBoundary.Close);

                if (closeUtc <= openUtc)
                {
                    // research.sessions has CHECK (close_utc > open_utc); failing here names the
                    // offending calendar and date instead of surfacing as an opaque constraint error.
                    throw new InvalidOperationException(
                        $"Calendar '{calendar}' produced a non-positive session on {date:yyyy-MM-dd} " +
                        $"({template.Label}): open {openUtc:O} >= close {closeUtc:O}.");
                }

                sessions.Add(new TradingSession(
                    UnpersistedSessionId, calendar, date, openUtc, closeUtc, template.Label, isHalfDay));
                emitted++;
            }

            // A partial-session date that produces nothing is the absent-row failure this platform
            // keeps rediscovering: the day silently reverts to "closed", which shrinks every
            // denominator and renders as health. Only reachable by naming a label the calendar does
            // not define, so fail at generation naming both.
            if (partial is not null && emitted == 0)
            {
                throw new InvalidOperationException(
                    $"Calendar '{calendar}' has a partial session on {date:yyyy-MM-dd} listing " +
                    $"[{string.Join(", ", partial.Sessions)}], but no session template matched — the day " +
                    "would silently generate as closed.");
            }
        }

        return [.. sessions.OrderBy(session => session.TradingDate).ThenBy(session => session.OpenUtc)];
    }

    private YearRules Rules(string venue, int year) =>
        _rulesByYear.GetOrAdd((venue, year), key => YearRules.Build(Data, key.Venue, key.Year));

    // ---------------- timezone conversion — the ONLY place local wall clock becomes UTC ----------------

    /// <summary>
    /// Converts an exchange-local wall-clock boundary to its UTC instant, resolving DST ambiguity and
    /// invalidity per the doctrine on <see cref="SessionGenerator"/>.
    /// </summary>
    internal static DateTimeOffset ToUtc(DateTime localWallClock, TimeZoneInfo timeZone, SessionBoundary boundary)
    {
        var local = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(local))
        {
            return OffsetTransitionInstant(local, timeZone);
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);

            // UTC = local - offset, so the LARGER offset (e.g. -05:00 CDT) is the FIRST occurrence
            // and the smaller (-06:00 CST) the second. Opens take the first, closes the second, so
            // the session spans the whole repeated hour rather than being truncated by it.
            var offset = boundary is SessionBoundary.Open ? offsets.Max() : offsets.Min();

            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>
    /// The exact UTC instant at which <paramref name="timeZone"/>'s offset jumps across
    /// <paramref name="invalidLocal"/> — found by bisecting on the offset rather than by assuming
    /// where or how large the transition is, so a zone with a 00:30 or half-hour transition is handled
    /// as correctly as a US 02:00 one.
    /// </summary>
    private static DateTimeOffset OffsetTransitionInstant(DateTime invalidLocal, TimeZoneInfo timeZone)
    {
        // Anchor a day earlier at midday: unambiguous and valid in every real zone, and safely before
        // any transition that could make invalidLocal nonexistent.
        var anchor = invalidLocal.Date.AddDays(-1).AddHours(12);
        var low = new DateTimeOffset(anchor, timeZone.GetUtcOffset(anchor)).ToUniversalTime();
        var high = low.AddDays(3);
        var before = timeZone.GetUtcOffset(low);

        if (timeZone.GetUtcOffset(high) == before)
        {
            throw new InvalidOperationException(
                $"{invalidLocal:O} is invalid in {timeZone.Id} but no offset transition was found around it.");
        }

        while (high - low > TimeSpan.FromTicks(1))
        {
            var middle = low + TimeSpan.FromTicks((high - low).Ticks / 2);

            if (timeZone.GetUtcOffset(middle) == before)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        // Self-check: the transition must actually bracket the requested wall time — local time just
        // before it is earlier than the request, and at it has jumped past the request.
        var localBefore = TimeZoneInfo.ConvertTimeFromUtc(low.UtcDateTime, timeZone);
        var localAt = TimeZoneInfo.ConvertTimeFromUtc(high.UtcDateTime, timeZone);

        if (localBefore >= invalidLocal || localAt <= invalidLocal)
        {
            throw new InvalidOperationException(
                $"Offset transition at {high:O} in {timeZone.Id} does not bracket invalid local time {invalidLocal:O}.");
        }

        return high;
    }

    // ---------------- holiday / early-close rules, resolved once per venue-year ----------------

    /// <summary>An early close on a given date: a flag plus an optional explicit override time.</summary>
    private readonly record struct EarlyClose(TimeOnly? Time, string Reason);

    /// <summary>One venue-year's resolved closed dates and early-close dates.</summary>
    private sealed class YearRules
    {
        private FrozenSet<DateOnly> _holidays = FrozenSet<DateOnly>.Empty;
        private FrozenDictionary<DateOnly, EarlyClose> _earlyCloses = FrozenDictionary<DateOnly, EarlyClose>.Empty;
        private FrozenDictionary<DateOnly, PartialSessionEntry> _partials =
            FrozenDictionary<DateOnly, PartialSessionEntry>.Empty;

        public FrozenDictionary<DateOnly, ClosureEntry> Closures { get; private set; } =
            FrozenDictionary<DateOnly, ClosureEntry>.Empty;

        public static YearRules Build(ExchangeCalendarSet data, string venueName, int year)
        {
            var venue = data.Venue(venueName);
            var holidayRules = data.HolidayRules(venue.HolidayRuleSet);
            var earlyCloseRules = data.EarlyCloseRules(venue.EarlyCloseRuleSet);
            var closures = data.Closures(venue.ClosureSet);
            var unscheduledEarly = data.UnscheduledEarlyCloses(venue.UnscheduledEarlyCloseSet);
            var partials = data.PartialSessions(venue.PartialSessionSet);

            // Neighbouring years are evaluated too: an observance rule can move a holiday across a
            // year boundary (a Jan 1 shifted back to Dec 31, say). Cheap, and it removes a whole
            // class of off-by-one-year defect that would otherwise only appear on rare dates.
            var holidays = new HashSet<DateOnly>();

            for (var candidateYear = year - 1; candidateYear <= year + 1; candidateYear++)
            {
                foreach (var rule in holidayRules)
                {
                    if (!Applies(rule.FromYear, rule.ToYear, candidateYear))
                    {
                        continue;
                    }

                    var observed = ObservedDate(rule, candidateYear);

                    if (observed.Year == year)
                    {
                        holidays.Add(observed);
                    }
                }
            }

            var earlyCloses = new Dictionary<DateOnly, EarlyClose>();

            bool IsClosed(DateOnly date) =>
                date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                || holidays.Contains(date)
                || closures.ContainsKey(date);

            for (var candidateYear = year - 1; candidateYear <= year + 1; candidateYear++)
            {
                foreach (var rule in earlyCloseRules)
                {
                    if (!Applies(rule.FromYear, rule.ToYear, candidateYear))
                    {
                        continue;
                    }

                    var holiday = holidayRules.FirstOrDefault(h => h.Name == rule.Holiday)
                                  ?? throw new InvalidOperationException(
                                      $"Early-close rule '{rule.Name}' references unknown holiday '{rule.Holiday}'.");

                    if (!Applies(holiday.FromYear, holiday.ToYear, candidateYear))
                    {
                        continue;
                    }

                    var date = rule.Kind switch
                    {
                        // Relative to the holiday's NATURAL date, and only when that date is itself a
                        // weekday. July 3 is a half day only when July 4 falls Mon-Fri; when July 4
                        // falls on a Saturday, July 3 is the observed holiday, not a half day.
                        "dayBefore" => NaturalDate(holiday, candidateYear) is { } natural
                                       && natural.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                            ? natural.AddDays(-1)
                            : (DateOnly?)null,

                        // Relative to the OBSERVED date — the Friday after Thanksgiving.
                        "dayAfter" => ObservedDate(holiday, candidateYear).AddDays(1),

                        _ => throw new InvalidOperationException($"Unknown early-close rule kind '{rule.Kind}'."),
                    };

                    if (date is { } value && value.Year == year && !IsClosed(value))
                    {
                        earlyCloses[value] = new EarlyClose(null, rule.Name);
                    }
                }
            }

            // Explicit entries win over rules: an unscheduled early close carries its own wall-clock
            // time, which a rule-derived one never does.
            foreach (var (date, entry) in unscheduledEarly)
            {
                if (date.Year == year && !IsClosed(date))
                {
                    earlyCloses[date] = new EarlyClose(entry.Close, entry.Reason);
                }
            }

            // A partial-session entry only ever REOPENS a day the rules close. One naming a date that
            // is already a trading day, or a weekend, is an operator error in the reference data —
            // and a silent one, because the entry's close would then quietly shorten a normal session
            // or manufacture a Saturday. Reject it here, so a mis-projected future holiday date (the
            // unverified 2027 entries are hand-computed observance dates) fails loudly the first time
            // anything generates that year.
            var thisYear = new Dictionary<DateOnly, PartialSessionEntry>();

            foreach (var (date, entry) in partials)
            {
                if (date.Year != year)
                {
                    continue;
                }

                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    throw new InvalidOperationException(
                        $"Venue '{venueName}' has a partial session on {date:yyyy-MM-dd}, a " +
                        $"{date.DayOfWeek}. Partial sessions reopen closed weekdays; they cannot " +
                        "manufacture a weekend session.");
                }

                if (!holidays.Contains(date) && !closures.ContainsKey(date))
                {
                    throw new InvalidOperationException(
                        $"Venue '{venueName}' has a partial session on {date:yyyy-MM-dd}, which is " +
                        "already an ordinary trading day. Use an unscheduled early close for a day the " +
                        "venue opened normally and closed early.");
                }

                thisYear[date] = entry;
            }

            return new YearRules
            {
                _holidays = holidays.ToFrozenSet(),
                _earlyCloses = earlyCloses.ToFrozenDictionary(),
                _partials = thisYear.ToFrozenDictionary(),
                Closures = closures,
            };
        }

        public bool IsTradingDay(DateOnly date) =>
            date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && (_partials.ContainsKey(date) || (!_holidays.Contains(date) && !Closures.ContainsKey(date)));

        public EarlyClose? EarlyCloseOn(DateOnly date) =>
            _earlyCloses.TryGetValue(date, out var early) ? early : null;

        public PartialSessionEntry? PartialSessionOn(DateOnly date) =>
            _partials.TryGetValue(date, out var partial) ? partial : null;

        private static bool Applies(int? fromYear, int? toYear, int year) =>
            (fromYear is null || year >= fromYear) && (toYear is null || year <= toYear);

        /// <summary>The holiday's date before any weekend observance is applied.</summary>
        private static DateOnly? NaturalDate(HolidayRule rule, int year) => rule.Kind switch
        {
            "fixed" => new DateOnly(year, rule.Month, rule.Day),
            _ => null,
        };

        private static DateOnly ObservedDate(HolidayRule rule, int year) => rule.Kind switch
        {
            "fixed" => Observe(new DateOnly(year, rule.Month, rule.Day), rule.Observance),
            "nthWeekday" => NthWeekday(year, rule.Month, rule.Weekday, rule.Nth),
            "lastWeekday" => LastWeekday(year, rule.Month, rule.Weekday),
            "goodFriday" => Easter(year).AddDays(-2),
            _ => throw new InvalidOperationException($"Unknown holiday rule kind '{rule.Kind}' for '{rule.Name}'."),
        };

        private static DateOnly Observe(DateOnly natural, string observance) => observance switch
        {
            "nearestWeekday" => natural.DayOfWeek switch
            {
                DayOfWeek.Saturday => natural.AddDays(-1),
                DayOfWeek.Sunday => natural.AddDays(1),
                _ => natural,
            },
            // New Year's Day: NYSE does NOT close the preceding Friday when Jan 1 falls on a
            // Saturday (Dec 31 2004, Dec 31 2010 and Dec 31 2021 were all full sessions).
            "sundayToMonday" => natural.DayOfWeek is DayOfWeek.Sunday ? natural.AddDays(1) : natural,
            "saturdayToFriday" => natural.DayOfWeek is DayOfWeek.Saturday ? natural.AddDays(-1) : natural,
            "none" or "" => natural,
            _ => throw new InvalidOperationException($"Unknown observance '{observance}'."),
        };

        private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int nth)
        {
            var first = new DateOnly(year, month, 1);
            var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;

            return first.AddDays(offset + 7 * (nth - 1));
        }

        private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
        {
            var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            var offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;

            return last.AddDays(-offset);
        }

        /// <summary>Gregorian Easter Sunday (Meeus/Butcher "anonymous" algorithm).</summary>
        private static DateOnly Easter(int year)
        {
            var a = year % 19;
            var b = year / 100;
            var c = year % 100;
            var d = b / 4;
            var e = b % 4;
            var f = (b + 8) / 25;
            var g = (b - f + 1) / 3;
            var h = ((19 * a) + b - d - g + 15) % 30;
            var i = c / 4;
            var k = c % 4;
            var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
            var m = (a + (11 * h) + (22 * l)) / 451;
            var month = (h + l - (7 * m) + 114) / 31;
            var day = ((h + l - (7 * m) + 114) % 31) + 1;

            return new DateOnly(year, month, day);
        }
    }
}
