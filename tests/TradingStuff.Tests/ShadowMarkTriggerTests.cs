using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    // ---- TickAsync: which outcomes settle a date, and which leave it for the catch-up window -----
    //
    // Everything above drives the pure schedule arithmetic. Nothing above reaches TickAsync, which is
    // where a day is actually lost or kept: both defects these tests pin — a refusal burning the date
    // permanently, and a mark for the WRONG date claiming the right one — passed the whole schedule
    // suite untouched. RunOverride stands in for the run so the outcome handling is reachable without
    // a database, a gateway, or three years of bars.

    /// <summary>Thursday 2026-08-06's mark falls due at 2026-08-07 00:10 UTC.</summary>
    private static readonly DateTimeOffset Due = new(2026, 8, 7, 0, 10, 0, TimeSpan.Zero);

    private static VrpShadowMark MarkFor(DateOnly markDate) =>
        new(markDate, markDate.AddYears(-3), markDate, 710,
            15.99, 0.0256, 0.021, 0.022, -0.0046, -0.0036, 0.0, 2, 2, 3, 0.5, 0.5, 1.0);

    /// <summary>A trigger wired to a stand-in run, with a real scope factory it never resolves from.</summary>
    private static ShadowMarkTrigger Trigger(
        Func<CancellationToken, Task<VolShadowMarkRunOutcome>> run,
        ShadowMarkTriggerOptions? settings = null,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();

        return new ShadowMarkTrigger(
            services.GetRequiredService<IServiceScopeFactory>(),
            new SessionClock(),
            Options.Create(settings ?? Settings()),
            time ?? TimeProvider.System,
            NullLogger<ShadowMarkTrigger>.Instance)
        {
            RunOverride = (_, cancellationToken) => run(cancellationToken),
        };
    }

    [Fact]
    public async Task A_refused_date_is_attempted_again_and_settles_when_the_input_lands()
    {
        var attempts = 0;
        var refuse = true;

        var trigger = Trigger(_ =>
        {
            attempts++;

            return Task.FromResult(refuse
                ? new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Refused, null, null, "No VIX close for 2026-08-06.")
                : new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Recorded, MarkFor(Thursday), new object(), null));
        });

        // 00:10 — due, and the daily-close slice has not drained yet. RunAtUtc is by construction the
        // EARLIEST instant that input can exist, so a refusal here is the expected first answer.
        await trigger.TickAsync(Due, CancellationToken.None);
        Assert.Equal(1, attempts);

        // Two minutes later: still due, but inside the retry spacing. A three-year bar load must not
        // run on every 60-second poll.
        await trigger.TickAsync(Due.AddMinutes(2), CancellationToken.None);
        Assert.Equal(1, attempts);

        // Sixteen minutes later: past RefusalRetryMinutes, so it tries again. THIS is the assertion
        // that fails if a refusal claims the date — the whole 720-minute window exists for it.
        await trigger.TickAsync(Due.AddMinutes(16), CancellationToken.None);
        Assert.Equal(2, attempts);

        // The backfill lands the close, and the next attempt records the mark.
        refuse = false;
        await trigger.TickAsync(Due.AddMinutes(32), CancellationToken.None);
        Assert.Equal(3, attempts);

        // Settled: a recorded date is claimed and is not run again for the rest of the window.
        await trigger.TickAsync(Due.AddMinutes(48), CancellationToken.None);
        await trigger.TickAsync(Due.AddHours(6), CancellationToken.None);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_refusal_retries_about_forty_eight_times_across_the_window_not_every_poll()
    {
        var attempts = 0;

        var trigger = Trigger(_ =>
        {
            attempts++;
            return Task.FromResult(
                new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Refused, null, null, "No VIX close."));
        });

        // Drive a 60-second poll across the whole 720-minute window: 721 polls, and the spacing must
        // turn them into ~48 attempts. Bounded retry is the point — an unbounded one would re-load
        // three years of bars 721 times, and a claimed refusal would run exactly once and lose the day.
        for (var minute = 0; minute <= 720; minute++)
        {
            await trigger.TickAsync(Due.AddMinutes(minute), CancellationToken.None);
        }

        Assert.Equal(48, attempts);
    }

    [Fact]
    public async Task A_mark_for_a_different_date_does_not_settle_the_date_that_was_due()
    {
        var attempts = 0;
        var landed = false;

        var trigger = Trigger(_ =>
        {
            attempts++;

            // research.bars has no complete session for Thursday yet, so the forecaster dates its
            // mark from WEDNESDAY — and upserts Wednesday's row, rewriting a good record's
            // planner_intent on the way past. Thursday is not what came back.
            return Task.FromResult(new VolShadowMarkRunOutcome(
                VolShadowMarkRunStatus.Recorded,
                MarkFor(landed ? Thursday : Thursday.AddDays(-1)),
                new object(),
                null));
        });

        await trigger.TickAsync(Due, CancellationToken.None);
        Assert.Equal(1, attempts);

        // Thursday must still be due. Claiming it on the strength of Wednesday's row would retire it
        // having never written it, and Thursday would never be marked at all.
        await trigger.TickAsync(Due.AddMinutes(16), CancellationToken.None);
        Assert.Equal(2, attempts);

        landed = true;
        await trigger.TickAsync(Due.AddMinutes(32), CancellationToken.None);
        Assert.Equal(3, attempts);

        // Now it matches, so it settles.
        await trigger.TickAsync(Due.AddMinutes(48), CancellationToken.None);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_run_that_threw_settles_the_date_rather_than_flooding_the_window()
    {
        var attempts = 0;

        var trigger = Trigger(_ =>
        {
            attempts++;
            throw new InvalidOperationException("something unexplained");
        });

        // The throw propagates so ExecuteAsync logs it once. Unlike a refusal, an unexplained fault
        // has no known remedy inside the window, and repeating it 48 times buries the one line that
        // says what broke.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => trigger.TickAsync(Due, CancellationToken.None));

        await trigger.TickAsync(Due.AddMinutes(30), CancellationToken.None);
        await trigger.TickAsync(Due.AddHours(6), CancellationToken.None);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_shutdown_cancellation_does_not_settle_the_date()
    {
        var attempts = 0;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var trigger = Trigger(token =>
        {
            attempts++;
            token.ThrowIfCancellationRequested();
            return Task.FromResult(
                new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Recorded, MarkFor(Thursday), null, null));
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => trigger.TickAsync(Due, cancelled.Token));

        // A date abandoned because the process is stopping was never attempted in any meaningful
        // sense; claiming it would be a claim made by a process that no longer exists.
        Assert.Equal(1, attempts);
        Assert.Null(typeof(ShadowMarkTrigger)
            .GetField("_lastFired", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(trigger));
    }

    [Fact]
    public async Task A_missing_connection_string_settles_the_date_because_it_cannot_resolve_in_the_window()
    {
        var attempts = 0;

        var trigger = Trigger(_ =>
        {
            attempts++;
            return Task.FromResult(
                new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.NotConfigured, null, null, null));
        });

        await trigger.TickAsync(Due, CancellationToken.None);
        await trigger.TickAsync(Due.AddMinutes(30), CancellationToken.None);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_restart_re_runs_a_date_it_already_marked_and_that_is_the_accepted_cost()
    {
        var attempts = 0;

        Func<ShadowMarkTrigger> fresh = () => Trigger(_ =>
        {
            attempts++;
            return Task.FromResult(
                new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Recorded, MarkFor(Thursday), null, null));
        });

        var before = fresh();
        await before.TickAsync(Due, CancellationToken.None);
        await before.TickAsync(Due.AddHours(2), CancellationToken.None);
        Assert.Equal(1, attempts);

        // The claim is in memory and deliberately not persisted, so a restart inside the window runs
        // the date again. The upsert makes that safe for the mark itself; it is NOT free, because
        // planner_intent is a live gateway read and the second run replaces it with whatever the
        // gateway says now. That is why the retry interval is minutes and not the poll interval.
        var after = fresh();
        await after.TickAsync(Due.AddHours(3), CancellationToken.None);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Nothing_runs_when_no_date_is_due()
    {
        var attempts = 0;

        var trigger = Trigger(_ =>
        {
            attempts++;
            return Task.FromResult(
                new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Recorded, MarkFor(Thursday), null, null));
        });

        // 30 minutes after the US close: due is 00:10 the following day, so there is nothing to do
        // and the run must not be reached at all.
        await trigger.TickAsync(ThursdayCloseUtc.AddMinutes(30), CancellationToken.None);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task A_dependency_timeout_does_not_silently_kill_the_loop_for_the_life_of_the_process()
    {
        var attempts = 0;
        var time = new FakeTimeProvider(Due.AddSeconds(-30));

        // The planner's quote read goes out over HttpClient, and an HttpClient timeout surfaces as
        // TaskCanceledException — which derives from OperationCanceledException. A catch filter of
        // `ex is not OperationCanceledException` lets that escape ExecuteAsync, and because
        // Program.cs sets BackgroundServiceExceptionBehavior.Ignore, the host does NOT fall over: the
        // trigger simply stops, silently, and the shadow record stops growing with nothing logged.
        var trigger = Trigger(
            _ =>
            {
                attempts++;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            },
            new ShadowMarkTriggerOptions
            {
                Calendar = "NYSE",
                SessionLabel = "RTH",
                RunAtUtc = TimeSpan.FromMinutes(10),
                CatchUpWindowMinutes = 720,
                // One minute apart, so the second attempt is a different DAY's mark rather than a
                // retry of the same one — the claim on a thrown run is working as intended.
                RefusalRetryMinutes = 1,
                PollSeconds = 60,
            },
            time);

        await trigger.StartAsync(CancellationToken.None);

        // One poll that lands on a due date. The run throws a TaskCanceledException.
        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);

        Assert.Equal(1, attempts);

        // THE assertion, and it is deliberately "is the loop still alive?" rather than "did it
        // fault?": an OperationCanceledException escaping an async method completes its Task as
        // CANCELLED, not faulted, so IsFaulted stays false either way and a test asking that question
        // passes against the defect. Under `ex is not OperationCanceledException` this task is
        // Canceled here and the daily mark is over for the life of the process.
        Assert.False(
            trigger.ExecuteTask!.IsCompleted,
            $"a dependency timeout ended the trigger loop ({trigger.ExecuteTask.Status}); under " +
            "BackgroundServiceExceptionBehavior.Ignore that stops the daily mark for the life of the " +
            "process with nothing logged");

        await trigger.StopAsync(CancellationToken.None);
    }
}
