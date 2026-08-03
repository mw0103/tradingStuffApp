namespace TradingStuff.ResearchService.Automation;

/// <summary>What the gateway reported about the broker connection, as measured at preflight.</summary>
public sealed record BrokerFacts(
    bool Connected,
    bool TradingPermitted,
    string? TradingBlockedReason,
    IReadOnlyList<string> ManagedAccounts);

/// <summary>
/// The gate between "automation is switched on" and "automation may place an order".
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of docs/LESSONS.md §9, and it is the only reason it exists.</b> Every
/// individual setting in the execution plane is fail-safe: an unrecognised <c>Execution:Router</c>
/// stays on the simulated engine, an unrecognised <c>Portfolio:Source</c> stays on fixed development
/// figures, an unrecognised <c>MarketData:Source</c> stays on the deterministic generator. Each is
/// correct alone and the combination is not — on 2026-08-01 <c>MarketData:Source</c> was set to
/// <c>"ibkr"</c>, a plausible string that is not one of the two recognised values, so the quote
/// provider silently degraded to the generator while <c>Execution:Router=ibkr</c> kept transmitting,
/// and a 10-lot SPY vertical was approved against invented bid 27.34 / ask 28.46 on a Saturday when
/// the real market was 0/0. Neither component can see that the other changed meaning. Something has
/// to look at all three together, and for automation that something is here.
/// </para>
/// <para>
/// <b>Everything this function reads is a measurement, not a configuration string.</b>
/// <see cref="ExecutionPlaneConfiguration.Router"/> is the name of the router ExecutionService
/// actually resolved, <see cref="ExecutionPlaneConfiguration.PortfolioSource"/> the source of the
/// provider it actually resolved, and <see cref="ExecutionPlaneConfiguration.MarketDataSource"/> the
/// <c>Source</c> of the quote provider MarketDataService actually resolved. ResearchService reading
/// its own copy of the same environment variables would prove only that ResearchService was
/// configured a certain way, which is not the claim that matters and is exactly the assumption the
/// incident above was made of.
/// </para>
/// <para>
/// <b>Failing to establish a fact is a refusal, never a pass.</b> An unreachable ExecutionService, a
/// gateway that will not answer, an empty account list — all refuse. There is no branch in which an
/// unknown becomes an assumed-good.
/// </para>
/// <para>
/// This is a pure function over already-gathered facts precisely so it can be tested exhaustively
/// without a broker, an HTTP stack, or a clock. <see cref="PaperAutomationService"/> gathers; this
/// decides.
/// </para>
/// </remarks>
public static class PaperAutomationArming
{
    /// <summary>The exact router name automation requires. Nothing else routes to the paper account.</summary>
    public const string RequiredRouter = "ibkr";

    /// <summary>The exact portfolio source automation requires.</summary>
    public const string RequiredPortfolioSource = "ibkr";

    /// <summary>
    /// The <c>Source</c> a resolved <c>IbkrOptionMarketDataProvider</c> reports.
    /// </summary>
    /// <remarks>
    /// <b>This is the provider's own name, not a <c>MarketData:Source</c> configuration value</b>, and
    /// getting that wrong is a trap this check walked into once already: the accepted set was written
    /// as <c>["ibkr-live", "ibkr-delayed"]</c> — the two configuration strings — and compared against
    /// what MarketDataService actually reports, which is <c>"ibkr-gateway"</c> for either of them. It
    /// would have matched nothing and refused to arm forever, in the safe direction but for a reason
    /// that had nothing to do with the configuration it claimed to be checking.
    /// <para>
    /// The distinction is the point rather than an annoyance. <c>ibkr-live</c> and <c>ibkr-delayed</c>
    /// are a statement of intent that lives in a configuration file; <c>ibkr-gateway</c> is the answer
    /// to the only question that decides whether an order means anything — are these quotes coming
    /// from TWS, or from the generator? A rename on the provider side would silently disarm this, so
    /// it is asserted by test against the real provider rather than trusted as a literal.
    /// </para>
    /// </remarks>
    public const string RequiredMarketDataProvider = "ibkr-gateway";

    /// <summary>The account prefix that identifies a simulated-money paper account.</summary>
    public const string PaperAccountPrefix = "DU";

    public static ArmingResult Evaluate(
        bool enabled,
        bool killSwitchEngaged,
        string? killSwitchReason,
        ExecutionPlaneConfiguration? executionPlane,
        string? executionPlaneError,
        BrokerFacts? broker,
        string? brokerError,
        int ordersThisSession,
        int orderCap)
    {
        if (!enabled)
        {
            return ArmingResult.Refuse(
                ArmStates.Disabled,
                "PaperAutomation:Enabled is not 'true'. Automation is off and no evaluation runs.");
        }

        // Ahead of every configuration check on purpose: the operator has said stop, and a refusal
        // reason of "your router is wrong" would send them to fix the wrong thing.
        if (killSwitchEngaged)
        {
            return ArmingResult.Refuse(
                ArmStates.Killed,
                $"The kill switch is engaged{(string.IsNullOrWhiteSpace(killSwitchReason) ? string.Empty : $": {killSwitchReason}")}. " +
                "POST /research/automation/resume to re-arm.");
        }

        if (executionPlaneError is { } planeError)
        {
            return ArmingResult.Refuse(
                ArmStates.PreflightFailed,
                $"The execution plane's effective configuration could not be established, so it " +
                $"cannot be checked for coherence: {planeError}");
        }

        if (executionPlane is null)
        {
            return ArmingResult.Refuse(
                ArmStates.PreflightFailed,
                "No execution-plane configuration was read. Automation does not arm on an unknown.");
        }

        // All three together, and the message names all three values whichever one is wrong: an
        // operator reading "MarketData:Source is deterministic" needs to see the other two to know
        // whether they are looking at a half-configured stack or a fully unconfigured one.
        var routerOk = string.Equals(executionPlane.Router, RequiredRouter, StringComparison.OrdinalIgnoreCase);
        var portfolioOk = string.Equals(executionPlane.PortfolioSource, RequiredPortfolioSource, StringComparison.OrdinalIgnoreCase);
        var marketDataOk = string.Equals(
            executionPlane.MarketDataSource, RequiredMarketDataProvider, StringComparison.OrdinalIgnoreCase);

        if (!routerOk || !portfolioOk || !marketDataOk)
        {
            var faults = new List<string>();

            if (!routerOk)
            {
                faults.Add(
                    $"ExecutionService resolved the '{executionPlane.Router}' router, not '{RequiredRouter}' — " +
                    "orders would be filled by the local simulator, not the paper account");
            }

            if (!portfolioOk)
            {
                faults.Add(
                    $"ExecutionService resolved the '{executionPlane.PortfolioSource}' portfolio provider, not " +
                    $"'{RequiredPortfolioSource}' — risk would evaluate against fabricated buying power and a flat day");
            }

            if (!marketDataOk)
            {
                faults.Add(
                    $"MarketDataService resolved the '{executionPlane.MarketDataSource}' quote provider, not " +
                    $"'{RequiredMarketDataProvider}' — risk would price against confident, well-formed, entirely " +
                    $"fictional quotes (its MarketData:Source is '{executionPlane.MarketDataSourceConfigured}', " +
                    "which is not one of the recognised opt-in values, or differs from ExecutionService's copy)");
            }

            return ArmingResult.Refuse(
                ArmStates.IncoherentConfiguration,
                "The execution plane's settings do not agree, so an order placed now would not mean what it " +
                $"appears to mean: {string.Join("; ", faults)}. Measured: router='{executionPlane.Router}', " +
                $"portfolio='{executionPlane.PortfolioSource}', marketData='{executionPlane.MarketDataSource}'.");
        }

        if (brokerError is { } gatewayError)
        {
            return ArmingResult.Refuse(
                ArmStates.BrokerUnusable,
                $"The IBKR gateway's status could not be read, so neither the connection nor the account " +
                $"prefix is established: {gatewayError}");
        }

        if (broker is null)
        {
            return ArmingResult.Refuse(
                ArmStates.BrokerUnusable,
                "No broker status was read. Automation does not arm on an unknown.");
        }

        if (!broker.Connected)
        {
            return ArmingResult.Refuse(ArmStates.BrokerUnusable, "The gateway is not connected to TWS.");
        }

        if (!broker.TradingPermitted)
        {
            return ArmingResult.Refuse(
                ArmStates.BrokerUnusable,
                $"The gateway's trading gate is closed: {broker.TradingBlockedReason ?? "no reason reported"}.");
        }

        if (broker.ManagedAccounts.Count == 0)
        {
            return ArmingResult.Refuse(
                ArmStates.BrokerUnusable,
                "The gateway reported no managed accounts, so the paper-account prefix cannot be verified.");
        }

        // Defence in depth, not the primary gate — IbkrConnection already refuses to permit trading on
        // a non-DU account unless IBKR:AllowLiveTrading is true. Repeated here because automation is a
        // process that places orders without a human in the loop, and the one property that must hold
        // for that to be acceptable is that the money is simulated. Checking it in the component that
        // decides to trade costs four lines and does not depend on another process's gate having been
        // configured the way this one assumes.
        //
        // EVERY account, not just the first: a session managing both a DU and a U account is one where
        // the gateway's configured account could be either, and this component cannot see which.
        if (broker.ManagedAccounts.FirstOrDefault(a => !a.StartsWith(PaperAccountPrefix, StringComparison.Ordinal)) is { } live)
        {
            return ArmingResult.Refuse(
                ArmStates.BrokerUnusable,
                $"The gateway is managing an account whose id does not start with '{PaperAccountPrefix}' " +
                $"(ending '{Redact(live)}'). Automation places orders only against a simulated-money paper account.");
        }

        // Last, and it stops rather than wrapping. A cap that resets is not a cap.
        if (ordersThisSession >= orderCap)
        {
            return ArmingResult.Refuse(
                ArmStates.CapReached,
                $"The per-session order cap is spent: {ordersThisSession} of {orderCap} orders submitted on this " +
                "trading date. Automation stops here for the session; it does not reset until the trading date does.");
        }

        return ArmingResult.Ok(
            $"Router '{executionPlane.Router}', portfolio '{executionPlane.PortfolioSource}', quotes " +
            $"'{executionPlane.MarketDataSource}', gateway connected on a {PaperAccountPrefix} account, " +
            $"{orderCap - ordersThisSession} of {orderCap} orders remaining.");
    }

    /// <summary>
    /// Whether a verdict from <see cref="Evaluate"/> still permits a CLOSING order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one refusal is passable, and it is the cap. Every other state means an order would not
    /// mean what it appears to mean — a simulated router, fabricated quotes, an unknown account, a
    /// gateway that will not answer — or that the operator has pressed stop, and none of those get
    /// better because the order in question reduces exposure rather than adding it. The cap is
    /// different in kind: it is a rail on how much NEW exposure one trading date may acquire, and
    /// enforcing it against an exit would leave a position open into an expiration the loop cannot
    /// handle. An uncloseable position is worse than an extra order, so the cap COUNTS exits (see
    /// <c>PaperAutomationStore.CountSubmittedOnAsync</c>) and does not block them.
    /// </para>
    /// <para>
    /// Deliberately a predicate over the one verdict rather than a second evaluation with the cap
    /// dropped. Two orderings of the same rules that could disagree is worse than one — the same
    /// reasoning the status endpoint records for re-deriving its own precedence.
    /// </para>
    /// </remarks>
    public static bool PermitsExit(ArmingResult arming) =>
        arming.Armed || string.Equals(arming.State, ArmStates.CapReached, StringComparison.Ordinal);

    /// <summary>Account numbers are never written whole to a log, a row, or an HTTP response.</summary>
    private static string Redact(string account) =>
        account.Length <= 3 ? "***" : $"***{account[^3..]}";
}
