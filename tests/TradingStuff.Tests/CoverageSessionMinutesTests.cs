using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Recording;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// Pure unit tests for the coverage denominator — no database, no wall clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the oracle.</b> Every minute count asserted below is a published exchange fact, arithmetic
/// done by hand, and is stated as a literal: NYSE trades 09:30-16:00 ET (390 minutes) and closes
/// 13:00 on a half day (210); Cboe index options trade 08:30-15:15 CT (405) and close 12:15 on a half
/// day (225); Cboe GTH runs 19:15 CT the prior evening to 08:15 CT (780); the CME Globex day runs
/// 17:00 CT the prior evening to 16:00 CT (1,380), truncated to 12:15 CT on a half day (1,155).
/// Nothing here re-derives an expectation from a session the code produced, so a wrong calendar entry
/// or a wrong timezone conversion fails these tests rather than being certified by them.
/// </para>
/// <para>
/// The specific number to watch is 1,185 — one Cboe RTH+GTH recording day. The previous coverage
/// denominator was <c>(to - from)</c>, so a trailing-24h report asked for 1,440 minutes of a market
/// open for 1,185 of them and could not clear the roadmap's 95% acceptance threshold on a flawless
/// day. That is the defect these tests exist to keep fixed.
/// </para>
/// </remarks>
public sealed class CoverageSessionMinutesTests
{
    private const string Nyse = "NYSE";
    private const string CboeRth = "CBOE_INDEX_RTH";
    private const string CboeGth = "CBOE_INDEX_GTH";
    private const string CmeEs = "CME_ES";

    private readonly SessionClock _clock = new();

    private static DateOnly Date(int year, int month, int day) => new(year, month, day);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>Sessions whose TRADING DATE is <paramref name="date"/>, measured over a window wide enough to clip nothing.</summary>
    private int MinutesOn(DateOnly date, params string[] calendars)
    {
        var sessions = calendars.SelectMany(calendar => _clock.SessionsBetween(calendar, date, date));
        var window = SessionMinutes.Clip(
            sessions,
            Utc(date.Year, date.Month, date.Day).AddDays(-3),
            Utc(date.Year, date.Month, date.Day).AddDays(3));

        return SessionMinutes.DistinctMinutes(window);
    }

    /// <summary>Everything overlapping one whole UTC calendar day — what a coverage window actually asks for.</summary>
    private IReadOnlyList<CoverageSession> UtcDay(DateOnly date, params string[] calendars)
    {
        var dayStart = Utc(date.Year, date.Month, date.Day);
        var sessions = calendars.SelectMany(
            calendar => _clock.SessionsBetween(calendar, date.AddDays(-2), date.AddDays(2)));

        return SessionMinutes.Clip(sessions, dayStart, dayStart.AddDays(1));
    }

    // ------------------------------------------------------------------------ the four required cases

    [Theory]
    // A plain Tuesday in June 2025, nothing special about it.
    [InlineData(Nyse, 390)]      // 09:30-16:00 ET
    [InlineData(CboeRth, 405)]   // 08:30-15:15 CT — fifteen minutes past the cash close
    [InlineData(CmeEs, 1380)]    // the Globex day, 17:00 CT the prior evening to 16:00 CT (RTH nests inside it)
    [InlineData(CboeGth, 780)]   // 19:15 CT the prior evening to 08:15 CT
    public void A_normal_trading_day_expects_the_published_session_length(string calendar, int minutes) =>
        Assert.Equal(minutes, MinutesOn(Date(2025, 6, 17), calendar));

    [Theory]
    // 2025-11-28, the Friday after Thanksgiving: the half day the whole US market observes.
    [InlineData(Nyse, 210)]      // 09:30-13:00 ET
    [InlineData(CboeRth, 225)]   // 08:30-12:15 CT
    [InlineData(CmeEs, 1155)]    // 17:00 CT Thursday evening to the 12:15 CT early close
    [InlineData(CboeGth, 780)]   // unchanged: a GTH session ENDS in the morning, before any early close
    public void A_half_day_expects_the_shortened_session_length(string calendar, int minutes)
    {
        var friday = Date(2025, 11, 28);

        Assert.Equal(minutes, MinutesOn(friday, calendar));

        // Not merely shorter by accident — the calendar has to have said so.
        Assert.All(
            _clock.SessionsBetween(calendar, friday, friday),
            session => Assert.Equal(calendar != CboeGth, session.IsHalfDay));
    }

    [Theory]
    [InlineData(2025, 11, 27)] // Thanksgiving
    [InlineData(2025, 1, 9)]   // national day of mourning — Jimmy Carter (a closure, not a rule)
    [InlineData(2025, 4, 18)]  // Good Friday
    [InlineData(2025, 6, 19)]  // Juneteenth, an NYSE holiday since 2022
    public void A_holiday_expects_nothing_at_all(int year, int month, int day)
    {
        var holiday = Date(year, month, day);

        // Zero minutes AND zero sessions: a market that was shut must not contribute a denominator,
        // and must not contribute a zero-length one either.
        foreach (var calendar in new[] { Nyse, CboeRth, CmeEs })
        {
            Assert.Empty(_clock.SessionsBetween(calendar, holiday, holiday));
            Assert.Equal(0, MinutesOn(holiday, calendar));
        }
    }

    [Theory]
    [InlineData(2025, 6, 21)] // Saturday
    [InlineData(2025, 6, 22)] // Sunday
    public void A_weekend_expects_nothing_at_all(int year, int month, int day)
    {
        var weekend = Date(year, month, day);

        foreach (var calendar in new[] { Nyse, CboeRth, CboeGth, CmeEs })
        {
            Assert.Empty(_clock.SessionsBetween(calendar, weekend, weekend));
            Assert.Equal(0, MinutesOn(weekend, calendar));
        }
    }

    // ----------------------------------------------------------------- what the wall clock got wrong

    [Fact]
    public void One_recorded_day_of_spx_is_1185_expected_minutes_not_1440()
    {
        // The regression this whole change exists for. 2026-07-31 is a plain Friday: Cboe GTH runs
        // 00:15-13:15 UTC (780 min) and Cboe RTH 13:30-20:15 UTC (405 min), so a full UTC day of
        // SPX/SPXW recording ought to produce 1,185 expected minutes. The old denominator was the
        // window length, 1,440, which put a perfect day at 82% and made the 95% gate unreachable.
        var day = UtcDay(Date(2026, 7, 31), CboeRth, CboeGth);

        Assert.Equal(1185, SessionMinutes.DistinctMinutes(day));
        Assert.Equal(2, day.Count);

        Assert.Equal(Utc(2026, 7, 31, 0, 15), day[0].MeasuredFromUtc);
        Assert.Equal(Utc(2026, 7, 31, 13, 15), day[0].MeasuredToUtc);
        Assert.Equal("GTH", day[0].Label);

        Assert.Equal(Utc(2026, 7, 31, 13, 30), day[1].MeasuredFromUtc);
        Assert.Equal(Utc(2026, 7, 31, 20, 15), day[1].MeasuredToUtc);
        Assert.Equal("RTH", day[1].Label);
    }

    [Fact]
    public void A_utc_day_containing_no_session_expects_nothing_rather_than_1440()
    {
        // Saturday 2026-08-01. Under wall-clock arithmetic this asked for 1,440 minutes and reported
        // 0% coverage of a market that was shut; the acceptance gate then reads permanently red,
        // which is the state in which a gate stops being read at all.
        Assert.Empty(UtcDay(Date(2026, 8, 1), CboeRth, CboeGth));
        Assert.Equal(0, SessionMinutes.DistinctMinutes(UtcDay(Date(2026, 8, 1), CboeRth, CboeGth)));
    }

    // --------------------------------------------------------------------------- union, not sum

    [Fact]
    public void A_session_nested_inside_another_is_counted_once()
    {
        // CME_ES records its 08:30-15:15 CT RTH row INSIDE the ~23h Globex row for the same trading
        // date. Summing the two would claim 1,785 expected minutes in a 1,380-minute day, which
        // depresses every coverage figure measured against it by ~23%.
        var day = SessionMinutes.Clip(
            _clock.SessionsBetween(CmeEs, Date(2025, 6, 17), Date(2025, 6, 17)),
            Utc(2025, 6, 14),
            Utc(2025, 6, 20));

        Assert.Equal(2, day.Count);
        Assert.Equal(1785, day.Sum(session => session.ExpectedMinutes));
        Assert.Equal(1380, SessionMinutes.DistinctMinutes(day));
    }

    [Fact]
    public void Adjacent_and_overlapping_sessions_merge_without_double_counting()
    {
        // Hand-built rather than generated: the sweep has to be right for shapes the shipped
        // calendars do not currently produce, because the next calendar added may produce them.
        var backToBack = Sessions(
            ("A", Utc(2026, 3, 2, 9, 0), Utc(2026, 3, 2, 10, 0)),
            ("B", Utc(2026, 3, 2, 10, 0), Utc(2026, 3, 2, 11, 0)));

        Assert.Equal(120, SessionMinutes.DistinctMinutes(backToBack));

        var overlapping = Sessions(
            ("A", Utc(2026, 3, 2, 9, 0), Utc(2026, 3, 2, 11, 0)),
            ("B", Utc(2026, 3, 2, 10, 0), Utc(2026, 3, 2, 12, 0)));

        Assert.Equal(180, SessionMinutes.DistinctMinutes(overlapping));

        // The one that breaks a naive sweep: the second session ends EARLIER than the first, so the
        // cursor must not be dragged backwards.
        var swallowed = Sessions(
            ("A", Utc(2026, 3, 2, 9, 0), Utc(2026, 3, 2, 17, 0)),
            ("B", Utc(2026, 3, 2, 10, 0), Utc(2026, 3, 2, 11, 0)),
            ("C", Utc(2026, 3, 2, 16, 0), Utc(2026, 3, 2, 18, 0)));

        Assert.Equal(540, SessionMinutes.DistinctMinutes(swallowed));
    }

    // ------------------------------------------------------------------------------------ DST

    [Fact]
    public void Dst_moves_a_session_in_utc_without_changing_how_many_minutes_it_expects()
    {
        // US DST began 2025-03-09 02:00 local. The Friday before and the Monday after are the same
        // wall-clock session an hour apart in UTC — a fixed-offset denominator would agree on the
        // minute count and disagree on WHICH minutes, which is what makes the numerator miss.
        var friday = UtcDay(Date(2025, 3, 7), Nyse).Single();
        var monday = UtcDay(Date(2025, 3, 10), Nyse).Single();

        Assert.Equal(390, friday.ExpectedMinutes);
        Assert.Equal(390, monday.ExpectedMinutes);

        Assert.Equal(Utc(2025, 3, 7, 14, 30), friday.MeasuredFromUtc);
        Assert.Equal(Utc(2025, 3, 10, 13, 30), monday.MeasuredFromUtc);
    }

    // ------------------------------------------------------------------------------- clipping

    [Fact]
    public void A_window_narrower_than_the_session_expects_only_the_overlap()
    {
        // 2026-07-31 Cboe RTH is 13:30-20:15 UTC; ask for 14:00-14:10 and exactly ten minutes are
        // expected, not the whole session and not the whole window.
        var clipped = SessionMinutes.Clip(
            _clock.SessionsBetween(CboeRth, Date(2026, 7, 31), Date(2026, 7, 31)),
            Utc(2026, 7, 31, 14, 0),
            Utc(2026, 7, 31, 14, 10));

        var session = Assert.Single(clipped);
        Assert.Equal(10, session.ExpectedMinutes);
        Assert.Equal(Utc(2026, 7, 31, 14, 0), session.MeasuredFromUtc);
        Assert.Equal(Utc(2026, 7, 31, 14, 10), session.MeasuredToUtc);

        // The session's own boundaries are still reported unclipped, so an operator can see what was
        // measured against what.
        Assert.Equal(Utc(2026, 7, 31, 13, 30), session.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 20, 15), session.CloseUtc);
    }

    [Fact]
    public void A_window_touching_a_session_at_one_endpoint_expects_nothing_from_it()
    {
        // Half-open [open, close), matching SessionClock's containment rule: a window ending exactly
        // at an open, or starting exactly at a close, contains no minute of that session.
        Assert.Empty(SessionMinutes.Clip(
            _clock.SessionsBetween(CboeRth, Date(2026, 7, 31), Date(2026, 7, 31)),
            Utc(2026, 7, 31, 12, 0),
            Utc(2026, 7, 31, 13, 30)));

        Assert.Empty(SessionMinutes.Clip(
            _clock.SessionsBetween(CboeRth, Date(2026, 7, 31), Date(2026, 7, 31)),
            Utc(2026, 7, 31, 20, 15),
            Utc(2026, 7, 31, 21, 0)));
    }

    [Fact]
    public void A_ragged_window_is_floored_to_whole_minutes_at_both_ends()
    {
        // Both ends floor. Flooring the end drops the partial minute in progress, which can never be
        // fully recorded and would otherwise show as a permanent one-minute deficit on every live
        // report; flooring the start can only ever expect a minute the session partly covers.
        var ragged = SessionMinutes.Clip(
            _clock.SessionsBetween(CboeRth, Date(2026, 7, 31), Date(2026, 7, 31)),
            Utc(2026, 7, 31, 14, 0).AddSeconds(30),
            Utc(2026, 7, 31, 14, 10).AddSeconds(45));

        var session = Assert.Single(ragged);
        Assert.Equal(Utc(2026, 7, 31, 14, 0), session.MeasuredFromUtc);
        Assert.Equal(Utc(2026, 7, 31, 14, 10), session.MeasuredToUtc);
        Assert.Equal(10, session.ExpectedMinutes);
    }

    // ------------------------------------------ a node's own tenure, not the whole window (rotation)

    [Fact]
    public void Intersect_minutes_measures_a_sub_interval_of_the_window_not_the_whole_session()
    {
        // A node's assignment tenure is a sub-interval of the report window, potentially narrower
        // than any one session. 2026-07-31 Cboe RTH is 13:30-20:15 UTC (405 published minutes); a
        // conId assigned only 14:00-14:10 should be measured against ten minutes, not 405 and not the
        // window.
        var day = UtcDay(Date(2026, 7, 31), CboeRth);

        Assert.Equal(10, SessionMinutes.IntersectMinutes(day, Utc(2026, 7, 31, 14, 0), Utc(2026, 7, 31, 14, 10)));
    }

    [Fact]
    public void Intersect_minutes_is_zero_when_the_sub_interval_falls_outside_every_session()
    {
        // Between the 08:15 CT GTH close and the 08:30 CT RTH open the Cboe index book is shut. A
        // node "assigned" only across that gap contributed nothing that ought to have been recorded,
        // so its denominator is zero — not a sliver of the (non-existent) session.
        var day = UtcDay(Date(2026, 7, 31), CboeRth, CboeGth);

        Assert.Equal(0, SessionMinutes.IntersectMinutes(day, Utc(2026, 7, 31, 13, 16), Utc(2026, 7, 31, 13, 29)));
    }

    [Fact]
    public void Intersect_minutes_splits_a_session_at_a_sub_minute_boundary_without_double_counting_or_dropping_it()
    {
        // This additivity is exactly what CoverageMonitor leans on to reassemble a rotated node from
        // its conId segments: splitting one conId's tenure into "old" and "new" at the instant of
        // rotation must reproduce the whole session's minutes exactly, with the boundary minute
        // attributed to exactly one side.
        var day = UtcDay(Date(2026, 7, 31), CboeRth);
        var rotatedAt = Utc(2026, 7, 31, 16, 47).AddSeconds(33); // deliberately not minute-aligned

        var before = SessionMinutes.IntersectMinutes(day, Utc(2026, 7, 31, 13, 30), rotatedAt);
        var after = SessionMinutes.IntersectMinutes(day, rotatedAt, Utc(2026, 7, 31, 20, 15));

        Assert.Equal(197, before); // 13:30 to the 16:47 floor: 3h17m
        Assert.Equal(208, after);  // the 16:47 floor to 20:15: 3h28m — the boundary minute lands HERE
        Assert.Equal(405, before + after); // the full published RTH length, split but not lost or doubled
    }

    [Fact]
    public void Intersect_minutes_counts_a_nested_session_once_even_across_the_whole_span()
    {
        // CME_ES nests its RTH row inside the Globex GTH row for the same trading date. A sub-interval
        // spanning the entire day must still see the union (1,380 minutes), not the naive sum (1,785),
        // the same guarantee DistinctMinutes gives the window as a whole.
        var day = SessionMinutes.Clip(
            _clock.SessionsBetween(CmeEs, Date(2025, 6, 17), Date(2025, 6, 17)),
            Utc(2025, 6, 14),
            Utc(2025, 6, 20));

        Assert.Equal(1380, SessionMinutes.IntersectMinutes(day, Utc(2025, 6, 16), Utc(2025, 6, 18)));
    }

    // ------------------------------------------------- the table must agree with the generator

    [Fact]
    public void A_persisted_calendar_identical_to_the_generator_matches_in_any_order()
    {
        var generated = _clock.SessionsBetween(CmeEs, Date(2025, 6, 16), Date(2025, 6, 18));

        // Real rows carry database-assigned session ids and can come back in any order; neither is a
        // disagreement about when the market was open.
        var persisted = generated
            .Select((session, index) => session with { SessionId = index + 1 })
            .Reverse()
            .ToArray();

        Assert.True(SessionMinutes.Matches(persisted, generated));
    }

    [Fact]
    public void A_session_missing_from_the_table_does_not_match()
    {
        // The dangerous direction, and the reason this check exists at all: a query over
        // research.sessions cannot emit a row for a session that is not there, so the absence shrinks
        // the denominator — and a smaller denominator makes coverage read HIGHER, not lower.
        var generated = _clock.SessionsBetween(CboeRth, Date(2025, 6, 16), Date(2025, 6, 20));
        var persisted = generated.Skip(1).ToArray();

        Assert.False(SessionMinutes.Matches(persisted, generated));
    }

    [Fact]
    public void A_boundary_that_is_an_hour_out_does_not_match_even_though_the_count_does()
    {
        // The classic DST mistake: same number of rows, one of them an hour wide of the truth. A
        // count comparison would pass this and quietly measure against 60 wrong minutes.
        var generated = _clock.SessionsBetween(Nyse, Date(2025, 3, 10), Date(2025, 3, 14));
        var persisted = generated
            .Select((session, index) => index == 0
                ? session with { OpenUtc = session.OpenUtc.AddHours(1) }
                : session)
            .ToArray();

        Assert.Equal(generated.Count, persisted.Length);
        Assert.False(SessionMinutes.Matches(persisted, generated));
    }

    [Fact]
    public void A_half_day_flag_that_disagrees_does_not_match()
    {
        var generated = _clock.SessionsBetween(Nyse, Date(2025, 11, 28), Date(2025, 11, 28));
        var persisted = generated.Select(session => session with { IsHalfDay = false }).ToArray();

        Assert.False(SessionMinutes.Matches(persisted, generated));
    }

    [Fact]
    public void A_phantom_row_the_generator_never_produced_does_not_match()
    {
        // A closure entered after the calendar was last written retires a session. Leaving the row
        // behind keeps a phantom trading day alive in every SQL consumer.
        var generated = _clock.SessionsBetween(Nyse, Date(2025, 1, 8), Date(2025, 1, 10));
        var persisted = generated
            .Append(new TradingSession(
                99, Nyse, Date(2025, 1, 9), Utc(2025, 1, 9, 14, 30), Utc(2025, 1, 9, 21, 0), "RTH", false))
            .ToArray();

        Assert.False(SessionMinutes.Matches(persisted, generated));
    }

    // ---------------------------------------------- the operator-facing reconciliation view

    [Fact]
    public void The_calendar_view_names_every_way_the_table_can_disagree()
    {
        var service = new SessionCalendarService(
            new SessionGenerator(),
            new ConfigurationBuilder().Build(),
            NullLogger<SessionCalendarService>.Instance);

        var week = (From: Date(2025, 11, 24), To: Date(2025, 11, 28));
        var generated = new SessionClock().SessionsBetween(Nyse, week.From, week.To);

        // Four trading dates in Thanksgiving week (the Thursday is a holiday). One is persisted
        // correctly, one an hour out, one at an older generator version, one absent altogether — plus
        // a row for the holiday that never happened.
        var persisted = new List<PersistedSession>
        {
            new(generated[0] with { SessionId = 1 }, SessionGenerator.GeneratorVersion),
            new(generated[1] with { SessionId = 2, CloseUtc = generated[1].CloseUtc.AddHours(1) },
                SessionGenerator.GeneratorVersion),
            new(generated[2] with { SessionId = 3 }, 0),
            new(new TradingSession(
                    4, Nyse, Date(2025, 11, 27), Utc(2025, 11, 27, 14, 30), Utc(2025, 11, 27, 21, 0), "RTH", false),
                SessionGenerator.GeneratorVersion),
        };

        var view = service.Reconcile(
            [Nyse], week.From, week.To,
            new Dictionary<string, List<PersistedSession>> { [Nyse] = persisted },
            databaseConfigured: true);

        Assert.Equal(4, view.Generated); // Mon, Tue, Wed, Fri — Thanksgiving Thursday is not a session
        Assert.Equal(1, view.InSync);
        Assert.Equal(2, view.Mismatched); // the hour-out close and the stale generator version
        Assert.Equal(1, view.Missing);    // the half-day Friday
        Assert.Equal(1, view.Phantom);    // Thanksgiving

        var thanksgiving = Assert.Single(view.Sessions, entry => entry.TradingDate == Date(2025, 11, 27));
        Assert.Equal(SessionCalendarEntryState.Phantom, thanksgiving.State);
        Assert.Null(thanksgiving.Generated);

        var halfDay = Assert.Single(view.Sessions, entry => entry.TradingDate == Date(2025, 11, 28));
        Assert.Equal(SessionCalendarEntryState.Missing, halfDay.State);
        Assert.Equal(210, halfDay.DurationMinutes);
        Assert.Null(halfDay.Persisted);
    }

    private static IReadOnlyList<CoverageSession> Sessions(
        params (string Label, DateTimeOffset From, DateTimeOffset To)[] intervals) =>
        SessionMinutes.Clip(
            intervals.Select((interval, index) => new TradingSession(
                index + 1,
                "TEST",
                DateOnly.FromDateTime(interval.From.UtcDateTime),
                interval.From,
                interval.To,
                interval.Label,
                false)),
            DateTimeOffset.MinValue.AddDays(1),
            DateTimeOffset.MaxValue.AddDays(-1));
}
