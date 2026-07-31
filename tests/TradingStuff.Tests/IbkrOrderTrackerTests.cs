using IBApi;
using Microsoft.Extensions.Logging;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;

namespace TradingStuff.Tests;

public sealed class IbkrOrderTrackerTests
{
    private static IbkrOrderTracker NewTracker() =>
        new(LoggerFactory.Create(_ => { }).CreateLogger<IbkrOrderTracker>());

    // Two legs of a vertical: conId 1001 is leg 0, conId 1002 is leg 1.
    private static readonly Dictionary<int, int> LegIndexByConId = new() { [1001] = 0, [1002] = 1 };

    private static IbkrOrderTracker TrackedOrder(int orderId, out Guid internalId)
    {
        var tracker = NewTracker();
        internalId = Guid.NewGuid();
        tracker.TryTrack(orderId, Guid.NewGuid(), internalId, LegIndexByConId);
        return tracker;
    }

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

        Assert.True(tracker.TryTrack(16, Guid.NewGuid(), internalId, LegIndexByConId));
        Assert.False(tracker.TryTrack(17, Guid.NewGuid(), internalId, LegIndexByConId));
    }

    [Fact]
    public void A_refused_second_claim_leaves_the_first_order_reachable()
    {
        // The caller returns this state instead of placing again, so it must be the original order.
        var tracker = NewTracker();
        var internalId = Guid.NewGuid();

        tracker.TryTrack(16, Guid.NewGuid(), internalId, LegIndexByConId);
        tracker.TryTrack(17, Guid.NewGuid(), internalId, LegIndexByConId);

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

        tracker.TryTrack(16, Guid.NewGuid(), internalId, LegIndexByConId);
        tracker.ApplyError(16, 163, "price exceeds the Percentage constraint of 3%.");

        Assert.Equal(OrderLifecycleStatus.Failed, tracker.Get(16)?.Status);
        Assert.False(tracker.TryTrack(17, Guid.NewGuid(), internalId, LegIndexByConId));
    }

    [Fact]
    public void Distinct_internal_orders_are_tracked_independently()
    {
        var tracker = NewTracker();

        Assert.True(tracker.TryTrack(16, Guid.NewGuid(), Guid.NewGuid(), LegIndexByConId));
        Assert.True(tracker.TryTrack(17, Guid.NewGuid(), Guid.NewGuid(), LegIndexByConId));
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
}
