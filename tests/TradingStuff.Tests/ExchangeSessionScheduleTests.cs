using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// The session shapes that <c>exchange-calendars.json</c> asserts, pinned against what the venues
/// actually publish.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the oracle.</b> Same discipline as <see cref="SessionClockTests"/>, and it matters more
/// here: every number below was read off a live paper TWS socket (server version 223) on 2026-08-01
/// and is stated as a literal. Nothing is re-derived from <see cref="SessionGenerator"/>, so a wrong
/// calendar entry fails these tests instead of being certified by them.
/// </para>
/// <para>
/// The primary source is <c>reqHistoricalData(whatToShow: "SCHEDULE")</c>, which returns one
/// (start, end, refDate) triple per session in the contract's own time zone — the venue schedule as
/// IBKR distributes it, not an inference from where bars stop. Where the same claim could be checked
/// a second way (<c>contractDetails</c> <c>tradingHours</c>, or the span of real 1-minute bars) it
/// was, and both agreed; those cross-checks are noted per test. UTC instants were converted from the
/// exchange-local wall clock with CPython's <c>zoneinfo</c>, a separate implementation from the ICU
/// tables .NET reads.
/// </para>
/// <para>
/// <b>The four defects these exist for.</b> (1) CME emitted no session at all on US holidays Globex
/// actually trades short, so a 1,140-minute session vanished and every observation inside it was
/// attributed to the following trading date. (2) The SPX INDEX was measured against the SPX OPTION
/// close, 405 minutes against 390 real ones, which made every SPX RTH session report
/// <c>succeeded_but_absent</c>. (3) The same mapping claimed a 780-minute overnight window the index
/// does not have. (4) Cboe's index-option GTH session closed ten minutes early in the calendar, so
/// real quotes belonged to no session.
/// </para>
/// </remarks>
public sealed class ExchangeSessionScheduleTests
{
    private const string Nyse = "NYSE";
    private const string CboeOptionRth = "CBOE_INDEX_RTH";
    private const string CboeOptionGth = "CBOE_INDEX_GTH";
    private const string SpxIndex = "CBOE_SPX_RTH";
    private const string VixRth = "CBOE_VIX_RTH";
    private const string VixGth = "CBOE_VIX_GTH";
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

    private static int Minutes(TradingSession session) => (int)(session.CloseUtc - session.OpenUtc).TotalMinutes;

    // ============================================================== DEFECT 1: CME holiday sessions

    [Theory]
    // Observed with reqHistoricalData(SCHEDULE) against ESU6 (conId 649180671) and ESZ4 (495512557),
    // whose schedules agree date-for-date over their overlap. Columns: trading date, then the
    // session's open and close as UTC instants, then its length in minutes.
    //
    // Every one of these is 17:00 CT the prior evening to 12:00 CT on the holiday = 1,140 minutes.
    // The generator used to emit NOTHING on any of them.
    [InlineData(2025, 11, 27, 2025, 11, 26, 23, 0, 2025, 11, 27, 18, 0, 1140)] // Thanksgiving
    [InlineData(2025, 7, 4, 2025, 7, 3, 22, 0, 2025, 7, 4, 17, 0, 1140)]       // Independence Day
    [InlineData(2026, 1, 19, 2026, 1, 18, 23, 0, 2026, 1, 19, 18, 0, 1140)]    // Martin Luther King Jr. Day
    [InlineData(2026, 7, 3, 2026, 7, 2, 22, 0, 2026, 7, 3, 17, 0, 1140)]       // Independence Day observed (Jul 4 a Saturday)
    [InlineData(2024, 6, 19, 2024, 6, 18, 22, 0, 2024, 6, 19, 17, 0, 1140)]    // Juneteenth
    [InlineData(2023, 9, 4, 2023, 9, 3, 22, 0, 2023, 9, 4, 17, 0, 1140)]       // Labor Day
    // Good Friday 2026 is the exception in length, not in kind: CME equity index closes at 08:15 CT.
    [InlineData(2026, 4, 3, 2026, 4, 2, 22, 0, 2026, 4, 3, 13, 15, 915)]
    // The Jimmy Carter day of mourning. NYSE and Cboe shut; Globex traded to 08:30 CT.
    [InlineData(2025, 1, 9, 2025, 1, 8, 23, 0, 2025, 1, 9, 14, 30, 930)]
    public void Cme_trades_a_shortened_globex_session_on_days_the_holiday_rules_call_it_closed(
        int year, int month, int day,
        int openYear, int openMonth, int openDay, int openHour, int openMinute,
        int closeYear, int closeMonth, int closeDay, int closeHour, int closeMinute,
        int minutes)
    {
        var tradingDate = Date(year, month, day);
        var globex = Single(CmeEs, tradingDate, "GTH");

        Assert.Equal(Utc(openYear, openMonth, openDay, openHour, openMinute), globex.OpenUtc);
        Assert.Equal(Utc(closeYear, closeMonth, closeDay, closeHour, closeMinute), globex.CloseUtc);
        Assert.Equal(minutes, Minutes(globex));
        Assert.True(globex.IsHalfDay);

        // ONLY the Globex row. There is no 08:30-15:15 CT regular session on a day the whole market
        // shuts at noon, IBKR emits no daily bar for these dates, and on a Good Friday closing at
        // 08:15 CT an RTH row would have negative length.
        Assert.Equal(
            new[] { "GTH" },
            _clock.SessionsBetween(CmeEs, tradingDate, tradingDate).Select(s => s.Label).ToArray());

        // The venues that really were shut stay shut. This is the whole reason closures are
        // per-venue data rather than one shared holiday list.
        Assert.Empty(_clock.SessionsBetween(Nyse, tradingDate, tradingDate));
        Assert.Empty(_clock.SessionsBetween(CboeOptionRth, tradingDate, tradingDate));

        // The reason is recorded, not just the shape — an operator auditing this file has to be able
        // to see WHY a rule-derived holiday became a trading date.
        var entry = new SessionGenerator().PartialSession(CmeEs, tradingDate);
        Assert.NotNull(entry);
        Assert.NotEmpty(entry!.Reason);
        Assert.Equal(CalendarConfidence.High, entry.Confidence);
    }

    [Theory]
    // The contrast that makes the fix a per-date fact rather than "CME ignores US holidays".
    // Measured: 2024-12-24 and 2025-12-24 close 12:15 CT with no evening reopen, and the next
    // session starts 17:00 CT on Christmas Day itself; Dec 31 is a full 16:00 CT session with no
    // evening reopen and the next starts 17:00 CT on Jan 1.
    [InlineData(2023, 12, 25)]
    [InlineData(2024, 1, 1)]
    [InlineData(2024, 12, 25)]
    [InlineData(2025, 1, 1)]
    [InlineData(2025, 12, 25)]
    [InlineData(2026, 1, 1)]
    public void Cme_really_is_closed_on_christmas_and_new_year(int year, int month, int day)
    {
        var closed = Date(year, month, day);

        Assert.Empty(_clock.SessionsBetween(CmeEs, closed, closed));
        Assert.False(_clock.IsTradingDay(CmeEs, closed));

        // And the evening AFTER the holiday reopens for the next trading date, which is why
        // eveningSessionClosedOn stays empty: Globex comes back at 17:00 CT on Christmas Day.
        var next = _clock.TradingDateOf(CmeEs, Utc(year, month, day, 23, 30));
        Assert.True(next > closed);
    }

    [Fact]
    public void Good_friday_is_a_cme_session_only_in_the_years_the_employment_report_falls_on_it()
    {
        // The single clearest argument for per-date data over a rule. Measured: 2023-04-07 and
        // 2026-04-03 both ran to 08:15 CT, and both are the first Friday of a month — an employment
        // report release day. 2024-03-29 and 2025-04-18 are not, and Globex was fully shut.
        foreach (var open in new[] { Date(2023, 4, 7), Date(2026, 4, 3) })
        {
            var session = Single(CmeEs, open, "GTH");
            Assert.Equal(915, Minutes(session));
        }

        foreach (var shut in new[] { Date(2024, 3, 29), Date(2025, 4, 18) })
        {
            Assert.Empty(_clock.SessionsBetween(CmeEs, shut, shut));
        }

        // All four are Good Friday on every other calendar in the dataset.
        foreach (var goodFriday in new[] { Date(2023, 4, 7), Date(2024, 3, 29), Date(2025, 4, 18), Date(2026, 4, 3) })
        {
            Assert.False(_clock.IsTradingDay(Nyse, goodFriday));
            Assert.False(_clock.IsTradingDay(CboeOptionRth, goodFriday));
        }
    }

    [Fact]
    public void An_es_observation_inside_a_holiday_session_is_attributed_to_the_holiday_not_the_next_day()
    {
        // The consequential half of defect 1. With no session on 2025-11-27, every ES observation in
        // a nineteen-hour window fell through TradingDateOf's forward rule onto 2025-11-28 — so an
        // as-of cutoff for the Friday would have admitted Thursday-morning information, and the
        // Thursday's own features would have had no data at all.
        var thanksgivingMorning = Utc(2025, 11, 27, 15, 0); // 09:00 CT

        Assert.Equal(Date(2025, 11, 27), _clock.TradingDateOf(CmeEs, thanksgivingMorning));

        var containing = _clock.SessionAt(CmeEs, thanksgivingMorning);
        Assert.NotNull(containing);
        Assert.Equal(Date(2025, 11, 27), containing!.TradingDate);
        Assert.Equal("GTH", containing.Label);

        // Half-open [open, close) still holds across the shortened close: 18:00Z is 12:00 CT.
        Assert.NotNull(_clock.SessionAt(CmeEs, Utc(2025, 11, 27, 17, 59)));
        Assert.Null(_clock.SessionAt(CmeEs, Utc(2025, 11, 27, 18, 0)));
        Assert.Equal(Date(2025, 11, 28), _clock.TradingDateOf(CmeEs, Utc(2025, 11, 27, 18, 0)));

        // And the evening reopen belongs to the Friday, which is a half day in its own right.
        var friday = Single(CmeEs, Date(2025, 11, 28), "GTH");
        Assert.Equal(Utc(2025, 11, 27, 23, 0), friday.OpenUtc);   // 17:00 CT Thanksgiving evening
        Assert.Equal(Utc(2025, 11, 28, 18, 15), friday.CloseUtc); // 12:15 CT
        Assert.True(friday.IsHalfDay);
    }

    [Fact]
    public void The_carter_day_of_mourning_shut_the_equity_venues_and_not_globex()
    {
        // 2025-01-09 was in closureSets.CME_UNSCHEDULED marked 'unverified' and it was wrong.
        var mourning = Date(2025, 1, 9);

        Assert.Empty(_clock.SessionsBetween(Nyse, mourning, mourning));
        Assert.Empty(_clock.SessionsBetween(CboeOptionRth, mourning, mourning));
        Assert.NotEmpty(_clock.SessionsBetween(CmeEs, mourning, mourning));

        // Both venues still carry the closure — it really was a market-wide day of mourning — but
        // CME's is overridden by a partial session, and NYSE's is not. That is the whole shape of the
        // mechanism: a closure (or a holiday rule) says the day is shut, a partial-session entry says
        // what traded anyway.
        var generator = new SessionGenerator();
        Assert.Contains("Jimmy Carter", generator.UnscheduledClosure(Nyse, mourning)!.Reason);
        Assert.Contains("Jimmy Carter", generator.UnscheduledClosure(CmeEs, mourning)!.Reason);
        Assert.Null(generator.PartialSession(Nyse, mourning));
        Assert.Contains("Jimmy Carter", generator.PartialSession(CmeEs, mourning)!.Reason);
    }

    [Fact]
    public void Projected_cme_holiday_sessions_are_marked_unverified_and_the_measured_ones_are_not()
    {
        // The confidence convention is load-bearing here: nothing past 2026-07-31 was observed, and
        // a study window reaching into it has to be able to see that.
        var generator = new SessionGenerator();

        Assert.Equal(CalendarConfidence.High, generator.PartialSession(CmeEs, Date(2026, 7, 3))!.Confidence);
        Assert.Equal(CalendarConfidence.Unverified, generator.PartialSession(CmeEs, Date(2026, 9, 7))!.Confidence);
        Assert.Equal(CalendarConfidence.Unverified, generator.PartialSession(CmeEs, Date(2027, 11, 25))!.Confidence);

        // Nothing before the measured window is asserted at all: IBKR's ES schedule reaches 2021 but
        // only distinguishes shortened holiday sessions from 2022-11-24, reporting every earlier
        // holiday it does not close outright as a full 17:00-16:00 session, which is demonstrably
        // wrong (Thanksgiving 2021). An omitted session under-states rather than inventing one.
        Assert.Null(generator.PartialSession(CmeEs, Date(2021, 11, 25)));
        Assert.Empty(_clock.SessionsBetween(CmeEs, Date(2021, 11, 25), Date(2021, 11, 25)));
    }

    // ================================================== DEFECT 2 and 3: the SPX index is not the option

    [Fact]
    public void The_spx_index_session_is_the_cash_session_not_the_fifteen_minutes_longer_option_session()
    {
        // Measured three ways on 2026-08-01: contractDetails for SPX IND (conId 416904) reports
        // tradingHours and liquidHours '0830-1500' US/Central; IBKR's published schedule returns
        // 08:30-15:00 CT on all 752 trading dates over 2023-08-02..2026-07-31; and five consecutive
        // useRTH=1 1-minute TRADES sessions returned exactly 390 bars each, 08:30..14:59 CT.
        var index = Single(SpxIndex, Date(2026, 7, 31), "RTH");

        Assert.Equal(Utc(2026, 7, 31, 13, 30), index.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 20, 0), index.CloseUtc);
        Assert.Equal(390, Minutes(index));

        // The option session on the same date is the 405 minutes the index was being measured
        // against. Both are correct; they are different instruments.
        var option = Single(CboeOptionRth, Date(2026, 7, 31), "RTH");
        Assert.Equal(405, Minutes(option));
        Assert.Equal(TimeSpan.FromMinutes(15), option.CloseUtc - index.CloseUtc);

        // The index closes with the cash market, to the minute.
        Assert.Equal(Single(Nyse, Date(2026, 7, 31), "RTH").CloseUtc, index.CloseUtc);
    }

    [Fact]
    public void The_spx_index_half_day_is_210_minutes_not_225()
    {
        // 2025-11-28, the Friday after Thanksgiving. Measured at 08:30-12:00 CT, the same 13:00 ET
        // the cash market closes at — not the 12:15 CT the options keep.
        var index = Single(SpxIndex, Date(2025, 11, 28), "RTH");

        Assert.True(index.IsHalfDay);
        Assert.Equal(Utc(2025, 11, 28, 14, 30), index.OpenUtc);
        Assert.Equal(Utc(2025, 11, 28, 18, 0), index.CloseUtc);
        Assert.Equal(210, Minutes(index));

        Assert.Equal(225, Minutes(Single(CboeOptionRth, Date(2025, 11, 28), "RTH")));
    }

    [Fact]
    public void The_spx_index_close_moved_from_1515_to_1500_in_march_2013()
    {
        // IBKR's published schedule reports 15:15 CT continuously from at least 2001-08-07 to
        // 2013-03-08 and 15:00 CT from 2013-03-11, with the boundary on a weekend. The two dated
        // session rows have to tile that, or a date in between produces no session at all.
        // 2013-03-08 is still CST (DST began 2013-03-10), so 15:15 CT is 21:15Z there and 15:00 CT
        // is 20:00Z on the CDT side of the change — the two rows are not an hour apart by accident.
        Assert.Equal(Utc(2013, 3, 8, 21, 15), Single(SpxIndex, Date(2013, 3, 8), "RTH").CloseUtc);
        Assert.Equal(Utc(2013, 3, 11, 20, 0), Single(SpxIndex, Date(2013, 3, 11), "RTH").CloseUtc);

        // Before the change the index calendar and the option calendar agree exactly, which is why
        // nothing regresses for the 2010-2013 stretch the seeded SPX backfill job reaches.
        Assert.Equal(
            Single(CboeOptionRth, Date(2012, 6, 15), "RTH").CloseUtc,
            Single(SpxIndex, Date(2012, 6, 15), "RTH").CloseUtc);
    }

    [Fact]
    public void The_spx_index_has_no_overnight_session_at_all()
    {
        // Defect 3. The mapping claimed CBOE_INDEX_GTH for the index, citing the capability matrix,
        // which says the opposite ("index computed RTH only"). Confirmed on the wire: a useRTH=0
        // "2 D" 1-minute request for SPX IND returns 780 bars = 2 x 390, every one inside the day
        // window, with a single 1,051-minute overnight gap and not one GTH bar.
        var mapping = InstrumentCalendars.For("SPX", "index");

        Assert.Equal(
            new[] { new InstrumentSessionExpectation(SpxIndex, "RTH") },
            mapping.Expectations.ToArray());
        Assert.False(mapping.Includes(CboeOptionGth, "GTH"));
        Assert.False(mapping.Includes(CboeOptionRth, "RTH"));

        // A full UTC day of the index calendar is 390 minutes, not 390 + 790.
        var day = _clock.SessionsBetween(SpxIndex, Date(2026, 7, 31), Date(2026, 7, 31));
        Assert.Equal(390, day.Sum(Minutes));

        // The OPTION class keeps both sessions — that mapping was never wrong.
        Assert.True(InstrumentCalendars.For("SPX", "option_class").Includes(CboeOptionGth, "GTH"));
    }

    // ============================================ DEFECT 4: the Cboe option GTH close is 08:25, not 08:15

    [Fact]
    public void Cboe_index_option_gth_runs_to_the_observed_0825_close()
    {
        // Measured two ways: contractDetails for SPXW 260807C06400000 (conId 889088590) reports
        // tradingHours '20260802:1915-20260803:0825', and a 1-minute BID_ASK useRTH=0 request for the
        // same contract has its last overnight bar stamped 08:24 CT on both 2026-07-30 and
        // 2026-07-31 (a bar stamped 08:24 covers 08:24-08:25), followed by a six-minute break to the
        // 08:30 CT regular open. The calendar said 08:15, so 13:15Z-13:25Z on every trading date
        // carried real quotes belonging to no session.
        var gth = Single(CboeOptionGth, Date(2026, 7, 31), "GTH");

        Assert.Equal(Utc(2026, 7, 31, 0, 15), gth.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 13, 25), gth.CloseUtc);
        Assert.Equal(790, Minutes(gth));

        // The ten minutes that used to belong to nothing.
        foreach (var orphan in new[] { Utc(2026, 7, 31, 13, 15), Utc(2026, 7, 31, 13, 24) })
        {
            Assert.NotNull(_clock.SessionAt(CboeOptionGth, orphan));
        }

        // And the gap to the regular open is still a gap — the overnight session must not run into
        // it, or one instant would belong to two sessions.
        Assert.Null(_clock.SessionAt(CboeOptionGth, Utc(2026, 7, 31, 13, 25)));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            Single(CboeOptionRth, Date(2026, 7, 31), "RTH").OpenUtc - gth.CloseUtc);
    }

    // ================================================================ VIX: both legs, from the venue

    [Fact]
    public void The_vix_index_publishes_two_legs_and_neither_is_the_option_session()
    {
        // Measured 2026-08-01: VIX IND (conId 13455763) reports tradingHours and liquidHours as two
        // separate legs, 0215-0815 and 0830-1600 US/Central; IBKR's published schedule returns both
        // on all 752 trading dates over 2023-08-02..2026-07-31; and a useRTH=0 1-minute TRADES
        // request returns 810 bars a day = 360 overnight (02:15..08:14 CT) + 450 day (08:30..15:59).
        var overnight = Single(VixGth, Date(2026, 7, 31), "GTH");
        var day = Single(VixRth, Date(2026, 7, 31), "RTH");

        Assert.Equal(Utc(2026, 7, 31, 7, 15), overnight.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 13, 15), overnight.CloseUtc);
        Assert.Equal(360, Minutes(overnight));

        Assert.Equal(Utc(2026, 7, 31, 13, 30), day.OpenUtc);
        Assert.Equal(Utc(2026, 7, 31, 21, 0), day.CloseUtc);
        Assert.Equal(450, Minutes(day));

        Assert.Equal(810, Minutes(overnight) + Minutes(day));

        // Neither leg resembles the index-OPTION overnight window that was originally asserted for
        // VIX (19:15 CT the prior evening to 08:25 CT), which is the point.
        var optionGth = Single(CboeOptionGth, Date(2026, 7, 31), "GTH");
        Assert.NotEqual(optionGth.OpenUtc, overnight.OpenUtc);
        Assert.True(overnight.OpenUtc > optionGth.OpenUtc);
    }

    [Fact]
    public void The_vix_overnight_leg_carries_its_own_calendar_date_unlike_every_other_gth_session()
    {
        // Cboe's option GTH and the Globex day both open the EVENING BEFORE their trading date;
        // VIX's overnight leg opens after midnight on the trading date itself. Getting that wrong by
        // one day is invisible in a minute count and fatal to an as-of cutoff, so it is asserted
        // here in exchange-local terms rather than only in UTC.
        var central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var overnight = Single(VixGth, Date(2026, 7, 31), "GTH");

        Assert.Equal(
            new DateTime(2026, 7, 31, 2, 15, 0),
            TimeZoneInfo.ConvertTimeFromUtc(overnight.OpenUtc.UtcDateTime, central));
        Assert.Equal(
            new DateTime(2026, 7, 31, 8, 15, 0),
            TimeZoneInfo.ConvertTimeFromUtc(overnight.CloseUtc.UtcDateTime, central));

        Assert.Equal(
            new DateTime(2026, 7, 30, 19, 15, 0),
            TimeZoneInfo.ConvertTimeFromUtc(Single(CboeOptionGth, Date(2026, 7, 31), "GTH").OpenUtc.UtcDateTime, central));
    }

    [Fact]
    public void The_vix_day_leg_lengthened_to_1600_in_april_2022_and_its_half_day_did_not_move()
    {
        // The change is measured twice over: IBKR's schedule flips from 08:30-15:15 to 08:30-16:00
        // between 2022-04-22 and 2022-04-25, and 1-minute bars independently show 765 bars a day
        // (last at 15:14 CT) on 2022-04-21/22 and 810 (last at 15:59 CT) on 2022-04-25/26.
        Assert.Equal(Utc(2022, 4, 22, 20, 15), Single(VixRth, Date(2022, 4, 22), "RTH").CloseUtc);
        Assert.Equal(Utc(2022, 4, 25, 21, 0), Single(VixRth, Date(2022, 4, 25), "RTH").CloseUtc);
        Assert.Equal(405, Minutes(Single(VixRth, Date(2022, 4, 22), "RTH")));
        Assert.Equal(450, Minutes(Single(VixRth, Date(2022, 4, 26), "RTH")));

        // Half days STAY at 12:15 CT after the change, so the early close is not the regular close
        // minus a fixed offset — measured on 2022-11-25 through 2025-12-24.
        var halfDay = Single(VixRth, Date(2025, 11, 28), "RTH");
        Assert.True(halfDay.IsHalfDay);
        Assert.Equal(Utc(2025, 11, 28, 18, 15), halfDay.CloseUtc);
        Assert.Equal(225, Minutes(halfDay));

        // And the overnight leg is NOT shortened by the afternoon early close — measured, not assumed.
        var overnight = Single(VixGth, Date(2025, 11, 28), "GTH");
        Assert.False(overnight.IsHalfDay);
        Assert.Equal(360, Minutes(overnight));
    }

    [Fact]
    public void The_vix_overnight_leg_opened_at_0200_before_2019_and_not_at_all_before_2016()
    {
        // IBKR's schedule reports 02:00-08:15 CT from 2016-04-15 to 2019-04-29 and 02:15-08:15 from
        // 2019-04-30, on consecutive trading days. Nothing is asserted before 2016-04-15, which is
        // the first date the leg appears at all — inventing it earlier would manufacture reference
        // data for a window that may not have existed.
        Assert.Equal(Utc(2018, 6, 1, 7, 0), Single(VixGth, Date(2018, 6, 1), "GTH").OpenUtc);
        Assert.Equal(375, Minutes(Single(VixGth, Date(2018, 6, 1), "GTH")));

        Assert.Equal(360, Minutes(Single(VixGth, Date(2019, 4, 30), "GTH")));
        Assert.Equal(375, Minutes(Single(VixGth, Date(2019, 4, 29), "GTH")));

        Assert.Empty(_clock.SessionsBetween(VixGth, Date(2015, 1, 1), Date(2016, 4, 14)));
        Assert.NotEmpty(_clock.SessionsBetween(VixGth, Date(2016, 4, 15), Date(2016, 4, 15)));

        // The day leg goes all the way back regardless — a VIX 1-minute job reaching 2005 still has
        // an RTH expectation, it just has no overnight one.
        Assert.NotEmpty(_clock.SessionsBetween(VixRth, Date(2005, 10, 3), Date(2005, 10, 31)));
    }

    [Fact]
    public void Vix_no_longer_declares_any_window_unmodelled()
    {
        // The reason the calendar entries were written rather than left out. With the overnight
        // window unmodelled, the VIX series reported as never-reconciled forever — a permanently red
        // gate for a non-problem, which is the state in which a gate stops being read at all.
        var vix = InstrumentCalendars.For("VIX", "index");

        Assert.Empty(vix.Unmodelled);
        Assert.True(vix.Includes(VixRth, "RTH"));
        Assert.True(vix.Includes(VixGth, "GTH"));
        Assert.False(vix.Includes(CboeOptionRth, "RTH"));
        Assert.False(vix.Includes(CboeOptionGth, "GTH"));
    }

    // ========================================================================== structural guards

    [Fact]
    public void Dated_session_templates_must_tile_their_calendar_with_no_gap()
    {
        // A gap between two dated session rows produces NO session for the dates inside it, and
        // nothing downstream rejects that: a missing session shrinks every denominator, so the hole
        // renders as health. This is the class (c) absent-row failure applied to the calendar file
        // itself, so it is refused at load rather than detected later.
        var withGap = Dataset(
            """
            { "label": "RTH", "openDayOffset": 0, "open": "08:30", "closeDayOffset": 0, "close": "15:15",
              "appliesEarlyClose": false, "effectiveTo": "2020-01-01" },
            { "label": "RTH", "openDayOffset": 0, "open": "08:30", "closeDayOffset": 0, "close": "16:00",
              "appliesEarlyClose": false, "effectiveFrom": "2020-01-03" }
            """);

        var gap = Assert.Throws<InvalidOperationException>(() => ExchangeCalendarSet.Parse(withGap));
        Assert.Contains("tile", gap.Message);

        var withOverlap = Dataset(
            """
            { "label": "RTH", "openDayOffset": 0, "open": "08:30", "closeDayOffset": 0, "close": "15:15",
              "appliesEarlyClose": false, "effectiveTo": "2020-01-31" },
            { "label": "RTH", "openDayOffset": 0, "open": "08:30", "closeDayOffset": 0, "close": "16:00",
              "appliesEarlyClose": false, "effectiveFrom": "2020-01-15" }
            """);

        Assert.Throws<InvalidOperationException>(() => ExchangeCalendarSet.Parse(withOverlap));

        // A dangling effectiveTo on the last row leaves every later date uncovered — same failure,
        // written a different way.
        var openEnded = Dataset(
            """
            { "label": "RTH", "openDayOffset": 0, "open": "08:30", "closeDayOffset": 0, "close": "15:15",
              "appliesEarlyClose": false, "effectiveTo": "2020-01-01" }
            """);

        Assert.Throws<InvalidOperationException>(() => ExchangeCalendarSet.Parse(openEnded));

        // The shipped file passes, which is the whole point of the check being at load time.
        Assert.NotEmpty(ExchangeCalendarSet.Embedded.CalendarKeys);
    }

    [Fact]
    public void A_partial_session_on_a_day_the_venue_is_already_open_is_refused()
    {
        // The guard that makes the hand-computed 2027 observance dates safe to ship: if one of them
        // is not actually a holiday, the entry would silently SHORTEN a normal session rather than
        // reopen a closed one. 2026-07-06 is an ordinary Monday.
        var generator = new SessionGenerator(
            ExchangeCalendarSet.Parse(DatasetWithPartial("2026-07-06", "12:00", "\"GTH\"")));

        var error = Assert.Throws<InvalidOperationException>(
            () => generator.Generate("TEST", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));

        Assert.Contains("already an ordinary trading day", error.Message);
    }

    [Fact]
    public void A_partial_session_that_would_generate_nothing_is_refused_rather_than_read_as_closed()
    {
        // Naming a label the calendar does not define would emit no rows at all, and the date would
        // quietly revert to "closed" — exactly the defect being fixed, reintroduced through the
        // mechanism meant to fix it. 2026-07-03 is the observed Independence Day.
        var generator = new SessionGenerator(
            ExchangeCalendarSet.Parse(DatasetWithPartial("2026-07-03", "12:00", "\"OVERNIGHT\"")));

        var error = Assert.Throws<InvalidOperationException>(
            () => generator.Generate("TEST", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));

        Assert.Contains("would silently generate as closed", error.Message);
    }

    [Fact]
    public void A_partial_session_cannot_manufacture_a_weekend_session()
    {
        var generator = new SessionGenerator(
            ExchangeCalendarSet.Parse(DatasetWithPartial("2026-07-04", "12:00", "\"GTH\"")));

        var error = Assert.Throws<InvalidOperationException>(
            () => generator.Generate("TEST", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));

        Assert.Contains("Saturday", error.Message);
    }

    private static string Dataset(string sessions) =>
        $$"""
        {
          "generatorVersion": 99,
          "revision": "test",
          "holidayRuleSets": { "NONE": [] },
          "earlyCloseRuleSets": { "NONE": [] },
          "closureSets": { "NONE": [] },
          "earlyCloseSets": { "NONE": [] },
          "venues": {
            "TEST": { "holidayRuleSet": "NONE", "earlyCloseRuleSet": "NONE",
                      "closureSet": "NONE", "unscheduledEarlyCloseSet": "NONE" }
          },
          "calendars": [
            { "key": "TEST", "venue": "TEST", "timeZone": "America/Chicago",
              "effectiveFrom": "2019-01-01", "sessions": [ {{sessions}} ] }
          ]
        }
        """;

    private static string DatasetWithPartial(string date, string close, string labels) =>
        $$"""
        {
          "generatorVersion": 99,
          "revision": "test",
          "holidayRuleSets": {
            "US": [ { "name": "Independence Day", "kind": "fixed", "month": 7, "day": 4,
                      "observance": "nearestWeekday" } ]
          },
          "earlyCloseRuleSets": { "NONE": [] },
          "closureSets": { "NONE": [] },
          "earlyCloseSets": { "NONE": [] },
          "partialSessionSets": {
            "TEST": [ { "date": "{{date}}", "close": "{{close}}", "sessions": [ {{labels}} ],
                        "reason": "test", "confidence": "unverified" } ]
          },
          "venues": {
            "TEST": { "holidayRuleSet": "US", "earlyCloseRuleSet": "NONE", "closureSet": "NONE",
                      "unscheduledEarlyCloseSet": "NONE", "partialSessionSet": "TEST" }
          },
          "calendars": [
            { "key": "TEST", "venue": "TEST", "timeZone": "America/Chicago",
              "effectiveFrom": "2019-01-01",
              "sessions": [
                { "label": "GTH", "openDayOffset": -1, "open": "17:00", "closeDayOffset": 0,
                  "close": "16:00", "earlyClose": "12:15", "appliesEarlyClose": true }
              ] }
          ]
        }
        """;
}
