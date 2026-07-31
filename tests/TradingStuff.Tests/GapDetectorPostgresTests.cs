using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// Postgres integration tests for gap detection (package 2f): the query semantics of
/// <see cref="BackfillStore"/>'s new landed-bar and request-window reads, and
/// <see cref="GapDetector"/>'s end-to-end behaviour against a real database.
/// </summary>
/// <remarks>
/// <para>
/// Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection string — see
/// <see cref="ResearchRecordingPostgresTests"/> for the same convention.
/// </para>
/// <para>
/// The whole point of this package is a NEGATIVE claim ("nothing is silently missing"), and a naive
/// implementation passes every test that only checks "a gap in the middle is found" — see the tests
/// marked below for the specific absent-row shapes: a session with zero landed bars between two
/// healthy ones, a job with zero request rows at all, and a job this detector cannot check at all
/// (NULL con_id) that must still appear in the report rather than vanish from it.
/// </para>
/// <para>
/// A fixed, past week (2024-01-08 Monday through 2024-01-12 Friday — no US holiday in range) is used
/// throughout so nothing here depends on the real wall clock at test-run time. Cboe index RTH that
/// week is 08:30-15:15 CT = 14:30-21:15 UTC (CST, no DST in January): 405 minutes/day, no half days.
/// </para>
/// </remarks>
[Trait("Category", "RequiresPostgres")]
public sealed class GapDetectorPostgresTests
{
    private const int SpxConId = 416904;
    private const short SpxInstrumentId = 1; // research.instruments seed: SPX, index, CBOE.
    private const short EsInstrumentId = 6;  // research.instruments seed: ES, future_family, CME.
    private const int MaxAttempts = 5;

    private static readonly DateOnly MondayDate = new(2024, 1, 8);
    private static readonly DateTimeOffset Monday = new(2024, 1, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RthOpenMonday = new(2024, 1, 8, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RthCloseMonday = new(2024, 1, 8, 21, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeekEnd = new(2024, 1, 13, 0, 0, 0, TimeSpan.Zero); // Saturday midnight, exclusive

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<string> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return connectionString;
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    private static BackfillStore StoreFor(string connectionString) =>
        new(ConfigurationFor(connectionString), NullLogger<BackfillStore>.Instance);

    private static GapDetector DetectorFor(string connectionString, int topUpLookbackDays = 2) =>
        new(
            StoreFor(connectionString),
            new SessionClock(),
            Options.Create(new GapOptions { TopUpDefaultLookbackDays = topUpLookbackDays }),
            Options.Create(new BackfillOptions { MaxAttempts = MaxAttempts }),
            NullLogger<GapDetector>.Instance);

    private static BackfillJobDefinition SpxHistorical(
        string name = "spx-1min-trades-test", DateTimeOffset? targetFrom = null) =>
        new(
            name, BackfillJobKinds.Historical, SpxInstrumentId, "SPX", "TRADES", "1 min",
            UseRth: true, targetFrom ?? Monday, WeekEnd, Priority: 100);

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts one 1-day request row (job_id, con_id fixed at SPX) ending at UTC midnight the day AFTER <paramref name="tradingDate"/>.</summary>
    private static Task InsertDailySliceAsync(
        string connectionString, long jobId, DateOnly tradingDate, string state, int attempts = 1, int? barsReturned = null) =>
        ExecuteAsync(
            connectionString,
            "INSERT INTO research.backfill_requests " +
            "  (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth, state, attempts, " +
            "   bars_returned, completed_at) " +
            "VALUES (@job, @con, @end, '1 D', 'TRADES', '1 min', true, @state, @attempts, @bars, now())",
            ("job", jobId), ("con", SpxConId),
            ("end", new DateTimeOffset(tradingDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
            ("state", state), ("attempts", attempts), ("bars", (object?)barsReturned ?? DBNull.Value));

    /// <summary>Lands one real 1-minute bar per minute of the trading date's full RTH session (405 bars).</summary>
    private static Task InsertFullRthBarsAsync(string connectionString, DateOnly tradingDate) =>
        ExecuteAsync(
            connectionString,
            "INSERT INTO research.bars (con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, open, high, low, close, source) " +
            "SELECT @con, @instrument, '1 min', 'TRADES', true, gs, 100, 100, 100, 100, 'backfill' " +
            "FROM generate_series(@from::timestamptz, @to::timestamptz - interval '1 minute', interval '1 minute') AS gs",
            ("con", SpxConId), ("instrument", SpxInstrumentId),
            ("from", RthWindowFrom(tradingDate)), ("to", RthWindowTo(tradingDate)));

    // ---- GetLandedBarCountsAsync: the absent-row check for intraday bar sizes ---------------------

    [Fact]
    public async Task GetLandedBarCountsAsync_reports_zero_for_a_window_with_no_bars_rather_than_omitting_it()
    {
        // The exact mechanism the class doc names: an INNER JOIN or a GROUP BY starting from
        // research.bars produces no row at all for the window with zero bars, and the caller would
        // then have to notice a SHORT array rather than read a genuine zero. Asserting the array
        // LENGTH here is the point, not just its middle value.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var monday = new DateOnly(2024, 1, 8);
        var tuesday = new DateOnly(2024, 1, 9);
        var wednesday = new DateOnly(2024, 1, 10);

        await InsertFullRthBarsAsync(connectionString, monday);
        // Tuesday: deliberately nothing landed.
        await InsertFullRthBarsAsync(connectionString, wednesday);

        var froms = new[] { RthWindowFrom(monday), RthWindowFrom(tuesday), RthWindowFrom(wednesday) };
        var tos = new[] { RthWindowTo(monday), RthWindowTo(tuesday), RthWindowTo(wednesday) };

        var counts = await store.GetLandedBarCountsAsync(SpxConId, "TRADES", "1 min", true, froms, tos, CancellationToken.None);

        Assert.Equal(3, counts.Length); // NOT 2 — the empty middle window must still produce an entry.
        Assert.Equal(405, counts[0]);
        Assert.Equal(0, counts[1]);
        Assert.Equal(405, counts[2]);
    }

    [Fact]
    public async Task GetRequestWindowsAsync_excludes_null_anchored_rows_which_have_no_derivable_window()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        await InsertDailySliceAsync(connectionString, job!.JobId, new DateOnly(2024, 1, 8), "succeeded", barsReturned: 405);
        await ExecuteAsync(
            connectionString,
            "INSERT INTO research.backfill_requests (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth) " +
            "VALUES (@job, @con, NULL, '1 D', 'TRADES', '1 min', true)",
            ("job", job.JobId), ("con", SpxConId));

        var windows = await store.GetRequestWindowsAsync(job.JobId, CancellationToken.None);

        Assert.Single(windows);
        Assert.Equal("succeeded", windows[0].State);
    }

    // ---- GapDetector end to end: the absent-row check one level up ---------------------------------

    [Fact]
    public async Task A_job_with_zero_request_rows_and_zero_bars_reports_not_requested_across_its_whole_window()
    {
        // The completely-dead-job case named explicitly in the package brief: a query that starts
        // from research.backfill_requests cannot emit anything for a job with no rows in it at all,
        // and a query that starts from research.bars cannot either. GapDetector must still produce a
        // loud, non-empty finding for it.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        var report = await DetectorFor(connectionString).GetReportAsync(job!.JobId, Monday, WeekEnd, CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(GapCheckStatus.Checked, jobReport.CheckStatus);
        Assert.False(jobReport.Truncated);

        var gap = Assert.Single(jobReport.Gaps);
        Assert.Equal(GapBasis.NotRequested, gap.Basis);
        // The whole Mon-Fri span collapses into ONE range: nothing covered separates any of the five
        // sessions, so they merge (see GapArithmeticTests for the pure-logic version of this claim).
        Assert.Equal(RthWindowFrom(new DateOnly(2024, 1, 8)), gap.From);
        Assert.Equal(RthWindowTo(new DateOnly(2024, 1, 12)), gap.To);
    }

    [Fact]
    public async Task A_session_with_zero_landed_bars_between_two_healthy_ones_is_its_own_gap_not_swallowed()
    {
        // Also exercises the alarming basis: the missing day's request row says succeeded.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        var monday = new DateOnly(2024, 1, 8);
        var tuesday = new DateOnly(2024, 1, 9);
        var wednesday = new DateOnly(2024, 1, 10);
        var thursday = new DateOnly(2024, 1, 11);
        var friday = new DateOnly(2024, 1, 12);

        foreach (var date in new[] { monday, tuesday, thursday, friday })
        {
            await InsertDailySliceAsync(connectionString, job!.JobId, date, "succeeded", barsReturned: 405);
            await InsertFullRthBarsAsync(connectionString, date);
        }

        // Wednesday: the checkpoint says succeeded, but nothing landed. A GROUP BY starting from
        // research.bars would have no row for this day at all and this test would pass trivially
        // under that design too — it is the "before/after are healthy" framing that makes the point:
        // an implementation that only checks "does the job have ANY gap" could satisfy that from the
        // day's own absence, but this asserts the gap is EXACTLY Wednesday's session and labelled
        // succeeded_but_absent, which only a correct join can produce.
        await InsertDailySliceAsync(connectionString, job!.JobId, wednesday, "succeeded", barsReturned: 405);

        var report = await DetectorFor(connectionString).GetReportAsync(job.JobId, Monday, WeekEnd, CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        var gap = Assert.Single(jobReport.Gaps);

        Assert.Equal(GapBasis.SucceededButAbsent, gap.Basis);
        Assert.Equal(RthWindowFrom(wednesday), gap.From);
        Assert.Equal(RthWindowTo(wednesday), gap.To);
    }

    [Fact]
    public async Task Terminal_empty_and_permanent_slices_are_reported_as_explained_but_still_visible()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        var monday = new DateOnly(2024, 1, 8);
        var tuesday = new DateOnly(2024, 1, 9);

        await InsertDailySliceAsync(connectionString, job!.JobId, monday, "empty", barsReturned: 0);
        await InsertDailySliceAsync(connectionString, job.JobId, tuesday, "permanent");

        var report = await DetectorFor(connectionString).GetReportAsync(
            job.JobId, RthWindowFrom(monday), RthWindowTo(tuesday), CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(2, jobReport.Gaps.Count);
        Assert.Equal(GapBasis.Empty, jobReport.Gaps[0].Basis);
        Assert.Equal(GapBasis.Permanent, jobReport.Gaps[1].Basis);
    }

    [Fact]
    public async Task Pending_and_inflight_slices_are_reported_as_in_progress_not_alarming()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        var monday = new DateOnly(2024, 1, 8);
        var tuesday = new DateOnly(2024, 1, 9);

        await InsertDailySliceAsync(connectionString, job!.JobId, monday, "pending", attempts: 0);
        await ExecuteAsync(
            connectionString,
            "INSERT INTO research.backfill_requests " +
            "  (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth, state, attempts, " +
            "   claimed_by, lease_expires_at) " +
            "VALUES (@job, @con, @end, '1 D', 'TRADES', '1 min', true, 'inflight', 1, 'test-owner', now() + interval '5 minutes')",
            ("job", job.JobId), ("con", SpxConId),
            ("end", new DateTimeOffset(tuesday.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));

        var report = await DetectorFor(connectionString).GetReportAsync(
            job.JobId, RthWindowFrom(monday), RthWindowTo(tuesday), CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(2, jobReport.Gaps.Count);
        Assert.Equal(GapBasis.Pending, jobReport.Gaps[0].Basis);
        Assert.Equal(GapBasis.Inflight, jobReport.Gaps[1].Basis);
    }

    [Fact]
    public async Task A_failed_slice_past_the_attempt_cap_is_exhausted_while_one_under_it_is_retrying()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        var monday = new DateOnly(2024, 1, 8);
        var tuesday = new DateOnly(2024, 1, 9);

        await InsertDailySliceAsync(connectionString, job!.JobId, monday, "failed", attempts: 2); // under cap
        await InsertDailySliceAsync(connectionString, job.JobId, tuesday, "failed", attempts: MaxAttempts); // at cap

        var report = await DetectorFor(connectionString).GetReportAsync(
            job.JobId, RthWindowFrom(monday), RthWindowTo(tuesday), CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(2, jobReport.Gaps.Count);
        Assert.Equal(GapBasis.Retrying, jobReport.Gaps[0].Basis);
        Assert.Equal(GapBasis.Exhausted, jobReport.Gaps[1].Basis);
    }

    [Fact]
    public async Task A_jobs_target_from_predating_its_real_head_timestamp_does_not_report_pre_history_as_a_gap()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        // target_from reaches back to 2004 (the roadmap's SPX floor probe), but the real head is
        // Monday of the test week — exactly the "probe to the floor" shape BackfillJobCatalog uses
        // for VIX, applied here to a fixed head instead.
        var deepFrom = new DateTimeOffset(2004, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var name = "spx-1min-trades-head-test";
        var job = await store.EnsureJobAsync(SpxHistorical(name, targetFrom: deepFrom), SpxConId, CancellationToken.None);

        await store.RecordHeadTimestampAsync($"head_timestamp:{name}", SpxConId, Monday, "test-seeded head", CancellationToken.None);

        var report = await DetectorFor(connectionString).GetReportAsync(job!.JobId, null, WeekEnd, CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(GapCheckStatus.Checked, jobReport.CheckStatus);
        Assert.Equal(Monday, jobReport.HeadTimestampUtc);
        Assert.Equal(Monday, jobReport.From); // NOT deepFrom (2004) — the head clamp, not the job's own floor.

        // No reported range may start before the head: the pre-history stretch (2004 → the head) is
        // simply not part of the checked window at all, so it cannot appear as not_requested forever.
        Assert.All(jobReport.Gaps, gap => Assert.True(gap.From >= Monday));
    }

    [Fact]
    public async Task A_topup_jobs_default_window_is_a_recent_lookback_not_its_deep_declared_target_from()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var deepFrom = new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero); // matches the real catalog's VixFrom shape
        var definition = new BackfillJobDefinition(
            "spx-1min-trades-topup-test", BackfillJobKinds.TopUp, SpxInstrumentId, "SPX", "TRADES", "1 min",
            UseRth: true, deepFrom, new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero), Priority: 1000);

        var job = await store.EnsureJobAsync(definition, SpxConId, CancellationToken.None);

        var report = await DetectorFor(connectionString, topUpLookbackDays: 2)
            .GetReportAsync(job!.JobId, null, null, CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(GapCheckStatus.Checked, jobReport.CheckStatus);
        Assert.NotNull(jobReport.From);
        // Nowhere near 2005 — a top-up job never walks backward past its own recent tail, so treating
        // its declared target_from as a real lower bound would falsely report two decades of gap.
        Assert.True(jobReport.From > DateTimeOffset.UtcNow.AddDays(-3));
    }

    [Fact]
    public async Task A_job_with_a_null_con_id_is_reported_as_unsupported_rather_than_silently_omitted()
    {
        // The ES-shaped case: EsContractWalker's own job row is ALWAYS created with conId: null (see
        // EsContractWalker.PlanOnceAsync) because it walks many rolled contracts under one job row.
        // Silently skipping such a job from the report would be exactly the absent-row failure this
        // whole package exists to avoid, just at the job level instead of the session level.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var definition = new BackfillJobDefinition(
            "es-1min-trades-test", BackfillJobKinds.Historical, EsInstrumentId, "ES", "TRADES", "1 min",
            UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60);

        var job = await store.EnsureJobAsync(definition, conId: null, CancellationToken.None);
        Assert.Null(job!.ConId);

        var report = await DetectorFor(connectionString).GetReportAsync(job.JobId, null, null, CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);
        Assert.Equal(GapCheckStatus.MultiContractJobUnsupported, jobReport.CheckStatus);
        Assert.NotNull(jobReport.CheckDetail);
        Assert.Empty(jobReport.Gaps);
    }

    [Fact]
    public async Task Requesting_an_unknown_job_id_yields_an_empty_report_rather_than_throwing()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        var report = await DetectorFor(connectionString).GetReportAsync(999_999, null, null, CancellationToken.None);

        Assert.Empty(report.Jobs);
    }

    // ---- window helpers: the RTH session bounds for a given trading date in the fixed test week ----

    private static DateTimeOffset RthWindowFrom(DateOnly date) =>
        RthOpenMonday.AddDays(date.DayNumber - MondayDate.DayNumber);

    private static DateTimeOffset RthWindowTo(DateOnly date) =>
        RthCloseMonday.AddDays(date.DayNumber - MondayDate.DayNumber);
}
