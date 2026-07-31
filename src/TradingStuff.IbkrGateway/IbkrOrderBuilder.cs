using System.Globalization;
using IBApi;
using TradingStuff.Contracts;
using IbContract = IBApi.Contract;
using IbOrder = IBApi.Order;

namespace TradingStuff.IbkrGateway;

/// <summary>A multi-leg order translated into the BAG contract and order TWS expects.</summary>
internal sealed record ComboOrderPlan(
    IbContract Contract,
    IbOrder Order,
    int SpreadCount,
    IReadOnlyList<int> Ratios,
    decimal? NetPricePerSpread);

/// <summary>
/// Translates a <see cref="SubmitOrderRequest"/> into a TWS combo (BAG) order.
/// </summary>
/// <remarks>
/// Pure and static so the parts most easily got wrong — ratio reduction, spread count, and the
/// credit/debit sign — are unit-testable without a socket.
/// </remarks>
internal static class IbkrOrderBuilder
{
    public static ComboOrderPlan Build(
        SubmitOrderRequest request,
        IReadOnlyDictionary<OptionContractKey, int> conIds,
        string? account,
        bool nonGuaranteed,
        bool outsideRegularTradingHours = false)
    {
        if (request.Legs.Count == 0)
        {
            throw new InvalidOperationException("An order must have at least one leg.");
        }

        var spreadCount = SpreadCount(request.Legs);
        var ratios = Ratios(request.Legs, spreadCount);

        var comboLegs = new List<ComboLeg>(request.Legs.Count);

        for (var index = 0; index < request.Legs.Count; index++)
        {
            var leg = request.Legs[index];

            if (!conIds.TryGetValue(leg.Contract.Key(), out var conId))
            {
                throw new InvalidOperationException(
                    $"Leg {index} ({leg.Contract.Underlying} {leg.Contract.Expiration:yyyy-MM-dd} " +
                    $"{leg.Contract.Strike} {leg.Contract.Right}) has no resolved IBKR conId.");
            }

            comboLegs.Add(new ComboLeg
            {
                ConId = conId,
                Ratio = ratios[index],
                Action = leg.Side == OrderSide.Buy ? "BUY" : "SELL",
                Exchange = "SMART",

                // SAME(0): open/close is only honoured for institutional accounts, and stating it
                // wrongly is a rejection source. Direction already lives in the leg action.
                OpenClose = 0,
            });
        }

        var first = request.Legs[0].Contract;

        var bag = new IbContract
        {
            Symbol = first.Underlying.ToUpperInvariant(),
            SecType = "BAG",
            Currency = first.Currency.ToUpperInvariant(),
            Exchange = "SMART",
            ComboLegs = comboLegs,
        };

        var netPerSpread = PerSpreadPrice(request.LimitPrice, spreadCount);
        var stopPerSpread = PerSpreadPrice(request.StopPrice, spreadCount);

        var order = new IbOrder
        {
            // Direction is carried by the individual leg actions; setting SELL here would invert
            // every leg. The combo is always "bought" as defined, with a signed net price.
            Action = "BUY",
            OrderType = ToIbOrderType(request.OrderType),
            TotalQuantity = spreadCount,
            Tif = ToIbTimeInForce(request.TimeInForce),
            Transmit = true,
            OrderRef = request.ClientOrderId?.ToString() ?? string.Empty,

            // Required for anything trading outside 09:30-16:15 ET. Index options such as SPXW run
            // nearly 24x5 in global trading hours, and without this the order is simply held until
            // the regular session opens rather than working.
            OutsideRth = outsideRegularTradingHours,
        };

        if (!string.IsNullOrWhiteSpace(account))
        {
            order.Account = account;
        }

        if (netPerSpread is { } limit)
        {
            order.LmtPrice = (double)limit;
        }

        if (stopPerSpread is { } stop)
        {
            order.AuxPrice = (double)stop;
        }

        if (nonGuaranteed)
        {
            // Lets SMART fill legs independently. Faster fills, but leg risk: you can end up with a
            // partial spread. Off by default.
            order.SmartComboRoutingParams = [new TagValue("NonGuaranteed", "1")];
        }

        return new ComboOrderPlan(bag, order, spreadCount, ratios, netPerSpread);
    }

    /// <summary>
    /// Number of spreads being traded — the greatest common divisor of the leg quantities.
    /// </summary>
    /// <remarks>
    /// <c>OrderLegRequest.Quantity</c> is an absolute per-leg contract count, but TWS wants a
    /// reduced ratio per leg plus a spread count in <c>TotalQuantity</c>. A 2-lot 1×1 vertical is
    /// <c>Ratio = 1</c> on both legs with <c>TotalQuantity = 2</c> — encoding it as
    /// <c>Ratio = 2, TotalQuantity = 2</c> silently trades four spreads.
    /// </remarks>
    public static int SpreadCount(IReadOnlyList<OrderLegRequest> legs)
    {
        var count = 0;

        foreach (var leg in legs)
        {
            count = Gcd(count, Math.Abs(leg.Quantity));
        }

        return count == 0 ? 1 : count;
    }

    public static IReadOnlyList<int> Ratios(IReadOnlyList<OrderLegRequest> legs, int spreadCount) =>
        [.. legs.Select(leg => Math.Abs(leg.Quantity) / spreadCount)];

    /// <summary>
    /// Converts a whole-order net price into the per-combo price TWS expects.
    /// </summary>
    /// <remarks>
    /// <c>SubmitOrderRequest.LimitPrice</c> is the net across the whole order — that is how
    /// <c>PaperExecutionEngine.CalculateNetDebit</c> defines it, summing signed prices multiplied by
    /// each leg's absolute quantity. TWS instead wants the net for a single combo unit. The two
    /// coincide only at one spread, which is why the distinction is easy to miss.
    /// <para>Sign is preserved: positive is a net debit, negative a net credit — the same convention
    /// both sides already use.</para>
    /// </remarks>
    public static decimal? PerSpreadPrice(decimal? wholeOrderPrice, int spreadCount) =>
        wholeOrderPrice is { } price && spreadCount > 0
            ? decimal.Round(price / spreadCount, 4, MidpointRounding.ToEven)
            : wholeOrderPrice;

    public static string ToIbOrderType(OrderType orderType) => orderType switch
    {
        OrderType.Market => "MKT",
        OrderType.Limit => "LMT",
        OrderType.Stop => "STP",
        OrderType.StopLimit => "STP LMT",
        _ => throw new InvalidOperationException($"Unsupported order type {orderType}."),
    };

    public static string ToIbTimeInForce(TimeInForce timeInForce) => timeInForce switch
    {
        TimeInForce.Day => "DAY",
        TimeInForce.GoodTillCanceled => "GTC",
        TimeInForce.ImmediateOrCancel => "IOC",
        TimeInForce.FillOrKill => "FOK",
        _ => throw new InvalidOperationException($"Unsupported time in force {timeInForce}."),
    };

    /// <summary>Maps a TWS <c>orderStatus</c> string onto the domain lifecycle.</summary>
    public static OrderLifecycleStatus ToLifecycleStatus(string status, decimal filled, decimal remaining) =>
        status switch
        {
            "Filled" => OrderLifecycleStatus.Filled,
            "Cancelled" or "ApiCancelled" => OrderLifecycleStatus.Cancelled,
            "Inactive" => OrderLifecycleStatus.Failed,
            "PendingSubmit" or "PreSubmitted" or "ApiPending" => OrderLifecycleStatus.Submitted,
            "Submitted" => filled > 0m && remaining > 0m
                ? OrderLifecycleStatus.PartiallyFilled
                : OrderLifecycleStatus.Submitted,

            // PendingCancel is not terminal — the order may still fill. Keep it submitted until a
            // terminal status arrives.
            "PendingCancel" => OrderLifecycleStatus.Submitted,
            _ => OrderLifecycleStatus.Submitted,
        };

    public static bool IsTerminal(OrderLifecycleStatus status) =>
        status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled or OrderLifecycleStatus.Failed;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    public static string Describe(ComboOrderPlan plan) =>
        $"{plan.Contract.Symbol} BAG {plan.Order.OrderType} x{plan.SpreadCount} " +
        $"ratios=[{string.Join(',', plan.Ratios)}] " +
        $"net={plan.NetPricePerSpread?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}";
}
