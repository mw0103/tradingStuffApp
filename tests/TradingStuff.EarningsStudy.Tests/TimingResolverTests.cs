using TradingStuff.EarningsStudy.Timing;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.EarningsStudy.Tests;

/// <summary>
/// Every case runs against the REAL platform calendar — <see cref="SessionClock"/> over
/// <c>exchange-calendars.json</c> — and not a fake. A fake clock here would test that the resolver
/// agrees with whatever this file asserts about NYSE, which is the failure mode class (b) work has:
/// the validator and the validated sharing an assumption. The half day, the holidays and the DST
/// weeks below are the venue's, measured in the calendar against IBKR's published schedule.
/// </summary>
public sealed class TimingResolverTests
{
    private const string Nyse = "NYSE";

    private static readonly ISessionClock Clock = new SessionClock();

    private static TimingResolution Resolve(int year, int month, int day, int hour, int minute, int second = 0) =>
        TimingResolver.Resolve(Clock, Nyse, "e1", new DateTime(year, month, day, hour, minute, second));

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public void Pre_open_acceptance_is_bmo_entering_on_the_previous_close()
    {
        // Thursday 2024-02-01, 08:00 ET. Nothing traded yet, so the last close before the print is
        // Wednesday's and the first after it is this day's own.
        var row = Resolve(2024, 2, 1, 8, 0).Row;

        Assert.Equal(TimingResolver.Bmo, row.TimingClass);
        Assert.Equal(D(2024, 2, 1), row.PrintDate);
        Assert.Equal(D(2024, 1, 30), row.PreEntryDate);
        Assert.Equal(D(2024, 1, 31), row.EntryDate);
        Assert.Equal(D(2024, 2, 1), row.ExitDate);
        Assert.Equal("2024-W05", row.EarningsWeek);
        Assert.False(row.Quarantined);
        Assert.Null(row.QuarantineReason);
    }

    [Fact]
    public void Post_close_acceptance_is_amc_exiting_on_the_next_close()
    {
        var row = Resolve(2024, 2, 1, 16, 5).Row;

        Assert.Equal(TimingResolver.Amc, row.TimingClass);
        Assert.Equal(D(2024, 1, 31), row.PreEntryDate);
        Assert.Equal(D(2024, 2, 1), row.EntryDate);
        Assert.Equal(D(2024, 2, 2), row.ExitDate);
        Assert.False(row.Quarantined);
    }

    [Fact]
    public void Acceptance_exactly_at_the_closing_bell_is_amc_and_one_second_earlier_is_not()
    {
        var atTheBell = Resolve(2024, 2, 1, 16, 0, 0).Row;

        Assert.Equal(TimingResolver.Amc, atTheBell.TimingClass);
        Assert.Equal(D(2024, 2, 1), atTheBell.EntryDate);
        Assert.Equal(D(2024, 2, 2), atTheBell.ExitDate);
        Assert.False(atTheBell.Quarantined);

        var aSecondEarlier = Resolve(2024, 2, 1, 15, 59, 59).Row;

        Assert.Equal(TimingResolver.Intraday, aSecondEarlier.TimingClass);
        Assert.Equal(D(2024, 1, 31), aSecondEarlier.EntryDate);
        Assert.Equal(D(2024, 2, 1), aSecondEarlier.ExitDate);
    }

    [Fact]
    public void Acceptance_inside_the_session_is_quarantined_with_the_window_still_resolved()
    {
        var row = Resolve(2024, 2, 1, 12, 0).Row;

        Assert.Equal(TimingResolver.Intraday, row.TimingClass);
        Assert.True(row.Quarantined);
        Assert.Equal(TimingResolver.IntradayReason, row.QuarantineReason);

        // Quarantined, but complete: the strictly-before / strictly-after rule still names the window.
        Assert.Equal(D(2024, 1, 30), row.PreEntryDate);
        Assert.Equal(D(2024, 1, 31), row.EntryDate);
        Assert.Equal(D(2024, 2, 1), row.ExitDate);
    }

    [Fact]
    public void Friday_evening_acceptance_exits_on_monday()
    {
        var row = Resolve(2024, 2, 2, 17, 45).Row;

        Assert.Equal(TimingResolver.Amc, row.TimingClass);
        Assert.Equal(D(2024, 2, 2), row.EntryDate);
        Assert.Equal(D(2024, 2, 5), row.ExitDate);
    }

    [Fact]
    public void The_half_day_close_is_read_from_the_calendar_rather_than_assumed()
    {
        // 2024-11-29, the day after Thanksgiving: the NYSE closed at 13:00 ET.
        var afterTheEarlyClose = Resolve(2024, 11, 29, 13, 30).Row;

        Assert.Equal(TimingResolver.Amc, afterTheEarlyClose.TimingClass);
        Assert.Equal(D(2024, 11, 29), afterTheEarlyClose.EntryDate);
        Assert.Equal(D(2024, 12, 2), afterTheEarlyClose.ExitDate);

        var beforeIt = Resolve(2024, 11, 29, 12, 30).Row;

        Assert.Equal(TimingResolver.Intraday, beforeIt.TimingClass);
        Assert.True(beforeIt.Quarantined);

        // The discriminator: the same wall clock on an ordinary session is intraday, so the first
        // assertion is reading 2024-11-29's real close and not a 16:00 constant.
        Assert.Equal(TimingResolver.Intraday, Resolve(2024, 11, 26, 13, 30).Row.TimingClass);
    }

    [Fact]
    public void Good_friday_acceptance_spans_the_closed_session()
    {
        // EDGAR is open on Good Friday; the NYSE is not. 2024-03-29 has no session at all.
        var morning = Resolve(2024, 3, 29, 8, 0);

        Assert.False(morning.AcceptanceDateIsTradingDay);
        Assert.Equal(D(2024, 3, 29), morning.Row.PrintDate);
        Assert.Equal(D(2024, 3, 27), morning.Row.PreEntryDate);
        Assert.Equal(D(2024, 3, 28), morning.Row.EntryDate);
        Assert.Equal(D(2024, 4, 1), morning.Row.ExitDate);
        Assert.False(morning.Row.Quarantined);
    }

    [Fact]
    public void The_non_trading_day_label_is_the_noon_cut_and_moves_no_date()
    {
        var justBeforeNoon = Resolve(2024, 3, 29, 11, 59).Row;
        var atNoon = Resolve(2024, 3, 29, 12, 0).Row;

        Assert.Equal(TimingResolver.Bmo, justBeforeNoon.TimingClass);
        Assert.Equal(TimingResolver.Amc, atNoon.TimingClass);

        // The label is a readout for the BMO/AMC split. The measurement window is identical.
        Assert.Equal(justBeforeNoon.PreEntryDate, atNoon.PreEntryDate);
        Assert.Equal(justBeforeNoon.EntryDate, atNoon.EntryDate);
        Assert.Equal(justBeforeNoon.ExitDate, atNoon.ExitDate);
    }

    [Fact]
    public void Both_sides_of_the_daylight_saving_transition_classify_against_the_real_close()
    {
        // 2024-03-11 is the Monday after the spring forward: the close is 20:00Z.
        Assert.Equal(TimingResolver.Amc, Resolve(2024, 3, 11, 16, 5).Row.TimingClass);
        Assert.Equal(D(2024, 3, 12), Resolve(2024, 3, 11, 16, 5).Row.ExitDate);

        // Kills a fixed-EST reading: under -05:00 this instant would be 20:55Z, past a 20:00Z close.
        Assert.Equal(TimingResolver.Intraday, Resolve(2024, 3, 11, 15, 55).Row.TimingClass);

        // 2024-11-04 is the Monday after the fall back: the close is 21:00Z.
        Assert.Equal(TimingResolver.Amc, Resolve(2024, 11, 4, 16, 5).Row.TimingClass);
        Assert.Equal(D(2024, 11, 5), Resolve(2024, 11, 4, 16, 5).Row.ExitDate);

        // Kills a fixed-EDT reading: under -04:00 this instant would be 20:05Z, inside a 21:00Z close.
        Assert.Equal(TimingResolver.Intraday, Resolve(2024, 11, 4, 15, 55).Row.TimingClass);
    }

    [Fact]
    public void Juneteenth_is_not_a_trading_day()
    {
        var resolution = Resolve(2024, 6, 19, 7, 30);

        Assert.False(resolution.AcceptanceDateIsTradingDay);
        Assert.Equal(TimingResolver.Bmo, resolution.Row.TimingClass);
        Assert.Equal(D(2024, 6, 18), resolution.Row.EntryDate);
        Assert.Equal(D(2024, 6, 20), resolution.Row.ExitDate);
        Assert.Equal(D(2024, 6, 17), resolution.Row.PreEntryDate);
    }

    [Fact]
    public void Pre_entry_steps_back_across_a_holiday()
    {
        // AMC on Friday 2024-07-05: the session before it is 2024-07-03 (itself a half day), because
        // Independence Day sits between them.
        var afterTheHoliday = Resolve(2024, 7, 5, 16, 5).Row;

        Assert.Equal(D(2024, 7, 3), afterTheHoliday.PreEntryDate);
        Assert.Equal(D(2024, 7, 5), afterTheHoliday.EntryDate);
        Assert.Equal(D(2024, 7, 8), afterTheHoliday.ExitDate);

        // BMO on Thursday 2024-06-20, the session after Juneteenth.
        var afterJuneteenth = Resolve(2024, 6, 20, 8, 0).Row;

        Assert.Equal(D(2024, 6, 17), afterJuneteenth.PreEntryDate);
        Assert.Equal(D(2024, 6, 18), afterJuneteenth.EntryDate);
        Assert.Equal(D(2024, 6, 20), afterJuneteenth.ExitDate);
    }

    [Fact]
    public void The_earnings_week_is_the_iso_week_year_at_a_year_boundary()
    {
        Assert.Equal("2024-W05", TimingResolver.EarningsWeek(D(2024, 2, 1)));
        Assert.Equal("2024-W52", TimingResolver.EarningsWeek(D(2024, 12, 29)));
        Assert.Equal("2025-W01", TimingResolver.EarningsWeek(D(2024, 12, 30)));
        Assert.Equal("2020-W53", TimingResolver.EarningsWeek(D(2021, 1, 1)));
        Assert.Equal("2021-W01", TimingResolver.EarningsWeek(D(2021, 1, 4)));

        // And through a resolved event, so the column is the one the bootstrap clusters on.
        Assert.Equal("2025-W01", Resolve(2024, 12, 30, 16, 5).Row.EarningsWeek);
    }

    [Fact]
    public void New_years_day_2021_is_closed_and_keeps_the_2020_week_53_key()
    {
        var row = Resolve(2021, 1, 1, 8, 0).Row;

        Assert.Equal("2020-W53", row.EarningsWeek);
        Assert.Equal(D(2020, 12, 31), row.EntryDate);
        Assert.Equal(D(2021, 1, 4), row.ExitDate);
    }

    [Fact]
    public void The_print_date_comes_from_the_wall_clock_not_from_the_utc_instant()
    {
        // 20:00 ET on 2024-02-01 is 01:00Z on 2024-02-02. Bucketing by the UTC date would move the
        // print a day forward and, with it, the earnings week and every date derived from it.
        var resolution = Resolve(2024, 2, 1, 20, 0);

        Assert.Equal(new DateTimeOffset(2024, 2, 2, 1, 0, 0, TimeSpan.Zero), resolution.AcceptanceInstantUtc);
        Assert.Equal(D(2024, 2, 1), resolution.Row.PrintDate);
        Assert.Equal(TimingResolver.Amc, resolution.Row.TimingClass);
        Assert.Equal(D(2024, 2, 1), resolution.Row.EntryDate);
        Assert.Equal(D(2024, 2, 2), resolution.Row.ExitDate);
    }

    [Fact]
    public void An_acceptance_wall_clock_that_never_existed_is_unresolved_rather_than_a_throw()
    {
        // 02:30 on 2024-03-10 is skipped by the spring forward.
        var resolution = Resolve(2024, 3, 10, 2, 30);

        Assert.Equal(TimingResolver.Unresolved, resolution.Row.TimingClass);
        Assert.True(resolution.Row.Quarantined);
        Assert.Contains("does not exist", resolution.Row.QuarantineReason);
        Assert.Null(resolution.Row.PreEntryDate);
        Assert.Null(resolution.Row.EntryDate);
        Assert.Null(resolution.Row.ExitDate);
        Assert.Null(resolution.AcceptanceInstantUtc);

        // The print date and the week survive, so the row is still countable.
        Assert.Equal(D(2024, 3, 10), resolution.Row.PrintDate);
        Assert.Equal("2024-W10", resolution.Row.EarningsWeek);
    }

    [Fact]
    public void An_ambiguous_acceptance_wall_clock_resolves_to_the_first_occurrence()
    {
        // 01:30 on 2024-11-03 happens twice: 05:30Z under EDT and 06:30Z under EST. The earlier one
        // is the leak-safe reading.
        var resolution = Resolve(2024, 11, 3, 1, 30);

        Assert.Equal(new DateTimeOffset(2024, 11, 3, 5, 30, 0, TimeSpan.Zero), resolution.AcceptanceInstantUtc);
        Assert.NotNull(resolution.AcceptanceAnomaly);
        Assert.Contains("first occurrence", resolution.AcceptanceAnomaly);

        // A Sunday: no session, so the window spans it and the noon cut labels it.
        Assert.False(resolution.AcceptanceDateIsTradingDay);
        Assert.Equal(TimingResolver.Bmo, resolution.Row.TimingClass);
        Assert.Equal(D(2024, 11, 1), resolution.Row.EntryDate);
        Assert.Equal(D(2024, 11, 4), resolution.Row.ExitDate);
    }

    [Fact]
    public void An_already_converted_acceptance_time_is_refused()
    {
        var utc = DateTime.SpecifyKind(new DateTime(2024, 2, 1, 21, 5, 0), DateTimeKind.Utc);

        var ex = Assert.Throws<ArgumentException>(() => EdgarAcceptance.Resolve(utc));
        Assert.Contains("Unspecified", ex.Message);
    }

    [Fact]
    public void Before_the_calendar_begins_the_dates_are_refused_rather_than_projected()
    {
        // The NYSE calendar's data starts 1993-01-01; 1993-01-04 is its first session. There is no
        // close before an 08:00 acceptance that day, and no session before the entry for a 16:05 one.
        var noEntry = Resolve(1993, 1, 4, 8, 0).Row;

        Assert.Equal(TimingResolver.Unresolved, noEntry.TimingClass);
        Assert.True(noEntry.Quarantined);
        Assert.Contains("at or before", noEntry.QuarantineReason);

        var noPreEntry = Resolve(1993, 1, 4, 16, 5).Row;

        Assert.Equal(TimingResolver.Unresolved, noPreEntry.TimingClass);
        Assert.Contains("pre-entry date unresolvable", noPreEntry.QuarantineReason);
    }

    [Fact]
    public void A_corrupt_acceptance_date_at_the_edge_of_the_calendar_type_is_refused_not_thrown()
    {
        // One row's bad data must not take the run with it. A date near DateOnly's bounds makes the
        // session generator resolve holiday rules for years that cannot be represented at all.
        foreach (var pathological in new[]
        {
            DateOnly.MinValue, DateOnly.MinValue.AddDays(3), new DateOnly(1899, 12, 31),
            new DateOnly(2201, 1, 1), DateOnly.MaxValue.AddDays(-3), DateOnly.MaxValue
        })
        {
            var row = TimingResolver.Resolve(
                Clock, Nyse, "e", pathological.ToDateTime(new TimeOnly(8, 0))).Row;

            Assert.Equal(TimingResolver.Unresolved, row.TimingClass);
            Assert.True(row.Quarantined);
            Assert.NotNull(row.QuarantineReason);
        }
    }

    [Fact]
    public void Every_trading_day_in_the_registered_window_resolves_to_its_calendar_neighbours()
    {
        // Independent construction: the ordered list of trading dates, walked by index, versus the
        // resolver's comparison of an instant against session closes. Nothing in this sweep is a
        // hand-written date, so it covers every half day, holiday and DST week in four years at once.
        var dates = TradingDates(D(2021, 11, 1), D(2026, 2, 28));
        var first = dates.FindIndex(date => date >= C1Registration.WindowFrom);
        var last = dates.FindLastIndex(date => date <= C1Registration.WindowTo);
        var checkedDays = 0;

        for (var i = first; i <= last; i++)
        {
            var date = dates[i];

            var bmo = TimingResolver.Resolve(Clock, Nyse, "e", date.ToDateTime(new TimeOnly(8, 0))).Row;
            Assert.Equal(TimingResolver.Bmo, bmo.TimingClass);
            Assert.Equal(dates[i - 2], bmo.PreEntryDate);
            Assert.Equal(dates[i - 1], bmo.EntryDate);
            Assert.Equal(date, bmo.ExitDate);
            Assert.False(bmo.Quarantined);

            var amc = TimingResolver.Resolve(Clock, Nyse, "e", date.ToDateTime(new TimeOnly(16, 5))).Row;
            Assert.Equal(TimingResolver.Amc, amc.TimingClass);
            Assert.Equal(dates[i - 1], amc.PreEntryDate);
            Assert.Equal(date, amc.EntryDate);
            Assert.Equal(dates[i + 1], amc.ExitDate);
            Assert.False(amc.Quarantined);

            var noon = TimingResolver.Resolve(Clock, Nyse, "e", date.ToDateTime(new TimeOnly(12, 0))).Row;
            Assert.Equal(TimingResolver.Intraday, noon.TimingClass);
            Assert.True(noon.Quarantined);

            checkedDays++;
        }

        Assert.True(checkedDays > 990, $"expected about 1,000 trading days in 2022-2025, walked {checkedDays}");
    }

    [Fact]
    public void Every_closed_weekday_in_the_registered_window_spans_to_the_surrounding_sessions()
    {
        // Holidays are where a hand-written test list runs out: this walks every weekday the venue
        // did not open in the registered window and checks the window brackets it on both sides.
        var dates = TradingDates(D(2021, 11, 1), D(2026, 2, 28));
        var trading = dates.ToHashSet();
        var closedWeekdays = 0;

        for (var date = C1Registration.WindowFrom; date <= C1Registration.WindowTo; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || trading.Contains(date))
            {
                continue;
            }

            var resolution = TimingResolver.Resolve(Clock, Nyse, "e", date.ToDateTime(new TimeOnly(8, 0)));
            var before = dates.FindLastIndex(candidate => candidate < date);

            Assert.False(resolution.AcceptanceDateIsTradingDay);
            Assert.Equal(TimingResolver.Bmo, resolution.Row.TimingClass);
            Assert.Equal(dates[before - 1], resolution.Row.PreEntryDate);
            Assert.Equal(dates[before], resolution.Row.EntryDate);
            Assert.Equal(dates[before + 1], resolution.Row.ExitDate);
            Assert.False(resolution.Row.Quarantined);

            var afternoon = TimingResolver.Resolve(Clock, Nyse, "e", date.ToDateTime(new TimeOnly(16, 30))).Row;

            Assert.Equal(TimingResolver.Amc, afternoon.TimingClass);
            Assert.Equal(resolution.Row.EntryDate, afternoon.EntryDate);
            Assert.Equal(resolution.Row.ExitDate, afternoon.ExitDate);

            closedWeekdays++;
        }

        // Ten or eleven federal-style holidays a year over four years, plus nothing else.
        Assert.InRange(closedWeekdays, 36, 48);
    }

    private static List<DateOnly> TradingDates(DateOnly from, DateOnly to) =>
        [.. Clock.SessionsBetween(Nyse, from, to)
            .Where(session => session.Label == TimingResolver.RegularTradingHours)
            .Select(session => session.TradingDate)
            .Distinct()
            .OrderBy(date => date)];
}
