namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// Asks for a position because the protocol mandates constant short-volatility exposure, and for no
/// other reason. Opt-in via <c>PaperAutomation:Signal=constant-exposure</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads no forecast and no market state. That is the point, not an omission.</b>
/// <c>docs/plans/paper-run-protocol.md</c> § Trading rule: "Constant one-vega-equivalent exposure.
/// QCJ does not determine whether to trade or how much." The confirmatory scale-down failure is what
/// put that sentence there, and a signal that consulted QCJ, HAR-X or the VRP spread "just to
/// decide entry" would be the rejected hypothesis re-entering through the door marked timing. Every
/// model quantity the run computes belongs in <c>research.vol_shadow_marks</c>, where it influences
/// nothing.
/// </para>
/// <para>
/// <b>What it does read is the authorization.</b> Protocol § Phases 2: entry "requires a registered
/// decision that the paper run may proceed on dev-provenance infrastructure (the signal's provenance
/// refusal is amended by that decision for PAPER only, never live)". So the one input is an unrevoked
/// row in <c>research.paper_run_decisions</c>, signed by a human. Nothing in this service creates one;
/// the endpoint exists and the operator calls it.
/// </para>
/// <para>
/// <b>This does not replace <see cref="VolResidualSignal"/>, which stays the default and keeps
/// refusing.</b> The two answer different questions: that one asks whether a forecast justifies a
/// position (it never does, by construction), this one asks whether the mandated constant exposure is
/// authorized today. Selecting between them is a configuration decision, so the live path — which
/// nothing here reaches anyway — is untouched by this file's existence.
/// </para>
/// </remarks>
public sealed class ConstantExposureSignal(
    IPaperRunDecisionStore decisions,
    ILogger<ConstantExposureSignal> logger) : IAutomationSignal
{
    public string Name => "constant-exposure/paper-decision";

    public async Task<SignalResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        PaperRunDecision? active;

        try
        {
            active = await decisions.GetActiveAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex, "The paper-run decision table could not be read; entry is refused because authorization " +
                    "is unknown, not because it is absent.");

            // A read failure is a no-trade, and it says which kind. "Nobody asked" and "the answer was
            // no" are the same row otherwise, and the second one is not something to page anyone about.
            return new SignalResult(
                SignalStates.PaperDecisionUnreadable,
                $"research.paper_run_decisions could not be read, so it is unknown whether the paper run is " +
                $"authorized: {ex.Message}",
                Trade: false);
        }

        return Interpret(active);
    }

    /// <summary>
    /// The decision-to-signal mapping, separated from the read so it can be tested without a database.
    /// </summary>
    /// <remarks>
    /// The only branch that returns <c>Trade: true</c> is the one holding an unrevoked, paper-scoped
    /// row. A revoked decision takes the same path as no decision at all — revocation is meant to stop
    /// entry on the next evaluation, without a restart and without editing configuration.
    /// </remarks>
    internal static SignalResult Interpret(PaperRunDecision? active)
    {
        if (active is null)
        {
            return new SignalResult(
                SignalStates.NoPaperDecision,
                "No unrevoked paper-run decision is registered, so the paper run is not authorized to proceed " +
                "on dev-provenance infrastructure (docs/plans/paper-run-protocol.md § Phases 2). An operator " +
                "registers one with POST /research/paper-run/decision; automation does not.",
                Trade: false);
        }

        if (!active.IsActive || !string.Equals(active.Scope, PaperRunScopes.Paper, StringComparison.Ordinal))
        {
            // Defensive, and kept rather than trimmed: the store's query and migration 023's CHECK both
            // rule this out today, so reaching it means one of those two stopped holding. The safe
            // answer to "the row is not what it claimed to be" is no.
            return new SignalResult(
                SignalStates.NoPaperDecision,
                $"Decision {active.DecisionId} is scoped '{active.Scope}'" +
                $"{(active.IsActive ? string.Empty : $" and was revoked at {active.RevokedAt:yyyy-MM-dd HH:mm}Z")}, " +
                "so it does not authorize paper entry.",
                Trade: false);
        }

        return new SignalResult(
            SignalStates.Enter,
            $"constant one-vega mandate per paper-run-protocol, decision {active.DecisionId} " +
            $"(signed by {active.SignedBy} at {active.DecidedAt:yyyy-MM-dd HH:mm}Z against {active.ProtocolRef}). " +
            "No forecast was consulted: the protocol's exposure is constant by construction.",
            Trade: true);
    }
}
