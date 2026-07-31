using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// The claims this package makes that only a real database can settle: resumability, idempotency
/// under concurrency, and recovery from a coordinator that died mid-flight.
/// </summary>
/// <remarks>
/// Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection string — see
/// <see cref="ResearchRecordingPostgresTests"/> for the same convention. Run against a dedicated
/// container so a concurrent suite cannot perturb the timing-sensitive claim races:
/// <c>docker run -d --name pg-2d -e POSTGRES_PASSWORD=postgres -p 5451:5432 postgres:17</c>.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
public sealed class BackfillCoordinatorPostgresTests
{
    private const int SpxConId = 416904;
    private const int MaxAttempts = 5;

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(300);

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

    private static BackfillJobDefinition HistoricalDefinition(
        string name = "spx-1min-trades",
        string barSize = "1 min",
        DateTimeOffset? targetFrom = null,
        DateTimeOffset? targetTo = null) =>
        new(
            name,
            BackfillJobKinds.Historical,
            InstrumentId: 1,
            Symbol: "SPX",
            WhatToShow: "TRADES",
            BarSize: barSize,
            UseRth: true,
            TargetFrom: targetFrom ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TargetTo: targetTo ?? new DateTimeOffset(2024, 1, 11, 0, 0, 0, TimeSpan.Zero),
            Priority: 100);

    private static BackfillJobDefinition TopUpDefinition() =>
        new(
            "spx-1min-trades-topup",
            BackfillJobKinds.TopUp,
            InstrumentId: 1,
            Symbol: "SPX",
            WhatToShow: "TRADES",
            BarSize: "1 min",
            UseRth: true,
            TargetFrom: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TargetTo: new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Priority: 1000);

    private static SliceCadence CadenceOf(BackfillJob job) =>
        BackfillPlanner.CadenceFor(job) ?? throw new InvalidOperationException("The test job has no cadence.");

    private static async Task<long> CountRequestsAsync(string connectionString, string? where = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.backfill_requests" + (where is null ? string.Empty : $" WHERE {where}"),
            connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // ---- resumability: an identical rerun costs nothing -----------------------------------------

    [Fact]
    public async Task Replanning_an_unchanged_job_adds_zero_backfill_request_rows()
    {
        // Asserted on research.backfill_requests deliberately. The obvious version of this test
        // counts research.bars, whose primary key silently absorbs duplicates — it would pass just
        // as happily against a planner whose boundaries drift on every run, which is exactly the
        // defect the idempotency key exists to prevent.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        Assert.NotNull(job);

        var slices = BackfillPlanner.PlanHistorical(job, SpxConId, null, CadenceOf(job));
        Assert.NotEmpty(slices);

        var firstInsert = await store.InsertSlicesAsync(slices, CancellationToken.None);
        var afterFirst = await CountRequestsAsync(connectionString);

        // A full second pass: re-ensure the job (which must not move target_to), re-plan, re-insert.
        var rediscovered = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        var replanned = BackfillPlanner.PlanHistorical(rediscovered!, SpxConId, null, CadenceOf(rediscovered!));
        var secondInsert = await store.InsertSlicesAsync(replanned, CancellationToken.None);

        Assert.Equal(slices.Count, firstInsert);
        Assert.Equal(0, secondInsert);
        Assert.Equal(afterFirst, await CountRequestsAsync(connectionString));
    }

    [Fact]
    public async Task A_jobs_target_to_is_fixed_at_creation_so_its_slice_grid_never_shifts()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        // TargetTo null means "the UTC midnight of the creation day"; re-ensuring must not re-derive
        // it, or every restart would shift the whole grid and re-plan the job from scratch.
        var first = await store.EnsureJobAsync(
            HistoricalDefinition(targetTo: null) with { TargetTo = null }, SpxConId, CancellationToken.None);

        await ExecuteAsync(connectionString, "UPDATE research.backfill_jobs SET updated_at = now()");

        var second = await store.EnsureJobAsync(
            HistoricalDefinition(targetTo: null) with { TargetTo = null }, SpxConId, CancellationToken.None);

        Assert.Equal(first!.TargetTo, second!.TargetTo);
        Assert.Equal(TimeSpan.Zero, first.TargetTo.ToUniversalTime().TimeOfDay);
    }

    [Fact]
    public async Task Deepening_a_jobs_target_from_adds_only_the_newly_reachable_older_slices()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var targetTo = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var job = await store.EnsureJobAsync(
            HistoricalDefinition(targetFrom: new DateTimeOffset(2024, 2, 20, 0, 0, 0, TimeSpan.Zero), targetTo: targetTo),
            SpxConId, CancellationToken.None);

        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);
        var shallowCount = await CountRequestsAsync(connectionString);

        // The operator deepens the job — the roadmap's "SPX from 2010, then probe toward 2004".
        await ExecuteAsync(
            connectionString, "UPDATE research.backfill_jobs SET target_from = '2024-02-10T00:00:00Z'");

        var deepened = (await store.GetActiveJobsAsync(CancellationToken.None)).Single();
        var deeperSlices = BackfillPlanner.PlanHistorical(deepened, SpxConId, null, CadenceOf(deepened));
        var added = await store.InsertSlicesAsync(deeperSlices, CancellationToken.None);

        Assert.Equal(deeperSlices.Count - (int)shallowCount, added);
        Assert.Equal(deeperSlices.Count, (int)await CountRequestsAsync(connectionString));
    }

    // ---- the top-up idempotency contradiction ---------------------------------------------------

    [Fact]
    public async Task Top_up_runs_collapse_within_a_bucket_and_advance_across_buckets()
    {
        // Migration 004's NULL-anchored design made every top-up run after the first collide with
        // itself under UNIQUE NULLS NOT DISTINCT: the insert was swallowed, the coordinator saw one
        // already-succeeded row, and the recent tail stopped advancing while the logs stayed clean.
        // Bucket-floored concrete anchors keep both halves — no new rows inside a bucket, exactly one
        // new row per bucket crossed.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var job = await store.EnsureJobAsync(TopUpDefinition(), SpxConId, CancellationToken.None);

        var bucketStart = new DateTimeOffset(2026, 7, 31, 14, 30, 0, TimeSpan.Zero);

        await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job!, SpxConId, bucketStart)], CancellationToken.None);
        await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job!, SpxConId, bucketStart.AddMinutes(7))], CancellationToken.None);
        await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job!, SpxConId, bucketStart.AddMinutes(14))], CancellationToken.None);

        Assert.Equal(1L, await CountRequestsAsync(connectionString));

        // Mark it succeeded, as a real run would, then cross into the next bucket. Under the old
        // design this is precisely where the tail froze: 'succeeded' means "never re-request", and
        // the next run's row was identical.
        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'succeeded'");

        var advanced = await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job!, SpxConId, bucketStart.AddMinutes(15))], CancellationToken.None);

        Assert.Equal(1, advanced);
        Assert.Equal(2L, await CountRequestsAsync(connectionString));
        Assert.Equal(1L, await CountRequestsAsync(connectionString, "state = 'pending'"));
        Assert.Equal(0L, await CountRequestsAsync(connectionString, "end_time_utc IS NULL"));
    }

    // ---- claiming under concurrency --------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_claimers_cannot_claim_the_same_slice()
    {
        // The Phase 1 review disproved SELECT ... FOR UPDATE followed by a write against live
        // Postgres 17: a blocked FOR UPDATE re-checks its WHERE against the row's NEW committed
        // version, silently excludes it, and both callers conclude "no row". Here the danger is
        // worse than a duplicate — the loser would see zero claimable rows, which is
        // indistinguishable from "the job is finished".
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job! with { Kind = BackfillJobKinds.TopUp }, SpxConId, DateTimeOffset.UtcNow.AddHours(-1))],
            CancellationToken.None);

        Assert.Equal(1L, await CountRequestsAsync(connectionString));

        var barrier = new Barrier(2);

        // Task.Run is essential. RaceAsync(a) and RaceAsync(b) are evaluated as ordinary synchronous
        // arguments to Task.WhenAll, and an async method runs synchronously up to its first await.
        // SignalAndWait() blocks rather than awaiting, so calling RaceAsync directly would block the
        // test thread inside evaluating the first argument — forever, because the barrier's second
        // participant would never be invoked.
        Task<IReadOnlyList<ClaimedSlice>> RaceAsync(string owner) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await StoreFor(connectionString).ClaimAsync(owner, Lease, MaxAttempts, 1, CancellationToken.None);
        });

        var results = await Task.WhenAll(RaceAsync("owner-a"), RaceAsync("owner-b"));

        Assert.Equal(1, results.Sum(claim => claim.Count));
        Assert.Equal(1L, await CountRequestsAsync(connectionString, "state = 'inflight'"));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var owners = new NpgsqlCommand(
            "SELECT count(DISTINCT claimed_by) FROM research.backfill_requests WHERE state = 'inflight'", connection);

        Assert.Equal(1L, (long)(await owners.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Concurrent_claimers_partition_the_queue_without_overlapping()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        var slices = BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!));
        await store.InsertSlicesAsync(slices, CancellationToken.None);

        var barrier = new Barrier(2);

        Task<IReadOnlyList<ClaimedSlice>> RaceAsync(string owner) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await StoreFor(connectionString).ClaimAsync(owner, Lease, MaxAttempts, slices.Count, CancellationToken.None);
        });

        var results = await Task.WhenAll(RaceAsync("owner-a"), RaceAsync("owner-b"));
        var claimedIds = results.SelectMany(claim => claim.Select(slice => slice.RequestId)).ToArray();

        // No request may be handed to two owners, and the two claims together must not exceed the
        // queue — SKIP LOCKED passes over contended rows rather than blocking and re-reading them.
        Assert.Equal(claimedIds.Length, claimedIds.Distinct().Count());
        Assert.True(claimedIds.Length <= slices.Count);
        Assert.Equal(claimedIds.Length, (int)await CountRequestsAsync(connectionString, "state = 'inflight'"));
    }

    [Fact]
    public async Task Claiming_increments_attempts_before_the_request_leaves_so_a_crash_still_burns_one()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None);

        Assert.Single(claimed);
        Assert.Equal(1, claimed[0].Attempts);
        Assert.Equal(1L, await CountRequestsAsync(connectionString, "state = 'inflight' AND attempts = 1"));
    }

    [Fact]
    public async Task A_slice_at_the_attempt_cap_is_never_claimed_again()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job! with { Kind = BackfillJobKinds.TopUp }, SpxConId, DateTimeOffset.UtcNow.AddHours(-1))],
            CancellationToken.None);

        await ExecuteAsync(
            connectionString,
            $"UPDATE research.backfill_requests SET state = 'failed', attempts = {MaxAttempts}, " +
            "completed_at = now() - interval '1 day'");

        Assert.Empty(await store.ClaimAsync("owner-a", Lease, MaxAttempts, 10, CancellationToken.None));
    }

    [Fact]
    public async Task A_now_anchored_row_is_never_claimed_but_is_reported_rather_than_hidden()
    {
        // The coordinator cannot rebuild a reproducible request from a NULL "whenever this runs"
        // anchor, and executing one would resurrect exactly the top-up collision. Skipping it
        // silently would be the same absent-row failure in miniature, so the status report counts it.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        await ExecuteAsync(
            connectionString,
            "INSERT INTO research.backfill_requests (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth) " +
            $"VALUES ({job!.JobId}, {SpxConId}, NULL, '1 D', 'TRADES', '1 min', true)");

        Assert.Empty(await store.ClaimAsync("owner-a", Lease, MaxAttempts, 10, CancellationToken.None));

        var status = (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single();

        Assert.Equal(1, status.NowAnchoredCount);
        Assert.Equal(1, status.TotalSlices);
    }

    // ---- crash recovery ---------------------------------------------------------------------------

    [Fact]
    public async Task A_slice_abandoned_inflight_by_a_crashed_coordinator_is_reclaimed()
    {
        // Migration 004 had no owner and no expiry, so a coordinator that died between claiming a
        // slice and writing its outcome left the row 'inflight' forever: no query could tell it apart
        // from a request still legitimately in the air, and the hole it left was invisible.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = await store.ClaimAsync("doomed-instance", Lease, MaxAttempts, 1, CancellationToken.None);
        Assert.Single(claimed);

        // The crash: the process is gone, so nothing writes an outcome and the lease simply lapses.
        await ExecuteAsync(
            connectionString,
            "UPDATE research.backfill_requests SET lease_expires_at = now() - interval '1 minute' " +
            "WHERE state = 'inflight'");

        Assert.Equal(1, await store.ReclaimExpiredAsync(CancellationToken.None));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var reclaimed = new NpgsqlCommand(
            "SELECT state, claimed_by, lease_expires_at, attempts, error_message " +
            "FROM research.backfill_requests WHERE request_id = $1",
            connection);
        reclaimed.Parameters.AddWithValue(claimed[0].RequestId);

        await using var reader = await reclaimed.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("failed", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(1, reader.GetInt32(3)); // the attempt is KEPT, so a slice that kills its coordinator cannot loop forever
        Assert.Contains("doomed-instance", reader.GetString(4));
    }

    [Fact]
    public async Task A_reclaimed_slice_becomes_claimable_again_by_a_live_coordinator()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            [BackfillPlanner.PlanTopUp(job! with { Kind = BackfillJobKinds.TopUp }, SpxConId, DateTimeOffset.UtcNow.AddHours(-1))],
            CancellationToken.None);

        var claimed = await store.ClaimAsync("doomed-instance", Lease, MaxAttempts, 1, CancellationToken.None);
        await ExecuteAsync(
            connectionString, "UPDATE research.backfill_requests SET lease_expires_at = now() - interval '1 minute'");
        await store.ReclaimExpiredAsync(CancellationToken.None);

        // Reclaimed rows land on the ordinary retry-backoff path rather than in a second, parallel
        // retry lane; age it past the first backoff step.
        await ExecuteAsync(
            connectionString, "UPDATE research.backfill_requests SET completed_at = now() - interval '1 hour'");

        var reclaimedBy = await store.ClaimAsync("live-instance", Lease, MaxAttempts, 1, CancellationToken.None);

        Assert.Single(reclaimedBy);
        Assert.Equal(claimed[0].RequestId, reclaimedBy[0].RequestId);
        Assert.Equal(2, reclaimedBy[0].Attempts);
    }

    [Fact]
    public async Task A_live_lease_is_not_reclaimed()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        await store.ClaimAsync("live-instance", Lease, MaxAttempts, 1, CancellationToken.None);

        Assert.Equal(0, await store.ReclaimExpiredAsync(CancellationToken.None));
        Assert.Equal(1L, await CountRequestsAsync(connectionString, "state = 'inflight'"));
    }

    [Fact]
    public async Task The_schema_refuses_an_inflight_row_with_no_lease()
    {
        // The engine, not the code path that happens to write the row, is what guarantees every
        // inflight row is reclaimable. There is no way to create an invisible one, including by hand.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        var violation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            "INSERT INTO research.backfill_requests " +
            "  (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth, state) " +
            $"VALUES ({job!.JobId}, {SpxConId}, '2024-01-05T00:00:00Z', '1 D', 'TRADES', '1 min', true, 'inflight')"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
        Assert.Contains("inflight_has_lease", violation.Message);
    }

    // ---- outcomes ---------------------------------------------------------------------------------

    [Fact]
    public async Task Landing_bars_and_marking_the_slice_succeeded_happen_in_one_transaction()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();
        var first = new DateTimeOffset(2024, 1, 10, 14, 30, 0, TimeSpan.Zero);

        var bars = Enumerable.Range(0, 3)
            .Select(i => new HistoricalBarDto(first.AddMinutes(i), null, 100m, 101m, 99m, 100.5m, -1m, 12, 100.2m))
            .ToArray();

        Assert.True(await store.LandBarsAsync(claimed, "owner-a", 1, bars, "backfill", CancellationToken.None));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var check = new NpgsqlCommand(
            "SELECT state, bars_returned, first_bar_utc, last_bar_utc, claimed_by, lease_expires_at " +
            "FROM research.backfill_requests WHERE request_id = $1",
            connection);
        check.Parameters.AddWithValue(claimed.RequestId);

        await using (var reader = await check.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("succeeded", reader.GetString(0));
            Assert.Equal(3, reader.GetInt32(1));
            Assert.Equal(first, reader.GetFieldValue<DateTimeOffset>(2));
            Assert.Equal(first.AddMinutes(2), reader.GetFieldValue<DateTimeOffset>(3));
            Assert.True(reader.IsDBNull(4));
            Assert.True(reader.IsDBNull(5));
        }

        // TWS reports -1 volume for an index; storing that verbatim would corrupt every later
        // aggregate, so migration 004 reserves NULL for it.
        await using var barCheck = new NpgsqlCommand(
            "SELECT count(*), count(volume), count(request_id) FROM research.bars WHERE con_id = $1", connection);
        barCheck.Parameters.AddWithValue(SpxConId);

        await using var barReader = await barCheck.ExecuteReaderAsync();
        Assert.True(await barReader.ReadAsync());
        Assert.Equal(3L, barReader.GetInt64(0));
        Assert.Equal(0L, barReader.GetInt64(1));
        Assert.Equal(3L, barReader.GetInt64(2));
    }

    [Fact]
    public async Task A_daily_bar_lands_with_its_trading_date_and_a_midnight_instant()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(name: "vix-daily", barSize: "1 day"), 13455763, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, 13455763, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();
        var tradingDate = new DateOnly(2023, 6, 15);

        Assert.True(await store.LandBarsAsync(
            claimed, "owner-a", 4,
            [new HistoricalBarDto(null, tradingDate, 14m, 15m, 13m, 14.5m, -1m, -1, -1m)],
            "backfill", CancellationToken.None));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT ts_utc, trading_date, bar_count, wap FROM research.bars WHERE con_id = 13455763", connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(new DateTimeOffset(2023, 6, 15, 0, 0, 0, TimeSpan.Zero), reader.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(tradingDate, reader.GetFieldValue<DateOnly>(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    public async Task A_coordinator_that_lost_its_lease_writes_neither_bars_nor_a_checkpoint()
    {
        // The lease expired while the request was genuinely in flight and a reaper took the row
        // back. Force-writing the outcome would leave research.bars rows whose lineage points at a
        // request row another owner is about to re-run and ultimately mark failed.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();

        await ExecuteAsync(
            connectionString, "UPDATE research.backfill_requests SET lease_expires_at = now() - interval '1 minute'");
        await store.ReclaimExpiredAsync(CancellationToken.None);

        var landed = await store.LandBarsAsync(
            claimed, "owner-a", 1,
            [new HistoricalBarDto(new DateTimeOffset(2024, 1, 10, 14, 30, 0, TimeSpan.Zero), null, 1m, 1m, 1m, 1m, 1m, 1, 1m)],
            "backfill", CancellationToken.None);

        Assert.False(landed);
        Assert.Equal(0L, await CountRequestsAsync(connectionString, "state = 'succeeded'"));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var bars = new NpgsqlCommand("SELECT count(*) FROM research.bars", connection);
        Assert.Equal(0L, (long)(await bars.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_pacing_release_refunds_the_attempt_so_a_busy_window_cannot_exhaust_the_queue()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();

        Assert.True(await store.ReleaseAsync(claimed.RequestId, "owner-a", CancellationToken.None));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT state, attempts, claimed_by FROM research.backfill_requests WHERE request_id = $1", connection);
        command.Parameters.AddWithValue(claimed.RequestId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("pending", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task A_permanent_error_retires_the_slice_and_leaves_the_job_running()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var claimed = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();

        Assert.True(await store.MarkOutcomeAsync(
            claimed.RequestId, "owner-a", BackfillRequestState.Permanent, 10339,
            "CONTFUT rejects a past endDateTime", CancellationToken.None));

        Assert.Equal(1L, await CountRequestsAsync(connectionString, "state = 'permanent' AND error_code = 10339"));

        var status = (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single();

        Assert.Equal(1, status.PermanentCount);
        Assert.NotEqual("failed", status.Status);
        Assert.False(await store.IsJobSettledAsync(job!.JobId, MaxAttempts, CancellationToken.None));
    }

    [Fact]
    public async Task A_data_bearing_neighbour_on_both_sides_makes_an_empty_slice_suspicious()
    {
        // TWS raises error 162 both for "no data" and for some pacing violations, distinguished only
        // by message text upstream. A slice bracketed by slices of the same contract that DID return
        // data is cheap corroboration that the verdict deserves one more look before it retires the
        // range permanently.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var middle = new DateTimeOffset(2024, 1, 6, 0, 0, 0, TimeSpan.Zero);

        await ExecuteAsync(
            connectionString,
            "UPDATE research.backfill_requests SET state = 'succeeded', bars_returned = 390 " +
            "WHERE end_time_utc IN ('2024-01-05T00:00:00Z', '2024-01-07T00:00:00Z')");

        Assert.True(await store.HasDataBearingNeighboursAsync(
            job!.JobId, SpxConId, middle, TimeSpan.FromDays(3), CancellationToken.None));

        // With only one side filled it is not suspicious — the edge of a filled region is exactly
        // where a legitimately empty slice lives.
        await ExecuteAsync(
            connectionString,
            "UPDATE research.backfill_requests SET state = 'pending', bars_returned = NULL " +
            "WHERE end_time_utc = '2024-01-05T00:00:00Z'");

        Assert.False(await store.HasDataBearingNeighboursAsync(
            job.JobId, SpxConId, middle, TimeSpan.FromDays(3), CancellationToken.None));
    }

    // ---- status: absence must not read as health -------------------------------------------------

    [Fact]
    public async Task A_job_with_no_request_rows_is_reported_at_zero_percent_rather_than_omitted()
    {
        // Three of the eight defects the Phase 1 review confirmed shared one root cause: a query
        // that cannot emit a row for the absent case, so absence renders as health. A job whose
        // planning never ran must be visible and at 0%, not missing.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        var status = await store.GetStatusAsync(MaxAttempts, CancellationToken.None);
        var reported = Assert.Single(status);

        Assert.Equal(job!.JobId, reported.JobId);
        Assert.Equal(0, reported.TotalSlices);
        Assert.Equal(0d, reported.PercentComplete);
        Assert.Null(reported.LowWaterMarkUtc);
        Assert.False(await store.IsJobSettledAsync(job.JobId, MaxAttempts, CancellationToken.None));
    }

    [Fact]
    public async Task Progress_counts_every_state_and_completes_only_when_nothing_is_outstanding()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        var slices = BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!));
        await store.InsertSlicesAsync(slices, CancellationToken.None);

        Assert.False(await store.IsJobSettledAsync(job!.JobId, MaxAttempts, CancellationToken.None));

        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'empty', bars_landed = 0");
        await ExecuteAsync(
            connectionString,
            "UPDATE research.backfill_requests SET state = 'succeeded', bars_returned = 390, bars_landed = 390, " +
            "first_bar_utc = end_time_utc - interval '1 day', last_bar_utc = end_time_utc " +
            "WHERE end_time_utc = (SELECT max(end_time_utc) FROM research.backfill_requests)");

        var status = (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single();

        Assert.Equal(slices.Count, status.TotalSlices);
        Assert.Equal(1, status.SucceededCount);
        Assert.Equal(slices.Count - 1, status.EmptyCount);
        Assert.Equal(390L, status.BarsLanded);
        Assert.Equal(1d, status.PercentComplete);
        Assert.True(await store.IsJobSettledAsync(job.JobId, MaxAttempts, CancellationToken.None));

        // An exhausted retry is settled too — it is not coming back — but it is counted separately
        // so a job that "completed" with dead slices in it cannot pass for a clean one.
        await ExecuteAsync(
            connectionString,
            $"UPDATE research.backfill_requests SET state = 'failed', attempts = {MaxAttempts} " +
            "WHERE end_time_utc = (SELECT min(end_time_utc) FROM research.backfill_requests)");

        var withExhausted = (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single();

        Assert.Equal(1, withExhausted.ExhaustedCount);
        Assert.Equal(0, withExhausted.RetryableCount);
        Assert.True(await store.IsJobSettledAsync(job.JobId, MaxAttempts, CancellationToken.None));

        // ...and "counted separately" is not enough on its own: the two figures an operator actually
        // reads are the status and the progress bar, and BOTH used to say the job was clean. A slice
        // nothing will ever fetch again cannot count toward completion.
        Assert.True(withExhausted.PercentComplete < 1d);
    }

    // ---- complete-with-holes must never render as complete ---------------------------------------

    [Fact]
    public async Task A_settled_job_with_exhausted_slices_completes_WITH_GAPS_rather_than_clean()
    {
        // The defect this pins: exhausted-is-settled is deliberate, but it fed a one-way "settled ⇒
        // complete" transition, so a job whose newest slices died during a gateway outage reported
        // status 'complete' at 100%. Both signals said finished; neither said "with holes in it".
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'succeeded', bars_landed = 390");
        await ExecuteAsync(
            connectionString,
            $"UPDATE research.backfill_requests SET state = 'failed', attempts = {MaxAttempts}, bars_landed = NULL " +
            "WHERE end_time_utc = (SELECT min(end_time_utc) FROM research.backfill_requests)");

        Assert.Equal(
            "complete_with_gaps",
            await store.RefreshJobStatusAsync(job!.JobId, MaxAttempts, planningComplete: true, CancellationToken.None));

        var status = (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single();

        Assert.Equal("complete_with_gaps", status.Status);
        Assert.Equal(1, status.ExhaustedCount);
        Assert.True(status.PercentComplete < 1d);

        // A rerun of the same derivation is a no-op, not a status flap.
        Assert.Null(await store.RefreshJobStatusAsync(job.JobId, MaxAttempts, planningComplete: true, CancellationToken.None));
    }

    [Fact]
    public async Task Raising_the_attempt_cap_is_a_working_way_back_into_a_complete_with_gaps_job()
    {
        // The operator path back. Under the old design the ROWS became claimable when the cap went
        // up while the JOB stayed outside ClaimAsync's status filter, so the natural remedy did
        // nothing at all and re-planning could not help either (an unchanged job re-derives
        // identical slices and inserts zero rows).
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        await ExecuteAsync(
            connectionString,
            $"UPDATE research.backfill_requests SET state = 'failed', attempts = {MaxAttempts}, " +
            "completed_at = now() - interval '1 day'");

        await store.RefreshJobStatusAsync(job!.JobId, MaxAttempts, planningComplete: true, CancellationToken.None);
        Assert.Equal("complete_with_gaps", (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single().Status);

        // Nothing is claimable at the current cap...
        Assert.Empty(await store.ClaimAsync("owner-a", Lease, MaxAttempts, 10, CancellationToken.None));

        // ...and raising it reopens the job, not just its rows.
        var raised = MaxAttempts + 3;
        Assert.NotEmpty(await store.ClaimAsync("owner-a", Lease, raised, 1, CancellationToken.None));
        Assert.Equal(
            "running", await store.RefreshJobStatusAsync(job.JobId, raised, planningComplete: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_job_with_nothing_planned_yet_is_never_moved_off_pending_by_a_status_refresh()
    {
        // The absent-row rule applied to the status derivation itself: zero request rows means
        // nothing has planned this job, and calling that 'running' (or worse, 'complete') would
        // claim work exists that nothing has enqueued.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        Assert.Null(await store.RefreshJobStatusAsync(job!.JobId, MaxAttempts, planningComplete: true, CancellationToken.None));
        Assert.Equal("pending", (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single().Status);
    }

    [Fact]
    public async Task A_paused_job_is_never_resurrected_by_a_status_refresh()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);
        await ExecuteAsync(connectionString, "UPDATE research.backfill_jobs SET status = 'paused'");

        Assert.Null(await store.RefreshJobStatusAsync(job!.JobId, MaxAttempts, planningComplete: true, CancellationToken.None));
        Assert.Equal("paused", (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single().Status);
    }

    [Fact]
    public async Task Partial_planning_holds_a_job_open_even_when_every_planned_slice_is_settled()
    {
        // The ES-walker shape, pinned at the store level: a caller whose planning was partial cannot
        // have that fact inferred from the checkpoint counts, because the contracts it skipped wrote
        // no rows to count. It has to say so, and saying so has to win.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);

        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);
        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'succeeded', bars_landed = 1");

        Assert.Equal(
            "running",
            await store.RefreshJobStatusAsync(job!.JobId, MaxAttempts, planningComplete: false, CancellationToken.None));

        Assert.Equal(
            "complete",
            await store.RefreshJobStatusAsync(job.JobId, MaxAttempts, planningComplete: true, CancellationToken.None));
    }

    // ---- the bars figure -------------------------------------------------------------------------

    [Fact]
    public async Task The_reported_bars_figure_counts_rows_persisted_not_bars_returned()
    {
        // Overlap is designed into this pipeline in three separate places, so summing what TWS
        // returned is not an approximation of what landed — it exceeds it by an amount no reader can
        // infer. A job that landed a quarter of its bars could report a full-looking total.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);
        var job = await store.EnsureJobAsync(HistoricalDefinition(), SpxConId, CancellationToken.None);
        await store.InsertSlicesAsync(
            BackfillPlanner.PlanHistorical(job!, SpxConId, null, CadenceOf(job!)), CancellationToken.None);

        var first = new DateTimeOffset(2024, 1, 10, 14, 30, 0, TimeSpan.Zero);

        HistoricalBarDto[] BarsFrom(DateTimeOffset start, int count) =>
            [.. Enumerable.Range(0, count).Select(i =>
                new HistoricalBarDto(start.AddMinutes(i), null, 100m, 101m, 99m, 100.5m, -1m, 12, 100.2m))];

        var one = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();
        Assert.True(await store.LandBarsAsync(one, "owner-a", 1, BarsFrom(first, 10), "backfill", CancellationToken.None));

        // The next slice's window overlaps the first by six bars — exactly what the leading slice,
        // the 4x top-up window, and the daily forward re-request all do on purpose.
        var two = (await store.ClaimAsync("owner-a", Lease, MaxAttempts, 1, CancellationToken.None)).Single();
        Assert.True(await store.LandBarsAsync(
            two, "owner-a", 1, BarsFrom(first.AddMinutes(4), 10), "backfill", CancellationToken.None));

        var status = (await store.GetStatusAsync(MaxAttempts, CancellationToken.None)).Single();

        Assert.Equal(20L, status.BarsReturned);
        Assert.Equal(14L, status.BarsLanded);
        Assert.Equal(status.BarsLanded, await CountBarsAsync(connectionString));
    }

    private static async Task<long> CountBarsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM research.bars", connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
