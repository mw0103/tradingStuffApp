using Microsoft.Extensions.Options;
using TradingStuff.Contracts;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// Places and cancels combo orders against TWS.
/// </summary>
/// <remarks>
/// This is the only place in the codebase that calls <c>placeOrder</c>. An order is irreversible the
/// instant it lands, so every path here goes through
/// <see cref="IbkrConnection.EnsureTradingPermitted"/> first.
/// </remarks>
public sealed class IbkrOrderClient(
    IbkrConnection connection,
    IbkrMarketDataClient marketData,
    IbkrOrderTracker tracker,
    IOptions<IbkrOptions> options,
    ILogger<IbkrOrderClient> logger)
{
    private readonly IbkrOptions _options = options.Value;

    public async Task<IbkrOrderState> PlaceAsync(
        Guid internalOrderId,
        SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        // Gate first, before any broker interaction.
        connection.EnsureTradingPermitted();

        var client = connection.RequireClient();

        if (request.OrderType == OrderType.Market && request.Legs.Count > 1)
        {
            // IBKR frequently rejects or badly fills market orders on multi-leg combos. Surfaced as a
            // warning rather than a block: the rejection, if it comes, is TWS's to make.
            logger.LogWarning(
                "Submitting a MARKET order on a {LegCount}-leg combo. IBKR often rejects these; " +
                "a marketable limit is more reliable.",
                request.Legs.Count);
        }

        // Every leg needs a resolved conId before it can appear in a combo.
        var conIds = new Dictionary<OptionContractKey, int>();

        foreach (var leg in request.Legs)
        {
            conIds[leg.Contract.Key()] = await marketData.ResolveOptionConIdAsync(leg.Contract, cancellationToken);
        }

        var account = string.IsNullOrWhiteSpace(_options.AccountId) ? null : _options.AccountId;
        var plan = IbkrOrderBuilder.Build(
            request,
            conIds,
            account,
            _options.NonGuaranteedCombos,
            _options.OutsideRegularTradingHours);

        // Allocated from the shared sequence seeded by nextValidId. Unique and increasing: reusing an
        // id modifies the existing order instead of creating one.
        var ibkrOrderId = connection.Registry.NextRequestId();
        plan.Order.OrderId = ibkrOrderId;

        // Registered BEFORE transmitting. A crash between placeOrder and the first orderStatus would
        // otherwise leave a live order nothing in this process knows about.
        var legIndexByConId = new Dictionary<int, int>();

        for (var index = 0; index < request.Legs.Count; index++)
        {
            legIndexByConId[conIds[request.Legs[index].Contract.Key()]] = index;
        }

        tracker.Track(ibkrOrderId, request.ClientOrderId, internalOrderId, legIndexByConId);

        logger.LogInformation(
            "Placing order {OrderId} for internal order {InternalOrderId}: {Plan}",
            ibkrOrderId,
            internalOrderId,
            IbkrOrderBuilder.Describe(plan));

        client.placeOrder(ibkrOrderId, plan.Contract, plan.Order);

        var settled = await tracker.WaitForSettlementAsync(
            ibkrOrderId,
            TimeSpan.FromSeconds(_options.OrderSettleTimeoutSeconds),
            cancellationToken);

        return settled ?? tracker.Get(ibkrOrderId)
            ?? throw new InvalidOperationException($"Order {ibkrOrderId} vanished from the tracker.");
    }

    public IbkrOrderState? Cancel(int ibkrOrderId, string reason)
    {
        connection.EnsureTradingPermitted();

        var client = connection.RequireClient();
        var state = tracker.Get(ibkrOrderId);

        if (state is null)
        {
            return null;
        }

        logger.LogInformation("Cancelling order {OrderId}: {Reason}", ibkrOrderId, reason);
        client.cancelOrder(ibkrOrderId, new IBApi.OrderCancel());

        return tracker.Get(ibkrOrderId);
    }

    public IbkrOrderState? Get(int ibkrOrderId) => tracker.Get(ibkrOrderId);

    public IReadOnlyList<IbkrOrderState> All() => tracker.All();

    /// <summary>
    /// Everything TWS still considers open on the account, including orders this process did not
    /// place. Read-only, and the honest answer to "is anything resting?" — the in-memory tracker
    /// only knows about orders from the current run.
    /// </summary>
    public async Task<IReadOnlyList<OpenOrderSummary>> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        var sweep = tracker.BeginOpenOrdersSweep();

        client.reqAllOpenOrders();

        try
        {
            return await sweep.Task
                .WaitAsync(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds), cancellationToken);
        }
        catch (TimeoutException)
        {
            // openOrderEnd always follows on a healthy connection; an empty account still gets one.
            tracker.CompleteOpenOrdersSweep();
            return await sweep.Task;
        }
    }
}
