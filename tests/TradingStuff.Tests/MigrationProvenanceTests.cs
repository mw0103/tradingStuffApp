using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>Health reporting that needs no database.</summary>
public sealed class MigrationHealthCheckTests
{
    private static async Task<HealthCheckResult> CheckAsync(MigrationRunner runner) =>
        await new MigrationHealthCheck(runner).CheckHealthAsync(
            new HealthCheckContext { Registration = new HealthCheckRegistration("migrations", _ => null!, null, null) },
            CancellationToken.None);

    [Fact]
    public async Task A_service_that_has_not_migrated_yet_is_not_reported_healthy()
    {
        var runner = new MigrationRunner(
            new ConfigurationBuilder().Build(), NullLogger<MigrationRunner>.Instance);

        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(runner)).Status);
    }

    [Fact]
    public async Task A_service_with_no_connection_string_is_degraded_not_healthy()
    {
        // "disabled" is a configured-off local run rather than a fault, but every research endpoint
        // will fail, so it is not health either.
        var runner = new MigrationRunner(
            new ConfigurationBuilder().Build(), NullLogger<MigrationRunner>.Instance);

        await runner.StartAsync(CancellationToken.None);

        // Awaited rather than followed by StopAsync: ExecuteAsync does not necessarily begin before
        // StartAsync returns, so stopping first cancels the run and leaves the state at "pending" —
        // observed intermittently, 2 runs in 6, before this was written this way.
        await (runner.ExecuteTask ?? Task.CompletedTask);

        Assert.Equal("disabled", runner.State.Status);
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(runner)).Status);
    }
}

/// <summary>
/// Migration 013: a checksum baseline says where it came from, and a backfilled one is never passed
/// off as a measurement of what actually ran.
/// </summary>
[Trait("Category", "RequiresPostgres")]
public sealed class MigrationProvenancePostgresTests
{
    private static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static (string ConnectionString, MigrationRunner Runner) Prepare(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();

        return (connectionString, new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance)
        {
            // The loop under test is the retry cadence, not the wall clock it waits on.
            ConflictFastRetry = TimeSpan.FromMilliseconds(10),
            ConflictSlowRetry = TimeSpan.FromMilliseconds(50),
        });
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? default : (T)value;
    }

    [Fact]
    public async Task Every_migration_this_runner_applies_records_a_verified_baseline()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        // The negative control for the "assumed" marking below: a database this runner built from
        // nothing has measured every checksum it holds, and must not be smeared with a caveat that
        // does not apply to it.
        var unverified = await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM research.schema_migrations WHERE checksum_source IS DISTINCT FROM 'verified'");

        Assert.Equal(0, unverified);
        Assert.Empty(runner.UnverifiedBaselines);
    }

    // The defect. An environment whose migration file was hand-patched before checksums shipped gets
    // the CURRENT file's checksum written in as its baseline and logged as "the baseline future runs
    // are checked against" — so it compares clean forever, its ledger is byte-identical to a clean
    // environment's, and their schemas differ. That is exactly the state migration 010's own header
    // says nothing could tell apart.
    //
    // The lost checksum is unrecoverable, so this cannot assert detection. It asserts the honesty
    // that IS available: the baseline is recorded as assumed, and the runner says so.
    [Fact]
    public async Task A_diverged_pre_existing_database_gets_an_assumed_baseline_not_a_verified_one()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);

        const string name = "996_diverged_before_checksums_test.sql";
        const string ranSql = "CREATE TABLE research.diverged_before_checksums (id int);";
        const string patchedSql = "CREATE TABLE research.diverged_before_checksums (id int, note text);";

        await runner.ApplyOnceAsync(
            connectionString,
            [(name, ranSql, MigrationRunner.ComputeChecksum(ranSql))],
            CancellationToken.None);

        // Roll the ledger back to a pre-checksum row: this is what every database that existed when
        // the checksum feature shipped looked like on its first startup afterwards.
        await ExecuteAsync(
            connectionString,
            "ALTER TABLE research.schema_migrations DROP CONSTRAINT IF EXISTS schema_migrations_checksum_provenance; " +
            $"UPDATE research.schema_migrations SET checksum = NULL, checksum_source = NULL WHERE name = '{name}'");

        // The file on disk is NOT what ran — a hand patch, one of the cases 010's header enumerates.
        var patchedChecksum = MigrationRunner.ComputeChecksum(patchedSql);

        await runner.ApplyOnceAsync(
            connectionString,
            [(name, patchedSql, patchedChecksum)],
            CancellationToken.None);

        var recorded = await ScalarAsync<string>(
            connectionString, $"SELECT checksum FROM research.schema_migrations WHERE name = '{name}'");
        var source = await ScalarAsync<string>(
            connectionString, $"SELECT checksum_source FROM research.schema_migrations WHERE name = '{name}'");

        // The blessed value is the PATCHED file's checksum, not the one that ran — which is precisely
        // why it may not be recorded as verified.
        Assert.Equal(patchedChecksum, recorded);
        Assert.Equal(ChecksumProvenance.Assumed, source);
        Assert.False(ChecksumProvenance.IsVerified(source));

        // And the claim is surfaced, not left implicit in the row: this is what /research/status and
        // the health check report, so a clean comparison here cannot read as evidence.
        Assert.Contains(name, runner.UnverifiedBaselines);
    }

    [Fact]
    public async Task An_assumed_baseline_still_catches_a_later_edit_and_says_it_is_assumed()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);

        // Note the name carries no word this test asserts on. The first draft called it
        // "995_assumed_then_edited_test.sql" and asserted the message contained "assumed" — which it
        // did, at index 15, inside the file name the message quotes back. It passed against the
        // defect it was written to catch.
        const string name = "995_blessed_then_edited_test.sql";
        const string firstSql = "CREATE TABLE research.blessed_then_edited (id int);";

        await runner.ApplyOnceAsync(
            connectionString,
            [(name, firstSql, MigrationRunner.ComputeChecksum(firstSql))],
            CancellationToken.None);

        await ExecuteAsync(
            connectionString,
            "ALTER TABLE research.schema_migrations DROP CONSTRAINT IF EXISTS schema_migrations_checksum_provenance; " +
            $"UPDATE research.schema_migrations SET checksum = NULL, checksum_source = NULL WHERE name = '{name}'");

        await runner.ApplyOnceAsync(
            connectionString,
            [(name, firstSql, MigrationRunner.ComputeChecksum(firstSql))],
            CancellationToken.None);

        // Detection starts from the assumed baseline: an edit made AFTER it is still fatal, and the
        // message has to say the baseline is an assumption or "revert the file to what actually ran"
        // is an instruction nobody can follow.
        const string editedSql = "CREATE TABLE research.blessed_then_edited (id int, note text);";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ApplyOnceAsync(
                connectionString,
                [(name, editedSql, MigrationRunner.ComputeChecksum(editedSql))],
                CancellationToken.None));

        Assert.Contains(name, ex.Message);
        Assert.Contains("not a measurement of what ran", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_010_era_row_is_marked_unknown_rather_than_assumed_to_be_verified()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        // Rewind to a 010-era ledger: checksums present, provenance column not yet populated, and
        // migration 013 not yet applied. Such a row could have come from a real apply OR from 010's
        // backfill, and nothing distinguishes them — so it must NOT be promoted to verified.
        await ExecuteAsync(
            connectionString,
            "ALTER TABLE research.schema_migrations " +
            "  DROP CONSTRAINT schema_migrations_checksum_source_domain, " +
            "  DROP CONSTRAINT schema_migrations_checksum_provenance; " +
            "UPDATE research.schema_migrations SET checksum_source = NULL; " +
            "DELETE FROM research.schema_migrations WHERE name = '013_checksum_provenance.sql'");

        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        var verified = await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM research.schema_migrations " +
            "WHERE checksum_source = 'verified' AND name <> '013_checksum_provenance.sql'");
        var unknown = await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM research.schema_migrations WHERE checksum_source = 'unknown'");

        Assert.Equal(0, verified);
        Assert.True(unknown > 0, "the 010-era rows should have been marked unknown");

        // 013's own row is the exception, and legitimately so: this runner just applied it.
        var thirteen = await ScalarAsync<string>(
            connectionString,
            "SELECT checksum_source FROM research.schema_migrations WHERE name = '013_checksum_provenance.sql'");

        Assert.Equal(ChecksumProvenance.Verified, thirteen);
        Assert.Equal(unknown, runner.UnverifiedBaselines.Count);
    }

    [Fact]
    public async Task The_ledger_refuses_a_checksum_that_will_not_say_where_it_came_from()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        // Held by the engine, not by convention: the next writer of an INSERT into this table cannot
        // quietly produce a checksum whose provenance every reader then has to guess at.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            "UPDATE research.schema_migrations SET checksum_source = NULL WHERE name = '001_foundations.sql'"));

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            "UPDATE research.schema_migrations SET checksum_source = 'probably fine' WHERE name = '001_foundations.sql'"));
    }

    // The other half of the defect: exhausting the bounded conflict retry used to exit ExecuteAsync
    // for good, ~10 s after startup, leaving a service with no schema behind a 200 from /health and
    // an `applied: []` on /research/status. The remediation that actually happens — an operator
    // dropping the hand-created object a minute later — had nothing watching for it.
    [Fact]
    public async Task A_migration_conflict_stays_unhealthy_and_recovers_without_a_restart()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;

        await ExecuteAsync(
            $"{server.TrimEnd(';')};Database=postgres", $"CREATE DATABASE \"{database}\"");

        // "Applied by hand outside the runner": the object exists, the ledger has never heard of it.
        await ExecuteAsync(
            connectionString,
            "CREATE SCHEMA research; CREATE TABLE research.instruments (placeholder int)");

        var health = new MigrationHealthCheck(runner);

        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("migrations", _ => null!, null, null),
        };

        await runner.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => runner.State.Status == "failed", "the conflict to be reported");

            var failed = await health.CheckHealthAsync(context, CancellationToken.None);
            Assert.Equal(HealthStatus.Unhealthy, failed.Status);
            Assert.Contains("001_foundations.sql", failed.Description);

            // Long enough that the bounded FAST attempts (3 × 10 ms) are certainly spent. The
            // remediation must not land inside that window or this test passes against the defect —
            // which is exactly what the first draft did: it dropped the table ~60 ms in, an attempt
            // still to come picked it up, and the permanent give-up never happened.
            await Task.Delay(500);

            Assert.False(
                runner.ExecuteTask!.IsCompleted,
                "the runner gave up for good; an operator dropping the conflicting object a minute " +
                "later has nothing left watching for it, and the service stays schemaless in silence");

            // The ordinary remediation, applied while the service keeps running.
            await ExecuteAsync(connectionString, "DROP TABLE research.instruments");

            await WaitUntilAsync(
                () => runner.State.Status == "applied",
                "the runner to pick the schema up again after the conflicting object was dropped");

            var recovered = await health.CheckHealthAsync(context, CancellationToken.None);
            Assert.Equal(HealthStatus.Healthy, recovered.Status);
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
