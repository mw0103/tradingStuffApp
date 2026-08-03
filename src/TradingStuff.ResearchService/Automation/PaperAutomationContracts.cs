using TradingStuff.Contracts;

namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// Why automation is (or is not) allowed to act. Every value other than <see cref="Armed"/> is a
/// refusal, and every refusal carries its own reason string to the status endpoint and to
/// <c>research.paper_automation_decisions</c>.
/// </summary>
public static class ArmStates
{
    /// <summary><c>PaperAutomation:Enabled</c> is not the exact opt-in value.</summary>
    public const string Disabled = "disabled";

    /// <summary>The kill switch is engaged.</summary>
    public const string Killed = "killed";

    /// <summary>
    /// The execution plane's settings do not agree with each other. This is the
    /// <c>docs/LESSONS.md</c> §9 case and it is the reason this whole check exists.
    /// </summary>
    public const string IncoherentConfiguration = "refused-incoherent-configuration";

    /// <summary>The broker connection, the trading gate, or the account prefix is not usable.</summary>
    public const string BrokerUnusable = "refused-broker-unusable";

    /// <summary>The per-session order cap is spent. A rail, not a knob — it stops, it does not wrap.</summary>
    public const string CapReached = "refused-cap-reached";

    /// <summary>Preflight could not be completed at all (a service did not answer).</summary>
    public const string PreflightFailed = "refused-preflight-failed";

    public const string Armed = "armed";
}

/// <summary>What the signal source said, as recorded verbatim on every decision row.</summary>
public static class SignalStates
{
    /// <summary>No study run exists to read.</summary>
    public const string NoRun = "no-run";

    /// <summary>The latest run reported <c>insufficient-data</c>. Today this is the only real state.</summary>
    public const string InsufficientData = "insufficient-data";

    /// <summary>
    /// The run completed but is a DEVELOPMENT run, which is not tradeable by construction — see
    /// <see cref="VolResidualSignal"/>.
    /// </summary>
    public const string DevelopmentRun = "development-run";

    /// <summary>The signal is asking for a position.</summary>
    public const string Enter = "enter";

    /// <summary>
    /// No unrevoked <c>research.paper_run_decisions</c> row exists, so the paper run is not authorized
    /// to proceed on dev-provenance infrastructure — see <see cref="ConstantExposureSignal"/>.
    /// </summary>
    public const string NoPaperDecision = "no-paper-decision";

    /// <summary>
    /// The decision table could not be read, so authorization is UNKNOWN. Distinct from
    /// <see cref="NoPaperDecision"/> deliberately: both refuse entry, but only one of them is a fault
    /// somebody has to fix, and a decision log that cannot tell them apart reports an outage as a
    /// deliberate no-trade.
    /// </summary>
    public const string PaperDecisionUnreadable = "paper-decision-unreadable";

    /// <summary>The signal was deliberately not consulted (the manual endpoint).</summary>
    public const string NotEvaluated = "not-evaluated";
}

/// <summary>What the evaluation did.</summary>
/// <remarks>
/// The four <c>exit-*</c> values mirror the entry three deliberately rather than reusing them. An
/// entry and an exit are opposite transitions of the same position and a record that cannot tell them
/// apart cannot answer "did the loop close what it opened?" — which is precisely the protocol's
/// success criterion 3. The parallel naming is also load-bearing for the cap query: see
/// <c>PaperAutomationStore.CountSubmittedOnAsync</c>, which counts BOTH unknown-outcome shapes.
/// </remarks>
public static class AutomationActions
{
    /// <summary>Evaluated, and the answer was "do not trade". The overwhelmingly common row.</summary>
    public const string NoTrade = "no-trade";

    /// <summary>The signal asked for a trade and something downstream refused it (no chain, no price, a risk rejection).</summary>
    public const string Refused = "refused";

    /// <summary>An order was handed to ExecutionService.</summary>
    public const string Submitted = "submitted";

    /// <summary>An order was handed over and no outcome came back. It may exist at the venue.</summary>
    public const string OutcomeUnknown = "outcome-unknown";

    /// <summary>A closing order was handed to ExecutionService for a position at or below the DTE threshold.</summary>
    public const string ExitSubmitted = "exit-submitted";

    /// <summary>
    /// A position is due to be closed and the closing order could not be constructed or priced. The
    /// position is still open; the next pass tries again.
    /// </summary>
    public const string ExitRefused = "exit-refused";

    /// <summary>A closing order was handed over and no outcome came back. It may exist at the venue.</summary>
    public const string ExitOutcomeUnknown = "exit-outcome-unknown";
}

/// <summary>
/// The exit rules this build knows. There is one, and adding a second is a decision, not an edit.
/// </summary>
/// <remarks>
/// Recorded verbatim at the head of every exit decision's <c>action_reason</c> and in its
/// <c>detail</c>, so a row can never say "closed" without saying which rule closed it. See
/// <see cref="PaperAutomationOptions.ExitDteThreshold"/> for why the list is one long.
/// </remarks>
public static class AutomationExitRules
{
    /// <summary>Calendar days to expiration at or below <see cref="PaperAutomationOptions.ExitDteThreshold"/>.</summary>
    public const string Dte = "exit-dte";
}

/// <summary>One row of <c>research.paper_automation_decisions</c>.</summary>
public sealed record AutomationDecision(
    long DecisionId,
    DateTimeOffset DecidedAt,
    string Trigger,
    bool Armed,
    string ArmState,
    string ArmReason,
    string? SessionCalendar,
    string? SessionLabel,
    DateOnly? SessionTradingDate,
    bool InSession,
    string SignalState,
    string SignalReason,
    Guid? StudyRunId,
    string Action,
    string ActionReason,
    bool OrderSubmitted,
    Guid? OrderId,
    Guid? CorrelationId,
    string? LifecycleStatus,
    decimal? LimitPrice,
    string? LimitPriceSource,
    int OrdersThisSession,
    int OrderCap,
    string? Detail);

/// <summary>The signal source's answer.</summary>
/// <param name="Trade">
/// The ONLY field that can cause an order. Everything else is reporting. False for every state
/// except <see cref="SignalStates.Enter"/>.
/// </param>
public sealed record SignalResult(string State, string Reason, bool Trade, Guid? StudyRunId = null);

/// <summary>The execution plane's effective settings, as measured rather than assumed.</summary>
/// <param name="Router">The name of the <c>IOrderRouter</c> ExecutionService actually resolved.</param>
/// <param name="PortfolioSource">The source of the <c>IPortfolioProvider</c> it actually resolved.</param>
/// <param name="MarketDataSource">
/// The <c>Source</c> of the quote provider MarketDataService actually resolved — <c>ibkr-gateway</c>
/// or <c>ibkr-deterministic-paper-feed</c>. <b>This is the field the coherence check runs on.</b> A
/// check on the configuration string would have caught the 2026-08-01 incident, but it can only ever
/// see ONE service's copy of that string; the resolved provider is a measurement of what will
/// actually price the order, and it also catches the case a string check structurally cannot —
/// ExecutionService and MarketDataService holding different values for the same setting.
/// </param>
/// <param name="MarketDataSourceConfigured">
/// The <c>MarketData:Source</c> string as ExecutionService sees it. Reported for the operator, never
/// checked: it names the intent (delayed vs live) that the resolved provider name does not carry.
/// </param>
public sealed record ExecutionPlaneConfiguration(
    string Router,
    string PortfolioSource,
    string MarketDataSource,
    string? MarketDataSourceConfigured);

/// <summary>What preflight decided, and why.</summary>
public sealed record ArmingResult(bool Armed, string State, string Reason)
{
    public static ArmingResult Ok(string reason) => new(true, ArmStates.Armed, reason);

    public static ArmingResult Refuse(string state, string reason) => new(false, state, reason);
}

/// <summary>A constructed order plus the provenance of the price on it.</summary>
public sealed record PlannedOrder(
    SubmitOrderRequest Request,
    decimal LimitPrice,
    string LimitPriceSource,
    string Description);

/// <summary>Why an order could not be constructed. Never a fabricated fallback.</summary>
public sealed record OrderPlanFailure(string Reason);

public static class LimitPriceSources
{
    /// <summary>Derived from live quotes: long ask − short bid, plus the marketable buffer.</summary>
    public const string ComputedMarketable = "computed-marketable";

    /// <summary>
    /// Supplied by the operator on the manual endpoint. The automated path can never produce this
    /// value, and a row carrying it was not an automated decision.
    /// </summary>
    public const string OperatorSupplied = "operator-supplied";
}

/// <summary>The body of <c>GET /research/automation</c>.</summary>
public sealed record AutomationStatusReport(
    bool Enabled,
    bool Armed,
    string ArmState,
    string ArmReason,
    DateTimeOffset? ArmCheckedAt,
    KillSwitchStatus KillSwitch,
    ExecutionPlaneConfiguration? ExecutionPlane,
    string? ExecutionPlaneError,
    SessionStatus Session,
    string SignalSource,
    AutomationDecision? LastDecision,
    IReadOnlyList<AutomationDecision> RecentDecisions,
    IReadOnlyList<AutomationDecision> SubmittedThisSession,
    int OrdersThisSession,
    int OrderCap,
    int CapRemaining,
    string? PersistenceError,
    string Notes);

public sealed record KillSwitchStatus(bool Engaged, DateTimeOffset? EngagedAt, string? Reason, string Durability);

public sealed record SessionStatus(string Calendar, bool InSession, string? Label, DateOnly? TradingDate, DateOnly SessionKey);
