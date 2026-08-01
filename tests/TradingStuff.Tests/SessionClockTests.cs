using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// Pure unit tests for the session calendar — no database, no clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the oracle.</b> <see cref="SessionGenerator"/> manufactures the reference data that coverage
/// denominators, gap detection and (in Phase 4) feature cutoffs are all validated against, so a test
/// that merely restates the implementation is worse than no test — it certifies whatever the code
/// happens to do. Every expected UTC instant below was therefore derived OUTSIDE this codebase,
/// from one of:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>docs/research/ibkr-data-capability-matrix.md</c> — probe-recorded, from a live 2026-07-31 TWS
/// session: SPX <c>useRTH=0</c> bars start at 19:15 US/Central on the prior calendar day, and the
/// SPX/SPXW GTH session runs 20:15-09:25 ET. Both halves of that (CT open, ET open) are asserted, so
/// a wrong timezone cannot satisfy them simultaneously. The close was re-measured on 2026-08-01 —
/// TWS reports <c>tradingHours</c> <c>1915-0825</c> CT for an SPXW contract and its last overnight
/// 1-minute bar is stamped 08:24 CT — which moved it ten minutes later; see
/// <see cref="ExchangeSessionScheduleTests"/>.
/// </item>
/// <item>
/// Published exchange facts: NYSE 09:30-16:00 ET with a 13:00 ET half-day close; Cboe index options
/// closing 15 minutes after the cash equity market (15:15 CT full days, 12:15 CT half days); the
/// dated closures (9/11, Sandy, the days of mourning); MLK becoming an NYSE holiday in 1998 and
/// Juneteenth in 2022; Good Friday's known dates.
/// </item>
/// <item>
/// The IANA tz database via CPython's <c>zoneinfo</c> — a completely separate implementation from
/// the ICU tables .NET reads — used to convert those exchange-local wall-clock facts into the exact
/// UTC instants asserted here.
/// </item>
/// </list>
/// </remarks>
public sealed class SessionClockTests
{
    private const string Nyse = "NYSE";
    private const string CboeRth = "CBOE_INDEX_RTH";
    private const string CboeGth = "CBOE_INDEX_GTH";
    private const string CmeEs = "CME_ES";

    private readonly SessionClock _clock = new();

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DateOnly Date(int year, int month, int day) => new(year, month, day);

    private TradingSession Single(string calendar, DateOnly date, string label)
    {
        var sessions = _clock.SessionsBetween(calendar, date, date).Where(s => s.Label == label).ToArray();
        Assert.Single(sessions);

        return sessions[0];
    }

    // ------------------------------------------------------------------ exact instants, plain days

    [Fact]
    public void Nyse_regular_sessions_are_exact_utc_instants_in_both_standard_and_daylight_time()
    {
        // NYSE 09:30-16:00 America/New_York. 2025-01-15 is EST (UTC-5), 2025-07-15 is EDT (UTC-4).
        var winter = Single(Nyse, Date(2025, 1, 15), "RTH");
        Assert.Equal(Utc(2025, 1, 15, 14, 30), winter.OpenUtc);
        Assert.Equal(Utc(2025, 1, 15, 21, 0), winter.CloseUtc);
        Assert.False(winter.IsHalfDay);

        // The contract says OpenUtc is "always the literal UTC instant" — assert the offset itself,
        // not just the instant, since DateTimeOffset equality would accept any equivalent offset.
        Assert.Equal(TimeSpan.Zero, winter.OpenUtc.Offset);
        Assert.Equal(TimeSpan.Zero, winter.CloseUtc.Offset);

        var summer = Single(Nyse, Date(2025, 7, 15), "RTH");
        Assert.Equal(Utc(2025, 7, 15, 13, 30), summer.OpenUtc);
        Assert.Equal(Utc(2025, 7, 15, 20, 0), summer.CloseUtc);
    }

    [Fact]
    public void Cboe_index_rth_closes_fifteen_minutes_after_the_cash_equity_market()
    {
        // SPX/SPXW RTH is 08:30-15:15 America/Chicago = 09:30-16:15 ET: the index options trade a
        // quarter hour past the equity close. 2026-07-31 is CDT (UTC-5).
        var cboe = Single(CboeRth, Date(2026, 7, 31), "RTH");
        Assert.Equal(Utc(2026, 7, 31, 13, 30), cboe.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 20, 15), cboe.CloseUtc);

        var nyse = Single(Nyse, Date(2026, 7, 31), "RTH");
        Assert.Equal(nyse.OpenUtc, cboe.OpenUtc);
        Assert.Equal(TimeSpan.FromMinutes(15), cboe.CloseUtc - nyse.CloseUtc);
    }

    // ------------------------------------------------------------------------------ DST: UTC ← local

    [Fact]
    public void Spring_forward_moves_every_subsequent_open_an_hour_earlier_in_utc()
    {
        // US DST began 2025-03-09 02:00 local. The Friday before and the Monday after are the same
        // wall-clock session on either side of the transition.
        Assert.Equal(Utc(2025, 3, 7, 14, 30), Single(Nyse, Date(2025, 3, 7), "RTH").OpenUtc);
        Assert.Equal(Utc(2025, 3, 10, 13, 30), Single(Nyse, Date(2025, 3, 10), "RTH").OpenUtc);

        // Same transition seen from America/Chicago, so a zone mix-up cannot pass both.
        Assert.Equal(Utc(2025, 3, 7, 14, 30), Single(CboeRth, Date(2025, 3, 7), "RTH").OpenUtc);
        Assert.Equal(Utc(2025, 3, 10, 13, 30), Single(CboeRth, Date(2025, 3, 10), "RTH").OpenUtc);
    }

    [Fact]
    public void Fall_back_moves_every_subsequent_open_an_hour_later_in_utc()
    {
        // US DST ended 2025-11-02 02:00 local.
        Assert.Equal(Utc(2025, 10, 31, 13, 30), Single(Nyse, Date(2025, 10, 31), "RTH").OpenUtc);
        Assert.Equal(Utc(2025, 11, 3, 14, 30), Single(Nyse, Date(2025, 11, 3), "RTH").OpenUtc);

        Assert.Equal(Utc(2025, 10, 31, 13, 30), Single(CboeRth, Date(2025, 10, 31), "RTH").OpenUtc);
        Assert.Equal(Utc(2025, 11, 3, 14, 30), Single(CboeRth, Date(2025, 11, 3), "RTH").OpenUtc);
    }

    [Fact]
    public void An_ambiguous_local_boundary_widens_the_session_instead_of_truncating_it()
    {
        // 2025-11-02 01:30 local occurs TWICE in both US zones. Policy: an open takes the first
        // occurrence and a close the second, so the session spans the whole repeated hour and no
        // observation recorded inside it can fall outside every session.
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var ambiguous = new DateTime(2025, 11, 2, 1, 30, 0);

        Assert.True(eastern.IsAmbiguousTime(ambiguous));
        Assert.True(central.IsAmbiguousTime(ambiguous));

        // 01:30 EDT (UTC-4) then 01:30 EST (UTC-5).
        Assert.Equal(
            Utc(2025, 11, 2, 5, 30),
            SessionGenerator.ToUtc(ambiguous, eastern, SessionBoundary.Open));
        Assert.Equal(
            Utc(2025, 11, 2, 6, 30),
            SessionGenerator.ToUtc(ambiguous, eastern, SessionBoundary.Close));

        // 01:30 CDT (UTC-5) then 01:30 CST (UTC-6).
        Assert.Equal(
            Utc(2025, 11, 2, 6, 30),
            SessionGenerator.ToUtc(ambiguous, central, SessionBoundary.Open));
        Assert.Equal(
            Utc(2025, 11, 2, 7, 30),
            SessionGenerator.ToUtc(ambiguous, central, SessionBoundary.Close));
    }

    [Fact]
    public void An_invalid_local_boundary_resolves_to_the_offset_transition_instant()
    {
        // 2025-03-09 02:30 local NEVER OCCURS in either zone — the clock jumps 01:59:59 -> 03:00:00.
        // Policy: both ends resolve to the transition instant itself, 02:00 standard time.
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var invalid = new DateTime(2025, 3, 9, 2, 30, 0);

        Assert.True(eastern.IsInvalidTime(invalid));
        Assert.True(central.IsInvalidTime(invalid));

        // 02:00 EST = 07:00Z; 02:00 CST = 08:00Z.
        Assert.Equal(Utc(2025, 3, 9, 7, 0), SessionGenerator.ToUtc(invalid, eastern, SessionBoundary.Open));
        Assert.Equal(Utc(2025, 3, 9, 7, 0), SessionGenerator.ToUtc(invalid, eastern, SessionBoundary.Close));
        Assert.Equal(Utc(2025, 3, 9, 8, 0), SessionGenerator.ToUtc(invalid, central, SessionBoundary.Open));
        Assert.Equal(Utc(2025, 3, 9, 8, 0), SessionGenerator.ToUtc(invalid, central, SessionBoundary.Close));

        // The resolved instant is the first one at which the wall clock has passed the request.
        Assert.Equal(
            new DateTime(2025, 3, 9, 3, 0, 0),
            TimeZoneInfo.ConvertTimeFromUtc(Utc(2025, 3, 9, 7, 0).UtcDateTime, eastern));
    }

    // ------------------------------------------------------------------------------ DST: local ← UTC

    [Fact]
    public void Instants_inside_both_dst_windows_resolve_to_the_following_trading_date()
    {
        // The reverse direction: given a UTC instant sitting in the skipped hour's window (Sunday
        // 07:30Z = 03:30 EDT) and in the repeated hour (Sunday 06:00Z = the second 01:00 EST),
        // the next session to open is the Monday after each transition.
        Assert.Equal(Date(2025, 3, 10), _clock.TradingDateOf(Nyse, Utc(2025, 3, 9, 7, 30)));
        Assert.Equal(Date(2025, 11, 3), _clock.TradingDateOf(Nyse, Utc(2025, 11, 2, 6, 0)));

        // Neither instant is inside any session — both are Sunday, when the venue is shut.
        Assert.Null(_clock.SessionAt(Nyse, Utc(2025, 3, 9, 7, 30)));
        Assert.Null(_clock.SessionAt(Nyse, Utc(2025, 11, 2, 6, 0)));

        // And the session boundaries either side of a transition still contain what they should.
        Assert.Equal(Date(2025, 3, 10), _clock.SessionAt(Nyse, Utc(2025, 3, 10, 13, 30))!.TradingDate);
        Assert.Null(_clock.SessionAt(Nyse, Utc(2025, 3, 10, 13, 29)));
        Assert.Equal(Date(2025, 11, 3), _clock.SessionAt(Nyse, Utc(2025, 11, 3, 14, 30))!.TradingDate);
        Assert.Null(_clock.SessionAt(Nyse, Utc(2025, 11, 3, 14, 29)));
    }

    // ------------------------------------------------------------- GTH: trading date != calendar date

    [Fact]
    public void Cboe_gth_opens_the_evening_before_its_trading_date_at_the_probe_recorded_time()
    {
        // INDEPENDENT ORACLE. docs/research/ibkr-data-capability-matrix.md, from the live read-only
        // TWS probe of 2026-07-31: SPX useRTH=0 bars begin at 19:15 US/Central on the PRIOR calendar
        // day, and the SPX/SPXW GTH session is 20:15-09:15 ET. Nothing in this repo computed those
        // two facts; they were observed on the wire and cross-checked against Cboe's published hours.
        //
        // 2026-07-30 19:15 CDT (UTC-5) = 2026-07-31 00:15Z, and the same instant is 2026-07-30
        // 20:15 EDT — so the CT-recorded probe time and the ET-published session time must agree on
        // one instant. A wrong zone or a wrong day offset fails at least one of these.
        var gth = Single(CboeGth, Date(2026, 7, 31), "GTH");

        Assert.Equal(Utc(2026, 7, 31, 0, 15), gth.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 13, 25), gth.CloseUtc);
        Assert.Equal(Date(2026, 7, 31), gth.TradingDate);

        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(
            new DateTime(2026, 7, 30, 20, 15, 0),
            TimeZoneInfo.ConvertTimeFromUtc(gth.OpenUtc.UtcDateTime, eastern));
        Assert.Equal(
            new DateTime(2026, 7, 31, 9, 25, 0),
            TimeZoneInfo.ConvertTimeFromUtc(gth.CloseUtc.UtcDateTime, eastern));

        // The open is on the previous CALENDAR day in exchange-local terms; bucketing by local date
        // would file this session's first hours under 2026-07-30.
        var central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        Assert.Equal(
            new DateTime(2026, 7, 30, 19, 15, 0),
            TimeZoneInfo.ConvertTimeFromUtc(gth.OpenUtc.UtcDateTime, central));
    }

    [Fact]
    public void A_sunday_evening_gth_observation_belongs_to_mondays_trading_date()
    {
        // 2026-01-04 is a Sunday; 2026-01-05 the Monday. Cboe GTH for Monday opens Sunday 19:15 CST
        // (UTC-6) = 2026-01-05 01:15Z and closes Monday 08:25 CST = 14:25Z.
        var gth = Single(CboeGth, Date(2026, 1, 5), "GTH");
        Assert.Equal(Utc(2026, 1, 5, 1, 15), gth.OpenUtc);
        Assert.Equal(Utc(2026, 1, 5, 14, 25), gth.CloseUtc);

        // An instant at 19:30 CST Sunday: exchange-local date SUNDAY, trading date MONDAY.
        var sundayEvening = Utc(2026, 1, 5, 1, 30);
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CboeGth, sundayEvening));
        Assert.Equal(Date(2026, 1, 5), _clock.SessionAt(CboeGth, sundayEvening)!.TradingDate);
    }

    [Fact]
    public void The_whole_es_globex_span_maps_to_one_trading_date_even_across_the_utc_date_change()
    {
        // The CME Globex day for trading date Monday 2026-01-05 runs 17:00 CST Sunday through
        // 16:00 CST Monday = 2026-01-04 23:00Z .. 2026-01-05 22:00Z. Its open is on the PREVIOUS
        // UTC DATE, which is where naive UTC-date bucketing breaks: an ES tick at 23:30Z on
        // Sunday 2026-01-04 is Monday's data.
        var globex = Single(CmeEs, Date(2026, 1, 5), "GTH");
        Assert.Equal(Utc(2026, 1, 4, 23, 0), globex.OpenUtc);
        Assert.Equal(Utc(2026, 1, 5, 22, 0), globex.CloseUtc);

        DateTimeOffset[] acrossTheSpan =
        [
            Utc(2026, 1, 4, 23, 0),   // the open itself
            Utc(2026, 1, 4, 23, 30),  // still Sunday in UTC and in Chicago
            Utc(2026, 1, 5, 0, 0),    // UTC midnight
            Utc(2026, 1, 5, 6, 0),    // overnight
            Utc(2026, 1, 5, 14, 30),  // the RTH open
            Utc(2026, 1, 5, 21, 59),  // after the RTH close, still inside the Globex day
        ];

        foreach (var instant in acrossTheSpan)
        {
            Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CmeEs, instant));
        }

        // Exactly at the close the session no longer contains the instant, and the forward rule
        // hands it to the NEXT trading date rather than back to the one that just ended.
        Assert.Null(_clock.SessionAt(CmeEs, Utc(2026, 1, 5, 22, 0)));
        Assert.Equal(Date(2026, 1, 6), _clock.TradingDateOf(CmeEs, Utc(2026, 1, 5, 22, 0)));
    }

    [Fact]
    public void The_rth_calendar_key_attributes_overnight_instants_correctly_but_not_the_other_way_round()
    {
        // Cboe's two sessions are separate calendar keys, which makes it possible to ask the wrong
        // one. This test pins both answers so the asymmetry is deliberate and visible rather than
        // discovered in Phase 5.
        //
        // A GTH instant (Sunday 19:30 CST) gets Monday from BOTH keys: it is inside Monday's GTH
        // session, and it is also before Monday's RTH open, which the forward rule resolves the
        // same way. The RTH key is therefore safe for trading-date attribution of any instant.
        var overnight = Utc(2026, 1, 5, 1, 30);
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CboeGth, overnight));
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CboeRth, overnight));

        // The reverse is the footgun: a midday RTH instant is inside no GTH session, so the GTH key
        // hands it forward to the NEXT overnight session — which belongs to the following trading
        // date. Correct for the question that key answers, wrong for the question a caller probably
        // meant. Documented on TradingDateOf.
        var midday = Utc(2026, 1, 5, 18, 0); // 12:00 CST Monday, mid-RTH
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CboeRth, midday));
        Assert.Equal(Date(2026, 1, 6), _clock.TradingDateOf(CboeGth, midday));
        Assert.Null(_clock.SessionAt(CboeGth, midday));

        // CME_ES carries both labels on one key, so it has no such asymmetry.
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CmeEs, midday));
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(CmeEs, Utc(2026, 1, 5, 6, 0)));
    }

    [Fact]
    public void Es_rth_wins_over_the_globex_day_it_is_nested_inside()
    {
        // CME_ES carries both rows for one trading date: the full Globex day as GTH and the
        // 08:30-15:15 CT equity-index regular session as RTH. SessionAt must return the more
        // specific one, while SessionsBetween still reports both.
        var rthInstant = Utc(2026, 1, 5, 15, 0);
        Assert.Equal("RTH", _clock.SessionAt(CmeEs, rthInstant)!.Label);
        Assert.Equal("GTH", _clock.SessionAt(CmeEs, Utc(2026, 1, 5, 6, 0))!.Label);

        var both = _clock.SessionsBetween(CmeEs, Date(2026, 1, 5), Date(2026, 1, 5));
        Assert.Equal(2, both.Count);
        Assert.Equal(Utc(2026, 1, 5, 14, 30), both.Single(s => s.Label == "RTH").OpenUtc);
        Assert.Equal(Utc(2026, 1, 5, 21, 15), both.Single(s => s.Label == "RTH").CloseUtc);
    }

    [Fact]
    public void An_instant_between_sessions_is_assigned_forward_never_back()
    {
        // Friday 2026-01-02 16:30 ET, half an hour after the NYSE close (21:30Z). Assigning it back
        // to Friday would let a "trading date 2026-01-02" cutoff admit post-close information; the
        // forward rule gives it the next trading date, Monday 2026-01-05.
        Assert.Null(_clock.SessionAt(Nyse, Utc(2026, 1, 2, 21, 30)));
        Assert.Equal(Date(2026, 1, 5), _clock.TradingDateOf(Nyse, Utc(2026, 1, 2, 21, 30)));

        // Across a three-day weekend too: Friday 2026-01-16 evening, with Monday 2026-01-19 being
        // MLK Day, lands on Tuesday 2026-01-20.
        Assert.False(_clock.IsTradingDay(Nyse, Date(2026, 1, 19)));
        Assert.Equal(Date(2026, 1, 20), _clock.TradingDateOf(Nyse, Utc(2026, 1, 16, 22, 0)));
    }

    // ----------------------------------------------------------------- unscheduled closures (data)

    [Fact]
    public void The_four_day_september_2001_closure_removes_every_session_and_trading_resumes_on_the_17th()
    {
        // Published exchange history: US equity markets closed Tuesday 2001-09-11 through Friday
        // 2001-09-14 and reopened Monday 2001-09-17 — the longest closure since 1933.
        Assert.NotEmpty(_clock.SessionsBetween(Nyse, Date(2001, 9, 10), Date(2001, 9, 10)));
        Assert.Empty(_clock.SessionsBetween(Nyse, Date(2001, 9, 11), Date(2001, 9, 14)));

        var reopening = Single(Nyse, Date(2001, 9, 17), "RTH");
        Assert.Equal(Utc(2001, 9, 17, 13, 30), reopening.OpenUtc);
        Assert.Equal(Utc(2001, 9, 17, 20, 0), reopening.CloseUtc);

        // A rule-only generator would have produced four phantom sessions here; the closure list is
        // what prevents it, so assert the reason is actually recorded rather than just the absence.
        var generator = new SessionGenerator();

        for (var day = 11; day <= 14; day++)
        {
            Assert.Contains("September 11", generator.UnscheduledClosure(Nyse, Date(2001, 9, day))!.Reason);
        }
    }

    [Theory]
    // Days of mourning: single-session closures no rule can derive.
    [InlineData(1994, 4, 27, "Richard Nixon")]
    [InlineData(2004, 6, 11, "Ronald Reagan")]
    [InlineData(2007, 1, 2, "Gerald Ford")]
    [InlineData(2018, 12, 5, "George H. W. Bush")]
    [InlineData(2025, 1, 9, "Jimmy Carter")]
    public void A_national_day_of_mourning_removes_exactly_one_session(int year, int month, int day, string who)
    {
        var date = Date(year, month, day);

        Assert.False(_clock.IsTradingDay(Nyse, date));
        Assert.Empty(_clock.SessionsBetween(Nyse, date, date));
        Assert.Contains(who, new SessionGenerator().UnscheduledClosure(Nyse, date)!.Reason);

        // Neighbouring trading days are untouched — a closure must not leak into adjacent dates.
        Assert.NotEmpty(_clock.SessionsBetween(Nyse, date.AddDays(-4), date.AddDays(-1)));
        Assert.NotEmpty(_clock.SessionsBetween(Nyse, date.AddDays(1), date.AddDays(4)));
    }

    [Fact]
    public void Hurricane_sandy_closed_nyse_and_cboe_while_cme_globex_stayed_open()
    {
        // 2012-10-29 and 2012-10-30: the NYSE was shut for two days — its first weather closure
        // since 1985 — while futures continued to trade electronically on Globex. This is the case
        // that proves closures are per-venue data rather than one shared holiday list.
        foreach (var date in new[] { Date(2012, 10, 29), Date(2012, 10, 30) })
        {
            Assert.Empty(_clock.SessionsBetween(Nyse, date, date));
            Assert.Empty(_clock.SessionsBetween(CboeRth, date, date));
            Assert.NotEmpty(_clock.SessionsBetween(CmeEs, date, date));
        }

        // 2012-10-29 is a Monday, so ES's Globex day opens Sunday 2012-10-28 17:00 CDT = 22:00Z.
        var globex = Single(CmeEs, Date(2012, 10, 29), "GTH");
        Assert.Equal(Utc(2012, 10, 28, 22, 0), globex.OpenUtc);
    }

    // ---------------------------------------------------------------------------- half days (data)

    [Fact]
    public void The_day_after_thanksgiving_closes_early_at_each_venues_own_time()
    {
        // Thanksgiving 2025 is the fourth Thursday, 2025-11-27; the half day is Friday 2025-11-28,
        // in EST/CST. NYSE closes 13:00 ET (18:00Z). Cboe index options close 12:15 CT (18:15Z) —
        // the same fifteen-minute tail they keep on full days. THE TWO VENUES DISAGREE by 15
        // minutes on the same date, which is the whole reason early-close times are per-venue.
        var nyse = Single(Nyse, Date(2025, 11, 28), "RTH");
        Assert.True(nyse.IsHalfDay);
        Assert.Equal(Utc(2025, 11, 28, 14, 30), nyse.OpenUtc);
        Assert.Equal(Utc(2025, 11, 28, 18, 0), nyse.CloseUtc);

        var cboe = Single(CboeRth, Date(2025, 11, 28), "RTH");
        Assert.True(cboe.IsHalfDay);
        Assert.Equal(Utc(2025, 11, 28, 14, 30), cboe.OpenUtc);
        Assert.Equal(Utc(2025, 11, 28, 18, 15), cboe.CloseUtc);
        Assert.Equal(TimeSpan.FromMinutes(15), cboe.CloseUtc - nyse.CloseUtc);

        // The CME Globex day is truncated by the same early close, so ES's overnight leg ends at
        // 12:15 CT rather than 16:00 CT.
        var globex = Single(CmeEs, Date(2025, 11, 28), "GTH");
        Assert.True(globex.IsHalfDay);
        Assert.Equal(Utc(2025, 11, 28, 18, 15), globex.CloseUtc);

        // Cboe's OVERNIGHT session ends in the morning, so an afternoon early close cannot shorten
        // it — it is a full-length session on a half day, and must not be flagged as one.
        var gth = Single(CboeGth, Date(2025, 11, 28), "GTH");
        Assert.False(gth.IsHalfDay);
        Assert.Equal(Utc(2025, 11, 28, 1, 15), gth.OpenUtc);   // 2025-11-27 19:15 CST
        Assert.Equal(Utc(2025, 11, 28, 14, 25), gth.CloseUtc); // 08:25 CST
    }

    [Fact]
    public void Christmas_eve_and_the_day_before_independence_day_close_early_only_when_the_holiday_is_a_weekday()
    {
        // 2025-12-24 is a Wednesday with Christmas on Thursday: a half day, 13:00 EST = 18:00Z.
        var christmasEve = Single(Nyse, Date(2025, 12, 24), "RTH");
        Assert.True(christmasEve.IsHalfDay);
        Assert.Equal(Utc(2025, 12, 24, 18, 0), christmasEve.CloseUtc);

        // 2025-07-03 is a Thursday with July 4 on Friday: a half day, 13:00 EDT = 17:00Z.
        var julyThird = Single(Nyse, Date(2025, 7, 3), "RTH");
        Assert.True(julyThird.IsHalfDay);
        Assert.Equal(Utc(2025, 7, 3, 17, 0), julyThird.CloseUtc);

        // 2015-07-04 fell on a Saturday, so Friday 2015-07-03 was the OBSERVED HOLIDAY, not a half
        // day — and the Thursday before it was a full session. This is the case a naive
        // "always shorten July 3" rule gets wrong in both directions at once.
        Assert.Empty(_clock.SessionsBetween(Nyse, Date(2015, 7, 3), Date(2015, 7, 3)));
        Assert.False(Single(Nyse, Date(2015, 7, 2), "RTH").IsHalfDay);

        // 2021-12-24 was the observed Christmas holiday (Dec 25 was a Saturday): closed, not short.
        Assert.Empty(_clock.SessionsBetween(Nyse, Date(2021, 12, 24), Date(2021, 12, 24)));
        Assert.False(Single(Nyse, Date(2021, 12, 23), "RTH").IsHalfDay);

        // 2016-12-26 was the observed Christmas holiday (Dec 25 a Sunday) and there was no early
        // close that year at all: Friday 2016-12-23 was a full session.
        Assert.Empty(_clock.SessionsBetween(Nyse, Date(2016, 12, 26), Date(2016, 12, 26)));
        Assert.False(Single(Nyse, Date(2016, 12, 23), "RTH").IsHalfDay);
    }

    [Fact]
    public void An_unscheduled_early_close_uses_its_own_recorded_time()
    {
        // 1997-10-27: the market-wide circuit breaker tripped at -550 on the Dow and the NYSE closed
        // for the day at 15:30 ET = 20:30Z, half an hour early. No rule derives this; it is data.
        // 1997 DST ended on the LAST Sunday in October (the 26th), so this Monday is already EST:
        // 09:30 ET = 14:30Z, 15:30 ET = 20:30Z. See the pre-2007 DST test below.
        var session = Single(Nyse, Date(1997, 10, 27), "RTH");

        Assert.True(session.IsHalfDay);
        Assert.Equal(Utc(1997, 10, 27, 14, 30), session.OpenUtc);
        Assert.Equal(Utc(1997, 10, 27, 20, 30), session.CloseUtc);
    }

    [Fact]
    public void Dst_boundaries_follow_the_rule_in_force_at_the_time_not_todays()
    {
        // The US DST window changed in 2007 (Energy Policy Act 2005): before then it ran from the
        // first Sunday in April to the last Sunday in October, after it from the second Sunday in
        // March to the first Sunday in November. This platform's history reaches back to 1993, so
        // roughly half of it lives under the old rule — dates in late March/early April and in late
        // October/early November are an hour off if the modern window is assumed. TimeZoneInfo
        // carries the historical adjustment rules; hand-rolled offset arithmetic would not.
        Assert.Equal(Utc(2005, 4, 1, 14, 30), Single(Nyse, Date(2005, 4, 1), "RTH").OpenUtc);   // still EST
        Assert.Equal(Utc(2005, 4, 4, 13, 30), Single(Nyse, Date(2005, 4, 4), "RTH").OpenUtc);   // EDT began Apr 3
        Assert.Equal(Utc(2006, 10, 30, 14, 30), Single(Nyse, Date(2006, 10, 30), "RTH").OpenUtc); // already EST
        Assert.Equal(Utc(2006, 11, 1, 14, 30), Single(Nyse, Date(2006, 11, 1), "RTH").OpenUtc);

        // Under the modern rule the same early-March and early-November dates are the other way up.
        Assert.Equal(Utc(2007, 3, 12, 13, 30), Single(Nyse, Date(2007, 3, 12), "RTH").OpenUtc);
        Assert.Equal(Utc(2007, 11, 1, 13, 30), Single(Nyse, Date(2007, 11, 1), "RTH").OpenUtc);
    }

    // ------------------------------------------------------------------ holidays that changed over time

    [Fact]
    public void Mlk_day_was_a_full_trading_day_before_1998()
    {
        // MLK Day became an NYSE holiday in 1998. The third Monday of January 1997 was the 20th and
        // the market traded; 1998's was the 19th and it did not.
        Assert.True(_clock.IsTradingDay(Nyse, Date(1997, 1, 20)));
        Assert.NotEmpty(_clock.SessionsBetween(Nyse, Date(1997, 1, 20), Date(1997, 1, 20)));

        Assert.False(_clock.IsTradingDay(Nyse, Date(1998, 1, 19)));
        Assert.Empty(_clock.SessionsBetween(Nyse, Date(1998, 1, 19), Date(1998, 1, 19)));
    }

    [Fact]
    public void Juneteenth_became_a_market_holiday_in_2022()
    {
        // June 19 2021 was a Saturday and Juneteenth was not yet observed by the exchanges: Friday
        // 2021-06-18 was a normal session. First observance was Monday 2022-06-20 (Jun 19 a Sunday).
        Assert.True(_clock.IsTradingDay(Nyse, Date(2021, 6, 18)));
        Assert.False(_clock.IsTradingDay(Nyse, Date(2022, 6, 20)));
        Assert.False(_clock.IsTradingDay(Nyse, Date(2023, 6, 19)));
    }

    [Fact]
    public void New_years_day_on_a_saturday_leaves_the_preceding_friday_a_full_session()
    {
        // NYSE Rule 7.2 excepts New Year's Day from the "Saturday holiday moves to Friday" rule.
        // Jan 1 2005, 2011 and 2022 all fell on a Saturday; Dec 31 2004, 2010 and 2021 all traded.
        foreach (var lastDay in new[] { Date(2004, 12, 31), Date(2010, 12, 31), Date(2021, 12, 31) })
        {
            Assert.True(_clock.IsTradingDay(Nyse, lastDay));
            Assert.False(Single(Nyse, lastDay, "RTH").IsHalfDay);
        }

        // Jan 1 on a Sunday moves the holiday to the Monday (2023-01-02).
        Assert.False(_clock.IsTradingDay(Nyse, Date(2023, 1, 2)));
        Assert.True(_clock.IsTradingDay(Nyse, Date(2023, 1, 3)));
    }

    [Theory]
    // Published Good Friday dates — the exchanges' only movable-feast holiday.
    [InlineData(2001, 4, 13)]
    [InlineData(2012, 4, 6)]
    [InlineData(2024, 3, 29)]
    [InlineData(2025, 4, 18)]
    [InlineData(2026, 4, 3)]
    public void Good_friday_is_closed_on_its_published_dates(int year, int month, int day)
    {
        var goodFriday = Date(year, month, day);

        Assert.Equal(DayOfWeek.Friday, goodFriday.DayOfWeek);
        Assert.False(_clock.IsTradingDay(Nyse, goodFriday));
        Assert.False(_clock.IsTradingDay(CboeRth, goodFriday));

        // The Thursday before and the Monday after are ordinary sessions.
        Assert.True(_clock.IsTradingDay(Nyse, goodFriday.AddDays(-1)));
        Assert.True(_clock.IsTradingDay(Nyse, goodFriday.AddDays(3)));
    }

    [Theory]
    // Published NYSE trading-day counts, and the number of half days each year. Both were derived
    // by hand from the calendar (weekdays in the year, minus the holidays actually observed that
    // year) and cross-checked against the widely published session counts — not from this code.
    // Between them they pin down the New Year's-on-a-Saturday exception (2022 has no Jan holiday),
    // Juneteenth's arrival (2020/2021 have nine holidays, 2022 onward ten), the day-of-mourning
    // closure (2025 loses one session to Jimmy Carter), and every half-day rule branch.
    [InlineData(2020, 253, 2)]
    [InlineData(2021, 252, 1)]
    [InlineData(2022, 251, 1)]
    [InlineData(2023, 250, 2)]
    [InlineData(2024, 252, 3)]
    [InlineData(2025, 250, 3)]
    public void Nyse_session_counts_match_the_published_trading_day_counts(int year, int sessions, int halfDays)
    {
        var year_ = _clock.SessionsBetween(Nyse, Date(year, 1, 1), Date(year, 12, 31));

        Assert.Equal(sessions, year_.Count);
        Assert.Equal(halfDays, year_.Count(session => session.IsHalfDay));
    }

    // --------------------------------------------------------------------------- structural guards

    [Fact]
    public void Cboe_gth_produces_no_sessions_before_the_overnight_market_existed()
    {
        // Cboe's near-24x5 index-option session began in November 2022. Generating GTH rows for
        // 2015 would manufacture reference data for a market that was not trading.
        Assert.Empty(_clock.SessionsBetween(CboeGth, Date(2015, 1, 1), Date(2015, 12, 31)));
        Assert.Empty(_clock.SessionsBetween(CboeGth, Date(2022, 1, 1), Date(2022, 11, 20)));
        Assert.NotEmpty(_clock.SessionsBetween(CboeGth, Date(2022, 11, 21), Date(2022, 11, 30)));

        // RTH for the same venue goes back to the start of the dataset.
        Assert.NotEmpty(_clock.SessionsBetween(CboeRth, Date(2015, 1, 1), Date(2015, 1, 31)));
    }

    [Fact]
    public void Asking_for_a_trading_date_before_a_calendar_existed_throws_rather_than_guessing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _clock.TradingDateOf(CboeGth, Utc(2015, 6, 1, 12, 0)));

        Assert.Throws<ArgumentException>(() => _clock.SessionsBetween("NOT_A_CALENDAR", Date(2025, 1, 1), Date(2025, 1, 2)));
    }

    [Fact]
    public void Sessions_are_ordered_positive_length_and_non_overlapping_within_a_label()
    {
        // A sweep over the platform's entire horizon — 1993 (the SPY head timestamp) through the
        // last year research.bars has a partition for. It crosses both DST rules, every unscheduled
        // closure, every half-day class, and roughly 45,000 sessions. Overlapping sessions would let
        // one instant belong to two trading dates, which is the failure mode SessionAt's half-open
        // interval exists to prevent; a non-positive session would be rejected by the table's
        // CHECK (close_utc > open_utc) at insert time instead of here.
        foreach (var calendar in _clock.Calendars)
        {
            var sessions = _clock.SessionsBetween(calendar, Date(1993, 1, 1), Date(2035, 12, 31));

            Assert.NotEmpty(sessions);

            foreach (var group in sessions.GroupBy(session => session.Label))
            {
                var ordered = group.OrderBy(session => session.OpenUtc).ToArray();

                for (var i = 0; i < ordered.Length; i++)
                {
                    Assert.True(ordered[i].CloseUtc > ordered[i].OpenUtc, $"{calendar} {ordered[i].TradingDate}");

                    if (i > 0)
                    {
                        Assert.True(
                            ordered[i].OpenUtc >= ordered[i - 1].CloseUtc,
                            $"{calendar} {group.Key} sessions overlap at {ordered[i].TradingDate}");
                    }
                }

                // One row per trading date per label — research.sessions has a UNIQUE constraint on
                // exactly this, so a duplicate here would fail the insert rather than be detected.
                Assert.Equal(ordered.Length, ordered.Select(session => session.TradingDate).Distinct().Count());
            }

            Assert.Equal(
                sessions.OrderBy(s => s.TradingDate).ThenBy(s => s.OpenUtc).ToArray(),
                sessions.ToArray());
        }
    }

    [Fact]
    public void Generation_is_deterministic_and_independent_of_the_requested_range()
    {
        // The same trading date must produce identical rows whether it is asked for alone, inside a
        // month, or inside a decade — otherwise a regenerated calendar could differ from a
        // previously persisted one for no reason but the range boundaries.
        var wide = new SessionGenerator().Generate(CboeRth, Date(2020, 1, 1), Date(2026, 12, 31));
        var narrow = new SessionGenerator().Generate(CboeRth, Date(2024, 3, 1), Date(2024, 3, 31));

        Assert.Equal(
            wide.Where(s => s.TradingDate.Year == 2024 && s.TradingDate.Month == 3).ToArray(),
            narrow.ToArray());

        var single = new SessionGenerator().Generate(CboeRth, Date(2024, 3, 8), Date(2024, 3, 8));
        Assert.Equal(narrow.Where(s => s.TradingDate == Date(2024, 3, 8)).ToArray(), single.ToArray());
    }

    [Fact]
    public void The_generator_version_constant_matches_the_checked_in_calendar_data()
    {
        // research.sessions.generator_version is what distinguishes a regenerated calendar from a
        // stale one. If the JSON is edited without bumping the constant (or vice versa) the stamped
        // version stops meaning anything — this test is the forcing function.
        Assert.Equal(SessionGenerator.GeneratorVersion, ExchangeCalendarSet.Embedded.GeneratorVersion);
    }

    [Fact]
    public void Every_calendar_key_in_the_data_resolves_and_produces_sessions()
    {
        Assert.Contains(Nyse, _clock.Calendars);
        Assert.Contains(CboeRth, _clock.Calendars);
        Assert.Contains(CboeGth, _clock.Calendars);
        Assert.Contains(CmeEs, _clock.Calendars);

        foreach (var calendar in _clock.Calendars)
        {
            var sessions = _clock.SessionsBetween(calendar, Date(2026, 1, 1), Date(2026, 1, 31));

            Assert.NotEmpty(sessions);
            Assert.All(sessions, session => Assert.True(session.Label is "RTH" or "GTH"));
            Assert.All(sessions, session => Assert.Equal(calendar, session.Calendar));
        }
    }
}
