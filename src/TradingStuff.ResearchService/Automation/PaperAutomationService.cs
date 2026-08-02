using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Automation;

/// <summary>How an evaluation was triggered. These must never be confusable in the record.</summary>
public static class AutomationTriggers
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
}

/// <summary>
/// The paper-automation loop: evaluate on a timer, record every evaluation, and submit at most
/// <see cref="PaperAutomationOptions.MaxOrdersPerSession"/> orders per trading date through
/// ExecutionService's ordinary order API.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this component is allowed to do.</b> It reads a signal, asks <see cref="ISessionClock"/>
/// what session it is in, builds a 1-lot SPY debit vertical, and POSTs it to ExecutionService. It
/// does not own order state, does not talk to the IBKR gateway about orders, and has no path to
/// <c>placeOrder</c> — that call site is in <c>PacedSocket</c>, two processes away, behind
/// ExecutionService's router and the gateway's own trading gate and persisted duplicate guard. The
/// research plane produces the signal; the execution plane owns the order. Collapsing that boundary
/// to "save a hop" would put an order-placement path inside the service that also runs migrations and
/// a backfill drain.
/// </para>
/// <para>
/// <b>Off by default, and enabling it is nowhere near sufficient.</b>
/// <c>PaperAutomation:Enabled</c> must be the exact string <c>true</c>, and then
/// <see cref="PaperAutomationArming"/> must independently establish that ExecutionService resolved
/// the IBKR router, that it resolved the IBKR portfolio provider, that MarketDataService resolved a
/// real quote provider, that the gateway is connected on a <c>DU</c> account, and that the cap is not
/// spent. Refusal is loud: Critical on every transition into a refusal state, and a permanent,
/// reasoned entry in <c>GET /research/automation</c> and in every decision row.
/// </para>
/// <para>
/// <b>Refusal is logged Critical on TRANSITION, not on every sweep.</b> A Critical every five minutes
/// for a stack that is simply configured for simulation is the permanently-red gate of
/// docs/LESSONS.md §10 — three separate incidents in this repository, each fixed by making the alarm
/// rarer and truer rather than louder. The state is always visible; the log fires when it changes.
/// </para>
/// </remarks>
public sealed class PaperAutomationService(
    IOptions<PaperAutomationOptions> options,
    IAutomationSignal signal,
    ISessionClock sessionClock,
    IPaperAutomationStore store,
    SpyVerticalPlanner planner,
    SpyShortVolPlanner shortVolPlanner,
    ExecutionServiceClient execution,
    MarketDataServiceClient marketData,
    IbkrGatewayClient gateway,
    TimeProvider timeProvider,
    ILogger<PaperAutomationService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions DetailOptions = new(JsonSerializerDefaults.Web);

    // One evaluation at a time. The scheduled loop and the manual endpoint both read the order count,
    // decide against the cap, and submit — a claim-then-act sequence that is only a cap if the read
    // and the claim cannot interleave with another one.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile KillSwitchState _killSwitch = new(false, null, null);

    // Orders handed over whose decision row is not yet counted by the table. Held per trading date so
    // a date roll cannot carry yesterday's claims forward. A claim is released only when a row that
    // the count query WILL see has been written; a failed write leaves it held, which spends the cap
    // on an order nobody can account for. That is the correct direction — the alternative is a cap
    // that a database hiccup can top up.
    private (DateOnly Date, int Pending) _claims = (DateOnly.MinValue, 0);

    private ArmingResult _lastArming = ArmingResult.Refuse(ArmStates.Disabled, "Not yet evaluated.");
    private DateTimeOffset? _lastArmCheckedAt;
    private ExecutionPlaneConfiguration? _lastPlane;
    private string? _lastPlaneError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.IsEnabled)
        {
            _lastArming = ArmingResult.Refuse(
                ArmStates.Disabled,
                $"PaperAutomation:Enabled is '{settings.Enabled ?? "(unset)"}', not 'true'.");

            // Not a silent no-op: the state is reported by GET /research/automation, and this line is
            // in the startup log. What it deliberately does NOT do is write decision rows — a loop
            // that is switched off has made no decisions, and manufacturing rows saying so would be
            // the mirror image of the absent-row defect rather than a fix for it.
            logger.LogInformation(
                "Paper automation is off (PaperAutomation:Enabled is '{Value}', not 'true'). No evaluation " +
                "will run and no order can be submitted from this service.",
                settings.Enabled ?? "(unset)");

            return;
        }

        logger.LogWarning(
            "Paper automation is ENABLED: every {Interval}s this service will evaluate the '{Signal}' signal and " +
            "may submit up to {Cap} order(s) per trading date to ExecutionService. Arming still requires a " +
            "coherent execution plane and a connected DU account.",
            settings.IntervalSeconds,
            signal.Name,
            settings.MaxOrdersPerSession);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds)), timeProvider);

        do
        {
            try
            {
                await EvaluateAsync(AutomationTriggers.Scheduled, null, false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The loop outlives its own failures. A faulted BackgroundService takes nothing else
                // down here (HostOptions sets Ignore) but it does stop evaluating, silently, which is
                // the failure this whole component is built to make impossible.
                logger.LogCritical(ex, "A paper-automation evaluation failed and was skipped.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Engages the kill switch. Takes effect on the next evaluation without restarting anything.</summary>
    public void Kill(string? reason)
    {
        _killSwitch = new KillSwitchState(true, timeProvider.GetUtcNow(), reason);

        logger.LogCritical(
            "Paper automation KILL SWITCH engaged{Reason}. No further orders will be submitted until it is released.",
            string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason}");
    }

    /// <summary>Releases the kill switch. Every other arming condition still has to hold.</summary>
    public void Resume()
    {
        _killSwitch = new KillSwitchState(false, null, null);
        logger.LogWarning("Paper automation kill switch released. Arming is re-evaluated on the next sweep.");
    }

    /// <summary>
    /// One evaluation, recorded whatever it decides.
    /// </summary>
    /// <param name="trigger">
    /// <see cref="AutomationTriggers.Scheduled"/> or <see cref="AutomationTriggers.Manual"/>. A manual
    /// evaluation does not consult the signal and may carry operator-supplied inputs; both facts are
    /// recorded on the row so the two can never be read as the same thing.
    /// </param>
    /// <param name="operatorLimitPrice">Manual only. Null on every scheduled evaluation.</param>
    /// <param name="acknowledgeOutsideSession">Manual only. Lets a human act outside a named session, on the record.</param>
    public async Task<AutomationDecision> EvaluateAsync(
        string trigger,
        decimal? operatorLimitPrice,
        bool acknowledgeOutsideSession,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return await EvaluateCoreAsync(trigger, operatorLimitPrice, acknowledgeOutsideSession, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutomationDecision> EvaluateCoreAsync(
        string trigger,
        decimal? operatorLimitPrice,
        bool acknowledgeOutsideSession,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();

        // ISessionClock is the only type in this platform permitted to convert a timezone, and this is
        // the only place automation asks what time it is in market terms. Nothing here reads a
        // wall clock, a local date, or TimeZoneInfo.
        var session = sessionClock.SessionAt(settings.Calendar, now);
        var tradingDate = sessionClock.TradingDateOf(settings.Calendar, now);

        var context = new DecisionContext(now, trigger, settings.Calendar, session, tradingDate, settings.MaxOrdersPerSession);

        // The cap first, because it is an input to arming. Derived from the table plus this process's
        // unaccounted claims — never from a counter that a restart resets.
        int ordersThisSession;

        try
        {
            var recorded = await store.CountSubmittedOnAsync(tradingDate, cancellationToken);
            ordersThisSession = recorded + PendingClaims(tradingDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing is written here, because the thing that failed IS the writer. Reported loudly
            // instead, and no order is submitted: automation does not act without an audit trail.
            logger.LogCritical(
                ex, "Paper automation could not read its own decision log, so it cannot know how many orders " +
                    "this session has already had. No order will be submitted.");

            _lastArming = ArmingResult.Refuse(
                ArmStates.PreflightFailed, $"The decision log could not be read: {ex.Message}");
            _lastArmCheckedAt = now;

            return context.Unrecorded(_lastArming, SignalStates.NotEvaluated, "Not reached.",
                AutomationActions.NoTrade, _lastArming.Reason, 0);
        }

        var (plane, planeError) = await ReadExecutionPlaneAsync(cancellationToken);
        var (broker, brokerError) = await ReadBrokerAsync(cancellationToken);

        _lastPlane = plane;
        _lastPlaneError = planeError;

        var arming = PaperAutomationArming.Evaluate(
            settings.IsEnabled,
            _killSwitch.Engaged,
            _killSwitch.Reason,
            plane,
            planeError,
            broker,
            brokerError,
            ordersThisSession,
            settings.MaxOrdersPerSession);

        ReportArming(arming);
        _lastArmCheckedAt = now;

        if (!arming.Armed)
        {
            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated, "The signal was not consulted: automation is not armed.",
                    null, AutomationActions.NoTrade, arming.Reason, ordersThisSession),
                cancellationToken);
        }

        // Session awareness. A scheduled evaluation acts only inside a session it can NAME; a manual
        // one may act outside it, but only with an explicit acknowledgement that lands on the row.
        if (session is null && !(trigger == AutomationTriggers.Manual && acknowledgeOutsideSession))
        {
            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated,
                    "The signal was not consulted: no session is open.",
                    null, AutomationActions.NoTrade,
                    $"{now:yyyy-MM-dd HH:mm}Z is inside no {settings.Calendar} session. The next session's trading " +
                    $"date is {tradingDate:yyyy-MM-dd}. Automation acts only inside a session it can name.",
                    ordersThisSession),
                cancellationToken);
        }

        SignalResult signalResult;

        if (trigger == AutomationTriggers.Manual)
        {
            // Deliberately NOT evaluated, and recorded as such. Reading the signal and then ignoring
            // it would put a signal state on a row whose order the signal did not ask for, which is
            // the one thing this trigger must not be able to look like.
            signalResult = new SignalResult(
                SignalStates.NotEvaluated,
                "Manual trigger: the signal was deliberately not consulted. This order was requested by an " +
                "operator, not produced by automation.",
                Trade: true);
        }
        else
        {
            signalResult = await signal.EvaluateAsync(cancellationToken);

            if (!signalResult.Trade)
            {
                return await RecordAsync(
                    context.Build(arming, signalResult.State, signalResult.Reason, signalResult.StudyRunId,
                        AutomationActions.NoTrade,
                        $"The '{signal.Name}' signal did not ask for a position.", ordersThisSession),
                    cancellationToken);
            }
        }

        var accountId = broker!.ManagedAccounts[0];

        // The structure switch: explicit, and an unknown value refuses rather than defaulting.
        // A loop configured for a structure this build does not know must not trade the one it does.
        var plan = options.Value.Structure switch
        {
            PaperAutomationOptions.Structures.DebitVertical =>
                await planner.PlanAsync(accountId, tradingDate, operatorLimitPrice, cancellationToken),
            PaperAutomationOptions.Structures.ShortVolCreditPut =>
                await shortVolPlanner.PlanAsync(accountId, tradingDate, operatorLimitPrice, cancellationToken),
            var unknown => OrderPlanResult.Refused(
                $"PaperAutomation:Structure is '{unknown}', which this build does not recognise. Known values: " +
                $"'{PaperAutomationOptions.Structures.DebitVertical}', " +
                $"'{PaperAutomationOptions.Structures.ShortVolCreditPut}'."),
        };

        if (plan.Failure is { } planFailure)
        {
            return await RecordAsync(
                context.Build(arming, signalResult.State, signalResult.Reason, signalResult.StudyRunId,
                    AutomationActions.Refused, planFailure, ordersThisSession),
                cancellationToken);
        }

        var order = plan.Order!;

        // Claimed BEFORE the order leaves this process. If the response is lost the claim stands, so
        // the cap is spent on an order that may exist rather than refunded on one that might.
        Claim(tradingDate);

        try
        {
            var response = await execution.SubmitAsync(order.Request, cancellationToken);

            var decision = await RecordAsync(
                context.Build(arming, signalResult.State, signalResult.Reason, signalResult.StudyRunId,
                    AutomationActions.Submitted,
                    $"{order.Description}. ExecutionService reports {response.Status}.",
                    ordersThisSession + 1,
                    orderSubmitted: true,
                    orderId: response.OrderId,
                    correlationId: response.CorrelationId,
                    lifecycleStatus: response.Status.ToString(),
                    limitPrice: order.LimitPrice,
                    limitPriceSource: order.LimitPriceSource,
                    detail: JsonSerializer.Serialize(new
                    {
                        risk = response.RiskDecision?.Decision.ToString(),
                        breaches = response.RiskDecision?.Breaches.Select(b => b.Code).ToArray() ?? [],
                        fills = response.Fills.Count,
                        legs = order.Request.Legs.Select(l => new
                        {
                            l.Contract.Underlying, l.Contract.Expiration, l.Contract.Strike,
                            right = l.Contract.Right.ToString(), side = l.Side.ToString(), l.Quantity,
                        }),
                    }, DetailOptions)),
                cancellationToken);

            // The row is in the table now, so the count query sees it and the in-process claim would
            // double-count. Released only when the row was actually persisted — RecordAsync returns
            // the decision with DecisionId still 0 when the write failed, and in that case the claim
            // must stay held or the cap is refunded by a database hiccup.
            if (decision.DecisionId != 0)
            {
                ReleaseClaim(tradingDate);
            }

            logger.LogWarning(
                "Paper automation submitted order {OrderId} ({Description}); ExecutionService reports {Status}.",
                response.OrderId, order.Description, response.Status);

            return decision;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(
                ex, "Paper automation handed an order to ExecutionService and no outcome came back. It may be " +
                    "live at the venue — reconcile against GET /orders and GET /ibkr/orders/open. The per-session " +
                    "cap counts this attempt.");

            var unknown = await RecordAsync(
                context.Build(arming, signalResult.State, signalResult.Reason, signalResult.StudyRunId,
                    AutomationActions.OutcomeUnknown,
                    $"{order.Description}. The order was handed to ExecutionService and no outcome was " +
                    $"established: {ex.Message}. It may be live at the venue; no order id was recorded here, " +
                    "so reconcile against GET /orders and GET /ibkr/orders/open.",
                    ordersThisSession + 1,
                    limitPrice: order.LimitPrice,
                    limitPriceSource: order.LimitPriceSource),
                cancellationToken);

            // An 'outcome-unknown' row IS counted by the cap query — that is the whole point of
            // counting it — so the in-process claim is released for the same reason as above. If the
            // row did not persist, the claim is what keeps the cap honest.
            if (unknown.DecisionId != 0)
            {
                ReleaseClaim(tradingDate);
            }

            return unknown;
        }
    }

    private async Task<(ExecutionPlaneConfiguration? Plane, string? Error)> ReadExecutionPlaneAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var (router, portfolioSource, configured) = await execution.GetResolvedConfigurationAsync(cancellationToken);
            var marketDataSource = await marketData.GetResolvedSourceAsync(cancellationToken);

            return (new ExecutionPlaneConfiguration(router, portfolioSource, marketDataSource, configured), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(BrokerFacts? Broker, string? Error)> ReadBrokerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await gateway.GetStatusAsync(cancellationToken);

            return (new BrokerFacts(
                status.Connected,
                status.TradingPermitted,
                status.TradingBlockedReason,
                status.ManagedAccounts ?? []), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private void ReportArming(ArmingResult arming)
    {
        var previous = _lastArming;
        _lastArming = arming;

        if (string.Equals(previous.State, arming.State, StringComparison.Ordinal)
            && string.Equals(previous.Reason, arming.Reason, StringComparison.Ordinal))
        {
            return;
        }

        if (arming.Armed)
        {
            logger.LogWarning("Paper automation is ARMED. {Reason}", arming.Reason);
            return;
        }

        // Critical on the transition. Every refusal is a state an operator asked for or a
        // misconfiguration they need to see; either way it is not something to whisper once and then
        // repeat forever until nobody reads it.
        logger.LogCritical(
            "Paper automation REFUSES TO ARM ({State}). {Reason}", arming.State, arming.Reason);
    }

    private async Task<AutomationDecision> RecordAsync(AutomationDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            var id = await store.RecordAsync(decision, cancellationToken);
            return decision with { DecisionId = id };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(
                ex, "Paper automation could not record a decision ({Action}: {Reason}). The action still " +
                    "happened; the log of it did not.",
                decision.Action, decision.ActionReason);

            return decision;
        }
    }

    private int PendingClaims(DateOnly tradingDate) =>
        _claims.Date == tradingDate ? _claims.Pending : 0;

    private void Claim(DateOnly tradingDate) =>
        _claims = _claims.Date == tradingDate ? (tradingDate, _claims.Pending + 1) : (tradingDate, 1);

    private void ReleaseClaim(DateOnly tradingDate)
    {
        if (_claims.Date == tradingDate && _claims.Pending > 0)
        {
            _claims = (tradingDate, _claims.Pending - 1);
        }
    }

    public async Task<AutomationStatusReport> GetStatusAsync(int recentLimit, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var session = sessionClock.SessionAt(settings.Calendar, now);
        var tradingDate = sessionClock.TradingDateOf(settings.Calendar, now);

        IReadOnlyList<AutomationDecision> recent = [];
        IReadOnlyList<AutomationDecision> submitted = [];
        var ordersThisSession = 0;
        string? persistenceError = null;

        try
        {
            recent = await store.RecentAsync(recentLimit, cancellationToken);
            submitted = await store.SubmittedOnAsync(tradingDate, cancellationToken);
            ordersThisSession = await store.CountSubmittedOnAsync(tradingDate, cancellationToken)
                                + PendingClaims(tradingDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported, never hidden. An empty decision list because the database is unreachable and
            // an empty decision list because nothing has happened are opposite states, and a UI that
            // cannot tell them apart shows the first as the second.
            persistenceError = ex.Message;
        }

        var kill = _killSwitch;

        // The last sweep's verdict is a MEASUREMENT WITH A TIMESTAMP, not the current state, and
        // reporting it as current was wrong in the one direction that matters. Observed live on
        // 2026-08-01: engaging the kill switch and immediately re-reading this endpoint returned
        // `armed: true, armState: "armed"` — correct as of armCheckedAt, and a banner reading ARMED
        // to the operator who had just pressed stop. The engaged switch was visible only in a
        // separate field further down the payload.
        //
        // So the two conditions this endpoint can decide FOR ITSELF, right now, without three HTTP
        // round trips on every poll, are applied on top: the kill switch and the cap. They are also
        // the two that change between sweeps in response to something a human just did. Everything
        // else — the execution plane, the broker — genuinely is as of armCheckedAt, and re-measuring
        // it on a status read would put three service calls behind a page refresh.
        //
        // The precedence matches PaperAutomationArming.Evaluate deliberately: disabled, then killed,
        // then the cap. Two orderings of the same rules that could disagree is worse than one.
        var arming = _lastArming;

        if (!settings.IsEnabled)
        {
            arming = ArmingResult.Refuse(
                ArmStates.Disabled, $"PaperAutomation:Enabled is '{settings.Enabled ?? "(unset)"}', not 'true'.");
        }
        else if (kill.Engaged)
        {
            arming = ArmingResult.Refuse(
                ArmStates.Killed,
                $"The kill switch is engaged{(string.IsNullOrWhiteSpace(kill.Reason) ? string.Empty : $": {kill.Reason}")}. " +
                "POST /research/automation/resume to re-arm.");
        }
        else if (persistenceError is null && ordersThisSession >= settings.MaxOrdersPerSession)
        {
            arming = ArmingResult.Refuse(
                ArmStates.CapReached,
                $"The per-session order cap is spent: {ordersThisSession} of {settings.MaxOrdersPerSession} orders " +
                "submitted on this trading date.");
        }

        return new AutomationStatusReport(
            settings.IsEnabled,
            arming.Armed,
            arming.State,
            arming.Reason,
            _lastArmCheckedAt,
            new KillSwitchStatus(
                kill.Engaged,
                kill.EngagedAt,
                kill.Reason,
                "In-memory only: a process restart clears it, and automation re-arms from configuration. " +
                "Set PaperAutomation__Enabled=false for a durable stop."),
            _lastPlane,
            _lastPlaneError,
            new SessionStatus(
                settings.Calendar,
                session is not null,
                session?.Label,
                session?.TradingDate,
                tradingDate),
            signal.Name,
            recent.Count > 0 ? recent[0] : null,
            recent,
            submitted,
            ordersThisSession,
            settings.MaxOrdersPerSession,
            Math.Max(0, settings.MaxOrdersPerSession - ordersThisSession),
            persistenceError,
            settings.IsEnabled
                ? "Armed state is re-evaluated on every sweep; the values above are from the last one."
                : "Automation is off, so no evaluation runs and no decision rows are written. Armed state below " +
                  "reflects that, not a failed check.");
    }

    private sealed record KillSwitchState(bool Engaged, DateTimeOffset? EngagedAt, string? Reason);

    /// <summary>The facts common to every decision this evaluation could produce.</summary>
    private sealed record DecisionContext(
        DateTimeOffset Now,
        string Trigger,
        string Calendar,
        TradingSession? Session,
        DateOnly TradingDate,
        int Cap)
    {
        public AutomationDecision Build(
            ArmingResult arming,
            string signalState,
            string signalReason,
            Guid? studyRunId,
            string action,
            string actionReason,
            int ordersThisSession,
            bool orderSubmitted = false,
            Guid? orderId = null,
            Guid? correlationId = null,
            string? lifecycleStatus = null,
            decimal? limitPrice = null,
            string? limitPriceSource = null,
            string? detail = null) =>
            new(0, Now, Trigger, arming.Armed, arming.State, arming.Reason,
                Session is null ? null : Calendar,
                Session?.Label,
                TradingDate,
                Session is not null,
                signalState, signalReason, studyRunId,
                action, actionReason,
                orderSubmitted, orderId, correlationId, lifecycleStatus,
                limitPrice, limitPriceSource,
                ordersThisSession, Cap, detail);

        /// <summary>A decision that could not be written because the writer is what failed.</summary>
        public AutomationDecision Unrecorded(
            ArmingResult arming, string signalState, string signalReason, string action, string actionReason, int orders) =>
            Build(arming, signalState, signalReason, null, action, actionReason, orders);
    }
}
