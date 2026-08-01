using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Trials;

namespace TradingStuff.Tests;

/// <summary>
/// The trial registry against a real database. Needs <c>TRADING_TEST_POSTGRES</c>; skipped when unset.
/// </summary>
/// <remarks>
/// The append-only guarantee is the reason this suite exists. It lives in a trigger, so no unit
/// test can demonstrate it — and it is the single property the registry is for: a registry that
/// can be rewritten after results are seen is evidence of nothing. The ordinal assignment is here
/// for the same reason, since it depends on a serializable read the database performs.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TrialRegistryPostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private const string Study = "volatility-forecast-residual";

    private static TrialDeclaration Declaration(string study = Study, long seed = 1, string rationale = "baseline") =>
        new(study, "sha256:features-v1", "elastic-net", """{"alpha":0.5}""",
            """{"folds":"F1,F2,F3"}""", seed, "abc1234", rationale);

    /// <summary>
    /// A fresh database per test, migrated to head — the same shape the other Postgres suites use.
    /// </summary>
    /// <remarks>
    /// The database is named but never explicitly created: <see cref="MigrationRunner"/> creates it
    /// on first application. An explicit CREATE DATABASE from a separate admin connection works in
    /// isolation and contends badly when several suites run at once, since Postgres serializes
    /// database creation against the template.
    /// </remarks>
    private static async Task<NpgsqlDataSource> FreshDatabaseAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return NpgsqlDataSource.Create(connectionString);
    }

    [Fact]
    public async Task TheMigrationCreatesTheRegistry()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);

        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'research' AND table_name IN ('registered_trials', 'trial_outcomes')",
            connection);

        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task OrdinalsAreAssignedInSequencePerStudy()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        var first = await registry.RegisterAsync(Declaration(seed: 1), CancellationToken.None);
        var second = await registry.RegisterAsync(Declaration(seed: 2), CancellationToken.None);

        Assert.Equal(1, first.VariantOrdinal);
        Assert.Equal(2, second.VariantOrdinal);

        // Counted per study, so a companion study starts its own cap at one.
        var companion = await registry.RegisterAsync(
            Declaration(study: "vrp-conditioning"), CancellationToken.None);
        Assert.Equal(1, companion.VariantOrdinal);

        Assert.Equal(2, await registry.CountAsync(Study, CancellationToken.None));
        Assert.Equal(1, await registry.CountAsync("vrp-conditioning", CancellationToken.None));
    }

    [Fact]
    public async Task ARegisteredTrialCannotBeAmended()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        var trial = await registry.RegisterAsync(Declaration(), CancellationToken.None);

        await using var connection = await source.OpenConnectionAsync();

        // The whole point: a declaration edited after its result is known is evidence of nothing.
        await using var update = new NpgsqlCommand(
            "UPDATE research.registered_trials SET model_family = 'gbt' WHERE trial_id = $1", connection);
        update.Parameters.AddWithValue(trial.TrialId);
        var amend = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", amend.MessageText, StringComparison.Ordinal);

        await using var delete = new NpgsqlCommand(
            "DELETE FROM research.registered_trials WHERE trial_id = $1", connection);
        delete.Parameters.AddWithValue(trial.TrialId);
        var remove = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("append-only", remove.MessageText, StringComparison.Ordinal);

        // And the row is still there, unchanged.
        var stored = await registry.ListAsync(Study, CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal("elastic-net", stored[0].ModelFamily);
    }

    [Fact]
    public async Task AnOutcomeCannotBeAmendedEither()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        var trial = await registry.RegisterAsync(Declaration(), CancellationToken.None);
        await registry.RecordOutcomeAsync(new TrialOutcome(
            trial.TrialId, DateTimeOffset.UtcNow, 0.42, 0.03, 0.11, -2.4, 0.016,
            TrialProtocol.DeflatedPThreshold(1), 2, 3, 0.35, TrialVerdicts.InsufficientMagnitude),
            CancellationToken.None);

        await using var connection = await source.OpenConnectionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE research.trial_outcomes SET verdict = 'validated' WHERE trial_id = $1", connection);
        update.Parameters.AddWithValue(trial.TrialId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVariantCannotProduceTwoOutcomes()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        var trial = await registry.RegisterAsync(Declaration(), CancellationToken.None);
        var outcome = new TrialOutcome(
            trial.TrialId, DateTimeOffset.UtcNow, 0.42, 0.03, 0.11, -2.4, 0.016,
            TrialProtocol.DeflatedPThreshold(1), 2, 3, 0.35, TrialVerdicts.InsufficientMagnitude);

        await registry.RecordOutcomeAsync(outcome, CancellationToken.None);

        // A second result for one declaration would leave a reader unable to tell which run the
        // registry describes.
        await Assert.ThrowsAsync<PostgresException>(
            () => registry.RecordOutcomeAsync(outcome, CancellationToken.None));
    }

    [Fact]
    public async Task TheCapIsRefusedRatherThanWarnedAbout()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        for (var i = 0; i < TrialProtocol.VariantCap; i++)
        {
            await registry.RegisterAsync(Declaration(seed: i), CancellationToken.None);
        }

        var ex = await Assert.ThrowsAsync<TrialProtocolException>(
            () => registry.RegisterAsync(Declaration(seed: 99), CancellationToken.None));

        // The registration treats an exhausted cap as a negative result, not a prompt to raise it.
        Assert.Contains("negative result", ex.Message, StringComparison.Ordinal);
        Assert.Equal(TrialProtocol.VariantCap, await registry.CountAsync(Study, CancellationToken.None));
    }

    [Fact]
    public async Task ADeclarationMustNameItsStudyAndCommit()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(
            Declaration() with { Study = "  " }, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(
            Declaration() with { GitSha = "" }, CancellationToken.None));
    }

    [Fact]
    public async Task RegisteredVariantsReadBackInOrderWithTheirConfiguration()
    {
        if (ServerConnectionString is not { } server) return;

        await using var source = await FreshDatabaseAsync(server);
        var registry = new TrialRegistry(source);

        await registry.RegisterAsync(Declaration(seed: 7, rationale: "baseline"), CancellationToken.None);
        await registry.RegisterAsync(Declaration(seed: 8, rationale: "ablation: no VIX"), CancellationToken.None);

        var trials = await registry.ListAsync(Study, CancellationToken.None);

        Assert.Equal(2, trials.Count);
        Assert.Equal([1, 2], trials.Select(t => t.VariantOrdinal));
        Assert.Equal([7L, 8L], trials.Select(t => t.Seed));
        Assert.Equal("ablation: no VIX", trials[1].Rationale);

        // The five enumerated fields survive the round trip, jsonb included.
        Assert.Equal("sha256:features-v1", trials[0].FeatureSetHash);
        Assert.Equal("elastic-net", trials[0].ModelFamily);
        Assert.Contains("alpha", trials[0].Hyperparameters, StringComparison.Ordinal);
        Assert.Contains("F1", trials[0].FoldConfig, StringComparison.Ordinal);
        Assert.Equal("abc1234", trials[0].GitSha);
    }
}
