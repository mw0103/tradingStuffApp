using IBApi;
using Microsoft.Extensions.Logging;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;

namespace TradingStuff.Tests;

public sealed class IbkrOrderTrackerTests
{
    private static IbkrOrderTracker NewTracker() =>
        new(LoggerFactory.Create(_ => { }).CreateLogger<IbkrOrderTracker>());

    // Two legs of a 1x1 vertical: conId 1001 is leg 0, conId 1002 is leg 1, one contract of each per
    // spread. So a spread count of N owes N contracts on each leg.
    private static readonly Dictionary<int, TrackedComboLeg> VerticalLegs = new()
    {
        [1001] = new TrackedComboLeg(0, 1),
        [1002] = new TrackedComboLeg(1, 1),
    };

    private static IbkrOrderTracker TrackedOrder(int orderId, out Guid internalId) =>
        TrackedOrder(orderId, VerticalLegs, out internalId);

    private static IbkrOrderTracker TrackedOrder(
        int orderId, IReadOnlyDictionary<int, TrackedComboLeg> legs, out Guid internalId)
    {
        var tracker = NewTracker();
        internalId = Guid.NewGuid();
        tracker.TryTrack(orderId, Guid.NewGuid(), internalId, legs);
        return tracker;
    }

    /// <summary>
    /// Whether settlement completed within a short window. Used for the "must NOT settle yet"
    /// assertions: <c>FillsSettled</c> runs its continuations asynchronously, so an immediate
    /// <c>IsCompleted</c> check can read false on a task that is already finishing — which would let
    /// a regression pass for the wrong reason.
    /// </summary>
    private static async Task<bool> SettledWithinAsync(Task<IbkrOrderState?> settlement) =>
        ReferenceEquals(
            await Task.WhenAny(settlement, Task.Delay(TimeSpan.FromMilliseconds(200))),
            settlement);

    private static Contract Leg(int conId) => new() { ConId = conId, SecType = "OPT" };

    private static Contract Bag() => new() { ConId = 0, SecType = "BAG" };

    // ---- one broker order per internal order ------------------------------------------------
    // Regression: on 2026-07-31 an HTTP retry re-sent a resting combo, so the same internal order
    // reached TWS as orders 16 and 17. The caller saw only 17's rejection while 16 stayed working.

    [Fact]
    public void An_internal_order_can_only_be_tracked_once()
    {
        var tracker = NewTracker();
        var internalId = Guid.NewGuid();

        Assert.True(tracker.TryTrack(16, Guid.NewGuid(), internalId, VerticalLegs));
        Assert.False(tracker.TryTrack(17, Guid.NewGuid(), internalId, VerticalLegs));
    }

    [Fact]
    public void A_refused_second_claim_leaves_the_first_order_reachable()
    {
        // The caller returns this state instead of placing again, so it must be the original order.
        var tracker = NewTracker();
        var internalId = Guid.NewGuid();

        tracker.TryTrack(16, Guid.NewGuid(), internalId, VerticalLegs);
        tracker.TryTrack(17, Guid.NewGuid(), internalId, VerticalLegs);

        Assert.Equal(16, tracker.FindByInternalOrderId(internalId)?.IbkrOrderId);
        Assert.Null(tracker.Get(17));
    }

    [Fact]
    public void A_terminal_first_attempt_still_blocks_a_second_placement()
    {
        // The claim is not released on rejection. A retry after a reject is still a duplicate
        // submission of an order the caller already has an answer for.
        var tracker = NewTracker();
        var internalId = Guid.NewGuid();

        tracker.TryTrack(16, Guid.NewGuid(), internalId, VerticalLegs);
        tracker.ApplyError(16, 163, "price exceeds the Percentage constraint of 3%.");

        Assert.Equal(OrderLifecycleStatus.Failed, tracker.Get(16)?.Status);
        Assert.False(tracker.TryTrack(17, Guid.NewGuid(), internalId, VerticalLegs));
    }

    [Fact]
    public void Distinct_internal_orders_are_tracked_independently()
    {
        var tracker = NewTracker();

        Assert.True(tracker.TryTrack(16, Guid.NewGuid(), Guid.NewGuid(), VerticalLegs));
        Assert.True(tracker.TryTrack(17, Guid.NewGuid(), Guid.NewGuid(), VerticalLegs));
    }

    [Fact]
    public void A_precautionary_rejection_settles_the_order_as_failed()
    {
        // Observed live: TWS error 163 rejects any price outside the Precautionary Settings
        // percentage band. Before this mapping the order sat at PendingSubmit forever while being
        // dead, and callers had no way to tell.
        var tracker = TrackedOrder(8, out _);

        tracker.ApplyError(8, 163, "price exceeds the Percentage constraint of 3%");

        var state = tracker.Get(8)!;
        Assert.Equal(OrderLifecycleStatus.Failed, state.Status);
        Assert.Equal("Error163", state.RawStatus);
    }

    [Fact]
    public void A_terminal_outcome_survives_trailing_notices()
    {
        // The real sequence: 163 rejects, TWS cancels (202), then a redundant cancel raises 161
        // ("not in a cancellable state"). Letting the epilogue win reported the wrong outcome.
        var tracker = TrackedOrder(8, out _);

        tracker.ApplyError(8, 163, "price exceeds the Percentage constraint of 3%");
        tracker.ApplyOrderStatus(8, "Cancelled", 0m, 1m, 0d, 2035059402L, string.Empty);
        tracker.ApplyError(8, 161, "Cancel attempted when order is not in a cancellable state.");

        var state = tracker.Get(8)!;
        Assert.Equal(OrderLifecycleStatus.Failed, state.Status);
        Assert.Contains("163", state.Message);
    }

    [Fact]
    public void A_non_fatal_notice_leaves_the_order_working()
    {
        var tracker = TrackedOrder(9, out _);

        tracker.ApplyOrderStatus(9, "Submitted", 0m, 1m, 0d, 1L, string.Empty);
        tracker.ApplyError(9, 399, "Order message: warning about routing");

        var state = tracker.Get(9)!;
        Assert.Equal(OrderLifecycleStatus.Submitted, state.Status);
        Assert.Contains("399", state.Message);
    }

    [Fact]
    public void Executions_are_deduplicated_on_exec_id()
    {
        // execDetails replays after a reconnect; without dedupe every fill is counted twice.
        var tracker = TrackedOrder(10, out var internalId);

        var execution = new Execution { OrderId = 10, ExecId = "exec-1", Shares = 1m, Price = 1.05d };
        tracker.ApplyExecution(Leg(1001), execution);
        tracker.ApplyExecution(Leg(1001), execution);
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 10, ExecId = "exec-2", Shares = 1m, Price = 0.95d });

        var state = tracker.Get(10)!;
        Assert.Equal(2, state.Fills.Count);
        Assert.All(state.Fills, fill => Assert.Equal(internalId, fill.OrderId));
        Assert.All(state.Fills, fill => Assert.Equal(FillLiquidity.BrokerReported, fill.Liquidity));
    }

    [Fact]
    public void The_bag_summary_execution_is_not_counted_as_a_fill()
    {
        // Observed live on a filled SPXW vertical: IBKR reports three executions for a two-leg combo
        // — one for the BAG carrying the net price (3.80), plus one per leg (36.40 buy, 32.60 sell).
        // Counting the BAG invents a third leg and records the net as if it were a leg price.
        var tracker = TrackedOrder(9, out _);

        tracker.ApplyExecution(Bag(), new Execution { OrderId = 9, ExecId = "bag-1", Shares = 1m, Price = 3.80d });
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 9, ExecId = "leg-a", Shares = 1m, Price = 36.40d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 9, ExecId = "leg-b", Shares = 1m, Price = 32.60d });

        var state = tracker.Get(9)!;
        Assert.Equal(2, state.Fills.Count);
        Assert.DoesNotContain(state.Fills, fill => fill.Price == 3.80m);
    }

    [Fact]
    public void Fills_are_attributed_to_legs_by_conid()
    {
        // Legs do not fill in request order, and one leg can fill in several executions while the
        // other has not started — so a running counter mislabels them.
        var tracker = TrackedOrder(9, out _);

        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 9, ExecId = "b", Shares = 1m, Price = 32.60d });
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 9, ExecId = "a", Shares = 1m, Price = 36.40d });

        var state = tracker.Get(9)!;
        Assert.Equal(0, state.Fills.Single(fill => fill.Price == 36.40m).LegIndex);
        Assert.Equal(1, state.Fills.Single(fill => fill.Price == 32.60m).LegIndex);
    }

    [Fact]
    public void A_leg_filling_in_pieces_keeps_one_leg_index()
    {
        var tracker = TrackedOrder(9, out _);

        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 9, ExecId = "a1", Shares = 1m, Price = 36.40d });
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 9, ExecId = "a2", Shares = 1m, Price = 36.45d });

        var state = tracker.Get(9)!;
        Assert.Equal(2, state.Fills.Count);
        Assert.All(state.Fills, fill => Assert.Equal(0, fill.LegIndex));
    }

    [Fact]
    public async Task Settlement_completes_when_the_order_fills()
    {
        var tracker = TrackedOrder(11, out _);

        var settlement = tracker.WaitForSettlementAsync(
            11, TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None);
        tracker.ApplyOrderStatus(11, "Filled", 1m, 0m, 1.02d, 42L, string.Empty);

        var state = await settlement;
        Assert.Equal(OrderLifecycleStatus.Filled, state!.Status);
        Assert.Equal(1.02m, state.AverageFillPrice);
        Assert.Equal(42L, state.PermId);
    }

    [Fact]
    public async Task Settlement_waits_past_terminal_status_for_the_fills_and_commissions()
    {
        // Regression, observed live on the paper account: a 1-lot SPY vertical returned
        // filled=1, avgFillPrice=1.28 with fills=[] and commission=0, because orderStatus="Filled"
        // arrives before the execDetails and those arrive before the commission reports. Four
        // seconds later the same order read two fills summing to 1.28 and commission 1.598693.
        // ExecutionService had already persisted the empty version as the record of a filled order.
        var tracker = TrackedOrder(21, out _);

        var settlement = tracker.WaitForSettlementAsync(
            21, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);

        tracker.ApplyOrderStatus(21, "Filled", 1m, 0m, 1.28d, 77L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 21, ExecId = "x1", Shares = 1m, Price = 1.67d });

        Assert.False(settlement.IsCompleted, "One leg of two had reported; the fills are still incomplete.");

        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 21, ExecId = "x2", Shares = 1m, Price = 0.39d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "x1", CommissionAndFees = 0.8d });

        Assert.False(settlement.IsCompleted, "Both legs filled, but one execution's cost is unknown.");

        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "x2", CommissionAndFees = 0.8d });

        var state = await settlement;
        Assert.Equal(OrderLifecycleStatus.Filled, state!.Status);
        Assert.Equal(2, state.Fills.Count);
        Assert.Equal(1.6m, state.Commission);
    }

    [Fact]
    public async Task Settlement_still_returns_when_the_fills_never_arrive()
    {
        // A filled order reported with incomplete fills beats hanging on a callback TWS may never
        // send. The grace expiring is logged, not thrown.
        var tracker = TrackedOrder(22, out _);

        var settlement = tracker.WaitForSettlementAsync(
            22, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), CancellationToken.None);

        tracker.ApplyOrderStatus(22, "Filled", 1m, 0m, 1.28d, 78L, string.Empty);

        var state = await settlement;
        Assert.Equal(OrderLifecycleStatus.Filled, state!.Status);
        Assert.Empty(state.Fills);
    }

    // ---- multi-execution legs: settlement is a QUANTITY question ------------------------------
    // The fix above (wait past terminal status for the fills) was itself defective for anything
    // bigger than one lot. It decided "the cost is known" from two counters — distinct leg indices
    // covered, and commissions-seen vs executions-seen — and TWS interleaves each execDetails with
    // its own commissionAndFeesReport, so both counters reach equality while a leg still owes
    // contracts. The order then settled on a truncated fill list that ExecutionService persisted as
    // the permanent record, while the remaining execDetails arrived seconds later and were never
    // read. Contracts owed per leg is filled (spreads, for a BAG) x that leg's ratio.

    [Fact]
    public async Task A_leg_that_still_owes_contracts_does_not_settle_the_fills()
    {
        // The exact delivery order that defeated the counters: leg0 x5 -> commission ->
        // leg1 x2 -> commission -> leg1 x3 -> commission. At the SECOND commission both legs are
        // "covered" and commissions equal executions, yet 3 of the 10 contracts have not reported.
        var tracker = TrackedOrder(31, out _);

        var settlement = tracker.WaitForSettlementAsync(
            31, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);

        tracker.ApplyOrderStatus(31, "Filled", 5m, 0m, 1.30d, 91L, string.Empty);

        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 31, ExecId = "a1", Shares = 5m, Price = 1.70d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "a1", CommissionAndFees = 3.5d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 31, ExecId = "b1", Shares = 2m, Price = 0.40d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "b1", CommissionAndFees = 1.4d });

        Assert.False(
            await SettledWithinAsync(settlement),
            "Leg 1 has reported 2 of the 5 contracts it owes; the order's cost is not known yet.");

        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 31, ExecId = "b2", Shares = 3m, Price = 0.41d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "b2", CommissionAndFees = 2.1d });

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, state!.Fills.Count);
        Assert.Equal(10, state.Fills.Sum(fill => fill.Quantity));
        Assert.Equal(7.0m, state.Commission);
    }

    [Fact]
    public async Task A_ratio_spread_waits_for_the_heavier_leg_to_report_its_multiple()
    {
        // Why the leg RATIO travels with the leg index rather than being assumed to be 1. Two spreads
        // of a 1x2 backspread: leg 0 owes 2 contracts, leg 1 owes 4. Counting spreads-per-leg — or
        // any predicate that only knows the filled quantity — settles this order half way through
        // leg 1.
        var legs = new Dictionary<int, TrackedComboLeg>
        {
            [1001] = new TrackedComboLeg(0, 1),
            [1002] = new TrackedComboLeg(1, 2),
        };

        var tracker = TrackedOrder(32, legs, out _);

        var settlement = tracker.WaitForSettlementAsync(
            32, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);

        tracker.ApplyOrderStatus(32, "Filled", 2m, 0m, 0.90d, 93L, string.Empty);

        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 32, ExecId = "c1", Shares = 2m, Price = 2.10d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "c1", CommissionAndFees = 1.4d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 32, ExecId = "d1", Shares = 2m, Price = 0.60d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "d1", CommissionAndFees = 1.4d });

        Assert.False(
            await SettledWithinAsync(settlement),
            "Leg 1 carries two contracts per spread, so 2 of its 4 contracts are still outstanding.");

        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 32, ExecId = "d2", Shares = 2m, Price = 0.61d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "d2", CommissionAndFees = 1.4d });

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(6, state!.Fills.Sum(fill => fill.Quantity));
    }

    [Fact]
    public async Task Executions_that_arrived_before_the_terminal_status_settle_without_waiting()
    {
        // Completion is asked on three callbacks, not two. When every execution and commission has
        // already landed, the terminal orderStatus is the last chance to notice that nothing is
        // outstanding — miss it and a fully-reported order sits out the entire grace and then warns
        // about fills it is holding.
        var tracker = TrackedOrder(33, out _);

        var settlement = tracker.WaitForSettlementAsync(
            33, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), CancellationToken.None);

        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 33, ExecId = "e1", Shares = 1m, Price = 1.67d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 33, ExecId = "e2", Shares = 1m, Price = 0.39d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "e1", CommissionAndFees = 0.8d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "e2", CommissionAndFees = 0.8d });

        tracker.ApplyOrderStatus(33, "Filled", 1m, 0m, 1.28d, 94L, string.Empty);

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, state!.Fills.Count);
        Assert.Equal(1.6m, state.Commission);
    }

    [Fact]
    public async Task Settlement_returns_after_the_grace_when_a_leg_never_completes()
    {
        // The other half of the contract: a predicate that can refuse to complete must never be able
        // to hang. Leg 1 of a 5-lot vertical reports nothing at all; the grace expires, the order is
        // returned with the fills it does have, and the warning names the shortfall.
        var tracker = TrackedOrder(34, out _);

        var settlement = tracker.WaitForSettlementAsync(
            34, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), CancellationToken.None);

        tracker.ApplyOrderStatus(34, "Filled", 5m, 0m, 1.30d, 95L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 34, ExecId = "f1", Shares = 5m, Price = 1.70d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "f1", CommissionAndFees = 3.5d });

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(OrderLifecycleStatus.Filled, state!.Status);
        Assert.Single(state.Fills);
        Assert.Equal(5m, state.Filled);
    }

    [Fact]
    public async Task A_working_partial_fill_is_not_waited_on_for_settlement()
    {
        // A partially filled order that is still working owes nothing yet: `filled` is still moving,
        // so there is no total to check the executions against. Waiting on one would burn the whole
        // grace and then warn about incomplete fills on an order that is simply mid-fill.
        var tracker = TrackedOrder(35, out _);
        tracker.ApplyOrderStatus(35, "Submitted", 2m, 3m, 1.30d, 96L, string.Empty);

        var settlement = tracker.WaitForSettlementAsync(
            35, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(30), CancellationToken.None);

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OrderLifecycleStatus.PartiallyFilled, state!.Status);
    }

    [Fact]
    public void A_combo_filled_for_a_credit_keeps_its_negative_average_price()
    {
        // Observed live closing a vertical: TWS reports the combo's average fill price as a signed
        // net, so a credit arrives negative. Running it through the price converter — which rejects
        // negatives as "no quote" — silently reported every credit fill as 0.
        var tracker = TrackedOrder(13, out _);

        tracker.ApplyOrderStatus(13, "Filled", 1m, 0m, -3.40d, 99L, string.Empty);

        Assert.Equal(-3.40m, tracker.Get(13)!.AverageFillPrice);
    }

    [Fact]
    public async Task A_resting_order_returns_its_working_state_rather_than_failing()
    {
        // A limit order that has not filled is not an error; the caller gets the working state back.
        var tracker = TrackedOrder(12, out _);
        tracker.ApplyOrderStatus(12, "Submitted", 0m, 1m, 0d, 7L, string.Empty);

        var state = await tracker.WaitForSettlementAsync(
            12, TimeSpan.FromMilliseconds(150), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(OrderLifecycleStatus.Submitted, state!.Status);
    }

    [Fact]
    public void Callbacks_for_untracked_orders_are_ignored()
    {
        // Orders placed manually in TWS, or by another client id, are not ours to report on.
        var tracker = NewTracker();

        tracker.ApplyOrderStatus(999, "Filled", 1m, 0m, 1d, 1L, string.Empty);

        Assert.Null(tracker.Get(999));
        Assert.Empty(tracker.All());
    }

    // ---- permId: the only identifier that survives a reconnect --------------------------------
    // ApplyError makes an order terminal without touching _permId, because the TWS error callback
    // carries no permId at all. ApplyStatus then early-returned on an already-terminal order, so the
    // trailing orderStatus that DOES carry one was discarded — and the order-map write turns a zero
    // permId into a NULL column. Precisely the orders whose fate is most ambiguous (rejected,
    // cancelled) were the ones stored without the identifier reconciliation has to key on.

    [Fact]
    public void A_permId_reported_after_a_rejection_is_still_recorded()
    {
        // The live sequence this project has already observed and encoded above: TWS rejects with
        // 163, then cancels the order, and the Cancelled orderStatus is where the permId arrives.
        var tracker = TrackedOrder(41, out _);

        tracker.ApplyError(41, 163, "price exceeds the Percentage constraint of 3%");
        tracker.ApplyOrderStatus(41, "Cancelled", 0m, 1m, 0d, 2035059402L, string.Empty);

        var state = tracker.Get(41)!;
        Assert.Equal(2035059402L, state.PermId);

        // Still refused, though. Taking the identity off a late callback must not take its status.
        Assert.Equal(OrderLifecycleStatus.Failed, state.Status);
        Assert.Equal("Error163", state.RawStatus);
    }

    [Fact]
    public void A_permId_first_seen_on_the_open_order_callback_is_recorded()
    {
        // Verified live on the paper account: openOrder is the FIRST message TWS sends for an order
        // it accepts, ahead of the first orderStatus. It was the one order callback carrying a permId
        // that the tracker never read.
        //
        // Driven through IbkrClientWrapper rather than by calling the tracker directly, because the
        // defect was that nothing routed the callback: a test calling ApplyOpenOrder itself passes
        // just as happily with the wiring deleted, which makes it a test of the wrong thing.
        var tracker = TrackedOrder(42, out _);
        var wrapper = new IbkrClientWrapper(
            new IbkrRequestRegistry(), tracker, new ExecutionCommissionRouter(),
            LoggerFactory.Create(_ => { }).CreateLogger<IbkrClientWrapper>());

        wrapper.openOrder(
            42,
            new Contract { Symbol = "SPY", SecType = "BAG" },
            new Order { PermId = 681713841L, Action = "BUY", OrderType = "LMT", TotalQuantity = 1m },
            new OrderState { Status = "PreSubmitted" });

        Assert.Equal(681713841L, tracker.Get(42)!.PermId);
    }

    [Fact]
    public void A_permId_from_an_execution_is_recorded_when_no_status_carried_one()
    {
        // execDetails carries it too, which is what recovers the identifier for an order that
        // partially filled and was then killed by an error before any status reported one.
        var tracker = TrackedOrder(43, out _);

        tracker.ApplyExecution(
            Leg(1001),
            new Execution { OrderId = 43, ExecId = "p1", Shares = 1m, Price = 1.05d, PermId = 555555L });

        Assert.Equal(555555L, tracker.Get(43)!.PermId);
    }

    [Fact]
    public void A_recorded_permId_is_never_replaced_by_a_later_zero()
    {
        // Zero is IBKR's not-set sentinel, not a value — Execution.PermId is documented as 0 for
        // trades originating outside IB. Letting one land would erase the real identifier.
        var tracker = TrackedOrder(44, out _);

        tracker.ApplyOrderStatus(44, "Submitted", 0m, 1m, 0d, 777777L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 44, ExecId = "q1", Shares = 1m, Price = 1.0d });
        tracker.ApplyOpenOrder(44, 0L);

        Assert.Equal(777777L, tracker.Get(44)!.PermId);
    }

    // ---- the error path can never settle the fills ---------------------------------------------
    // The fill-settle rewrite made _filled the authoritative total, and its comment argued the
    // predicate could only ever over-estimate. That holds for the orderStatus path it was reasoning
    // about and fails for ApplyError, which makes an order terminal without touching _filled: an
    // order cancelled by TWS 202 after a partial fill matched its executions against the last WORKING
    // status's quantity and latched FillsSettled on a fill list that was short by everything filled
    // since. The latch cannot be un-set, so ExecutionService persisted the truncated version.

    [Fact]
    public async Task A_partial_fill_cancelled_by_TWS_does_not_settle_on_the_stale_total()
    {
        var tracker = TrackedOrder(51, out _);

        var settlement = tracker.WaitForSettlementAsync(
            51, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);

        // 3 of 5 spreads have filled, fully reported, and the order is still working.
        tracker.ApplyOrderStatus(51, "Submitted", 3m, 2m, 1.20d, 100L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 51, ExecId = "g1", Shares = 3m, Price = 1.60d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "g1", CommissionAndFees = 2.1d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 51, ExecId = "h1", Shares = 3m, Price = 0.40d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "h1", CommissionAndFees = 2.1d });

        // TWS cancels the remainder. This callback carries no quantity, so nothing here can say the
        // 3 is final — and by the time it arrives, 5 have filled.
        tracker.ApplyError(51, 202, "Order Canceled - reason:");

        Assert.False(
            await SettledWithinAsync(settlement),
            "The error callback reports no quantity, so the fill total it was matched against is a " +
            "lower bound, not a total.");

        // The terminal orderStatus is the one that does carry a final quantity.
        tracker.ApplyOrderStatus(51, "Cancelled", 5m, 0m, 1.21d, 100L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 51, ExecId = "g2", Shares = 2m, Price = 1.61d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "g2", CommissionAndFees = 1.4d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 51, ExecId = "h2", Shares = 2m, Price = 0.41d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "h2", CommissionAndFees = 1.4d });

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(4, state!.Fills.Count);
        Assert.Equal(10, state.Fills.Sum(fill => fill.Quantity));
        Assert.Equal(5m, state.Filled);
        Assert.Equal(7.0m, state.Commission);

        // Absorbing the late quantity must not have revived the order.
        Assert.Equal(OrderLifecycleStatus.Cancelled, state.Status);
        Assert.Equal("Error202", state.RawStatus);
    }

    [Fact]
    public async Task An_error_ended_order_whose_total_is_never_confirmed_says_so_out_loud()
    {
        // The other half: when no terminal orderStatus ever arrives, the order must not quietly
        // report a fill list as though it were settled. It waits out the grace and warns, and the
        // warning has to name WHICH kind of incomplete this is — the per-leg figures read "3/3"
        // because they are measured against an unconfirmed total, which without that sentence looks
        // exactly like a leg that has finished.
        var logger = new CollectingLogger();
        var tracker = new IbkrOrderTracker(logger);
        tracker.TryTrack(52, Guid.NewGuid(), Guid.NewGuid(), VerticalLegs);

        var settlement = tracker.WaitForSettlementAsync(
            52, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), CancellationToken.None);

        tracker.ApplyOrderStatus(52, "Submitted", 3m, 2m, 1.20d, 100L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 52, ExecId = "i1", Shares = 3m, Price = 1.60d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "i1", CommissionAndFees = 2.1d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 52, ExecId = "j1", Shares = 3m, Price = 0.40d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "j1", CommissionAndFees = 2.1d });

        tracker.ApplyError(52, 202, "Order Canceled - reason:");

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(OrderLifecycleStatus.Cancelled, state!.Status);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("NOT CONFIRMED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_order_with_nothing_filled_still_costs_no_grace()
    {
        // The counterweight to the two above: refusing to settle the fill list must not turn every
        // rejection into a grace timeout. A rejected order has no executions and no filled quantity,
        // so there is nothing outstanding to wait for. Verified live on the paper account: TWS errors
        // 110 and 201 produce an error callback and nothing else at all.
        var tracker = TrackedOrder(53, out _);

        var settlement = tracker.WaitForSettlementAsync(
            53, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), CancellationToken.None);

        tracker.ApplyError(53, 110, "The price does not conform to the minimum price variation for this contract.");

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(OrderLifecycleStatus.Failed, state!.Status);
        Assert.Empty(state.Fills);
    }

    [Fact]
    public async Task A_replayed_status_reporting_less_than_is_recorded_cannot_shrink_the_total()
    {
        // orderStatus replays after a reconnect, and the late-absorption path above is the only place
        // a terminal order's quantities can still move. Monotone, so a trailing message carrying an
        // older figure cannot lower the total the fill list is checked against — which would settle a
        // short list all over again, by the opposite route.
        var tracker = TrackedOrder(54, out _);

        var settlement = tracker.WaitForSettlementAsync(
            54, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), CancellationToken.None);

        tracker.ApplyOrderStatus(54, "Filled", 5m, 0m, 1.30d, 101L, string.Empty);
        tracker.ApplyExecution(Leg(1001), new Execution { OrderId = 54, ExecId = "k1", Shares = 5m, Price = 1.70d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "k1", CommissionAndFees = 3.5d });
        tracker.ApplyExecution(Leg(1002), new Execution { OrderId = 54, ExecId = "l1", Shares = 2m, Price = 0.40d });
        tracker.ApplyCommission(new CommissionAndFeesReport { ExecId = "l1", CommissionAndFees = 1.4d });

        // A replay of an earlier, smaller picture of the same order.
        tracker.ApplyOrderStatus(54, "Submitted", 2m, 3m, 1.28d, 101L, string.Empty);

        var state = await settlement.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(5m, state!.Filled);
        Assert.Equal(1.30m, state.AverageFillPrice);

        // Leg 1 is still 3 contracts short of the real total, so the record is returned incomplete
        // rather than settled against the replayed 2.
        Assert.Equal(7, state.Fills.Sum(fill => fill.Quantity));
    }

    /// <summary>
    /// Captures level and rendered message. The grace-expiry warning exists to be read by an
    /// operator, so the test asserts on the text rather than on the fact that something was logged.
    /// </summary>
    private sealed class CollectingLogger : ILogger<IbkrOrderTracker>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
