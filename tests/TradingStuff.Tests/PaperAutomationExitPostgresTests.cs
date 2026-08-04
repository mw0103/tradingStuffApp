using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.Contracts;
using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// Exit idempotency measured on the real decision log, not on an in-process claim.
/// </summary>
/// <remarks>
/// <para>
/// The unit suite already proves a second evaluation does not send a second closing order. It cannot
/// prove the half that matters after a crash: the in-memory claim set is empty in a freshly started
/// process, so the only thing standing between a restart and a duplicate closing order is
/// <c>detail-&gt;&gt;'exitKey'</c> coming back out of <c>research.paper_automation_decisions</c>. That
/// projection is SQL and jsonb, and it is asserted here against a real schema.
/// </para>
/// <para>
/// A duplicate close is not a harmless extra order. The first one flattens the spread; the second
/// opens the opposite one, unmonitored, in an account that was meant to be flat.
/// </para>
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class PaperAutomationExitPostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    // The harness clock: 10:00 ET on Wednesday 2026-08-05, so the NYSE trading date is 2026-08-05.
    private static readonly DateOnly TradingDate = new(2026, 8, 5);

    // Five days out: at or below the seven-day threshold.
    private static readonly DateOnly Expiration = new(2026, 8, 10);

    [Fact]
    public async Task An_exit_survives_a_restart_and_still_submits_exactly_one_closing_order()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = new PaperAutomationStore(ConfigurationFor(connectionString));

        AutomationDecision first;
        int firstOrders;

        // The process that closes the position.
        using (var harness = Harness(store))
        {
            first = await harness.EvaluateScheduledAsync();
            firstOrders = harness.OrdersPosted;
        }

        Assert.Equal(AutomationActions.ExitSubmitted, first.Action);
        Assert.True(first.OrderSubmitted);
        Assert.Equal(1, firstOrders);
        Assert.NotEqual(0, first.DecisionId);

        // A second service over the same log: a restarted process, with an empty claim set and the
        // position still open because the close has not filled. Nothing in memory can help it.
        using var restarted = Harness(store);

        var second = await restarted.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.NoTrade, second.Action);
        Assert.Contains("already accepted on this trading date", second.ActionReason);
        Assert.Equal(0, restarted.OrdersPosted);

        // Measured on the table: two decisions, one order.
        var rows = await store.RecentAsync(10, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.OrderSubmitted);

        // The claim key round-trips through jsonb, which is the projection this whole test exists for.
        var keys = await store.ExitKeysOrderedOnAsync(TradingDate, CancellationToken.None);
        Assert.Single(keys);
        Assert.StartsWith("SPY|2026-08-10|", keys[0]);
    }

    [Fact]
    public async Task The_cap_counts_a_closing_order_and_an_exit_whose_outcome_was_lost()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = new PaperAutomationStore(ConfigurationFor(connectionString));

        await store.RecordAsync(ExitRow(AutomationActions.ExitSubmitted, TradingDate, "key-a"), CancellationToken.None);
        await store.RecordAsync(ExitRow(AutomationActions.ExitOutcomeUnknown, TradingDate, "key-b"), CancellationToken.None);
        await store.RecordAsync(ExitRow(AutomationActions.ExitRefused, TradingDate, "key-c"), CancellationToken.None);
        await store.RecordAsync(ExitRow(AutomationActions.ExitRejected, TradingDate, "key-d"), CancellationToken.None);

        // An exit-submitted row carries an order id, and an exit-outcome-unknown closing order may be
        // resting at the venue: both are orders this loop put there and both count. An exit-rejected
        // one also reached ExecutionService and has an id to reconcile against, so it counts too —
        // the same treatment a risk-rejected ENTRY already gets, and the conservative direction for a
        // rail on new exposure. A plan-time refusal placed no order and does not count.
        Assert.Equal(3, await store.CountSubmittedOnAsync(TradingDate, CancellationToken.None));

        var submitted = await store.SubmittedOnAsync(TradingDate, CancellationToken.None);
        Assert.Equal(3, submitted.Count);

        // The CLAIM query follows a different split, and this is the line that keeps a rejected close
        // retryable: only an order that might exist at a venue suppresses the next attempt. Counting
        // an order and claiming a position are separate questions and they answer differently here.
        var keys = await store.ExitKeysOrderedOnAsync(TradingDate, CancellationToken.None);
        Assert.Equal(["key-a", "key-b"], keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Yesterdays_closing_order_does_not_suppress_todays()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = new PaperAutomationStore(ConfigurationFor(connectionString));

        await store.RecordAsync(
            ExitRow(AutomationActions.ExitSubmitted, TradingDate.AddDays(-1), "key-a"), CancellationToken.None);

        // One closing order per position per TRADING DATE, which is what makes a close that never
        // filled get another attempt tomorrow instead of leaving the position to expire.
        Assert.Empty(await store.ExitKeysOrderedOnAsync(TradingDate, CancellationToken.None));
        Assert.Single(await store.ExitKeysOrderedOnAsync(TradingDate.AddDays(-1), CancellationToken.None));
    }

    // ---- fixtures --------------------------------------------------------------------------------

    /// <summary>
    /// The same loop the unit suite drives, over a real decision log and holding a due position.
    /// </summary>
    /// <remarks>
    /// The quotes make the close a 0.41 natural debit (buy the 740 back at 0.58, sell the 739 wing at
    /// 0.17), 0.46 with the marketable buffer.
    /// </remarks>
    private static PaperAutomationServiceTests.Harness Harness(IPaperAutomationStore store) =>
        new(
            signal: new SignalResult(SignalStates.Enter, "A test signal asking for a position.", Trade: true),
            longAsk: 0.20m,
            shortBid: 0.55m,
            cap: 5,
            positions:
            [
                Position(740m, -1),
                Position(739m, 1),
            ],
            store: store);

    private static PositionSnapshot Position(decimal strike, int quantity) =>
        new(
            new OptionContract(
                $"SPY{Expiration:yyyyMMdd}P{strike:F0}", "SPY", Expiration, strike, OptionRight.Put,
                TradingClass: "SPY"),
            quantity,
            1.00m,
            GreeksVector.Zero);

    /// <summary>A hand-built exit row, so the store's queries can be tested without the loop.</summary>
    /// <remarks>
    /// The order id follows the schema's CHECK: exit-submitted and exit-rejected both reached
    /// ExecutionService and have one; exit-outcome-unknown and exit-refused do not.
    /// </remarks>
    private static AutomationDecision ExitRow(string action, DateOnly tradingDate, string exitKey) =>
        new(
            0,
            new DateTimeOffset(tradingDate.ToDateTime(new TimeOnly(14, 0)), TimeSpan.Zero),
            AutomationTriggers.Scheduled,
            true, ArmStates.Armed, "Armed for the test.",
            "NYSE", "regular", tradingDate, true,
            SignalStates.NotEvaluated, "The exit rule is unconditional.", null,
            action, $"{AutomationExitRules.Dte}: seeded row.",
            OrderSubmitted: action is AutomationActions.ExitSubmitted or AutomationActions.ExitRejected,
            OrderId: action is AutomationActions.ExitSubmitted or AutomationActions.ExitRejected
                ? Guid.NewGuid()
                : null,
            CorrelationId: null,
            LifecycleStatus: null,
            LimitPrice: 0.46m,
            LimitPriceSource: LimitPriceSources.ComputedMarketable,
            OrdersThisSession: 1,
            OrderCap: 5,
            Detail: $$"""{"exitKey":"{{exitKey}}","rule":"exit-dte"}""");

    // ---- plumbing -------------------------------------------------------------------------------

    private static async Task<string> PrepareAsync(string server)
    {
        var connectionString = PostgresCollection.FreshDatabase(server);
        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        // Fails loudly here rather than as a confusing query error three asserts later.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('research.paper_automation_decisions')::text", connection);
        Assert.Equal("research.paper_automation_decisions", await command.ExecuteScalarAsync());

        return connectionString;
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();
}
