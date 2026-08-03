using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TradingStuff.Contracts;
using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// The loop's own behaviour: what it records, what it refuses, and what it never sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test asserts on the recorded row, not only on whether an order went out.</b> A loop that
/// silently did nothing and a loop that decided not to trade are the same thing from the outside, and
/// telling them apart is the entire reason <c>research.paper_automation_decisions</c> exists
/// (docs/LESSONS.md §3). A test that only asserted "no order was placed" would pass against a service
/// whose evaluation had been deleted.
/// </para>
/// <para>
/// The HTTP clients are real, driven by stub handlers, so the JSON contracts between these three
/// services are exercised rather than mocked away. What is faked is the store (so the loop is
/// provable without Postgres) and the signal (so the submission path is provable without fabricating
/// a study run — see <see cref="IAutomationSignal"/>).
/// </para>
/// </remarks>
public sealed class PaperAutomationServiceTests
{
    // 10:00 ET on Wednesday 2026-08-05: inside NYSE regular hours by any reading.
    private static readonly DateTimeOffset InsideNyseSession = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

    // Saturday 2026-08-01, 22:00 UTC. Inside no NYSE session at all — the state this was built in.
    private static readonly DateTimeOffset OutsideAnySession = new(2026, 8, 1, 22, 0, 0, TimeSpan.Zero);

    // ---- refusal to arm --------------------------------------------------------------------------

    [Fact]
    public async Task Refusing_to_arm_writes_a_decision_row_and_sends_nothing()
    {
        // The 2026-08-01 combination: a real router transmitting against the deterministic generator.
        using var harness = new Harness(marketDataProvider: "ibkr-deterministic-paper-feed");

        var decision = await harness.EvaluateScheduledAsync();

        Assert.False(decision.Armed);
        Assert.Equal(ArmStates.IncoherentConfiguration, decision.ArmState);
        Assert.Equal(AutomationActions.NoTrade, decision.Action);
        Assert.False(decision.OrderSubmitted);

        // A refusal that leaves no row is a silent no-op, which is what this must never be.
        var row = Assert.Single(harness.Store.Recorded);
        Assert.Contains("quote provider", row.ArmReason);
        Assert.Contains("fictional", row.ArmReason);

        Assert.Equal(0, harness.OrdersPosted);
    }

    [Fact]
    public async Task An_unreachable_execution_service_refuses_rather_than_assuming_it_is_fine()
    {
        using var harness = new Harness(executionServiceReachable: false);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.False(decision.Armed);
        Assert.Equal(ArmStates.PreflightFailed, decision.ArmState);
        Assert.Equal(0, harness.OrdersPosted);
        Assert.Single(harness.Store.Recorded);
    }

    // ---- the no-trade path -----------------------------------------------------------------------

    [Fact]
    public async Task A_no_trade_signal_writes_an_explicit_row_naming_the_reason()
    {
        using var harness = new Harness(signal: Signals.InsufficientData);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.True(decision.Armed);
        Assert.Equal(AutomationActions.NoTrade, decision.Action);
        Assert.Equal(SignalStates.InsufficientData, decision.SignalState);
        Assert.False(decision.OrderSubmitted);
        Assert.Null(decision.OrderId);

        var row = Assert.Single(harness.Store.Recorded);

        // The row carries the study's own words, not a generic "no trade". Losing them makes every
        // idle day look identical and unattributable.
        Assert.Contains("holdout", row.SignalReason);
        Assert.Equal(AutomationTriggers.Scheduled, row.Trigger);
        Assert.True(row.InSession);
        Assert.Equal("NYSE", row.SessionCalendar);

        Assert.Equal(0, harness.OrdersPosted);
    }

    [Fact]
    public async Task Outside_a_named_session_it_records_that_and_does_not_consult_the_signal()
    {
        using var harness = new Harness(now: OutsideAnySession, signal: Signals.Trade);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.True(decision.Armed);
        Assert.Equal(AutomationActions.NoTrade, decision.Action);
        Assert.False(decision.InSession);
        Assert.Null(decision.SessionLabel);
        Assert.Contains("inside no NYSE session", decision.ActionReason);

        // The signal says trade, and it is still not consulted — the session gate is ahead of it, so
        // the row must not claim a signal state it never asked for.
        Assert.Equal(SignalStates.NotEvaluated, decision.SignalState);
        Assert.Equal(0, harness.SignalEvaluations);
        Assert.Equal(0, harness.OrdersPosted);
    }

    // ---- the submission path ---------------------------------------------------------------------

    [Fact]
    public async Task A_trading_signal_submits_exactly_one_order_through_ExecutionService()
    {
        using var harness = new Harness(signal: Signals.Trade);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.Submitted, decision.Action);
        Assert.True(decision.OrderSubmitted);
        Assert.NotNull(decision.OrderId);
        Assert.Equal(LimitPriceSources.ComputedMarketable, decision.LimitPriceSource);

        // 2.41 - 1.98 = 0.43 natural, plus the 0.05 buffer.
        Assert.Equal(0.48m, decision.LimitPrice);

        Assert.Equal(1, harness.OrdersPosted);

        var submitted = Assert.Single(harness.SubmittedOrders);
        Assert.Equal(OrderType.Limit, submitted.OrderType);
        Assert.Equal(StrategyKind.Vertical, submitted.Strategy);
        Assert.Equal(2, submitted.Legs.Count);
        Assert.All(submitted.Legs, leg => Assert.Equal(1, leg.Quantity));
        Assert.Equal(OrderSide.Buy, submitted.Legs[0].Side);
        Assert.Equal(OrderSide.Sell, submitted.Legs[1].Side);
        Assert.Equal("SPY", submitted.Legs[0].Contract.Underlying);
        Assert.NotNull(submitted.ClientOrderId);
    }

    [Fact]
    public async Task A_combo_is_never_submitted_as_a_market_order()
    {
        // IBKR rejects or badly fills MKT on multi-leg BAG orders, and a market order on a combo is
        // the one shape whose fill price nobody can predict.
        using var harness = new Harness(signal: Signals.Trade);

        await harness.EvaluateScheduledAsync();

        Assert.NotEqual(OrderType.Market, Assert.Single(harness.SubmittedOrders).OrderType);
    }

    [Fact]
    public async Task An_unpriceable_spread_is_refused_with_its_reason_and_no_order_goes_out()
    {
        // Measured against the live paper gateway on 2026-08-01: SPY 2026-08-07 740C and 742C both
        // came back bid 0 / ask 0. Outside the regular session SPY options have no book at all.
        using var harness = new Harness(signal: Signals.Trade, longAsk: 0m, shortBid: 0m);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.Refused, decision.Action);
        Assert.False(decision.OrderSubmitted);
        Assert.Contains("no offer", decision.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);

        // Still a row. A refusal at the pricing step is a decision, and an unrecorded one is
        // indistinguishable from a loop that never ran.
        Assert.Single(harness.Store.Recorded);
    }

    [Fact]
    public async Task A_spread_above_the_debit_cap_is_refused()
    {
        // Natural 0.80 + 0.05 = 0.85, over the 0.75 cap. The debit IS the maximum loss.
        using var harness = new Harness(signal: Signals.Trade, longAsk: 2.80m, shortBid: 2.00m);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.Refused, decision.Action);
        Assert.Contains("debit cap", decision.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);
    }

    // ---- the cap ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_per_session_cap_stops_rather_than_wrapping()
    {
        using var harness = new Harness(signal: Signals.Trade, cap: 1);

        var first = await harness.EvaluateScheduledAsync();
        var second = await harness.EvaluateScheduledAsync();
        var third = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.Submitted, first.Action);

        Assert.Equal(ArmStates.CapReached, second.ArmState);
        Assert.False(second.Armed);
        Assert.Equal(ArmStates.CapReached, third.ArmState);

        // The one assertion that matters: the broker saw one order, not three.
        Assert.Equal(1, harness.OrdersPosted);

        // And the refusals are on the record, not silence.
        Assert.Equal(3, harness.Store.Recorded.Count);
    }

    [Fact]
    public async Task An_order_whose_outcome_is_unknown_still_consumes_the_cap()
    {
        // ExecutionService answers 502: the order WAS routed and no outcome came back, so it may be
        // live at the venue. A cap that refunded this would let a gateway timeout buy an extra order.
        using var harness = new Harness(signal: Signals.Trade, cap: 1, orderResponse: HttpStatusCode.BadGateway);

        var first = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.OutcomeUnknown, first.Action);
        Assert.False(first.OrderSubmitted);
        Assert.Contains("may be live at the venue", first.ActionReason);

        var second = await harness.EvaluateScheduledAsync();

        Assert.Equal(ArmStates.CapReached, second.ArmState);
        Assert.Equal(1, harness.OrdersPosted);
    }

    [Fact]
    public async Task A_cap_that_cannot_be_read_refuses_rather_than_assuming_zero()
    {
        using var harness = new Harness(signal: Signals.Trade);
        harness.Store.FailCount = true;

        var decision = await harness.EvaluateScheduledAsync();

        Assert.False(decision.Armed);
        Assert.Equal(ArmStates.PreflightFailed, decision.ArmState);
        Assert.Equal(0, harness.OrdersPosted);
    }

    // ---- the lifecycle: one declared exit rule ---------------------------------------------------

    [Fact]
    public async Task A_position_at_the_threshold_is_closed_and_the_row_names_the_rule_that_closed_it()
    {
        // Short the 740 put, long the 739 wing, five days out. Closing buys the 740 back at its ask
        // (shortBid + 0.03 = 0.58) and sells the wing at its bid (longAsk - 0.03 = 0.17): a 0.41
        // natural debit, 0.46 with the buffer paid to cross.
        using var harness = new Harness(
            signal: Signals.Trade, longAsk: 0.20m, shortBid: 0.55m, positions: PutCreditSpread(new DateOnly(2026, 8, 10)));

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitSubmitted, decision.Action);
        Assert.True(decision.OrderSubmitted);
        Assert.NotNull(decision.OrderId);
        Assert.Equal(0.46m, decision.LimitPrice);

        // The rule is at the head of the reason, and the row can never say "closed" without saying
        // which rule closed it. A second rule would have to declare itself here.
        Assert.StartsWith(AutomationExitRules.Dte, decision.ActionReason);
        Assert.Contains("5 calendar day(s)", decision.ActionReason);
        Assert.Contains("7-day threshold", decision.ActionReason);
        Assert.Contains("\"rule\":\"exit-dte\"", decision.Detail);

        // The signal has no part in an exit and was genuinely not asked — an overridden signal would
        // leave its own state on the row.
        Assert.Equal(SignalStates.NotEvaluated, decision.SignalState);
        Assert.Equal(0, harness.SignalEvaluations);

        // The order is the exact inverse of what is open: the short leg bought back, the wing sold,
        // both marked closing, and still a limit.
        var submitted = Assert.Single(harness.SubmittedOrders);
        Assert.Equal(OrderType.Limit, submitted.OrderType);
        Assert.All(submitted.Legs, leg => Assert.Equal(PositionEffect.Close, leg.PositionEffect));
        Assert.Equal(739m, submitted.Legs[0].Contract.Strike);
        Assert.Equal(OrderSide.Sell, submitted.Legs[0].Side);
        Assert.Equal(740m, submitted.Legs[1].Contract.Strike);
        Assert.Equal(OrderSide.Buy, submitted.Legs[1].Side);
    }

    [Fact]
    public async Task A_position_outside_the_threshold_is_held_and_the_signal_is_not_consulted()
    {
        // Thirty days out with a signal asking to enter: the position is held and nothing is stacked
        // on top of it. Constant one-vega exposure is one spread, not one per evaluation that felt
        // like it.
        using var harness = new Harness(signal: Signals.Trade, positions: PutCreditSpread(new DateOnly(2026, 9, 4)));

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.NoTrade, decision.Action);
        Assert.Contains("Holding 1 open managed structure(s)", decision.ActionReason);
        Assert.Contains("2026-09-04 (30d)", decision.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);
        Assert.Equal(0, harness.SignalEvaluations);
        Assert.Single(harness.Store.Recorded);
    }

    [Fact]
    public async Task Entry_resumes_on_the_pass_after_the_account_is_flat()
    {
        using var harness = new Harness(signal: Signals.Trade, cap: 5, positions: PutCreditSpread(new DateOnly(2026, 9, 4)));

        Assert.Equal(AutomationActions.NoTrade, (await harness.EvaluateScheduledAsync()).Action);

        harness.Positions.Clear();

        Assert.Equal(AutomationActions.Submitted, (await harness.EvaluateScheduledAsync()).Action);
        Assert.Equal(1, harness.OrdersPosted);
    }

    [Fact]
    public async Task An_exit_evaluated_twice_submits_one_closing_order()
    {
        // The claim discipline the entry path uses, applied per position: the broker still reports
        // the spread on the next pass because the close has not filled yet, and a second closing
        // order would sell the spread the first one bought back.
        using var harness = new Harness(
            signal: Signals.Trade, cap: 5, longAsk: 0.20m, shortBid: 0.55m,
            positions: PutCreditSpread(new DateOnly(2026, 8, 10)));

        var first = await harness.EvaluateScheduledAsync();
        var second = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitSubmitted, first.Action);

        Assert.Equal(AutomationActions.NoTrade, second.Action);
        Assert.Contains("already submitted on this trading date", second.ActionReason);

        // The assertion that matters: the venue saw one closing order, not two.
        Assert.Equal(1, harness.OrdersPosted);

        // And the suppressed pass is still a row. "Nothing happened because a close is pending" has
        // to BE a row, not an absence.
        Assert.Equal(2, harness.Store.Recorded.Count);
    }

    [Fact]
    public async Task A_spent_cap_counts_the_exit_and_does_not_block_it()
    {
        // The realistic sequence: the day's one order is spent on an entry, it fills, and the
        // position later comes due. A cap enforced against the exit would leave it open into an
        // expiration this platform does not handle — an uncloseable position is worse than an extra
        // order.
        using var harness = new Harness(signal: Signals.Trade, cap: 1);

        var entry = await harness.EvaluateScheduledAsync();
        Assert.Equal(AutomationActions.Submitted, entry.Action);

        // The fill: the 739/740 call vertical the entry planner just bought, seven days out.
        harness.Positions.AddRange(CallDebitVertical(new DateOnly(2026, 8, 12)));

        var exit = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitSubmitted, exit.Action);
        Assert.Equal(2, harness.OrdersPosted);

        // The gate genuinely refused — this is the cap being passed, not the cap failing to fire.
        Assert.False(exit.Armed);
        Assert.Equal(ArmStates.CapReached, exit.ArmState);

        // And the closing order is counted: two of a cap of one, on the record and in the status.
        Assert.Equal(2, exit.OrdersThisSession);
        Assert.Equal(2, (await harness.Service.GetStatusAsync(10, CancellationToken.None)).OrdersThisSession);
    }

    [Fact]
    public async Task A_spent_cap_still_blocks_an_entry()
    {
        // The other half of the same rule: nothing about permitting exits loosens the cap on new
        // exposure. Without this the test above would pass against a loop that had simply lost its cap.
        using var harness = new Harness(signal: Signals.Trade, cap: 1);

        await harness.EvaluateScheduledAsync();
        var second = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.NoTrade, second.Action);
        Assert.Equal(ArmStates.CapReached, second.ArmState);
        Assert.Equal(1, harness.OrdersPosted);
    }

    [Fact]
    public async Task An_unpriceable_closing_order_is_refused_with_its_reason_and_retried_next_pass()
    {
        // 0/0 on both legs, which is what SPY options quote outside the regular session. The position
        // is due and cannot be closed; the refusal reappears on every pass rather than being handled
        // into silence — coverage of failure paths IS the point of this run.
        using var harness = new Harness(
            signal: Signals.Trade, cap: 5, longAsk: 0m, shortBid: 0m,
            positions: PutCreditSpread(new DateOnly(2026, 8, 10)));

        var first = await harness.EvaluateScheduledAsync();
        var second = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitRefused, first.Action);
        Assert.Equal(AutomationActions.ExitRefused, second.Action);
        Assert.Contains("no bid", first.ActionReason);
        Assert.StartsWith(AutomationExitRules.Dte, first.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);
        Assert.Equal(2, harness.Store.Recorded.Count);
    }

    [Fact]
    public async Task A_closing_order_whose_outcome_is_unknown_is_not_sent_again()
    {
        // ExecutionService answers 502: the closing order WAS routed and may be resting at the venue.
        // Re-sending it because the response was lost is the same defect as re-entering for the same
        // reason, and it would leave the account short the spread it meant to be flat of.
        using var harness = new Harness(
            signal: Signals.Trade, cap: 5, longAsk: 0.20m, shortBid: 0.55m,
            orderResponse: HttpStatusCode.BadGateway, positions: PutCreditSpread(new DateOnly(2026, 8, 10)));

        var first = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitOutcomeUnknown, first.Action);
        Assert.False(first.OrderSubmitted);
        Assert.Contains("may be live at the venue", first.ActionReason);

        var second = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.NoTrade, second.Action);
        Assert.Equal(1, harness.OrdersPosted);
    }

    [Fact]
    public async Task An_account_that_cannot_be_read_neither_exits_nor_enters()
    {
        // An unreadable account is not a flat one. Folding the failure into an empty position list
        // would skip a due exit AND unblock an entry in one silent step.
        using var harness = new Harness(signal: Signals.Trade, portfolioReadable: false);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.NoTrade, decision.Action);
        Assert.Contains("could not be read", decision.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);
        Assert.Equal(0, harness.SignalEvaluations);
        Assert.Single(harness.Store.Recorded);
    }

    [Fact]
    public async Task A_position_in_another_underlying_is_neither_closed_nor_treated_as_the_managed_spread()
    {
        // The account is not this loop's to tidy. A QQQ position must not block a SPY entry and must
        // certainly not be closed by a rule that was declared about the managed structure.
        using var harness = new Harness(
            signal: Signals.Trade, cap: 5,
            positions:
            [
                Position("QQQ", new DateOnly(2026, 8, 10), 500m, -1),
                Position("QQQ", new DateOnly(2026, 8, 10), 499m, 1),
            ]);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.Submitted, decision.Action);
        Assert.Equal("SPY", Assert.Single(harness.SubmittedOrders).Legs[0].Contract.Underlying);
    }

    [Fact]
    public async Task An_open_structure_that_is_not_the_managed_shape_is_refused_rather_than_closed_wrongly()
    {
        // A lone short leg, due. Reversing "whatever is there" would be a guess about a position
        // automation did not build; the refusal is recorded on every pass instead.
        using var harness = new Harness(
            signal: Signals.Trade, cap: 5,
            positions: [Position("SPY", new DateOnly(2026, 8, 10), 740m, -1)]);

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitRefused, decision.Action);
        Assert.Contains("1 leg(s)", decision.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);
    }

    [Fact]
    public async Task A_decision_log_that_cannot_say_what_was_already_closed_submits_nothing()
    {
        // The same conclusion as an unreadable cap: an exit whose prior attempts cannot be read is an
        // exit that might already be resting at the venue.
        using var harness = new Harness(
            signal: Signals.Trade, cap: 5, longAsk: 0.20m, shortBid: 0.55m,
            positions: PutCreditSpread(new DateOnly(2026, 8, 10)));

        harness.Store.FailExitKeys = true;

        var decision = await harness.EvaluateScheduledAsync();

        Assert.Equal(AutomationActions.ExitRefused, decision.Action);
        Assert.Contains("duplicate closing order cannot be ruled out", decision.ActionReason);
        Assert.Equal(0, harness.OrdersPosted);
    }

    // ---- the kill switch -------------------------------------------------------------------------

    [Fact]
    public async Task The_kill_switch_stops_submission_without_restarting_anything()
    {
        using var harness = new Harness(signal: Signals.Trade, cap: 5);

        var before = await harness.EvaluateScheduledAsync();
        Assert.Equal(AutomationActions.Submitted, before.Action);

        harness.Service.Kill("testing the switch");

        var after = await harness.EvaluateScheduledAsync();

        Assert.False(after.Armed);
        Assert.Equal(ArmStates.Killed, after.ArmState);
        Assert.Contains("testing the switch", after.ArmReason);
        Assert.Equal(1, harness.OrdersPosted);

        // And it is releasable in the same process, which is what makes it a switch rather than a fuse.
        harness.Service.Resume();

        var resumed = await harness.EvaluateScheduledAsync();
        Assert.True(resumed.Armed);
        Assert.Equal(2, harness.OrdersPosted);
    }

    /// <summary>
    /// The status endpoint must not report a stale "armed" after the switch is pressed.
    /// </summary>
    /// <remarks>
    /// Found live on 2026-08-01, not by this suite: engaging the kill switch and immediately
    /// re-reading <c>GET /research/automation</c> returned <c>armed: true, armState: "armed"</c>,
    /// because the arm verdict was only refreshed by a sweep. Correct as of its timestamp, and a
    /// banner reading ARMED to the operator who had just pressed stop.
    /// </remarks>
    [Fact]
    public async Task Status_reports_the_kill_switch_immediately_not_at_the_next_sweep()
    {
        using var harness = new Harness(signal: Signals.Trade, cap: 5);

        await harness.EvaluateScheduledAsync();
        Assert.True((await harness.Service.GetStatusAsync(10, CancellationToken.None)).Armed);

        harness.Service.Kill("pressed stop");

        // No evaluation in between — that is the whole point.
        var status = await harness.Service.GetStatusAsync(10, CancellationToken.None);

        Assert.False(status.Armed);
        Assert.Equal(ArmStates.Killed, status.ArmState);
        Assert.Contains("pressed stop", status.ArmReason);
        Assert.True(status.KillSwitch.Engaged);
    }

    [Fact]
    public async Task Status_reports_a_spent_cap_immediately_not_at_the_next_sweep()
    {
        using var harness = new Harness(signal: Signals.Trade, cap: 1);

        await harness.EvaluateScheduledAsync();

        var status = await harness.Service.GetStatusAsync(10, CancellationToken.None);

        Assert.False(status.Armed);
        Assert.Equal(ArmStates.CapReached, status.ArmState);
        Assert.Equal(0, status.CapRemaining);
    }

    // ---- the manual trigger ----------------------------------------------------------------------

    [Fact]
    public async Task A_manual_order_is_recorded_as_manual_and_never_as_a_signal()
    {
        using var harness = new Harness(signal: Signals.InsufficientData);

        var decision = await harness.Service.EvaluateAsync(
            AutomationTriggers.Manual, 0.42m, acknowledgeOutsideSession: false, CancellationToken.None);

        Assert.Equal(AutomationTriggers.Manual, decision.Trigger);
        Assert.Equal(AutomationActions.Submitted, decision.Action);

        // The two facts that make this row unmistakable as a human's decision rather than automation's.
        Assert.Equal(SignalStates.NotEvaluated, decision.SignalState);
        Assert.Equal(LimitPriceSources.OperatorSupplied, decision.LimitPriceSource);
        Assert.Equal(0.42m, decision.LimitPrice);

        // The signal that would have said no-trade was genuinely not asked, rather than asked and
        // overridden — an overridden signal would leave its own state on the row.
        Assert.Equal(0, harness.SignalEvaluations);

        // And no quote was consulted for the price, which is why this path works when the book is 0/0.
        Assert.Equal(0, harness.QuoteRequests);
    }

    [Fact]
    public async Task A_manual_order_still_obeys_arming_the_kill_switch_and_the_cap()
    {
        // It bypasses the reason to trade, not the permission to.
        using var harness = new Harness(marketDataProvider: "ibkr-deterministic-paper-feed");

        var incoherent = await harness.Service.EvaluateAsync(
            AutomationTriggers.Manual, 0.42m, true, CancellationToken.None);

        Assert.False(incoherent.Armed);
        Assert.Equal(ArmStates.IncoherentConfiguration, incoherent.ArmState);
        Assert.Equal(0, harness.OrdersPosted);
    }

    [Fact]
    public async Task A_manual_order_outside_a_session_requires_an_explicit_acknowledgement()
    {
        using var harness = new Harness(now: OutsideAnySession);

        var refused = await harness.Service.EvaluateAsync(
            AutomationTriggers.Manual, 0.42m, acknowledgeOutsideSession: false, CancellationToken.None);

        Assert.Equal(AutomationActions.NoTrade, refused.Action);
        Assert.Equal(0, harness.OrdersPosted);

        var acknowledged = await harness.Service.EvaluateAsync(
            AutomationTriggers.Manual, 0.42m, acknowledgeOutsideSession: true, CancellationToken.None);

        Assert.Equal(AutomationActions.Submitted, acknowledged.Action);
        Assert.False(acknowledged.InSession);
        Assert.Equal(1, harness.OrdersPosted);
    }

    // ---- the structural invariant ----------------------------------------------------------------

    /// <summary>
    /// Nothing in the automation path can reach <c>placeOrder</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted structurally rather than behaviourally, because behaviour can only prove that this
    /// particular sequence did not reach it. ResearchService has no reference to the gateway project
    /// or to the vendored <c>IBApi</c> at all, so there is no expression anywhere in it — reachable or
    /// not, today or after a refactor — that can name <c>EClientSocket.placeOrder</c>. Its only route
    /// to an order is an HTTP POST to ExecutionService.
    /// </para>
    /// <para>
    /// <b>Two assertions, because the first one alone was a false green.</b> The original version of
    /// this test checked <c>GetReferencedAssemblies()</c> only. Adding the gateway
    /// <c>ProjectReference</c> to <c>TradingStuff.ResearchService.csproj</c> and rebuilding left it
    /// PASSING — the compiler emits a manifest reference only for assemblies whose types are actually
    /// used, so an unused project reference is invisible there. The manifest check is kept because it
    /// is the stronger claim when it does fire (a type is genuinely being used), but the project file
    /// is what catches the reference arriving at all, which is the moment to catch it: nobody adds a
    /// reference and no code, they add it and then use it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_research_plane_cannot_name_placeOrder_at_all()
    {
        var research = typeof(PaperAutomationService).Assembly;
        var referenced = research.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain("TradingStuff.IbkrGateway", referenced);
        Assert.DoesNotContain("IBApi", referenced);

        // The positive control: it DOES reference the things it is supposed to, so this is not passing
        // merely because GetReferencedAssemblies came back empty.
        Assert.Contains("TradingStuff.Contracts", referenced);
        Assert.Contains("Npgsql", referenced);

        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TradingStuff.ResearchService",
            "TradingStuff.ResearchService.csproj"));

        Assert.DoesNotContain("TradingStuff.IbkrGateway", project);
        Assert.DoesNotContain("IBApi", project);

        // Positive control on the file read as well — an empty or wrong path would otherwise satisfy
        // both DoesNotContain assertions perfectly.
        Assert.Contains("TradingStuff.Contracts", project);
    }

    /// <summary>Walks up from the test binary to the directory holding the solution file.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TradingStuff.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");
    }

    // ---- harness ---------------------------------------------------------------------------------

    // ---- position fixtures -----------------------------------------------------------------------

    /// <summary>The short-vol structure: short the 740 put, long the 739 wing.</summary>
    private static PositionSnapshot[] PutCreditSpread(DateOnly expiration) =>
        [
            Position("SPY", expiration, 740m, -1),
            Position("SPY", expiration, 739m, 1),
        ];

    /// <summary>The debit vertical the entry planner builds against the harness chain: long 739, short 740.</summary>
    private static PositionSnapshot[] CallDebitVertical(DateOnly expiration) =>
        [
            Position("SPY", expiration, 739m, 1, OptionRight.Call),
            Position("SPY", expiration, 740m, -1, OptionRight.Call),
        ];

    private static PositionSnapshot Position(
        string underlying, DateOnly expiration, decimal strike, int quantity, OptionRight right = OptionRight.Put) =>
        new(
            new OptionContract(
                $"{underlying}{expiration:yyyyMMdd}{(right == OptionRight.Put ? 'P' : 'C')}{strike:F0}",
                underlying, expiration, strike, right, TradingClass: underlying),
            quantity,
            1.00m,
            GreeksVector.Zero);

    private static class Signals
    {
        public static SignalResult InsufficientData => new(
            SignalStates.InsufficientData,
            "The latest run reports 'insufficient-data': every backfilled bar so far falls inside the reserved holdout.",
            Trade: false);

        public static SignalResult Trade => new(SignalStates.Enter, "A test signal asking for a position.", Trade: true);
    }

    private sealed class FakeSignal(SignalResult result, Action onEvaluate) : IAutomationSignal
    {
        public string Name => "test-signal";

        public Task<SignalResult> EvaluateAsync(CancellationToken cancellationToken)
        {
            onEvaluate();
            return Task.FromResult(result);
        }
    }

    /// <summary>An in-memory decision log that answers the cap from what it was told to record.</summary>
    private sealed class FakeStore : IPaperAutomationStore
    {
        private long _nextId = 1;

        public List<AutomationDecision> Recorded { get; } = [];

        public bool FailCount { get; set; }

        public Task<long> RecordAsync(AutomationDecision decision, CancellationToken cancellationToken)
        {
            var id = _nextId++;
            Recorded.Add(decision with { DecisionId = id });
            return Task.FromResult(id);
        }

        public bool FailExitKeys { get; set; }

        // Mirrors PaperAutomationStore's query, exit actions included: an exit-submitted row carries
        // an order id so order_submitted covers it, and exit-outcome-unknown is named alongside its
        // entry counterpart because a closing order whose outcome was lost may be live too.
        public Task<int> CountSubmittedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken) =>
            FailCount
                ? Task.FromException<int>(new InvalidOperationException("the decision log is unreachable"))
                : Task.FromResult(Recorded.Count(d =>
                    d.SessionTradingDate == tradingDate
                    && (d.OrderSubmitted
                        || d.Action == AutomationActions.OutcomeUnknown
                        || d.Action == AutomationActions.ExitOutcomeUnknown)));

        public Task<IReadOnlyList<AutomationDecision>> RecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationDecision>>([.. Recorded.AsEnumerable().Reverse().Take(limit)]);

        public Task<IReadOnlyList<AutomationDecision>> SubmittedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationDecision>>([.. Recorded.Where(d => d.OrderSubmitted)]);

        /// <summary>The same projection the SQL does: <c>detail-&gt;&gt;'exitKey'</c> on order-bearing exit rows.</summary>
        public Task<IReadOnlyList<string>> ExitKeysOrderedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken)
        {
            if (FailExitKeys)
            {
                return Task.FromException<IReadOnlyList<string>>(
                    new InvalidOperationException("the decision log is unreachable"));
            }

            var keys = Recorded
                .Where(d => d.SessionTradingDate == tradingDate
                            && d.Action is AutomationActions.ExitSubmitted or AutomationActions.ExitOutcomeUnknown
                            && d.Detail is not null)
                .Select(d => JsonDocument.Parse(d.Detail!).RootElement.TryGetProperty("exitKey", out var key)
                    ? key.GetString()
                    : null)
                .Where(key => key is not null)
                .Select(key => key!)
                .ToArray();

            return Task.FromResult<IReadOnlyList<string>>(keys);
        }
    }

    private sealed class Harness : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _executionHttp;
        private readonly HttpClient _marketDataHttp;
        private readonly HttpClient _gatewayHttp;

        public Harness(
            SignalResult? signal = null,
            DateTimeOffset? now = null,
            string marketDataProvider = "ibkr-gateway",
            string router = "ibkr",
            string portfolioSource = "ibkr",
            bool executionServiceReachable = true,
            decimal longAsk = 2.41m,
            decimal shortBid = 1.98m,
            int cap = 2,
            HttpStatusCode orderResponse = HttpStatusCode.Created,
            IReadOnlyList<PositionSnapshot>? positions = null,
            bool portfolioReadable = true,
            int exitDteThreshold = 7)
        {
            Store = new FakeStore();
            Positions = [.. positions ?? []];

            var time = new FakeTimeProvider(now ?? InsideNyseSession);

            _executionHttp = new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (!executionServiceReachable)
                {
                    throw new HttpRequestException("Connection refused");
                }

                if (request.RequestUri!.AbsolutePath == "/execution/configuration")
                {
                    return Ok(new
                    {
                        router,
                        portfolioSource,
                        marketDataSourceConfigured = "ibkr-delayed",
                    });
                }

                OrdersPosted++;

                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                SubmittedOrders.Add(JsonSerializer.Deserialize<SubmitOrderRequest>(body, Json)!);

                if (orderResponse != HttpStatusCode.Created)
                {
                    return new HttpResponseMessage(orderResponse)
                    {
                        Content = new StringContent("{\"title\":\"routed, outcome unestablished\"}"),
                    };
                }

                return Ok(new SubmitOrderResponse(
                    Guid.NewGuid(), Guid.NewGuid(), OrderLifecycleStatus.Submitted, null, []));
            }))
            { BaseAddress = new Uri("http://execution") };

            _marketDataHttp = new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/market-data/ibkr/status")
                {
                    return Ok(new { required = true, mode = marketDataProvider, connected = true });
                }

                QuoteRequests++;

                var legs = JsonSerializer
                    .Deserialize<MarketDataQuoteRequest>(
                        request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), Json)!
                    .Legs;

                return Ok(new MarketDataQuoteResponse(
                    [
                        Quote(legs[0].Contract, longAsk - 0.03m, longAsk),
                        Quote(legs[1].Contract, shortBid, shortBid + 0.03m),
                    ],
                    time.GetUtcNow(),
                    "ibkr-delayed"));
            }))
            { BaseAddress = new Uri("http://marketdata") };

            _gatewayHttp = new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/ibkr/status")
                {
                    return Ok(new
                    {
                        connected = true,
                        tradingPermitted = true,
                        tradingBlockedReason = (string?)null,
                        managedAccounts = new[] { "DUQ000001" },
                        marketDataType = 3,
                    });
                }

                if (request.RequestUri.AbsolutePath == "/ibkr/account/portfolio")
                {
                    PortfolioReads++;

                    if (!portfolioReadable)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent("{\"title\":\"Not connected to TWS.\"}"),
                        };
                    }

                    // Read from the mutable list, not the constructor argument: a fill between two
                    // passes is exactly the transition the lifecycle tests exercise.
                    return Ok(new
                    {
                        portfolio = new PortfolioSnapshot(
                            "DUQ000001", 25_000m, 0m, GreeksVector.Zero, Positions),
                        capturedAt = time.GetUtcNow(),
                        dailyPnLAvailable = true,
                        greeksComplete = true,
                        optionPositionCount = Positions.Count,
                        nonOptionPositionCount = 0,
                    });
                }

                return Ok(Chain());
            }))
            { BaseAddress = new Uri("http://gateway") };

            var options = Options.Create(new PaperAutomationOptions
            {
                Enabled = "true",
                MaxOrdersPerSession = cap,
                Calendar = "NYSE",
                SpreadWidthDollars = 1m,
                MarketableBufferDollars = 0.05m,
                MaxDebitDollars = 0.75m,
                ExitDteThreshold = exitDteThreshold,
            });

            var marketDataClient = new MarketDataServiceClient(_marketDataHttp);

            Service = new PaperAutomationService(
                options,
                new FakeSignal(signal ?? Signals.InsufficientData, () => SignalEvaluations++),
                new SessionClock(),
                Store,
                new SpyVerticalPlanner(
                    new OptionChainClient(_gatewayHttp, NullLogger<OptionChainClient>.Instance),
                    marketDataClient,
                    options,
                    NullLogger<SpyVerticalPlanner>.Instance),
                new SpyShortVolPlanner(
                    new OptionChainClient(_gatewayHttp, NullLogger<OptionChainClient>.Instance),
                    marketDataClient,
                    options,
                    NullLogger<SpyShortVolPlanner>.Instance),
                new SpyExitPlanner(marketDataClient, options),
                new ExecutionServiceClient(_executionHttp),
                marketDataClient,
                new IbkrGatewayClient(_gatewayHttp, NullLogger<IbkrGatewayClient>.Instance),
                time,
                NullLogger<PaperAutomationService>.Instance);
        }

        public PaperAutomationService Service { get; }

        public FakeStore Store { get; }

        public int OrdersPosted { get; private set; }

        public int QuoteRequests { get; private set; }

        public int SignalEvaluations { get; private set; }

        public int PortfolioReads { get; private set; }

        /// <summary>What the gateway reports open, mutable so a pass can fill between evaluations.</summary>
        public List<PositionSnapshot> Positions { get; }

        public List<SubmitOrderRequest> SubmittedOrders { get; } = [];

        public Task<AutomationDecision> EvaluateScheduledAsync() =>
            Service.EvaluateAsync(AutomationTriggers.Scheduled, null, false, CancellationToken.None);

        private static QuoteSnapshot Quote(OptionContract contract, decimal bid, decimal ask) =>
            new(Guid.NewGuid(), contract, bid, ask, (bid + ask) / 2m,
                new OptionGreeks(0.5m, 0.01m, -0.2m, 0.3m), InsideNyseSession, "ibkr-delayed");

        private static object Chain() => new
        {
            contracts = new[] { 739m, 740m, 741m, 742m }.Select(strike => new
            {
                symbol = $"SPY20260812C{strike:F0}",
                underlying = "SPY",
                expiration = "2026-08-12",
                strike,
                right = 0,
                exchange = "SMART",
                currency = "USD",
                multiplier = 100,
                tradingClass = "SPY",
            }),
            spotCentred = true,
            referencePrice = 739.40m,
            expiration = "2026-08-12",
            windowLow = 739m,
            windowHigh = 742m,
            unavailable = (string?)null,
        };

        private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };

        public void Dispose()
        {
            _executionHttp.Dispose();
            _marketDataHttp.Dispose();
            _gatewayHttp.Dispose();
            Service.Dispose();
        }
    }
}
