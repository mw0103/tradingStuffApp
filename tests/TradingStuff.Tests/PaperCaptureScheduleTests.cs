using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Capture;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// The capture trigger: which sessions a pass at a given instant is due to capture.
/// </summary>
/// <remarks>
/// Driven through the real <see cref="SessionClock"/> rather than a stub, deliberately. The whole
/// point of the trigger is that it is a SESSION clock and not a wall clock — a stub that returned
/// whatever the test wanted would prove nothing about DST, holidays, or the 13:00 ET half-day close,
/// which are exactly the cases a wall-clock schedule gets wrong.
/// </remarks>
public sealed class PaperCaptureScheduleTests
{
    private static PaperCaptureOptions Settings(int lookback = 3, int delayMinutes = 15) =>
        new() { Calendar = "NYSE", SessionLabel = "RTH", CloseDelayMinutes = delayMinutes, LookbackSessions = lookback };

    // Thursday 2026-08-06. NYSE RTH closes 16:00 ET = 20:00 UTC (EDT).
    private static readonly DateTimeOffset CloseUtc = new(2026, 8, 6, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_due_before_the_close_delay_has_elapsed()
    {
        var clock = new SessionClock();

        // One minute after the bell: the session is over but TWS is still settling executions and
        // recomputing margin, which is the entire reason the delay exists.
        var due = PaperCaptureService.DueSessions(clock, Settings(), CloseUtc.AddMinutes(1));

        Assert.DoesNotContain(due, session => session.TradingDate == new DateOnly(2026, 8, 6));
    }

    [Fact]
    public void The_session_becomes_due_once_the_close_delay_has_elapsed()
    {
        var clock = new SessionClock();

        var due = PaperCaptureService.DueSessions(clock, Settings(), CloseUtc.AddMinutes(15));

        Assert.Equal(new DateOnly(2026, 8, 6), due[0].TradingDate);
        Assert.Equal("RTH", due[0].Label);
    }

    [Fact]
    public void Due_sessions_come_back_newest_first()
    {
        var clock = new SessionClock();

        var due = PaperCaptureService.DueSessions(clock, Settings(), CloseUtc.AddMinutes(30));

        // Newest first is load-bearing: an execution whose TWS time string could not be parsed is
        // attributed to the most recent uncaptured session, so the order decides where it lands.
        Assert.Equal(due.OrderByDescending(session => session.TradingDate).Select(s => s.TradingDate), due.Select(s => s.TradingDate));
        Assert.Equal(new DateOnly(2026, 8, 6), due[0].TradingDate);
        Assert.Equal(new DateOnly(2026, 8, 5), due[1].TradingDate);
    }

    [Fact]
    public void The_lookback_is_counted_in_sessions_so_a_long_weekend_cannot_shrink_it()
    {
        var clock = new SessionClock();

        // Tuesday 2026-09-08, after the close. The Monday before is Labor Day (2026-09-07), so the
        // three most recent closed sessions reach back to Thursday 2026-09-03 — five calendar days.
        // A calendar-day window of three would have stopped at Saturday and lost Friday entirely.
        var due = PaperCaptureService.DueSessions(
            clock, Settings(), new DateTimeOffset(2026, 9, 8, 21, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, due.Count);
        Assert.Equal(new DateOnly(2026, 9, 8), due[0].TradingDate);
        Assert.Equal(new DateOnly(2026, 9, 4), due[1].TradingDate);
        Assert.Equal(new DateOnly(2026, 9, 3), due[2].TradingDate);
    }

    [Fact]
    public void A_half_day_close_is_taken_from_the_calendar_not_from_a_fixed_hour()
    {
        var clock = new SessionClock();

        // 2026-11-27, the day after Thanksgiving: NYSE closes 13:00 ET = 18:00 UTC (EST). A pass at
        // 18:30 UTC is already past that close, and would not be past a hardcoded 21:00.
        var due = PaperCaptureService.DueSessions(
            clock, Settings(), new DateTimeOffset(2026, 11, 27, 18, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 11, 27), due[0].TradingDate);
        Assert.True(due[0].IsHalfDay);
    }

    [Fact]
    public void A_holiday_has_no_session_and_so_is_never_due()
    {
        var clock = new SessionClock();

        // Independence Day observed, 2026-07-03 (the 4th is a Saturday). No NYSE session exists, so
        // there is nothing to capture and no refusal to record — absence of a session is not a gap.
        var due = PaperCaptureService.DueSessions(
            clock, Settings(), new DateTimeOffset(2026, 7, 3, 23, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain(due, session => session.TradingDate == new DateOnly(2026, 7, 3));
    }

    [Fact]
    public void Only_the_configured_session_label_triggers_a_capture()
    {
        var clock = new SessionClock();
        var settings = Settings();
        settings.SessionLabel = "GTH";

        // NYSE publishes no GTH session, so a mislabelled configuration captures nothing rather
        // than silently falling back to RTH and dating the record by the wrong boundary.
        Assert.Empty(PaperCaptureService.DueSessions(clock, settings, CloseUtc.AddHours(4)));
    }

    [Fact]
    public void A_zero_or_negative_lookback_still_captures_the_session_that_just_closed()
    {
        var clock = new SessionClock();

        // A misconfigured 0 must not silently disable capture: the floor is one session, because
        // "capture nothing" is not a state this component is allowed to reach by arithmetic.
        var due = PaperCaptureService.DueSessions(clock, Settings(lookback: 0), CloseUtc.AddMinutes(20));

        Assert.Single(due);
        Assert.Equal(new DateOnly(2026, 8, 6), due[0].TradingDate);
    }

    [Fact]
    public void Sessions_are_typed_as_the_contract_the_capture_reads_its_window_from()
    {
        var clock = new SessionClock();

        var session = PaperCaptureService.DueSessions(clock, Settings(), CloseUtc.AddMinutes(20))[0];

        // The executions pull is bounded below by OpenUtc; a session whose open is not before its
        // close would ask TWS for an empty window.
        Assert.IsType<TradingSession>(session);
        Assert.True(session.OpenUtc < session.CloseUtc);
        Assert.Equal(CloseUtc, session.CloseUtc);
    }
}
