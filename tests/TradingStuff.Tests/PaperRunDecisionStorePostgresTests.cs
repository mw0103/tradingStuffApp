using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// Migration 023 and the decision store against real SQL: the round trip, the single-active rule,
/// and revocation flipping <see cref="ConstantExposureSignal"/> back to a refusal.
/// </summary>
/// <remarks>
/// The signal is exercised through the REAL store here, not the fake in
/// <see cref="ConstantExposureSignalTests"/>. The two halves of this mechanism can each be correct
/// and still not meet: "the store returns unrevoked rows" and "the signal trades on what the store
/// returns" are both true of an implementation whose <c>WHERE revoked_at IS NULL</c> was dropped, and
/// only a test that revokes a row and then asks the signal catches that.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class PaperRunDecisionStorePostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    [Fact]
    public async Task A_registered_decision_round_trips_and_is_the_active_one()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperRunDecisionStore(await PrepareAsync(server));

        // Absence first, so the test proves the row is what changed rather than assuming the table
        // starts however it happens to start.
        Assert.Null(await store.GetActiveAsync(CancellationToken.None));

        var registered = await store.RegisterAsync(
            "  The paper run may proceed on dev-provenance infrastructure.  ",
            "  Madison  ",
            "docs/plans/paper-run-protocol.md",
            CancellationToken.None);

        Assert.Null(registered.Refusal);

        var decision = registered.Decision!;
        Assert.Equal(PaperRunScopes.Paper, decision.Scope);
        Assert.True(decision.IsActive);

        // Trimmed on the way in: a signature with trailing whitespace is the same signature, and
        // "Madison " vs "Madison" reading as two different signers is a defect nobody would suspect.
        Assert.Equal("Madison", decision.SignedBy);
        Assert.Equal("The paper run may proceed on dev-provenance infrastructure.", decision.Statement);

        var active = await store.GetActiveAsync(CancellationToken.None);
        Assert.Equal(decision.DecisionId, active!.DecisionId);
        Assert.Equal(decision.DecidedAt, active.DecidedAt);
    }

    [Fact]
    public async Task A_second_decision_is_refused_while_one_is_in_force()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperRunDecisionStore(await PrepareAsync(server));

        var first = await store.RegisterAsync("First.", "Madison", "protocol", CancellationToken.None);
        Assert.Null(first.Refusal);

        var second = await store.RegisterAsync("Second.", "Somebody else", "protocol", CancellationToken.None);

        Assert.Null(second.Decision);
        Assert.NotNull(second.Refusal);

        // The refusal NAMES the decision already in force. "Conflict" alone would leave an operator
        // to go find out what they are conflicting with.
        Assert.Contains($"Decision {first.Decision!.DecisionId}", second.Refusal);
        Assert.Contains("Madison", second.Refusal);

        // And only one row exists, so the refusal was a refusal and not a rollback of a write.
        Assert.Single(await store.ListAsync(50, CancellationToken.None));
    }

    /// <summary>
    /// The check-then-insert race, closed by migration 023's partial unique index rather than by the
    /// store's read.
    /// </summary>
    /// <remarks>
    /// Written against the index directly: two connections that both observed an empty table would
    /// both pass <see cref="PaperRunDecisionStore.RegisterAsync"/>'s guard, and the second INSERT has
    /// to be the thing that fails. Without the index this test writes two active decisions and the
    /// question "which one authorized the orders?" has no answer.
    /// </remarks>
    [Fact]
    public async Task The_database_refuses_a_second_active_decision_even_when_the_read_guard_is_bypassed()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = ConnectionStringOf(await PrepareAsync(server));

        await InsertDirectlyAsync(connectionString, "First.", "Madison");

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertDirectlyAsync(connectionString, "Second.", "Somebody else"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);

        // Revoking the first frees the slot: the index constrains ACTIVE decisions, not the history.
        var store = new PaperRunDecisionStore(ConfigurationFor(connectionString));
        Assert.Null((await store.RevokeActiveAsync("making way", CancellationToken.None)).Refusal);

        await InsertDirectlyAsync(connectionString, "Second.", "Somebody else");
        Assert.Equal(2, (await store.ListAsync(50, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Revocation_flips_the_signal_back_to_a_refusal_and_keeps_the_history()
    {
        if (ServerConnectionString is not { } server) return;

        var configuration = await PrepareAsync(server);
        var store = new PaperRunDecisionStore(configuration);
        var signal = new ConstantExposureSignal(store, NullLogger<ConstantExposureSignal>.Instance);

        var before = await signal.EvaluateAsync(CancellationToken.None);
        Assert.False(before.Trade);
        Assert.Equal(SignalStates.NoPaperDecision, before.State);

        var registered = await store.RegisterAsync(
            "The paper run may proceed.", "Madison", "docs/plans/paper-run-protocol.md", CancellationToken.None);

        var authorized = await signal.EvaluateAsync(CancellationToken.None);
        Assert.True(authorized.Trade);
        Assert.Equal(SignalStates.Enter, authorized.State);
        Assert.Contains($"decision {registered.Decision!.DecisionId}", authorized.Reason);

        var revoked = await store.RevokeActiveAsync("phase 2 review", CancellationToken.None);
        Assert.Null(revoked.Refusal);
        Assert.Equal(registered.Decision.DecisionId, revoked.Decision!.DecisionId);
        Assert.False(revoked.Decision.IsActive);

        // Asserted on the QUERY, not only on the signal. ConstantExposureSignal also refuses a revoked
        // row defensively, so a GetActiveAsync that had lost its `WHERE revoked_at IS NULL` would still
        // produce a refusing signal and every assertion below would pass — measured, not reasoned: the
        // predicate was removed and this test stayed green until this line existed.
        Assert.Null(await store.GetActiveAsync(CancellationToken.None));

        var after = await signal.EvaluateAsync(CancellationToken.None);
        Assert.False(after.Trade);
        Assert.Equal(SignalStates.NoPaperDecision, after.State);

        // The ABSENT refusal, not the revoked-row one. Same state, different reason, and the reason is
        // what an operator reads off the decision row.
        Assert.Contains("No unrevoked paper-run decision is registered", after.Reason);

        // Revocation withdraws the authorization; it does not erase that it was ever given. The row
        // is still the answer to "what authorized the orders placed while it stood?".
        var history = await store.ListAsync(50, CancellationToken.None);
        var kept = Assert.Single(history);
        Assert.Equal(registered.Decision.DecisionId, kept.DecisionId);
        Assert.Equal("phase 2 review", kept.RevokedReason);
        Assert.NotNull(kept.RevokedAt);

        // A second revoke changes nothing and says so, rather than reporting a stop nobody performed.
        var again = await store.RevokeActiveAsync(null, CancellationToken.None);
        Assert.NotNull(again.Refusal);
        Assert.Contains("no active decision", again.Refusal);

        // And the slot is genuinely free: a revoked decision does not block a later one, which is the
        // operating mode the whole revoke path exists for.
        var replacement = await store.RegisterAsync(
            "The paper run may resume.", "Madison", "docs/plans/paper-run-protocol.md", CancellationToken.None);

        Assert.Null(replacement.Refusal);
        Assert.NotEqual(registered.Decision.DecisionId, replacement.Decision!.DecisionId);
        Assert.True((await signal.EvaluateAsync(CancellationToken.None)).Trade);
    }

    [Fact]
    public async Task An_unsigned_or_empty_decision_is_refused_before_it_reaches_the_table()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperRunDecisionStore(await PrepareAsync(server));

        Assert.NotNull((await store.RegisterAsync("   ", "Madison", "protocol", CancellationToken.None)).Refusal);
        Assert.NotNull((await store.RegisterAsync("A statement.", " ", "protocol", CancellationToken.None)).Refusal);

        Assert.Empty(await store.ListAsync(50, CancellationToken.None));
        Assert.Null(await store.GetActiveAsync(CancellationToken.None));
    }

    /// <summary>
    /// The "never live" clause, enforced by the schema rather than by the code that writes it.
    /// </summary>
    [Fact]
    public async Task The_schema_refuses_a_decision_scoped_to_anything_but_paper()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = ConnectionStringOf(await PrepareAsync(server));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.paper_run_decisions (scope, protocol_ref, statement, signed_by) " +
            "VALUES ('live', 'protocol', 'Trade live.', 'Anyone')",
            connection);

        var violation = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private static async Task InsertDirectlyAsync(string connectionString, string statement, string signedBy)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.paper_run_decisions (scope, protocol_ref, statement, signed_by) " +
            "VALUES ('paper', 'protocol', $1, $2)",
            connection)
        {
            Parameters = { new() { Value = statement }, new() { Value = signedBy } },
        };

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IConfiguration> PrepareAsync(string server)
    {
        var connectionString = PostgresCollection.FreshDatabase(server);
        var configuration = ConfigurationFor(connectionString);
        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);

        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return configuration;
    }

    private static string ConnectionStringOf(IConfiguration configuration) =>
        configuration.GetConnectionString("trading")!;

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();
}
