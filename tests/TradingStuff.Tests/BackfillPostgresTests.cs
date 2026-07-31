using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// Postgres integration tests for migration 004: the session-calendar table, the backfill
/// job/request checkpoint tables and their idempotency key, and the yearly-partitioned
/// <c>research.bars</c> table. Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection
/// string — see <see cref="ResearchRecordingPostgresTests"/> for the same convention.
/// </summary>
[Trait("Category", "RequiresPostgres")]
public sealed class BackfillPostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<(string ConnectionString, MigrationRunner Runner)> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return (connectionString, runner);
    }

    /// <summary>Inserts a minimal backfill_jobs row (SPX, instrument_id 1 from the migration 001 seed) and returns its job_id.</summary>
    private static async Task<long> InsertJobAsync(NpgsqlConnection connection)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO research.backfill_jobs " +
            "(name, instrument_id, con_id, what_to_show, bar_size, use_rth, target_from, target_to) " +
            "VALUES ($1, 1, 416904, 'TRADES', '1 min', true, $2, $3) RETURNING job_id",
            connection);
        insert.Parameters.AddWithValue($"spx-1min-trades-{Guid.NewGuid():N}");
        insert.Parameters.AddWithValue(new DateTimeOffset(2004, 3, 4, 0, 0, 0, TimeSpan.Zero));
        insert.Parameters.AddWithValue(DateTimeOffset.UtcNow);

        return (long)(await insert.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Migration_004_applies_cleanly_on_top_of_001_003_and_is_idempotent()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = await PrepareAsync(server);

        var first = await runner.ApplyOnceAsync(connectionString, CancellationToken.None);
        Assert.Contains("004_backfill.sql", first);

        // Rerun must be a pure no-op: same applied set, no exception from re-running DDL that
        // already ran (CREATE TABLE, the yearly-partition DO block, etc. all guarded by
        // schema_migrations rather than IF NOT EXISTS everywhere).
        var second = await runner.ApplyOnceAsync(connectionString, CancellationToken.None);
        Assert.Equal(first, second);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var table in new[] { "sessions", "backfill_jobs", "backfill_requests", "bars" })
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'research' AND table_name = $1", connection);
            command.Parameters.AddWithValue(table);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task Duplicate_backfill_request_slice_is_rejected_by_the_idempotency_key()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var jobId = await InsertJobAsync(connection);

        async Task InsertSliceAsync(DateTimeOffset endTimeUtc)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.backfill_requests " +
                "(job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7)",
                connection);
            insert.Parameters.AddWithValue(jobId);
            insert.Parameters.AddWithValue(416904);
            insert.Parameters.AddWithValue(endTimeUtc);
            insert.Parameters.AddWithValue("1 D");
            insert.Parameters.AddWithValue("TRADES");
            insert.Parameters.AddWithValue("1 min");
            insert.Parameters.AddWithValue(true);
            await insert.ExecuteNonQueryAsync();
        }

        var endTime = new DateTimeOffset(2010, 7, 30, 0, 0, 0, TimeSpan.Zero);
        await InsertSliceAsync(endTime);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertSliceAsync(endTime));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        await using var count = new NpgsqlCommand("SELECT count(*) FROM research.backfill_requests", connection);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Duplicate_backfill_request_slice_is_rejected_even_when_end_time_utc_is_null()
    {
        // The "now"-anchored case: end_time_utc is NULL. Plain UNIQUE would treat every NULL as
        // distinct from every other NULL and let this insert twice — exactly the case migration
        // 004's UNIQUE NULLS NOT DISTINCT constraint exists to close.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var jobId = await InsertJobAsync(connection);

        async Task InsertOpenEndedSliceAsync()
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.backfill_requests " +
                "(job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth) " +
                "VALUES ($1, $2, NULL, $3, $4, $5, $6)",
                connection);
            insert.Parameters.AddWithValue(jobId);
            insert.Parameters.AddWithValue(416904);
            insert.Parameters.AddWithValue("1 D");
            insert.Parameters.AddWithValue("TRADES");
            insert.Parameters.AddWithValue("1 min");
            insert.Parameters.AddWithValue(true);
            await insert.ExecuteNonQueryAsync();
        }

        await InsertOpenEndedSliceAsync();

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertOpenEndedSliceAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM research.backfill_requests WHERE end_time_utc IS NULL", connection);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Reingesting_the_same_bar_via_ON_CONFLICT_DO_NOTHING_lands_exactly_one_row()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tsUtc = new DateTimeOffset(2026, 7, 30, 14, 30, 0, TimeSpan.Zero);

        async Task InsertBarAsync()
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.bars " +
                "(con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, open, high, low, close, source) " +
                "VALUES (416904, 1, '1 min', 'TRADES', true, $1, 100.0, 101.0, 99.5, 100.5, 'backfill') " +
                "ON CONFLICT (con_id, what_to_show, bar_size, use_rth, ts_utc) DO NOTHING",
                connection);
            insert.Parameters.AddWithValue(tsUtc);
            await insert.ExecuteNonQueryAsync();
        }

        await InsertBarAsync();
        await InsertBarAsync(); // identical slice re-ingested — must be a silent no-op, not an error or a duplicate row

        await using var count = new NpgsqlCommand("SELECT count(*) FROM research.bars WHERE con_id = 416904", connection);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Old_and_recent_bars_land_in_real_yearly_partitions_not_the_default_partition()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var oldBar = new DateTimeOffset(1995, 6, 15, 14, 30, 0, TimeSpan.Zero); // SPY-head-era date
        var recentBar = DateTimeOffset.UtcNow;

        async Task<string> InsertAndGetPartitionAsync(DateTimeOffset tsUtc)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.bars " +
                "(con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, open, high, low, close, source) " +
                "VALUES (5, 1, '1 day', 'TRADES', true, $1, 1.0, 1.0, 1.0, 1.0, 'backfill') " +
                "RETURNING tableoid::regclass::text",
                connection);
            insert.Parameters.AddWithValue(tsUtc);

            return (string)(await insert.ExecuteScalarAsync())!;
        }

        var oldPartition = await InsertAndGetPartitionAsync(oldBar);
        var recentPartition = await InsertAndGetPartitionAsync(recentBar);

        Assert.Equal("research.bars_1995", oldPartition);
        Assert.Equal($"research.bars_{recentBar.Year}", recentPartition);

        Assert.NotEqual("research.bars_default", oldPartition);
        Assert.NotEqual("research.bars_default", recentPartition);

        await using var defaultCount = new NpgsqlCommand("SELECT count(*) FROM research.bars_default", connection);
        Assert.Equal(0L, (long)(await defaultCount.ExecuteScalarAsync())!);
    }
}
