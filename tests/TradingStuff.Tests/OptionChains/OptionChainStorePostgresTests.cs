using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.OptionChains;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests.OptionChains;

/// <summary>
/// The claims Phase 9's ingestion package makes that only a real database can settle: planning is
/// idempotent, landing quotes is idempotent even across two DIFFERENT requests that happen to cover
/// the same contract/day, and a 'tick' job is genuinely never treated as active by the automatic
/// coordinator rather than merely documented as such.
/// </summary>
/// <remarks>
/// Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection string — see
/// <see cref="BackfillCoordinatorPostgresTests"/> for the identical convention this file follows.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class OptionChainStorePostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<string> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return connectionString;
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    private static OptionChainStore StoreFor(string connectionString) =>
        new(ConfigurationFor(connectionString), NullLogger<OptionChainStore>.Instance);

    private static OptionChainQuoteRow SampleRow(DateOnly expiration, DateTimeOffset observedAt, decimal strike = 4500m) =>
        new(
            Underlying: "SPX",
            TradingClass: "SPXW",
            Expiration: expiration,
            Strike: strike,
            Right: 'C',
            ObservedAt: observedAt,
            TradingDate: DateOnly.FromDateTime(observedAt.UtcDateTime),
            Bid: 12.30m,
            Ask: 12.80m,
            BidSize: 25m,
            AskSize: 40m,
            BidExchange: 5,
            AskExchange: 5);

    private static async Task<long> CountQuotesAsync(string connectionString, DateOnly expiration)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.option_chain_quotes WHERE expiration = $1", connection);
        command.Parameters.AddWithValue(expiration);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountRequestsAsync(string connectionString, long jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.option_chain_requests WHERE job_id = $1", connection);
        command.Parameters.AddWithValue(jobId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Scope item 2's headline requirement: "Identical rerun must add ZERO rows." Planning the same
    /// job with the same expiration list twice must insert the expirations exactly once.
    /// </summary>
    [Fact]
    public async Task Replanning_the_same_expirations_adds_zero_new_request_rows()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var job = await store.EnsureJobAsync(
            "test-spxw-1m", "SPX", "SPXW", new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31),
            OptionChainIntervals.OneMinute, priority: 0, CancellationToken.None);

        Assert.NotNull(job);

        var expirations = new List<DateOnly> { new(2024, 1, 5), new(2024, 1, 12), new(2024, 2, 16) };

        var firstPass = await store.PlanExpirationsAsync(job!.JobId, expirations, CancellationToken.None);
        Assert.Equal(3, firstPass);
        Assert.Equal(3, await CountRequestsAsync(connectionString, job.JobId));

        var secondPass = await store.PlanExpirationsAsync(job.JobId, expirations, CancellationToken.None);
        Assert.Equal(0, secondPass);
        Assert.Equal(3, await CountRequestsAsync(connectionString, job.JobId));
    }

    /// <summary>
    /// The idempotency guarantee measured at the granularity that actually matters: TWO DIFFERENT
    /// requests (different jobs, different request_ids — a realistic shape, since an operator can
    /// declare two overlapping ingestion jobs the same way overlapping backfill windows already
    /// happen) that land the identical canonical quote must not duplicate it. This is exactly what
    /// the primary key deliberately excludes request-level identity from (migration 019's header
    /// comment, docs/DECISIONS.md §15) — request_id is provenance, never identity.
    /// </summary>
    [Fact]
    public async Task Landing_the_same_quote_from_two_different_requests_adds_zero_duplicate_rows()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var expiration = new DateOnly(2024, 1, 19);
        var observedAt = new DateTimeOffset(2024, 1, 5, 20, 45, 0, TimeSpan.Zero);

        var jobA = await store.EnsureJobAsync(
            "test-spxw-a", "SPX", "SPXW", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            OptionChainIntervals.OneMinute, 0, CancellationToken.None);
        var jobB = await store.EnsureJobAsync(
            "test-spxw-b", "SPX", "SPXW", new DateOnly(2023, 12, 15), new DateOnly(2024, 2, 15),
            OptionChainIntervals.OneMinute, 0, CancellationToken.None);

        Assert.NotNull(jobA);
        Assert.NotNull(jobB);

        await store.PlanExpirationsAsync(jobA!.JobId, [expiration], CancellationToken.None);
        await store.PlanExpirationsAsync(jobB!.JobId, [expiration], CancellationToken.None);

        var claimedA = (await store.ClaimAsync("owner-a", TimeSpan.FromMinutes(5), 5, 1, CancellationToken.None)).Single();
        var rows = new[] { SampleRow(expiration, observedAt), SampleRow(expiration, observedAt, strike: 4600m) };

        var landedFirst = await store.LandQuotesAsync(claimedA, "owner-a", rows, "SPXW", "1m", CancellationToken.None);
        Assert.True(landedFirst);
        Assert.Equal(2, await CountQuotesAsync(connectionString, expiration));

        var claimedB = (await store.ClaimAsync("owner-b", TimeSpan.FromMinutes(5), 5, 1, CancellationToken.None)).Single();
        Assert.NotEqual(claimedA.RequestId, claimedB.RequestId);

        // The identical canonical quotes, landed under a DIFFERENT request_id.
        var landedSecond = await store.LandQuotesAsync(claimedB, "owner-b", rows, "SPXW", "1m", CancellationToken.None);
        Assert.True(landedSecond);

        // Still 2, not 4: the primary key (underlying, trading_class, expiration, strike,
        // option_right, observed_at) collided, exactly as designed.
        Assert.Equal(2, await CountQuotesAsync(connectionString, expiration));
    }

    /// <summary>
    /// The actual enforcement mechanism for "tick is never planned automatically" (see
    /// OptionChainStore.EnsureJobAsync's remarks): a tick job is created already 'paused', and
    /// GetActiveJobsAsync — the ONLY set OptionChainCoordinator.PlanAsync iterates — excludes it.
    /// Tested at this level rather than by asserting on a log message, per docs/LESSONS.md #7
    /// ("verify the mechanism, not the symptom").
    /// </summary>
    [Fact]
    public async Task A_tick_job_is_created_paused_and_never_appears_as_active()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        var oneMinuteJob = await store.EnsureJobAsync(
            "test-1m-job", "SPX", "SPXW", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            OptionChainIntervals.OneMinute, 0, CancellationToken.None);
        var tickJob = await store.EnsureJobAsync(
            "test-tick-job", "SPX", "SPXW", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31),
            OptionChainIntervals.Tick, 0, CancellationToken.None);

        Assert.NotNull(oneMinuteJob);
        Assert.NotNull(tickJob);
        Assert.Equal("pending", oneMinuteJob!.Status);
        Assert.Equal("paused", tickJob!.Status);

        var active = await store.GetActiveJobsAsync(CancellationToken.None);

        Assert.Contains(active, j => j.JobId == oneMinuteJob.JobId);
        Assert.DoesNotContain(active, j => j.JobId == tickJob.JobId);
    }

    [Fact]
    public async Task RecordCapabilityProbeAsync_persists_a_row_research_can_read_back()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = StoreFor(connectionString);

        await store.RecordCapabilityProbeAsync(
            "thetadata:options:test:expirations", true, """{"count": 42}""", "test note", CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT succeeded, result ->> 'count', notes FROM research.capability_probes " +
            "WHERE probe_key = 'thetadata:options:test:expirations'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("42", reader.GetString(1));
        Assert.Equal("test note", reader.GetString(2));
    }
}
