using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.IbkrGateway.Persistence;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>Pure unit coverage of the migration set — no database required.</summary>
public sealed class MigrationSetTests
{
    [Fact]
    public void Embedded_migrations_exist_and_apply_in_name_order()
    {
        var migrations = MigrationRunner.LoadEmbeddedMigrations();

        Assert.True(migrations.Count >= 2);
        Assert.Contains(migrations, migration => migration.Name == "001_foundations.sql");
        Assert.Contains(migrations, migration => migration.Name == "002_probe_seed_20260731.sql");
        Assert.All(migrations, migration => Assert.False(string.IsNullOrWhiteSpace(migration.Sql)));

        var ordered = migrations.Select(migration => migration.Name).ToArray();
        Assert.Equal(ordered.OrderBy(name => name, StringComparer.Ordinal), ordered);
    }

    [Fact]
    public void Migration_names_are_recorded_as_bare_file_names_that_survive_renames()
    {
        // The applied-migration history is keyed on these names; a namespace or folder rename must
        // not orphan it. That also means migration file names may contain no dots except ".sql".
        var migrations = MigrationRunner.LoadEmbeddedMigrations();

        Assert.All(migrations, migration =>
        {
            Assert.DoesNotContain("TradingStuff", migration.Name);
            Assert.EndsWith(".sql", migration.Name);
            Assert.Equal(2, migration.Name.Split('.').Length);
        });
    }

    [Fact]
    public void Embedded_migrations_carry_a_content_checksum_that_matches_their_own_text()
    {
        var migrations = MigrationRunner.LoadEmbeddedMigrations();

        Assert.All(migrations, migration =>
        {
            Assert.False(string.IsNullOrWhiteSpace(migration.Checksum));
            Assert.Equal(MigrationRunner.ComputeChecksum(migration.Sql), migration.Checksum);
        });

        // Not a promise about SHA-256, just a sanity check: a bug that always returned a constant
        // would otherwise pass every assertion above.
        Assert.Equal(migrations.Count, migrations.Select(m => m.Checksum).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Checksum_is_stable_for_identical_text_and_changes_when_text_changes()
    {
        var checksum = MigrationRunner.ComputeChecksum("CREATE TABLE research.x (id int);");

        Assert.Equal(checksum, MigrationRunner.ComputeChecksum("CREATE TABLE research.x (id int);"));
        Assert.NotEqual(checksum, MigrationRunner.ComputeChecksum("CREATE TABLE research.x (id int, note text);"));
    }
}

/// <summary>
/// Integration tests against a real Postgres. Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a
/// connection string (e.g. <c>Host=127.0.0.1;Port=5433;Username=postgres;Password=trading</c>);
/// each run uses a fresh database so reruns are independent.
/// </summary>
[Trait("Category", "RequiresPostgres")]
public sealed class OrderIdStorePostgresTests
{
    private static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<(string ConnectionString, MigrationRunner Runner)> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();

        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return (connectionString, runner);
    }

    private static OrderIdStore CreateStore(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();

        return new OrderIdStore(configuration, NullLogger<OrderIdStore>.Instance);
    }

    [Fact]
    public async Task Migrations_are_idempotent_and_seed_the_probe_registry()
    {
        if (ServerConnectionString is not { } server)
        {
            return; // Postgres not available in this environment; covered by the trait-gated run.
        }

        var (connectionString, runner) = await PrepareAsync(server);

        var first = await runner.ApplyOnceAsync(connectionString, CancellationToken.None);
        var second = await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        Assert.Equal(first, second);

        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var count = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM research.capability_probes", connection);
        var probes = (long)(await count.ExecuteScalarAsync())!;

        Assert.True(probes >= 20, $"expected the 2026-07-31 probe seed, found {probes} rows");
    }

    [Fact]
    public async Task The_order_map_survives_a_store_restart_and_refuses_a_second_placement()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        var internalOrderId = Guid.NewGuid();

        using (var store = CreateStore(connectionString))
        {
            var recorded = await store.TryRecordPlacementAsync(
                internalOrderId, 101, "DUTEST", "PendingSubmit", CancellationToken.None);
            Assert.IsType<OrderMappingResult.Recorded>(recorded);

            await store.TryUpdateStatusAsync(101, "Filled", 987654321, CancellationToken.None);
        }

        // A NEW store instance — the restart. The in-memory tracker is empty, but the map is not.
        using (var restarted = CreateStore(connectionString))
        {
            var retried = await restarted.TryRecordPlacementAsync(
                internalOrderId, 202, "DUTEST", "PendingSubmit", CancellationToken.None);

            var mapped = Assert.IsType<OrderMappingResult.AlreadyMapped>(retried);
            Assert.Equal(101, mapped.ExistingIbkrOrderId);
            Assert.Equal("Filled", mapped.LastStatus);
        }
    }

    [Fact]
    public async Task A_never_transmitted_mapping_can_be_compensated_and_the_internal_order_retried()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        var internalOrderId = Guid.NewGuid();

        using var store = CreateStore(connectionString);

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            internalOrderId, 301, "DUTEST", "PendingSubmit", CancellationToken.None));

        // The transmit provably never happened; compensation frees the internal order for retry.
        Assert.True(await store.TryDeleteNeverTransmittedAsync(
            internalOrderId, 301, "PendingSubmit", CancellationToken.None));

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            internalOrderId, 302, "DUTEST", "PendingSubmit", CancellationToken.None));

        // Once the status has moved past the recorded sentinel, the guarded delete must refuse.
        await store.TryUpdateStatusAsync(302, "Submitted", 0, CancellationToken.None);
        Assert.False(await store.TryDeleteNeverTransmittedAsync(
            internalOrderId, 302, "PendingSubmit", CancellationToken.None));
    }

    [Fact]
    public async Task A_reused_broker_order_id_is_an_integrity_violation_not_a_duplicate()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        using var store = CreateStore(connectionString);

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 401, "DUTEST", "PendingSubmit", CancellationToken.None));

        // A DIFFERENT internal order reusing broker id 401 must be refused as corruption, never
        // treated as a benign duplicate or a mere availability problem.
        Assert.IsType<OrderMappingResult.IntegrityViolation>(await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 401, "DUTEST", "PendingSubmit", CancellationToken.None));
    }

    [Fact]
    public async Task A_reused_broker_order_id_is_diagnosed_rather_than_called_impossible()
    {
        // It is not impossible, and saying so sends the operator looking in the wrong place. TWS's
        // "Reset API order ID sequence" makes nextValidId small again, and a gateway restart after
        // one reseeds this process straight back into the range old ibkr_order_map rows already
        // occupy. Every placement then fails here with no self-healing path until the shared
        // request/order sequence climbs past the highest id on record — so the message has to name
        // that cause and that number. Deleting the colliding rows is NOT the remedy: they are the
        // audit trail linking an internal order to a real broker order.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        using var store = CreateStore(connectionString);

        var firstOwner = Guid.NewGuid();

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            firstOwner, 511, "DUTEST", "PendingSubmit", CancellationToken.None));

        // A higher id exists too, so the "sequence must clear" figure is a real maximum rather than
        // just the id that happened to collide.
        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 998, "DUTEST", "PendingSubmit", CancellationToken.None));

        var violation = Assert.IsType<OrderMappingResult.IntegrityViolation>(
            await store.TryRecordPlacementAsync(
                Guid.NewGuid(), 511, "DUTEST", "PendingSubmit", CancellationToken.None));

        Assert.Contains(firstOwner.ToString(), violation.Reason);
        Assert.Contains("Reset API order ID sequence", violation.Reason);
        Assert.Contains("998", violation.Reason);
    }

    [Fact]
    public async Task A_store_without_a_connection_string_reports_unavailable()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var store = new OrderIdStore(configuration, NullLogger<OrderIdStore>.Instance);

        Assert.False(store.Enabled);

        var result = await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 1, null, "PendingSubmit", CancellationToken.None);

        Assert.IsType<OrderMappingResult.Unavailable>(result);
    }
}

/// <summary>
/// Postgres integration tests for the migration-content checksum (migration 010): a migration file
/// edited after it was applied must be detected, an old ledger without checksums must upgrade
/// cleanly, and a hand-applied migration's "already exists" conflict must be reported rather than
/// retried forever.
/// </summary>
[Trait("Category", "RequiresPostgres")]
public sealed class MigrationChecksumPostgresTests
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

        return (connectionString, new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance));
    }

    [Fact]
    public async Task A_real_upgrade_records_a_checksum_for_every_applied_migration()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM research.schema_migrations WHERE checksum IS NULL", connection);
        var withoutChecksum = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, withoutChecksum);
    }

    // The negative control the review specifically asked for: a migration whose file content
    // changed after it was recorded as applied must fail startup loudly, naming the file — not
    // silently re-apply the new text, and not silently keep reporting "applied".
    [Fact]
    public async Task A_migration_edited_after_it_applied_fails_loudly_and_names_the_file()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);

        const string name = "999_checksum_drift_test.sql";
        const string originalSql = "CREATE TABLE research.checksum_drift_test (id int);";
        var original = new[] { (Name: name, Sql: originalSql, Checksum: MigrationRunner.ComputeChecksum(originalSql)) };

        await runner.ApplyOnceAsync(connectionString, original, CancellationToken.None);

        // Same name, DIFFERENT text — exactly what a hand patch to an already-applied file produces.
        const string editedSql = "CREATE TABLE research.checksum_drift_test (id int, note text);";
        var edited = new[] { (Name: name, Sql: editedSql, Checksum: MigrationRunner.ComputeChecksum(editedSql)) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.ApplyOnceAsync(connectionString, edited, CancellationToken.None));

        Assert.Contains(name, ex.Message);
    }

    // Old ledgers (created before this column existed) must upgrade cleanly: a NULL checksum on an
    // already-applied row is a baseline to establish, not a divergence to reject.
    [Fact]
    public async Task An_old_ledger_row_with_no_checksum_is_backfilled_not_rejected()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);

        const string name = "998_checksum_backfill_test.sql";
        const string sql = "CREATE TABLE research.checksum_backfill_test (id int);";
        var checksum = MigrationRunner.ComputeChecksum(sql);
        var migrations = new[] { (Name: name, Sql: sql, Checksum: checksum) };

        await runner.ApplyOnceAsync(connectionString, migrations, CancellationToken.None);

        // Roll the ledger row back to look like it predates the checksum column — an upgrading
        // environment's exact starting state.
        await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var clear = new Npgsql.NpgsqlCommand(
                "UPDATE research.schema_migrations SET checksum = NULL WHERE name = $1", connection);
            clear.Parameters.AddWithValue(name);
            await clear.ExecuteNonQueryAsync();
        }

        var second = await runner.ApplyOnceAsync(connectionString, migrations, CancellationToken.None);
        Assert.Contains(name, second);

        await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var check = new Npgsql.NpgsqlCommand(
                "SELECT checksum FROM research.schema_migrations WHERE name = $1", connection);
            check.Parameters.AddWithValue(name);
            var backfilled = (string?)await check.ExecuteScalarAsync();

            Assert.Equal(checksum, backfilled);
        }
    }

    // The observed live failure mode: a migration applied by hand outside the runner leaves its
    // target object already there, with no ledger row to say so. The runner's own attempt to apply
    // it must fail fast, name the migration, and explain the probable cause rather than retry
    // forever against a statement that will never succeed.
    [Fact]
    public async Task A_hand_applied_migrations_conflict_is_reported_not_silently_retried()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, runner) = Prepare(server);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None); // real migrations: schema + ledger exist

        const string name = "997_hand_applied_conflict_test.sql";
        const string sql = "CREATE TABLE research.hand_applied_conflict_test (id int);";

        // "Hand-applied": the object exists, but schema_migrations has never heard of this migration.
        await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand(sql, connection);
            await create.ExecuteNonQueryAsync();
        }

        var migrations = new[] { (Name: name, Sql: sql, Checksum: MigrationRunner.ComputeChecksum(sql)) };

        var ex = await Assert.ThrowsAsync<MigrationConflictException>(
            () => runner.ApplyOnceAsync(connectionString, migrations, CancellationToken.None));

        Assert.Equal(name, ex.MigrationName);
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
