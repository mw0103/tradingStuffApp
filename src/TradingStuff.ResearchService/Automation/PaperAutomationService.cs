using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
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
/// <b>It also closes what it opens, by one declared rule.</b> Before any entry logic, every pass
/// reads the account's open positions and submits a closing order for a managed spread at or below
/// <see cref="PaperAutomationOptions.ExitDteThreshold"/> calendar days to expiration. That is the
/// whole lifecycle: no roll, no P&amp;L exit, no volatility exit — see that option's remarks and
/// docs/research/hedged-carry-menu.md §6 for why the rule is deliberately unimprovable in this build.
/// Two consequences fall out of it rather than being separate features: entry is skipped while
/// anything is open (constant exposure means one spread at a time, not stacking), and a
/// close-then-re-enter is what a later pass does on its own once the account is flat again.
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
    SpyExitPlanner exitPlanner,
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

    // The exit keys this process has handed a closing order for, held per trading date exactly like
    // the cap claims above. The durable half lives in the decision log
    // (IPaperAutomationStore.ExitKeysOrderedOnAsync); this covers the window between the order
    // leaving and its row landing, and it is deliberately never released. A released exit claim
    // whose row failed to write would let the very next pass — five minutes later, same position,
    // same reason — send a second closing order for a spread whose first one is resting at the
    // venue. Held, the position waits a day and is closed again with room to spare; refunded, it is
    // closed twice and the second fill opens the opposite spread.
    private (DateOnly Date, HashSet<string> Keys) _exitClaims = (DateOnly.MinValue, new HashSet<string>(StringComparer.Ordinal));

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
            "may submit up to {Cap} order(s) per trading date to ExecutionService. Open positions are closed at " +
            "{ExitDte} calendar day(s) to expiration — the only exit rule this build has, and closing orders are " +
            "counted by the cap but never blocked by it. Arming still requires a coherent execution plane and a " +
            "connected DU account.",
            settings.IntervalSeconds,
            signal.Name,
            settings.MaxOrdersPerSession,
            settings.ExitDteThreshold);

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

        // Consulted through PermitsExit rather than Armed, because a spent cap must still let a due
        // position be closed — the cap is a rail on new exposure, and an uncloseable position is
        // worse than an extra order. Every other refusal stops the pass here, exits included: a
        // simulated router or fabricated quotes make a CLOSING order meaningless in exactly the way
        // they make an opening one meaningless. The remaining cap check is below, in front of the
        // entry logic and nothing else.
        if (!PaperAutomationArming.PermitsExit(arming))
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

        // Armed or cap-reached are the only two verdicts that reach here, and both are decided after
        // every broker check, so the account list is populated.
        var accountId = broker!.ManagedAccounts[0];

        // ---- lifecycle, ahead of everything about entering ------------------------------------------
        // The account is read on EVERY pass, before the signal is consulted and whatever it would say.
        // Exiting is not conditional on wanting to enter, and the same read answers both questions the
        // pass has to settle: is anything due to be closed, and is the account flat enough to open
        // something new.
        var (positions, positionsError) = await ReadPositionsAsync(cancellationToken);

        if (positionsError is { } accountError)
        {
            // An unreadable account is not a flat one. Folding the failure into an empty position list
            // would skip a due exit AND unblock an entry in the same silent step — the two worst
            // outcomes available here, reached by assuming the friendliest reading of a missing fact.
            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated,
                    "The signal was not consulted: the account's open positions could not be read.",
                    null, AutomationActions.NoTrade,
                    "The account could not be read, so neither a due exit nor a flat account can be established: " +
                    $"{accountError}. Nothing is submitted against an unknown position state; the next pass tries " +
                    "again.",
                    ordersThisSession),
                cancellationToken);
        }

        var structures = SpyExitPlanner.ManagedStructures(positions!, settings.Underlying);

        if (await EvaluateExitAsync(context, arming, accountId, structures, ordersThisSession, cancellationToken)
            is { } exitDecision)
        {
            return exitDecision;
        }

        // Entry-when-flat. Constant one-vega exposure is ONE spread, not one spread per evaluation
        // that felt like it: without this guard a signal that keeps saying enter stacks a position
        // every pass until the cap stops it, and the run measures something nobody chose. Nothing here
        // is a judgement about the open position — it is not due yet, so it is simply held.
        if (structures.Count > 0)
        {
            var held = string.Join(
                ", ",
                structures.Select(s =>
                    $"{s.Underlying} {s.Expiration:yyyy-MM-dd} ({SpyExitPlanner.DaysToExpiration(s.Expiration, tradingDate)}d)"));

            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated,
                    "The signal was not consulted: a managed position is already open.",
                    null, AutomationActions.NoTrade,
                    $"Holding {structures.Count} open managed structure(s): {held}. None is at or below the " +
                    $"{settings.ExitDteThreshold}-day exit threshold, and automation runs one spread at a time — " +
                    "entry resumes on the pass after the account is flat.",
                    ordersThisSession),
                cancellationToken);
        }

        // The cap, applied to entries and to nothing else. It sits here rather than in the arming
        // verdict because a spent cap must not stop the exit branch above it; PermitsExit is the other
        // half of the same rule and the two are meant to be read together.
        if (!arming.Armed)
        {
            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated,
                    "The signal was not consulted: automation is not armed for entry.",
                    null, AutomationActions.NoTrade, arming.Reason, ordersThisSession),
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

    /// <summary>
    /// The exit branch: at most one closing order per pass, or null when nothing is due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null means "nothing was due", not "nothing happened".</b> Every other outcome — including
    /// every failure — returns a recorded decision and ends the pass, because a position that is due
    /// to close is a position the entry logic must not be reasoning about.
    /// </para>
    /// <para>
    /// <b>One structure per pass, earliest expiration first.</b> Two due structures is not a state the
    /// entry-when-flat guard can produce, but a manual order can; closing them one at a time keeps one
    /// decision row per evaluation instead of making a pass's record depend on how many legs were
    /// open, and the next pass takes the next one.
    /// </para>
    /// </remarks>
    private async Task<AutomationDecision?> EvaluateExitAsync(
        DecisionContext context,
        ArmingResult arming,
        string accountId,
        IReadOnlyList<ManagedStructure> structures,
        int ordersThisSession,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        var due = structures.FirstOrDefault(
            s => SpyExitPlanner.IsDue(s.Expiration, context.TradingDate, settings.ExitDteThreshold));

        if (due is null)
        {
            return null;
        }

        var days = SpyExitPlanner.DaysToExpiration(due.Expiration, context.TradingDate);

        // Every exit row starts with the rule that produced it. A row that says "closed" without
        // saying which rule closed it is the first step towards a second rule nobody declared.
        var rule =
            $"{AutomationExitRules.Dte}: the open {due.Underlying} {due.Expiration:yyyy-MM-dd} structure is {days} " +
            $"calendar day(s) from expiration, at or below the declared {settings.ExitDteThreshold}-day threshold";

        // The signal has no part in this and is not asked. Recording it as consulted would put a
        // signal state on a row whose order the signal did not ask for — the same reason the manual
        // trigger refuses to read it.
        const string signalReason =
            "The signal was not consulted: the exit rule is time-based and unconditional. See " +
            "PaperAutomationOptions.ExitDteThreshold.";

        IReadOnlyList<string> alreadyOrdered;

        try
        {
            alreadyOrdered = await store.ExitKeysOrderedOnAsync(context.TradingDate, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing is written, because the thing that failed IS the writer — the same shape as the
            // cap read, and the same conclusion: an exit whose prior attempts cannot be read is an
            // exit that might already be resting at the venue.
            logger.LogCritical(
                ex, "Paper automation could not read which positions it has already sent closing orders for, so it " +
                    "cannot tell a first close from a second. No closing order will be submitted this pass.");

            return context.Unrecorded(
                arming, SignalStates.NotEvaluated, signalReason, AutomationActions.ExitRefused,
                $"{rule}. The decision log could not be read, so a duplicate closing order cannot be ruled out: " +
                $"{ex.Message}", ordersThisSession);
        }

        if (alreadyOrdered.Contains(due.ExitKey, StringComparer.Ordinal)
            || IsExitClaimed(context.TradingDate, due.ExitKey))
        {
            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated, signalReason, null, AutomationActions.NoTrade,
                    $"{rule}, and a closing order for it was already submitted on this trading date. It is not sent " +
                    "again while it may be resting at the venue; if it does not fill, the next trading date closes " +
                    "the position it still finds open.",
                    ordersThisSession,
                    detail: ExitDetail(due, days, settings)),
                cancellationToken);
        }

        var plan = await exitPlanner.PlanCloseAsync(accountId, due, context.TradingDate, cancellationToken);

        if (plan.Failure is { } planFailure)
        {
            // Recorded and left alone. A closing order that cannot be built or priced is retried on
            // every subsequent pass, and a position that keeps refusing keeps producing rows saying so
            // — the protocol's success criterion 3 is coverage of failure paths, which means they have
            // to remain visible rather than be handled into silence.
            return await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated, signalReason, null, AutomationActions.ExitRefused,
                    $"{rule}. {planFailure}",
                    ordersThisSession,
                    detail: ExitDetail(due, days, settings)),
                cancellationToken);
        }

        var order = plan.Order!;

        // Both claims BEFORE the order leaves this process, for the reason the entry path records: a
        // lost response leaves the claim standing rather than refunded. The cap claim is released once
        // its row is in the table; the exit claim is not released at all (see _exitClaims).
        Claim(context.TradingDate);
        ClaimExit(context.TradingDate, due.ExitKey);

        try
        {
            var response = await execution.SubmitAsync(order.Request, cancellationToken);

            var decision = await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated, signalReason, null, AutomationActions.ExitSubmitted,
                    $"{rule}. {order.Description}. ExecutionService reports {response.Status}.",
                    ordersThisSession + 1,
                    orderSubmitted: true,
                    orderId: response.OrderId,
                    correlationId: response.CorrelationId,
                    lifecycleStatus: response.Status.ToString(),
                    limitPrice: order.LimitPrice,
                    limitPriceSource: order.LimitPriceSource,
                    detail: ExitDetail(due, days, settings, response)),
                cancellationToken);

            if (decision.DecisionId != 0)
            {
                ReleaseClaim(context.TradingDate);
            }

            logger.LogWarning(
                "Paper automation submitted CLOSING order {OrderId} ({Description}); ExecutionService reports " +
                "{Status}. Rule: {Rule}.",
                response.OrderId, order.Description, response.Status, AutomationExitRules.Dte);

            return decision;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(
                ex, "Paper automation handed a CLOSING order to ExecutionService and no outcome came back. It may be " +
                    "live at the venue — reconcile against GET /orders and GET /ibkr/orders/open. The position is " +
                    "not sent another closing order on this trading date.");

            var unknown = await RecordAsync(
                context.Build(arming, SignalStates.NotEvaluated, signalReason, null,
                    AutomationActions.ExitOutcomeUnknown,
                    $"{rule}. {order.Description}. The closing order was handed to ExecutionService and no outcome " +
                    $"was established: {ex.Message}. It may be live at the venue; no order id was recorded here, so " +
                    "reconcile against GET /orders and GET /ibkr/orders/open.",
                    ordersThisSession + 1,
                    limitPrice: order.LimitPrice,
                    limitPriceSource: order.LimitPriceSource,
                    detail: ExitDetail(due, days, settings)),
                cancellationToken);

            if (unknown.DecisionId != 0)
            {
                ReleaseClaim(context.TradingDate);
            }

            return unknown;
        }
    }

    /// <summary>
    /// The exit facts that no fixed column names, including the claim key.
    /// </summary>
    /// <remarks>
    /// <c>exitKey</c> is what <see cref="IPaperAutomationStore.ExitKeysOrderedOnAsync"/> reads back,
    /// and it is written on every exit row rather than only the submitted ones — the query filters on
    /// the action, and a refused row that says which position it refused for is worth more to whoever
    /// is reading the log than one that does not.
    /// </remarks>
    private static string ExitDetail(
        ManagedStructure structure, int days, PaperAutomationOptions settings, SubmitOrderResponse? response = null) =>
        JsonSerializer.Serialize(new
        {
            exitKey = structure.ExitKey,
            rule = AutomationExitRules.Dte,
            dteThreshold = settings.ExitDteThreshold,
            daysToExpiration = days,
            expiration = structure.Expiration,
            risk = response?.RiskDecision?.Decision.ToString(),
            breaches = response?.RiskDecision?.Breaches.Select(b => b.Code).ToArray() ?? [],
            fills = response?.Fills.Count,
            legs = structure.Legs.Select(leg => new
            {
                leg.Contract.Underlying, leg.Contract.Expiration, leg.Contract.Strike,
                right = leg.Contract.Right.ToString(), leg.Quantity,
            }),
        }, DetailOptions);

    /// <summary>The account's open positions, or why they could not be established. Never both.</summary>
    private async Task<(IReadOnlyList<PositionSnapshot>? Positions, string? Error)> ReadPositionsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var portfolio = await gateway.GetPortfolioAsync(cancellationToken);

            return (portfolio.Positions, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
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

    private bool IsExitClaimed(DateOnly tradingDate, string exitKey) =>
        _exitClaims.Date == tradingDate && _exitClaims.Keys.Contains(exitKey);

    /// <summary>Records that a closing order for this position left the process on this trading date.</summary>
    /// <remarks>
    /// A date roll discards the set rather than merging it, so yesterday's closes cannot suppress
    /// today's. There is no release: see <see cref="_exitClaims"/>.
    /// </remarks>
    private void ClaimExit(DateOnly tradingDate, string exitKey)
    {
        if (_exitClaims.Date != tradingDate)
        {
            _exitClaims = (tradingDate, new HashSet<string>(StringComparer.Ordinal));
        }

        _exitClaims.Keys.Add(exitKey);
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
