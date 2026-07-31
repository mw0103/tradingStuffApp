using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.IbkrGateway;

namespace TradingStuff.Tests;

/// <summary>
/// Combo construction arithmetic. Getting any of this wrong trades the wrong size or the wrong
/// direction, and it cannot be caught without a live broker unless it is unit-tested here.
/// </summary>
public sealed class IbkrOrderBuilderTests
{
    private static readonly DateOnly Expiry = new(2026, 8, 21);

    private static OrderLegRequest Leg(decimal strike, OrderSide side, int quantity, OptionRight right = OptionRight.Call) =>
        new(
            new OptionContract($"XYZ{strike}", "XYZ", Expiry, strike, right),
            side,
            quantity,
            PositionEffect.Open);

    private static SubmitOrderRequest Order(
        IReadOnlyList<OrderLegRequest> legs,
        OrderType orderType = OrderType.Limit,
        decimal? limitPrice = 1.20m) =>
        new("DU1234567", StrategyKind.Vertical, orderType, TimeInForce.Day, legs, LimitPrice: limitPrice);

    private static Dictionary<OptionContractKey, int> ConIds(SubmitOrderRequest order) =>
        order.Legs.ToDictionary(leg => leg.Contract.Key(), leg => (int)(leg.Contract.Strike * 10));

    // ---- ratio and spread count ---------------------------------------------------------------

    [Fact]
    public void A_two_lot_one_by_one_spread_is_ratio_one_quantity_two()
    {
        // The trap: leg quantities are absolute contract counts, but TWS wants a reduced ratio plus a
        // spread count. Encoding 2 contracts as Ratio=2 AND TotalQuantity=2 trades four spreads.
        var legs = new[] { Leg(100m, OrderSide.Buy, 2), Leg(105m, OrderSide.Sell, 2) };

        Assert.Equal(2, IbkrOrderBuilder.SpreadCount(legs));
        Assert.Equal([1, 1], IbkrOrderBuilder.Ratios(legs, 2));
    }

    [Fact]
    public void An_unbalanced_ratio_spread_reduces_by_the_gcd()
    {
        // 2x4 is two lots of a 1x2 ratio spread.
        var legs = new[] { Leg(100m, OrderSide.Buy, 2), Leg(105m, OrderSide.Sell, 4) };

        var spreads = IbkrOrderBuilder.SpreadCount(legs);

        Assert.Equal(2, spreads);
        Assert.Equal([1, 2], IbkrOrderBuilder.Ratios(legs, spreads));
    }

    [Fact]
    public void A_coprime_ratio_stays_a_single_spread()
    {
        var legs = new[] { Leg(100m, OrderSide.Buy, 2), Leg(105m, OrderSide.Sell, 3) };

        var spreads = IbkrOrderBuilder.SpreadCount(legs);

        Assert.Equal(1, spreads);
        Assert.Equal([2, 3], IbkrOrderBuilder.Ratios(legs, spreads));
    }

    // ---- net price ----------------------------------------------------------------------------

    [Fact]
    public void The_whole_order_limit_is_divided_into_a_per_spread_price()
    {
        // SubmitOrderRequest.LimitPrice is the net across the whole order (that is how
        // PaperExecutionEngine computes net debit — signed price times each leg's quantity).
        // TWS wants the net for one combo unit. They coincide only at one spread.
        Assert.Equal(1.20m, IbkrOrderBuilder.PerSpreadPrice(2.40m, 2));
        Assert.Equal(1.20m, IbkrOrderBuilder.PerSpreadPrice(1.20m, 1));
    }

    [Fact]
    public void A_net_credit_stays_negative()
    {
        // TWS reads a negative combo limit as a credit received. The sign must survive the division.
        Assert.Equal(-0.75m, IbkrOrderBuilder.PerSpreadPrice(-1.50m, 2));
    }

    // ---- the per-spread price has to be tradeable ----------------------------------------------
    //
    // Verified against TWS 223 on the paper account: a BAG limit that is not a multiple of the legs'
    // ContractDetails.MinTick is rejected with error 110, "The price does not conform to the minimum
    // price variation for this contract". A SPY vertical (minTick 0.01) was refused at 0.3333 and
    // accepted at 0.33. This used to round to four decimal places, which is a price no US option
    // combo can trade at, so any multi-lot order whose net did not divide evenly was dead on arrival.

    [Fact]
    public void A_price_that_does_not_divide_evenly_is_snapped_to_a_tradeable_increment()
    {
        Assert.Equal(0.33m, IbkrOrderBuilder.PerSpreadPrice(1.00m, 3));
        Assert.Equal(0.14m, IbkrOrderBuilder.PerSpreadPrice(1.00m, 7));
    }

    [Fact]
    public void Snapping_is_never_more_aggressive_than_the_price_asked_for()
    {
        // The BAG is always submitted as a BUY with a signed net, so a lower limit is uniformly the
        // less aggressive one: a debit pays no more than asked, and a credit demands no less.
        // Nearest-rounding would push about half of all multi-lot orders the other way.
        Assert.Equal(0.33m, IbkrOrderBuilder.PerSpreadPrice(0.999m, 3));
        Assert.Equal(-0.34m, IbkrOrderBuilder.PerSpreadPrice(-1.00m, 3));
        Assert.Equal(-0.34m, IbkrOrderBuilder.PerSpreadPrice(-0.999m, 3));
    }

    [Fact]
    public void A_price_already_on_the_increment_is_untouched()
    {
        Assert.Equal(1.20m, IbkrOrderBuilder.PerSpreadPrice(2.40m, 2));
        Assert.Equal(0.05m, IbkrOrderBuilder.PerSpreadPrice(0.15m, 3));
        Assert.Equal(-0.75m, IbkrOrderBuilder.PerSpreadPrice(-1.50m, 2));
    }

    [Fact]
    public void A_coarser_increment_is_honoured_when_supplied()
    {
        // SPXW legs report minTick 0.05, and TWS refused an SPXW combo at 0.33 while accepting 0.35 —
        // so the default is right for equity options and too fine for nickel-quoted index series.
        Assert.Equal(0.30m, IbkrOrderBuilder.PerSpreadPrice(1.00m, 3, minimumTick: 0.05m));
        Assert.Equal(-0.35m, IbkrOrderBuilder.PerSpreadPrice(-1.00m, 3, minimumTick: 0.05m));
    }

    [Fact]
    public void The_built_order_carries_the_snapped_price()
    {
        var order = Order([Leg(100m, OrderSide.Buy, 3), Leg(105m, OrderSide.Sell, 3)], limitPrice: 1.00m);

        var plan = IbkrOrderBuilder.Build(order, ConIds(order), account: null, nonGuaranteed: false);

        Assert.Equal(3, plan.SpreadCount);
        Assert.Equal(0.33m, plan.NetPricePerSpread);
        Assert.Equal(0.33d, plan.Order.LmtPrice);
    }

    // ---- full plan ----------------------------------------------------------------------------

    [Fact]
    public void Builds_a_bag_contract_with_signed_leg_actions()
    {
        var order = Order([Leg(100m, OrderSide.Buy, 2), Leg(105m, OrderSide.Sell, 2)], limitPrice: 2.40m);

        var plan = IbkrOrderBuilder.Build(order, ConIds(order), account: "DU1234567", nonGuaranteed: false);

        Assert.Equal("BAG", plan.Contract.SecType);
        Assert.Equal("XYZ", plan.Contract.Symbol);
        Assert.Equal(2, plan.Contract.ComboLegs.Count);
        Assert.Equal("BUY", plan.Contract.ComboLegs[0].Action);
        Assert.Equal("SELL", plan.Contract.ComboLegs[1].Action);
        Assert.Equal(1000, plan.Contract.ComboLegs[0].ConId);

        // Direction lives in the legs; the combo itself is always "bought", or TWS inverts every leg.
        Assert.Equal("BUY", plan.Order.Action);
        Assert.Equal("LMT", plan.Order.OrderType);
        Assert.Equal(2, plan.Order.TotalQuantity);
        Assert.Equal(1.20d, plan.Order.LmtPrice);
        Assert.Equal("DAY", plan.Order.Tif);
        Assert.Equal("DU1234567", plan.Order.Account);
    }

    [Fact]
    public void Refuses_a_leg_without_a_resolved_conid()
    {
        // Placing a combo leg without a conId would submit an under-specified contract.
        var order = Order([Leg(100m, OrderSide.Buy, 1), Leg(105m, OrderSide.Sell, 1)]);
        var partial = new Dictionary<OptionContractKey, int>
        {
            [order.Legs[0].Contract.Key()] = 1000,
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => IbkrOrderBuilder.Build(order, partial, null, false));

        Assert.Contains("no resolved IBKR conId", error.Message);
    }

    [Fact]
    public void Outside_regular_hours_is_off_unless_requested()
    {
        // Required for index options such as SPXW, which trade nearly 24x5. Without it a pre-market
        // order is held until the regular session rather than working.
        var order = Order([Leg(100m, OrderSide.Buy, 1), Leg(105m, OrderSide.Sell, 1)]);

        Assert.False(IbkrOrderBuilder.Build(order, ConIds(order), null, false).Order.OutsideRth);
        Assert.True(IbkrOrderBuilder.Build(order, ConIds(order), null, false, true).Order.OutsideRth);
    }

    [Fact]
    public void Non_guaranteed_routing_is_off_unless_requested()
    {
        var order = Order([Leg(100m, OrderSide.Buy, 1), Leg(105m, OrderSide.Sell, 1)]);

        var guaranteed = IbkrOrderBuilder.Build(order, ConIds(order), null, nonGuaranteed: false);
        var legRisk = IbkrOrderBuilder.Build(order, ConIds(order), null, nonGuaranteed: true);

        Assert.Null(guaranteed.Order.SmartComboRoutingParams);
        Assert.Equal("NonGuaranteed", legRisk.Order.SmartComboRoutingParams[0].Tag);
    }

    // ---- enum mapping --------------------------------------------------------------------------

    [Theory]
    [InlineData(OrderType.Market, "MKT")]
    [InlineData(OrderType.Limit, "LMT")]
    [InlineData(OrderType.Stop, "STP")]
    [InlineData(OrderType.StopLimit, "STP LMT")]
    public void Order_types_map_to_tws_codes(OrderType orderType, string expected)
    {
        Assert.Equal(expected, IbkrOrderBuilder.ToIbOrderType(orderType));
    }

    [Theory]
    [InlineData(TimeInForce.Day, "DAY")]
    [InlineData(TimeInForce.GoodTillCanceled, "GTC")]
    [InlineData(TimeInForce.ImmediateOrCancel, "IOC")]
    [InlineData(TimeInForce.FillOrKill, "FOK")]
    public void Time_in_force_maps_to_tws_codes(TimeInForce timeInForce, string expected)
    {
        Assert.Equal(expected, IbkrOrderBuilder.ToIbTimeInForce(timeInForce));
    }

    // ---- status mapping ------------------------------------------------------------------------

    [Theory]
    [InlineData("PendingSubmit", OrderLifecycleStatus.Submitted)]
    [InlineData("PreSubmitted", OrderLifecycleStatus.Submitted)]
    [InlineData("Filled", OrderLifecycleStatus.Filled)]
    [InlineData("Cancelled", OrderLifecycleStatus.Cancelled)]
    [InlineData("ApiCancelled", OrderLifecycleStatus.Cancelled)]
    [InlineData("Inactive", OrderLifecycleStatus.Failed)]
    public void Tws_order_status_maps_to_the_lifecycle(string status, OrderLifecycleStatus expected)
    {
        Assert.Equal(expected, IbkrOrderBuilder.ToLifecycleStatus(status, 0m, 1m));
    }

    [Fact]
    public void A_working_order_with_partial_fills_is_partially_filled()
    {
        Assert.Equal(
            OrderLifecycleStatus.PartiallyFilled,
            IbkrOrderBuilder.ToLifecycleStatus("Submitted", filled: 1m, remaining: 1m));

        Assert.Equal(
            OrderLifecycleStatus.Submitted,
            IbkrOrderBuilder.ToLifecycleStatus("Submitted", filled: 0m, remaining: 2m));
    }

    [Fact]
    public void Pending_cancel_is_not_terminal()
    {
        // The order can still fill while a cancel is in flight; treating it as done loses the fill.
        var status = IbkrOrderBuilder.ToLifecycleStatus("PendingCancel", 0m, 1m);

        Assert.Equal(OrderLifecycleStatus.Submitted, status);
        Assert.False(IbkrOrderBuilder.IsTerminal(status));
    }

    // ---- router selection ----------------------------------------------------------------------

    [Theory]
    [InlineData("ibkr", true)]
    [InlineData("IBKR", true)]
    [InlineData("paper", false)]
    [InlineData("ibkr-paper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_exact_opt_in_routes_orders_to_the_broker(string? router, bool expected)
    {
        Assert.Equal(expected, OrderRouters.UsesIbkr(router));
    }
}
