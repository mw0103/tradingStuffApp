using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// The claim only a real database can settle: seeding the ES job's per-contract request rows is
/// resumable, and an identical rerun of the walk adds zero new rows.
/// </summary>
/// <remarks>
/// Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection string — see
/// <see cref="BackfillCoordinatorPostgresTests"/> for the same convention. Run against a dedicated
/// container so a concurrent suite cannot perturb these tests:
/// <c>docker run -d --name pg-2e -e POSTGRES_PASSWORD=postgres -p 5453:5432 postgres:17</c>.
/// <para>
/// No TWS socket and no gateway HTTP call is ever exercised here: every fixture contract's head
/// timestamp is pre-seeded straight into <c>research.capability_probes</c> via
/// <see cref="EsContractWalker.SeedAsync"/>'s cache-first lookup, exactly as a prior scan would have
/// left it, so the gateway client constructed below is never actually invoked.
/// </para>
/// <para>
/// Asserted on <c>research.backfill_requests</c> deliberately, not <c>research.bars</c> — the same
/// reasoning <see cref="BackfillCoordinatorPostgresTests.Replanning_an_unchanged_job_adds_zero_backfill_request_rows"/>
/// documents: the bars table's primary key would silently absorb a duplicated request and hide
/// exactly the defect this test exists to catch.
/// </para>
/// </remarks>
[Trait("Category", "RequiresPostgres")]
public sealed class EsContractWalkerPostgresTests
{
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

    // Never called in these tests (every head timestamp is pre-cached), so a bare HttpClient with no
    // base address is fine — SeedAsync's cache hit short-circuits before any request would be sent.
    private static IbkrGatewayClient UnusedGateway() => new(new HttpClient(), NullLogger<IbkrGatewayClient>.Instance);

    private static EsContractWalker WalkerFor(string connectionString) => new(
        StoreFor(connectionString),
        UnusedGateway(),
        Options.Create(new BackfillOptions { Enabled = true, MaxAttempts = 5, HeadTimestampMaxAgeDays = 30 }),
        NullLogger<EsContractWalker>.Instance);

    private static async Task<long> CountRequestsAsync(string connectionString, string? where = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.backfill_requests" + (where is null ? string.Empty : $" WHERE {where}"),
            connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> JobStatusAsync(string connectionString, long jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT status FROM research.backfill_jobs WHERE job_id = $1", connection);
        command.Parameters.AddWithValue(jobId);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static BackfillJobDefinition EsDefinition() => new(
        EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
        UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60);

    /// <summary>Pre-warms the head-timestamp cache for a contract exactly as a prior scan would have left it.</summary>
    private static Task SeedHeadAsync(BackfillStore store, int conId, DateTimeOffset head) =>
        store.RecordHeadTimestampAsync(
            $"head_timestamp:{EsContractWalker.JobName}:{conId}", conId, head, "test fixture", CancellationToken.None);

    private static readonly EsContractCandidate ExpiredContract = new(495512001, new DateOnly(2022, 12, 16));
    private static readonly EsContractCandidate FrontMonthContract = new(495512563, new DateOnly(2026, 9, 18));

    // ---- seeding + rerun idempotency --------------------------------------------------------------

    [Fact]
    public async Task Seeding_the_same_contracts_twice_adds_zero_backfill_request_rows()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedHeadAsync(store, FrontMonthContract.ConId, new DateTimeOffset(2023, 8, 20, 0, 0, 0, TimeSpan.Zero));

        var contracts = new[] { ExpiredContract, FrontMonthContract };

        var walker = WalkerFor(connectionString);
        var job = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
                UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60),
            conId: null, CancellationToken.None);
        Assert.NotNull(job);

        var first = await walker.SeedAsync(job!, contracts, CancellationToken.None);
        Assert.True(first.SlicesInserted > 0);

        var afterFirst = await CountRequestsAsync(connectionString);
        Assert.Equal(first.SlicesInserted, afterFirst);

        // A full second pass with a fresh walker instance, the identical fixture contract list, and
        // the job re-ensured (not re-created) — everything a real restart would actually do.
        var rediscoveredJob = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
                UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60),
            conId: null, CancellationToken.None);

        var second = await WalkerFor(connectionString).SeedAsync(rediscoveredJob!, contracts, CancellationToken.None);

        Assert.Equal(0, second.SlicesInserted);
        Assert.Equal(afterFirst, await CountRequestsAsync(connectionString));
    }

    [Fact]
    public async Task Different_contracts_land_under_their_own_conid_with_no_cross_contamination()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedHeadAsync(store, FrontMonthContract.ConId, new DateTimeOffset(2023, 8, 20, 0, 0, 0, TimeSpan.Zero));

        var job = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
                UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60),
            conId: null, CancellationToken.None);

        await WalkerFor(connectionString).SeedAsync(job!, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        var expiredRows = await CountRequestsAsync(connectionString, $"con_id = {ExpiredContract.ConId}");
        var frontRows = await CountRequestsAsync(connectionString, $"con_id = {FrontMonthContract.ConId}");

        Assert.True(expiredRows > 0);
        Assert.True(frontRows > 0);
        Assert.Equal(expiredRows + frontRows, await CountRequestsAsync(connectionString));

        // The expired contract's newest row must not reach past its own last trading day, even
        // though the job's own target_to reaches all the way to (effectively) today.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT max(end_time_utc) FROM research.backfill_requests WHERE con_id = {ExpiredContract.ConId}", connection);
        var newestExpiredRow = (DateTime)(await command.ExecuteScalarAsync())!;

        Assert.True(
            newestExpiredRow <= ExpiredContract.LastTradeDateOrContractMonth.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    [Fact]
    public async Task Seeding_moves_the_job_from_pending_to_running()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var job = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
                UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60),
            conId: null, CancellationToken.None);

        Assert.Equal("pending", await JobStatusAsync(connectionString, job!.JobId));

        await WalkerFor(connectionString).SeedAsync(job, [ExpiredContract], CancellationToken.None);

        Assert.Equal("running", await JobStatusAsync(connectionString, job.JobId));
    }

    [Fact]
    public async Task A_contract_with_no_resolvable_head_is_skipped_without_failing_the_others()
    {
        // A quarter CME lists years ahead of its own expiry, with no head timestamp cached and no
        // gateway reachable in this test, must not block the other contracts in the same scan.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));
        // FrontMonthContract's head is deliberately left un-cached; since the unused gateway can
        // never answer a live probe, ResolveContractHeadAsync's default branch returns null for it.

        var job = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
                UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60),
            conId: null, CancellationToken.None);

        var result = await WalkerFor(connectionString).SeedAsync(
            job!, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        Assert.True(result.SlicesInserted > 0);
        Assert.Equal(0L, await CountRequestsAsync(connectionString, $"con_id = {FrontMonthContract.ConId}"));
        Assert.True(await CountRequestsAsync(connectionString, $"con_id = {ExpiredContract.ConId}") > 0);
    }

    // ---- completion: "some contracts planned" is not "the job is done" ----------------------------

    [Fact]
    public async Task A_scan_that_could_not_plan_every_contract_never_marks_the_job_complete()
    {
        // The defect, in the shape it actually shipped: completion was derived from
        // IsJobSettledAsync, which counts rows in research.backfill_requests — and a contract whose
        // head could not be resolved writes NO rows, so it lowers no count and is invisible to that
        // query. `total > 0` proves ONE contract was planned, never that all 29 were. With
        // es-1min-trades holding the lowest priority in the system, a handful of contracts planning
        // while the rest were paced away was enough to report the job complete at 100% with most of
        // the intended ES history never requested.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));
        // FrontMonthContract's head is deliberately left un-cached and the gateway is unreachable,
        // so it is skipped exactly the way a paced head probe skips one.

        var job = await store.EnsureJobAsync(EsDefinition(), conId: null, CancellationToken.None);

        var result = await WalkerFor(connectionString).SeedAsync(
            job!, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        Assert.Equal(1, result.UnplannedContractCount);
        Assert.False(result.PlanningComplete);

        // Settle every row that WAS planned — the state the old code read as "nothing outstanding".
        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'succeeded', bars_landed = 1");
        Assert.True(await store.IsJobSettledAsync(job!.JobId, 5, CancellationToken.None));

        // A second scan in the same condition must still refuse to call it finished.
        var second = await WalkerFor(connectionString).SeedAsync(
            job, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        Assert.False(second.PlanningComplete);
        Assert.Equal("running", await JobStatusAsync(connectionString, job.JobId));

        // ...and once the missing contract's head resolves, the job completes on its own.
        await SeedHeadAsync(store, FrontMonthContract.ConId, new DateTimeOffset(2023, 8, 20, 0, 0, 0, TimeSpan.Zero));
        var third = await WalkerFor(connectionString).SeedAsync(
            job, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        Assert.True(third.PlanningComplete);
        Assert.Equal("running", await JobStatusAsync(connectionString, job.JobId)); // its new slices are pending

        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'succeeded', bars_landed = 1");
        await WalkerFor(connectionString).SeedAsync(job, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        Assert.Equal("complete", await JobStatusAsync(connectionString, job.JobId));
    }

    [Fact]
    public async Task A_contract_missing_from_this_scans_family_listing_holds_the_job_open()
    {
        // The walker's expectation cannot be only "the list TWS just handed me": a short listing
        // would otherwise shrink the expected set to match itself and declare victory. Contracts
        // already carrying request rows are remembered, so an incomplete enumeration is visible.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await SeedHeadAsync(store, FrontMonthContract.ConId, new DateTimeOffset(2023, 8, 20, 0, 0, 0, TimeSpan.Zero));

        var job = await store.EnsureJobAsync(EsDefinition(), conId: null, CancellationToken.None);

        var full = await WalkerFor(connectionString).SeedAsync(
            job!, [ExpiredContract, FrontMonthContract], CancellationToken.None);
        Assert.True(full.PlanningComplete);

        await ExecuteAsync(connectionString, "UPDATE research.backfill_requests SET state = 'succeeded', bars_landed = 1");

        // The next scan's enumeration comes back short of one contract this walker already knows.
        var partial = await WalkerFor(connectionString).SeedAsync(job!, [ExpiredContract], CancellationToken.None);

        Assert.Equal(1, partial.ForgottenContractCount);
        Assert.False(partial.PlanningComplete);
        Assert.Equal("running", await JobStatusAsync(connectionString, job!.JobId));
    }

    [Fact]
    public async Task Discovering_a_new_contract_later_only_adds_that_contracts_rows()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await SeedHeadAsync(store, ExpiredContract.ConId, new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var job = await store.EnsureJobAsync(
            new BackfillJobDefinition(
                EsContractWalker.JobName, BackfillJobKinds.Historical, InstrumentId: 6, "ES", "TRADES", "1 min",
                UseRth: false, new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), TargetTo: null, Priority: 60),
            conId: null, CancellationToken.None);

        await WalkerFor(connectionString).SeedAsync(job!, [ExpiredContract], CancellationToken.None);
        var afterFirstScan = await CountRequestsAsync(connectionString);

        // A later scan discovers a newly-listed quarter. Only its rows should be new.
        await SeedHeadAsync(store, FrontMonthContract.ConId, new DateTimeOffset(2023, 8, 20, 0, 0, 0, TimeSpan.Zero));
        var second = await WalkerFor(connectionString).SeedAsync(
            job!, [ExpiredContract, FrontMonthContract], CancellationToken.None);

        Assert.True(second.SlicesInserted > 0);
        Assert.Equal(afterFirstScan + second.SlicesInserted, await CountRequestsAsync(connectionString));
        Assert.Equal(second.SlicesInserted, await CountRequestsAsync(connectionString, $"con_id = {FrontMonthContract.ConId}"));
    }
}
