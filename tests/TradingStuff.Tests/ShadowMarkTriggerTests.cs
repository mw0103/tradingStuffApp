using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.VrpConditioning;

namespace TradingStuff.Tests;

/// <summary>
/// The daily shadow-mark trigger's schedule: which trading date's mark is due at a given instant.
/// </summary>
/// <remarks>
/// Driven through the real <see cref="SessionClock"/> rather than a stub, for the reason
/// <see cref="PaperCaptureScheduleTests"/> gives: the claim being tested is that this is a SESSION
/// schedule and not a wall-clock one, and a stub that answered whatever the test wanted would prove
/// nothing about DST, holidays, or a half-day close.
/// </remarks>
public sealed class ShadowMarkTriggerTests
{
    private static ShadowMarkTriggerOptions Settings(int? afterCloseMinutes = null, int catchUp = 720) =>
        new()
        {
            Calendar = "NYSE",
            SessionLabel = "RTH",
            RunAtUtc = TimeSpan.FromMinutes(10),
            AfterCloseMinutes = afterCloseMinutes,
            CatchUpWindowMinutes = catchUp,
        };

    // Thursday 2026-08-06. NYSE RTH closes 16:00 ET = 20:00 UTC in EDT.
    private static readonly DateOnly Thursday = new(2026, 8, 6);
    private static readonly DateTimeOffset ThursdayCloseUtc = new(2026, 8, 6, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_due_at_the_us_close_because_the_daily_close_does_not_exist_yet()
    {
        // The whole reason for 00:10 UTC: the backfill slice carrying this session's VIX daily close
        // is not claimable until 00:00 UTC. A mark fired at the bell could only ever be refused.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), ThursdayCloseUtc.AddMinutes(30), lastFired: null));
    }

    [Fact]
    public void The_session_is_due_at_ten_past_midnight_utc_on_the_following_day()
    {
        var due = ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 8, 7, 0, 10, 0, TimeSpan.Zero), lastFired: null);

        Assert.Equal(Thursday, due);
    }

    [Fact]
    public void The_due_date_is_the_sessions_trading_date_not_the_utc_date_of_the_run()
    {
        // The run happens on 2026-08-07 UTC and marks 2026-08-06. Dating the record by the day the
        // job happened to fire would shift every mark forward by one and silently misalign the whole
        // Phase 1 record against research.bars.
        var due = ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 8, 7, 3, 0, 0, TimeSpan.Zero), lastFired: null);

        Assert.Equal(new DateOnly(2026, 8, 6), due);
    }

    [Fact]
    public void A_process_that_started_late_still_runs_the_mark_inside_the_catch_up_window()
    {
        // 11:00 UTC is 07:00 ET — still before the open, so the planner reads the same closed market
        // a 00:10 run would have. That is what makes the catch-up the SAME measurement, not a new one.
        var due = ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 8, 7, 11, 0, 0, TimeSpan.Zero), lastFired: null);

        Assert.Equal(Thursday, due);
    }

    [Fact]
    public void The_catch_up_window_closes_before_the_us_open_rather_than_running_into_the_session()
    {
        // 12:10 UTC is 08:10 ET. Past this the planner would quote a LIVE market and upsert that over
        // the prior date's intent — a different measurement wearing the same date. A visible gap is
        // the honest outcome instead.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 8, 7, 12, 10, 0, TimeSpan.Zero), lastFired: null));
    }

    [Fact]
    public void A_date_this_process_already_marked_is_not_marked_again()
    {
        var clock = new SessionClock();
        var at = new DateTimeOffset(2026, 8, 7, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal(Thursday, ShadowMarkTrigger.DueTradingDate(clock, Settings(), at, lastFired: null));
        Assert.Null(ShadowMarkTrigger.DueTradingDate(clock, Settings(), at, lastFired: Thursday));
    }

    [Fact]
    public void A_weekend_does_not_re_run_fridays_mark_on_saturday_sunday_and_monday()
    {
        var clock = new SessionClock();
        var settings = Settings();

        // Friday 2026-08-07 closes 20:00 UTC. Its mark is due Saturday 00:10 UTC and only then.
        Assert.Equal(
            new DateOnly(2026, 8, 7),
            ShadowMarkTrigger.DueTradingDate(
                clock, settings, new DateTimeOffset(2026, 8, 8, 0, 10, 0, TimeSpan.Zero), lastFired: null));

        // Sunday and Monday still see Friday as the most recently closed session. Anchoring the due
        // instant on "today" instead of on the SESSION would make all three read as due, and a fresh
        // process (lastFired: null) would repeat a three-year bar load twice for nothing.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            clock, settings, new DateTimeOffset(2026, 8, 9, 0, 10, 0, TimeSpan.Zero), lastFired: null));
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            clock, settings, new DateTimeOffset(2026, 8, 10, 0, 10, 0, TimeSpan.Zero), lastFired: null));
    }

    [Fact]
    public void A_holiday_produces_no_mark_of_its_own_and_does_not_re_trigger_the_previous_session()
    {
        // Independence Day observed, Friday 2026-07-03 — no NYSE session. Thursday 2026-07-02's mark
        // was due at 2026-07-03 00:10 UTC; by the 4th its window has closed and the holiday has
        // nothing to mark. Absence of a session is not a gap.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 7, 4, 0, 10, 0, TimeSpan.Zero), lastFired: null));
    }

    [Fact]
    public void A_half_day_is_scheduled_off_the_calendars_close_not_a_fixed_hour()
    {
        // 2026-11-27, the day after Thanksgiving: NYSE closes 13:00 ET = 18:00 UTC (EST). The mark is
        // still due the following 00:10 UTC — the early close does not move the daily-close arrival.
        var due = ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 11, 28, 0, 10, 0, TimeSpan.Zero), lastFired: null);

        Assert.Equal(new DateOnly(2026, 11, 27), due);
    }

    [Fact]
    public void After_close_scheduling_lands_at_sixteen_twenty_eastern_in_edt()
    {
        var clock = new SessionClock();
        var settings = Settings(afterCloseMinutes: 20);

        // EDT: 16:00 ET = 20:00 UTC, so 16:20 ET = 20:20 UTC.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            clock, settings, new DateTimeOffset(2026, 8, 6, 20, 19, 0, TimeSpan.Zero), lastFired: null));
        Assert.Equal(
            Thursday,
            ShadowMarkTrigger.DueTradingDate(
                clock, settings, new DateTimeOffset(2026, 8, 6, 20, 20, 0, TimeSpan.Zero), lastFired: null));
    }

    [Fact]
    public void After_close_scheduling_lands_at_sixteen_twenty_eastern_in_est_too()
    {
        var clock = new SessionClock();
        var settings = Settings(afterCloseMinutes: 20);

        // EST: 16:00 ET = 21:00 UTC, so 16:20 ET = 21:20 UTC — an hour later in UTC than the EDT case
        // above. This is the whole reason the future schedule is expressed relative to the close: a
        // fixed UTC time of day would fire an hour early or late across every DST boundary, twice a
        // year, and nothing in the record would say which side of the boundary a row came from.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            clock, settings, new DateTimeOffset(2026, 12, 3, 21, 19, 0, TimeSpan.Zero), lastFired: null));
        Assert.Equal(
            new DateOnly(2026, 12, 3),
            ShadowMarkTrigger.DueTradingDate(
                clock, settings, new DateTimeOffset(2026, 12, 3, 21, 20, 0, TimeSpan.Zero), lastFired: null));
    }

    [Fact]
    public void A_utc_schedule_is_unmoved_by_the_dst_boundary_the_close_crosses()
    {
        // The counterpart of the test above, for today's schedule: the session's close moves an hour
        // in UTC between EDT and EST, but the mark stays at 00:10 UTC on the following day, because
        // what it waits for is a backfill slice that becomes claimable at 00:00 UTC — an event with
        // no relationship to New York's clock at all.
        var due = ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(), new DateTimeOffset(2026, 12, 4, 0, 10, 0, TimeSpan.Zero), lastFired: null);

        Assert.Equal(new DateOnly(2026, 12, 3), due);
    }

    [Fact]
    public void A_mislabelled_session_marks_nothing_rather_than_falling_back_to_rth()
    {
        var settings = Settings();
        settings.SessionLabel = "GTH";

        // NYSE publishes no GTH session. Dating the record by a boundary the calendar does not have
        // would be worse than not marking at all.
        Assert.Null(ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), settings, new DateTimeOffset(2026, 8, 7, 0, 10, 0, TimeSpan.Zero), lastFired: null));
    }

    [Fact]
    public void A_zero_catch_up_window_still_allows_the_instant_the_mark_falls_due()
    {
        // A misconfigured 0 must not silently disable the trigger forever; the floor is one minute.
        var due = ShadowMarkTrigger.DueTradingDate(
            new SessionClock(), Settings(catchUp: 0),
            new DateTimeOffset(2026, 8, 7, 0, 10, 0, TimeSpan.Zero), lastFired: null);

        Assert.Equal(Thursday, due);
    }

    [Fact]
    public void The_trigger_is_on_unless_it_is_switched_off_by_the_exact_opt_out_value()
    {
        // The same opt-out shape as PaperCapture:Enabled, and for the same reason: a missed day is a
        // permanent hole in the Phase 1 record, so every unrecognised value has to degrade to ON.
        Assert.True(new ShadowMarkTriggerOptions().IsEnabled);
        Assert.True(new ShadowMarkTriggerOptions { Enabled = "true" }.IsEnabled);
        Assert.True(new ShadowMarkTriggerOptions { Enabled = "no" }.IsEnabled);
        Assert.False(new ShadowMarkTriggerOptions { Enabled = "false" }.IsEnabled);
        Assert.False(new ShadowMarkTriggerOptions { Enabled = "FALSE" }.IsEnabled);
    }

    [Fact]
    public void The_committed_defaults_are_the_schedule_the_runbook_documents()
    {
        var defaults = new ShadowMarkTriggerOptions();

        // Pinned because these three ARE the operational contract: 00:10 UTC is dictated by when the
        // daily-close backfill slice becomes claimable, and AfterCloseMinutes stays null until the
        // live recorder is proven to land same-day closes. A silent change to either would move the
        // mark off the only instant its input is known to exist at.
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.RunAtUtc);
        Assert.Null(defaults.AfterCloseMinutes);
        Assert.Equal("NYSE", defaults.Calendar);
        Assert.Equal("RTH", defaults.SessionLabel);
    }
}
