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
    public async Task GetLandedBarCountsAsync_measures_the_empty_window_rather_than_defaulting_it_to_zero()
    {
        // THE HEADLINE SAFETY PROPERTY OF THIS PACKAGE, and the previous version of this test could
        // not fail. It asserted `counts.Length == 3` and `counts[1] == 0` — but the array is
        // allocated `new int[windowFrom.Count]` by C# and zero-filled, so BOTH assertions hold
        // verbatim under the naive GROUP BY the test claimed to guard against: that query simply
        // returns two rows, index 1 is never written, and the default 0 reads exactly like a measured
        // 0. The test was asserting the language, not the SQL.
        //
        // Two things fix it. The store now counts which ordinals the engine actually returned and
        // throws when any window went unmeasured; and the second half below runs the naive shape
        // against the same fixture to prove the fixture genuinely distinguishes them — without that,
        // the store's guard could be passing for the wrong reason.
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

        Assert.Equal(3, counts.Length);
        Assert.Equal(405, counts[0]);
        Assert.Equal(0, counts[1]);
        Assert.Equal(405, counts[2]);

        // The fixture really does separate the two query shapes: grouping from research.bars emits a
        // row only for windows that HAVE bars, so it answers this same question with two rows and no
        // way to tell which window is missing. If the store's SQL were ever rewritten this way, its
        // ordinal check now throws instead of returning a plausible zero.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var naive = new NpgsqlCommand(
            """
            SELECT s.idx, count(*)
            FROM unnest($1::timestamptz[], $2::timestamptz[]) WITH ORDINALITY AS s(from_utc, to_utc, idx)
            JOIN research.bars b
              ON b.con_id = $3 AND b.what_to_show = 'TRADES' AND b.bar_size = '1 min' AND b.use_rth
             AND b.ts_utc >= s.from_utc AND b.ts_utc < s.to_utc
            GROUP BY s.idx
            """,
            connection);

        naive.Parameters.AddWithValue(froms);
        naive.Parameters.AddWithValue(tos);
        naive.Parameters.AddWithValue(SpxConId);

        var naiveRows = 0;
        await using (var reader = await naive.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                naiveRows++;
            }
        }

        Assert.Equal(2, naiveRows); // NOT 3 — Tuesday cannot produce a row it has no bars for.
    }

    [Fact]
    public async Task No_windows_asked_about_means_no_counts_and_no_complaint()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        Assert.Empty(await StoreFor(connectionString).GetLandedBarCountsAsync(
            SpxConId, "TRADES", "1 min", true, [], [], CancellationToken.None));
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

    // ---- the seam between a historical job and its top-up ----------------------------------------

    [Fact]
    public async Task Sessions_after_a_historical_jobs_frozen_target_to_are_audited_rather_than_belonging_to_nobody()
    {
        // The reported defect, end to end. A historical job's target_to is frozen at the UTC midnight
        // of the day the job row was created, and the detector used it as the audit CEILING; a
        // top-up job only looks back two days. The band between them widened by one day per day of
        // operation and no job's analysis covered it — three complete RTH sessions with zero rows in
        // research.bars produced status=checked, gaps=0 from BOTH jobs, and no query could surface
        // them. The caller could not rescue it either: `from` clamps up to the lower bound and `to`
        // clamps down to the ceiling, so asking explicitly for the lost days returned window-rejected.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var now = DateTimeOffset.UtcNow;
        var createdOn = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-30);

        // A job created 30 days ago: target_from 40 days back, target_to frozen at creation.
        var definition = new BackfillJobDefinition(
            "spx-1min-trades-seam", BackfillJobKinds.Historical, SpxInstrumentId, "SPX", "TRADES", "1 min",
            UseRth: true, createdOn.AddDays(-10), createdOn, Priority: 100);

        var job = await store.EnsureJobAsync(definition, SpxConId, CancellationToken.None);

        // A real RTH session comfortably inside the band — after target_to, before the top-up's
        // two-day lookback — with nothing planned and nothing landed for it.
        var abandoned = new SessionClock()
            .SessionsBetween("CBOE_INDEX_RTH", DateOnly.FromDateTime(createdOn.UtcDateTime).AddDays(5),
                DateOnly.FromDateTime(now.UtcDateTime).AddDays(-5))
            .First(s => s.Label == "RTH" && s.CloseUtc < now.AddDays(-4) && s.OpenUtc > createdOn.AddDays(4));

        var report = await DetectorFor(connectionString).GetReportAsync(job!.JobId, null, null, CancellationToken.None);
        var jobReport = Assert.Single(report.Jobs);

        Assert.Equal(GapCheckStatus.Checked, jobReport.CheckStatus);
        Assert.True(jobReport.To > createdOn, "The audited ceiling must not be the frozen planning anchor.");
        Assert.Contains(jobReport.Gaps, gap => gap.From <= abandoned.OpenUtc && gap.To >= abandoned.CloseUtc);
    }

    [Fact]
    public async Task A_series_whose_jobs_between_them_leave_a_window_unaudited_says_so_explicitly()
    {
        // The negative claim was measured per job and never across the job set, so the seam BETWEEN
        // two jobs covering the same series was precisely where nothing looked. Here the historical
        // job's window is rejected outright (its span exceeds MaxWindowDays) while its top-up sibling
        // audits only the last two days — the per-job reports are individually defensible and the
        // series is not covered, which only a cross-job reconciliation can state.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var now = DateTimeOffset.UtcNow;

        await store.EnsureJobAsync(
            new BackfillJobDefinition(
                "spx-1min-trades-wide", BackfillJobKinds.Historical, SpxInstrumentId, "SPX", "TRADES", "1 min",
                UseRth: true, now.AddDays(-60), now, Priority: 100),
            SpxConId, CancellationToken.None);

        await store.EnsureJobAsync(
            new BackfillJobDefinition(
                "spx-1min-trades-wide-topup", BackfillJobKinds.TopUp, SpxInstrumentId, "SPX", "TRADES", "1 min",
                UseRth: true, now.AddDays(-60), now.AddYears(5), Priority: 1000),
            SpxConId, CancellationToken.None);

        var detector = new GapDetector(
            StoreFor(connectionString),
            new SessionClock(),
            Options.Create(new GapOptions { TopUpDefaultLookbackDays = 2, MaxWindowDays = 10 }),
            Options.Create(new BackfillOptions { MaxAttempts = MaxAttempts }),
            NullLogger<GapDetector>.Instance);

        var report = await detector.GetReportAsync(null, null, null, CancellationToken.None);

        Assert.Equal(2, report.Jobs.Count);

        // Both jobs write the same (con_id, what_to_show, bar_size, use_rth) rows, so they are one
        // series and are jointly responsible for it.
        var series = Assert.Single(report.Series);

        Assert.False(series.Reconciled);
        Assert.Equal(["spx-1min-trades-wide", "spx-1min-trades-wide-topup"], series.JobNames);
        Assert.All(series.Unaudited, range => Assert.Equal(GapAuditReasons.NoJobAuditedIt, range.Reason));

        // ~58 of the 60 claimed days: everything except the top-up's two-day tail.
        Assert.True(series.Unaudited.Sum(r => (r.To - r.From).TotalDays) > 50);
    }

    [Fact]
    public async Task Two_jobs_on_different_instruments_are_never_reconciled_against_each_other()
    {
        // A union across instruments would let SPY's audited window "cover" an SPX hole, which is
        // worse than not reconciling at all.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var now = DateTimeOffset.UtcNow;

        await store.EnsureJobAsync(SpxHistorical("spx-series", targetFrom: now.AddDays(-5)), SpxConId, CancellationToken.None);
        await store.EnsureJobAsync(
            new BackfillJobDefinition(
                "spy-series", BackfillJobKinds.Historical, InstrumentId: 5, "SPY", "TRADES", "1 min",
                UseRth: true, now.AddDays(-5), now, Priority: 90),
            756733, CancellationToken.None);

        var report = await DetectorFor(connectionString).GetReportAsync(null, null, null, CancellationToken.None);

        Assert.Equal(2, report.Series.Count);
        Assert.Equal([SpxConId, 756733], report.Series.Select(s => s.ConId).Order());
    }

    // ---- the in-progress tail ---------------------------------------------------------------------

    [Fact]
    public async Task The_session_currently_in_progress_is_excluded_from_the_expectation_and_reported_as_such()
    {
        // A top-up job's data necessarily lags the clock, so measuring the running session against
        // its full elapsed minutes reported succeeded_but_absent — the basis that is supposed to mean
        // "the checkpoint lied" — on every poll during market hours. Excluding it is only half the
        // fix: the excluded tail has to be visible, or "not checked yet" starts passing for "checked
        // and clean".
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var definition = new BackfillJobDefinition(
            "spx-1min-trades-topup-grace", BackfillJobKinds.TopUp, SpxInstrumentId, "SPX", "TRADES", "1 min",
            UseRth: true, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5), Priority: 1000);

        var job = await store.EnsureJobAsync(definition, SpxConId, CancellationToken.None);

        var before = DateTimeOffset.UtcNow;
        var report = await DetectorFor(connectionString).GetReportAsync(job!.JobId, null, null, CancellationToken.None);
        var jobReport = Assert.Single(report.Jobs);

        Assert.NotNull(jobReport.To);
        Assert.True(
            jobReport.To <= before.AddMinutes(-59),
            $"The audited ceiling ({jobReport.To:O}) must stop short of now by the in-progress grace.");

        var inProgress = Assert.Single(jobReport.Unaudited, range => range.Reason == GapAuditReasons.InProgress);
        Assert.Equal(jobReport.To, inProgress.From);
        Assert.Equal(jobReport.NominalTo, inProgress.To);
    }

    // ---- "nothing was checked" must not render as "everything passed" ------------------------------

    [Fact]
    public async Task A_window_the_calendar_produces_no_session_for_is_not_reported_as_checked_and_clean()
    {
        // `checked` with an empty gap list was emitted identically for "every session verified
        // complete" — the strongest statement this report can make — and "the calendar produced zero
        // expectation units", the weakest.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(
            SpxHistorical(targetFrom: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            SpxConId, CancellationToken.None);

        // Saturday 2024-01-06 into Sunday 2024-01-07: no Cboe index RTH session exists at all.
        var report = await DetectorFor(connectionString).GetReportAsync(
            job!.JobId,
            new DateTimeOffset(2024, 1, 6, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 7, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);

        Assert.Equal(GapCheckStatus.NoExpectationUnits, jobReport.CheckStatus);
        Assert.Equal(0, jobReport.UnitsChecked);
        Assert.Empty(jobReport.Gaps);
        Assert.NotEmpty(jobReport.Unaudited);
    }

    [Fact]
    public async Task A_checked_job_reports_how_many_expectation_units_it_actually_verified()
    {
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
        Assert.Equal(5, jobReport.UnitsChecked); // Monday through Friday, one RTH session each
    }

    [Fact]
    public async Task A_job_whose_analysis_throws_reports_its_whole_window_as_unaudited_not_as_zero_gaps()
    {
        // The error path had no test and reported an empty gap list, which is exactly what a clean
        // job reports. A connection that fails mid-analysis must not be able to render as health.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(SpxHistorical(), SpxConId, CancellationToken.None);

        // The job row is read through a working store; the per-job analysis then hits a database that
        // no longer answers. Dropping the table it queries is the least contrived way to get there.
        await ExecuteAsync(connectionString, "ALTER TABLE research.bars RENAME TO bars_hidden");

        var report = await DetectorFor(connectionString).GetReportAsync(job!.JobId, Monday, WeekEnd, CancellationToken.None);
        var jobReport = Assert.Single(report.Jobs);

        Assert.Equal(GapCheckStatus.Error, jobReport.CheckStatus);
        Assert.NotNull(jobReport.CheckDetail);
        Assert.Empty(jobReport.Gaps);

        var unaudited = Assert.Single(jobReport.Unaudited);
        Assert.Equal(GapAuditReasons.JobNotChecked, unaudited.Reason);
        Assert.Equal(jobReport.NominalFrom, unaudited.From);

        // ...and the series it belongs to must inherit that: nothing audited this window.
        var series = Assert.Single(report.Series);
        Assert.False(series.Reconciled);
    }

    [Fact]
    public async Task Every_job_is_reported_when_no_job_id_is_requested_including_ones_that_cannot_be_checked()
    {
        // The multi-job path had no test at all, and it is the path the UI actually calls.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await store.EnsureJobAsync(SpxHistorical("spx-checkable"), SpxConId, CancellationToken.None);
        await store.EnsureJobAsync(
            new BackfillJobDefinition(
                "es-uncheckable", BackfillJobKinds.Historical, EsInstrumentId, "ES", "TRADES", "1 min",
                UseRth: false, Monday, WeekEnd, Priority: 60),
            conId: null, CancellationToken.None);

        var report = await DetectorFor(connectionString).GetReportAsync(null, Monday, WeekEnd, CancellationToken.None);

        Assert.Equal(2, report.Jobs.Count);
        Assert.Contains(report.Jobs, j => j.JobName == "spx-checkable" && j.CheckStatus == GapCheckStatus.Checked);
        Assert.Contains(
            report.Jobs,
            j => j.JobName == "es-uncheckable" && j.CheckStatus == GapCheckStatus.MultiContractJobUnsupported);
    }

    // ---- the instrument -> calendar mapping ---------------------------------------------------------

    [Fact]
    public async Task A_vix_intraday_job_is_not_judged_against_the_index_option_overnight_session()
    {
        // CBOE_INDEX_GTH describes the 19:15-08:15 CT index-OPTION session — 780 minutes — and VIX
        // index values are not published across it (the capability matrix records VIX 1-minute GTH
        // bars beginning at 02:15 CT, roughly 360). Expecting 780 where 360 exist reported every
        // correct VIX overnight session as succeeded_but_absent, forever. A report that cries wolf on
        // every session of an instrument trains its operator to ignore it.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        const int vixConId = 13455763;
        const short vixInstrumentId = 4;

        var job = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                "vix-1min-trades-test", BackfillJobKinds.Historical, vixInstrumentId, "VIX", "TRADES", "1 min",
                UseRth: false, Monday, WeekEnd, Priority: 70),
            vixConId, CancellationToken.None);

        // Land a complete RTH session and nothing overnight — the shape real, correct VIX data has.
        await ExecuteAsync(
            connectionString,
            "INSERT INTO research.bars (con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, open, high, low, close, source) " +
            "SELECT @con, @instrument, '1 min', 'TRADES', false, gs, 14, 14, 14, 14, 'backfill' " +
            "FROM generate_series(@from::timestamptz, @to::timestamptz - interval '1 minute', interval '1 minute') AS gs",
            ("con", vixConId), ("instrument", vixInstrumentId),
            ("from", RthWindowFrom(MondayDate)), ("to", RthWindowTo(MondayDate)));

        await InsertDailySliceAsync(connectionString, job!.JobId, MondayDate, "succeeded", barsReturned: 405);

        var report = await DetectorFor(connectionString).GetReportAsync(
            job.JobId, RthWindowFrom(MondayDate), RthWindowTo(MondayDate), CancellationToken.None);

        var jobReport = Assert.Single(report.Jobs);

        Assert.Equal(GapCheckStatus.Checked, jobReport.CheckStatus);
        Assert.Empty(jobReport.Gaps);

        // The genuine absence — VIX's real overnight window has no calendar and is therefore not
        // audited — is stated rather than silently passing.
        Assert.Contains(jobReport.Unaudited, range => range.Reason.StartsWith(GapAuditReasons.NoSessionDefinition));
    }

    // ---- window helpers: the RTH session bounds for a given trading date in the fixed test week ----

    private static DateTimeOffset RthWindowFrom(DateOnly date) =>
        RthOpenMonday.AddDays(date.DayNumber - MondayDate.DayNumber);

    private static DateTimeOffset RthWindowTo(DateOnly date) =>
        RthCloseMonday.AddDays(date.DayNumber - MondayDate.DayNumber);
}
