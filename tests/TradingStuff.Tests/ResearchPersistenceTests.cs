using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.IbkrGateway.Persistence;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>Pure unit coverage of the migration set — no database required.</summary>
[Collection(PostgresCollection.Name)]
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
[Collection(PostgresCollection.Name)]
public sealed class OrderIdStorePostgresTests
{
    private static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<(string ConnectionString, MigrationRunner Runner)> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

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

            await store.TryUpdateStatusAsync(101, "Filled", 987654321, true, CancellationToken.None);
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
        await store.TryUpdateStatusAsync(302, "Submitted", 0, false, CancellationToken.None);
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

    // ---- perm_id provenance (migration 014) ----------------------------------------------------
    // perm_id is the only order identifier that survives a reconnect or a gateway restart, so a null
    // one means the row cannot be matched at the broker. It used to be written as NULLIF(permId, 0),
    // which made "TWS never reported one" and "we had one and lost it" the same column value — on
    // exactly the orders (rejected, cancelled) whose fate most needs resolving.

    [Fact]
    public async Task A_terminal_order_TWS_never_gave_a_permId_records_that_as_the_conclusion()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        using var store = CreateStore(connectionString);

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 601, "DUTEST", "PendingSubmit", CancellationToken.None));

        // A rejection: verified live on the paper account, TWS errors 110 and 201 deliver an `error`
        // callback and nothing else — no openOrder, no orderStatus, so no permId exists to record.
        await store.TryUpdateStatusAsync(601, "Error110", 0, true, CancellationToken.None);

        Assert.Equal(("never_reported", true), await ReadPermIdStateAsync(connectionString, 601));
    }

    [Fact]
    public async Task An_order_with_no_outcome_yet_is_not_called_a_missing_permId()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        using var store = CreateStore(connectionString);

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 602, "DUTEST", "PendingSubmit", CancellationToken.None));

        // A resting limit order. Nothing is missing yet; the permId simply has not arrived.
        await store.TryUpdateStatusAsync(602, "PreSubmitted", 0, false, CancellationToken.None);

        Assert.Equal(("pending", true), await ReadPermIdStateAsync(connectionString, 602));
    }

    [Fact]
    public async Task A_recorded_permId_is_never_erased_by_a_later_update_without_one()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, _) = await PrepareAsync(server);
        using var store = CreateStore(connectionString);

        Assert.IsType<OrderMappingResult.Recorded>(await store.TryRecordPlacementAsync(
            Guid.NewGuid(), 603, "DUTEST", "PendingSubmit", CancellationToken.None));

        await store.TryUpdateStatusAsync(603, "PreSubmitted", 681713841L, false, CancellationToken.None);

        // A cancel of an order this process no longer holds state for reports permId 0. The old
        // NULLIF wrote the column unconditionally, so this erased the one field that cannot be
        // recovered from anywhere else afterwards.
        await store.TryUpdateStatusAsync(603, "Cancelled", 0, true, CancellationToken.None);

        Assert.Equal(("assigned", false), await ReadPermIdStateAsync(connectionString, 603));
    }

    /// <summary>The row's perm_id_state, and whether perm_id itself is null.</summary>
    private static async Task<(string State, bool PermIdIsNull)> ReadPermIdStateAsync(
        string connectionString, int ibkrOrderId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT perm_id_state, perm_id IS NULL FROM gateway.ibkr_order_map WHERE ibkr_order_id = $1",
            connection);
        command.Parameters.AddWithValue(ibkrOrderId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"no order-map row for broker order {ibkrOrderId}");

        return (reader.GetString(0), reader.GetBoolean(1));
    }
}

/// <summary>
/// Postgres integration tests for the migration-content checksum (migration 010): a migration file
/// edited after it was applied must be detected, an old ledger without checksums must upgrade
/// cleanly, and a hand-applied migration's "already exists" conflict must be reported rather than
/// retried forever.
/// </summary>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class MigrationChecksumPostgresTests
{
    private static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static (string ConnectionString, MigrationRunner Runner) Prepare(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

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
    //
    // Note what this test does NOT establish, because it looks like it does. It applies a migration,
    // NULLs its own checksum, and asserts the same value comes back — with the file unchanged by
    // construction, so the assertion cannot fail whatever the backfill writes, including the wrong
    // thing. It says nothing about whether the blessed value is what actually ran. The assertion
    // added at the end is the part that can fail: the baseline must be recorded as ASSUMED, since
    // the runner cannot know that this database's copy of the file is the one that ran. The case it
    // cannot see — a database whose file diverged before the upgrade — is covered by
    // MigrationProvenancePostgresTests.
    [Fact]
    public async Task An_old_ledger_row_with_no_checksum_is_backfilled_as_an_assumed_baseline()
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
            // Separate commands: a parameterised statement goes out on the extended protocol, which
            // cannot carry two statements in one round trip.
            await using (var drop = new Npgsql.NpgsqlCommand(
                "ALTER TABLE research.schema_migrations " +
                "DROP CONSTRAINT IF EXISTS schema_migrations_checksum_provenance",
                connection))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using var clear = new Npgsql.NpgsqlCommand(
                "UPDATE research.schema_migrations SET checksum = NULL, checksum_source = NULL WHERE name = $1",
                connection);
            clear.Parameters.AddWithValue(name);
            await clear.ExecuteNonQueryAsync();
        }

        var second = await runner.ApplyOnceAsync(connectionString, migrations, CancellationToken.None);
        Assert.Contains(name, second);

        await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var check = new Npgsql.NpgsqlCommand(
                "SELECT checksum, checksum_source FROM research.schema_migrations WHERE name = $1", connection);
            check.Parameters.AddWithValue(name);

            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            Assert.Equal(checksum, reader.GetString(0));
            Assert.Equal(ChecksumProvenance.Assumed, reader.GetString(1));
        }

        Assert.Contains(name, runner.UnverifiedBaselines);
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
