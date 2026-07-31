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
