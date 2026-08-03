using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.ResearchService.Automation;

namespace TradingStuff.Tests;

/// <summary>
/// The one signal in this platform that can say "trade", and the single fact it says it on.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist to hold two properties in place, and they pull in opposite directions on
/// purpose. The first: an unrevoked <c>research.paper_run_decisions</c> row is SUFFICIENT — the
/// protocol mandates constant one-vega exposure and the signal must not quietly acquire a second
/// condition (a forecast, a spread, a bucket) that turns the rejected QCJ hypothesis back into a
/// timing rule. The second: it is NECESSARY — no decision, a revoked decision, or an unreadable
/// table each refuse, and each says which of those it was.
/// </para>
/// <para>
/// The store is faked so both are provable without a database; the same shapes are then proved
/// against real SQL in <see cref="PaperRunDecisionStorePostgresTests"/>.
/// </para>
/// </remarks>
public sealed class ConstantExposureSignalTests
{
    private static PaperRunDecision Decision(
        long id = 7,
        string scope = PaperRunScopes.Paper,
        DateTimeOffset? revokedAt = null) =>
        new(id,
            new DateTimeOffset(2026, 8, 3, 14, 30, 0, TimeSpan.Zero),
            scope,
            "docs/plans/paper-run-protocol.md",
            "The paper run may proceed on dev-provenance infrastructure.",
            "Madison",
            revokedAt,
            revokedAt is null ? null : "superseded");

    [Fact]
    public void An_active_decision_asks_for_the_constant_position_and_names_its_authorization()
    {
        var result = ConstantExposureSignal.Interpret(Decision());

        Assert.True(result.Trade);
        Assert.Equal(SignalStates.Enter, result.State);

        // The protocol's own words on the row, and the decision id that authorized it. A row reading
        // only "trade" cannot answer "who signed for this position?" months later.
        Assert.Contains("constant one-vega mandate per paper-run-protocol, decision 7", result.Reason);
        Assert.Contains("Madison", result.Reason);
        Assert.Contains("docs/plans/paper-run-protocol.md", result.Reason);
    }

    [Fact]
    public void No_decision_is_a_no_trade_naming_exactly_what_is_missing()
    {
        var result = ConstantExposureSignal.Interpret(null);

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.NoPaperDecision, result.State);

        // Actionable, not merely negative: the operator has to be able to read the refusal and know
        // what would lift it, without reading this class.
        Assert.Contains("POST /research/paper-run/decision", result.Reason);
        Assert.Contains("dev-provenance infrastructure", result.Reason);
    }

    /// <summary>
    /// Revocation is the stop button, and it works on the row rather than on configuration.
    /// </summary>
    /// <remarks>
    /// The store only ever returns unrevoked rows, so this branch is defensive — and it is asserted
    /// because the alternative shape (trusting the caller's query) is one edit away from a revoked
    /// decision still trading.
    /// </remarks>
    [Fact]
    public void A_revoked_decision_does_not_trade_even_if_it_reaches_the_mapping()
    {
        var revoked = Decision(revokedAt: new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero));

        var result = ConstantExposureSignal.Interpret(revoked);

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.NoPaperDecision, result.State);
        Assert.Contains("revoked", result.Reason);
    }

    /// <summary>
    /// A non-paper scope refuses, however it got there.
    /// </summary>
    /// <remarks>
    /// Migration 023's CHECK makes such a row unconstructible through the schema. This asserts the
    /// code does not depend on that being the only guard: "never live" is the clause the whole
    /// mechanism exists to keep, so it is checked where the trade decision is actually made.
    /// </remarks>
    [Fact]
    public void A_decision_scoped_to_anything_but_paper_does_not_authorize_entry()
    {
        var result = ConstantExposureSignal.Interpret(Decision(scope: "live"));

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.NoPaperDecision, result.State);
        Assert.Contains("'live'", result.Reason);
    }

    /// <summary>
    /// An unreadable table refuses, and is NOT recorded as an absent decision.
    /// </summary>
    /// <remarks>
    /// Both states refuse entry, so a test that only checked <c>Trade</c> would pass against code
    /// that conflated them — and the conflation is the expensive one: "nobody could ask" would render
    /// in the decision log as "the answer was no", which is a fault nobody would go looking for.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_decision_table_refuses_as_unknown_rather_than_as_absent()
    {
        var signal = new ConstantExposureSignal(
            new ThrowingDecisionStore("connection refused"), NullLogger<ConstantExposureSignal>.Instance);

        var result = await signal.EvaluateAsync(CancellationToken.None);

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.PaperDecisionUnreadable, result.State);
        Assert.NotEqual(SignalStates.NoPaperDecision, result.State);
        Assert.Contains("connection refused", result.Reason);
    }

    [Fact]
    public async Task The_evaluated_signal_reads_the_store_and_trades_on_what_it_finds()
    {
        var store = new StubDecisionStore(Decision(id: 41));
        var signal = new ConstantExposureSignal(store, NullLogger<ConstantExposureSignal>.Instance);

        var entered = await signal.EvaluateAsync(CancellationToken.None);

        Assert.True(entered.Trade);
        Assert.Contains("decision 41", entered.Reason);

        store.Active = null;

        var refused = await signal.EvaluateAsync(CancellationToken.None);

        Assert.False(refused.Trade);
        Assert.Equal(SignalStates.NoPaperDecision, refused.State);
    }

    // ---- the configuration switch ----------------------------------------------------------------

    [Fact]
    public void The_default_signal_is_the_one_that_refuses_everything()
    {
        var selected = PaperAutomationOptions.Signals.Select(
            new PaperAutomationOptions().Signal, out var recognised);

        Assert.True(recognised);
        Assert.Equal(PaperAutomationOptions.Signals.VolResidual, selected);
    }

    /// <summary>
    /// Only the exact opt-in string selects the trading signal, and everything else lands on the
    /// refusing one while reporting that it was not recognised.
    /// </summary>
    /// <remarks>
    /// Case and whitespace are deliberately NOT tolerated. <c>PaperAutomation:Enabled</c> takes the
    /// same line and for the same reason: a value nobody recognised must degrade to the safe side,
    /// and the one direction this must never fail in is "a typo selected the signal that trades".
    /// </remarks>
    [Theory]
    [InlineData("constant-exposure", true, true)]
    [InlineData("vol-residual", false, true)]
    [InlineData("Constant-Exposure", false, false)]
    [InlineData("constant exposure", false, false)]
    [InlineData(" constant-exposure ", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void Only_the_exact_opt_in_selects_the_trading_signal(
        string? configured, bool expectConstantExposure, bool expectRecognised)
    {
        var selected = PaperAutomationOptions.Signals.Select(configured, out var recognised);

        Assert.Equal(expectRecognised, recognised);
        Assert.Equal(
            expectConstantExposure
                ? PaperAutomationOptions.Signals.ConstantExposure
                : PaperAutomationOptions.Signals.VolResidual,
            selected);
    }

    private sealed class StubDecisionStore(PaperRunDecision? active) : IPaperRunDecisionStore
    {
        public PaperRunDecision? Active { get; set; } = active;

        public Task<PaperRunDecision?> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Active);

        public Task<PaperRunDecisionResult> RegisterAsync(
            string statement, string signedBy, string protocolRef, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The signal must never register a decision.");

        public Task<PaperRunDecisionResult> RevokeActiveAsync(string? reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The signal must never revoke a decision.");

        public Task<IReadOnlyList<PaperRunDecision>> ListAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaperRunDecision>>([]);
    }

    private sealed class ThrowingDecisionStore(string message) : IPaperRunDecisionStore
    {
        public Task<PaperRunDecision?> GetActiveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<PaperRunDecisionResult> RegisterAsync(
            string statement, string signedBy, string protocolRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<PaperRunDecisionResult> RevokeActiveAsync(string? reason, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<IReadOnlyList<PaperRunDecision>> ListAsync(int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
