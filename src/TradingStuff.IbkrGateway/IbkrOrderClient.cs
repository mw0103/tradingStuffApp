using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Persistence;

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
    PacedSocket socket,
    IbkrMarketDataClient marketData,
    IbkrOrderTracker tracker,
    OrderIdStore orderIdStore,
    IOptions<IbkrOptions> options,
    ILogger<IbkrOrderClient> logger)
{
    /// <summary>
    /// Status a mapping row carries between being recorded and the first orderStatus callback.
    /// The never-transmitted compensation deletes only rows still holding this exact status.
    /// </summary>
    private const string RecordedStatus = "PendingSubmit";

    private readonly IbkrOptions _options = options.Value;

    public async Task<IbkrOrderState> PlaceAsync(
        Guid internalOrderId,
        SubmitOrderRequest request,
        CancellationToken cancellationToken)
    {
        // Gate first, before any broker interaction.
        connection.EnsureTradingPermitted();

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

        // Persisted BEFORE transmitting, and consulted on every placement. The in-memory claim below
        // protects against concurrent duplicates in this process; this row is what recognises an
        // already-transmitted internal order across a gateway restart.
        var recordedByThisCall = false;

        switch (await orderIdStore.TryRecordPlacementAsync(
            internalOrderId, ibkrOrderId, account, RecordedStatus, cancellationToken))
        {
            case OrderMappingResult.Recorded:
                recordedByThisCall = true;
                break;

            case OrderMappingResult.IntegrityViolation violation:
                // Unconditional refusal — an integrity violation is not an availability problem,
                // and RequireOrderPersistence has no say in it.
                throw new InvalidOperationException(
                    $"Order-map integrity violation; refusing to place: {violation.Reason}");

            case OrderMappingResult.AlreadyMapped mapped:
                logger.LogWarning(
                    "Internal order {InternalOrderId} was already transmitted as IBKR order " +
                    "{ExistingIbkrOrderId} ({Status}), per the persisted order map. Refusing to place again.",
                    internalOrderId,
                    mapped.ExistingIbkrOrderId,
                    mapped.LastStatus);

                return tracker.Get(mapped.ExistingIbkrOrderId)
                       ?? throw new InvalidOperationException(
                           $"Internal order {internalOrderId} was already transmitted as IBKR order " +
                           $"{mapped.ExistingIbkrOrderId} (last status {mapped.LastStatus}), likely by a " +
                           "previous gateway session. Reconcile via GET /ibkr/orders/open before retrying.");

            case OrderMappingResult.Unavailable unavailable when _options.RequireOrderPersistence:
                throw new InvalidOperationException(
                    $"Order persistence is required but unavailable ({unavailable.Reason}); refusing to place.");

            case OrderMappingResult.Unavailable unavailable:
                logger.LogCritical(
                    "Placing order {IbkrOrderId} WITHOUT a persisted mapping ({Reason}). A gateway " +
                    "restart will not recognise internal order {InternalOrderId} as already transmitted.",
                    ibkrOrderId,
                    unavailable.Reason,
                    internalOrderId);
                break;
        }

        // Registered BEFORE transmitting. A crash between placeOrder and the first orderStatus would
        // otherwise leave a live order nothing in this process knows about.
        //
        // The ratio travels with the leg index because the tracker cannot decide when an order's
        // fills are fully reported without it: orderStatus counts a BAG's fills in spreads, while
        // execDetails counts each leg in contracts (see TrackedComboLeg).
        var legsByConId = new Dictionary<int, TrackedComboLeg>();

        for (var index = 0; index < request.Legs.Count; index++)
        {
            legsByConId[conIds[request.Legs[index].Contract.Key()]] = new TrackedComboLeg(index, plan.Ratios[index]);
        }

        // Claiming the internal order id and registering the order are one atomic step. If the claim
        // is already held, this internal order has been transmitted before — a caller retry, a
        // duplicate request — and placing again would put a second live order on the book under a
        // different broker id, with the caller only ever seeing the last one.
        if (!tracker.TryTrack(ibkrOrderId, request.ClientOrderId, internalOrderId, legsByConId))
        {
            var existing = tracker.FindByInternalOrderId(internalOrderId)
                           ?? throw new InvalidOperationException(
                               $"Internal order {internalOrderId} is claimed but has no tracked order.");

            logger.LogWarning(
                "Internal order {InternalOrderId} is already at IBKR as order {IbkrOrderId} ({Status}). " +
                "Returning its state instead of placing again.",
                internalOrderId,
                existing.IbkrOrderId,
                existing.RawStatus);

            return existing;
        }

        logger.LogInformation(
            "Placing order {OrderId} for internal order {InternalOrderId}: {Plan}",
            ibkrOrderId,
            internalOrderId,
            IbkrOrderBuilder.Describe(plan));

        try
        {
            await socket.PlaceOrderAsync(ibkrOrderId, plan.Contract, plan.Order, cancellationToken);
        }
        catch (Exception ex) when (ex is IbkrConnectionException or TimeoutException or OperationCanceledException)
        {
            // These can only originate BEFORE the socket write (RequireClient, the pacing wait, or
            // the trading gate re-check) — nothing reached TWS. Without compensation, the mapping
            // row and tracker claim would make every retry of this internal order return a phantom
            // "working" order that does not exist at the broker.
            tracker.Untrack(ibkrOrderId, internalOrderId);

            if (recordedByThisCall)
            {
                await orderIdStore.TryDeleteNeverTransmittedAsync(
                    internalOrderId, ibkrOrderId, RecordedStatus, CancellationToken.None);
            }

            throw;
        }

        var settled = await tracker.WaitForSettlementAsync(
            ibkrOrderId,
            TimeSpan.FromSeconds(_options.OrderSettleTimeoutSeconds),
            TimeSpan.FromSeconds(_options.FillSettleGraceSeconds),
            cancellationToken);

        var state = settled ?? tracker.Get(ibkrOrderId)
            ?? throw new InvalidOperationException($"Order {ibkrOrderId} vanished from the tracker.");

        await orderIdStore.TryUpdateStatusAsync(ibkrOrderId, state.RawStatus, state.PermId, cancellationToken);

        return state;
    }

    public async Task<IbkrOrderState?> CancelAsync(int ibkrOrderId, string reason, CancellationToken cancellationToken)
    {
        // Deliberately NOT gated on EnsureTradingPermitted: cancelling reduces risk, and the one
        // moment the gate slams shut (say, a reconnect landed on an unexpected account) is exactly
        // when a resting order most needs to be cancellable.
        var state = tracker.Get(ibkrOrderId);

        if (state is null)
        {
            // Not in this process's tracker — but a pre-restart order surfaced by the open-orders
            // sweep is exactly the one an operator most needs to cancel. cancelOrder is harmless
            // on an id TWS does not recognise, so send it and report honestly that it was a blind
            // cancel of an untracked order.
            logger.LogWarning(
                "Cancelling UNTRACKED order {OrderId} ({Reason}) — likely placed by a previous " +
                "gateway session. TWS will error harmlessly if the id is unknown.",
                ibkrOrderId,
                reason);

            await socket.CancelOrderAsync(ibkrOrderId, new IBApi.OrderCancel(), cancellationToken);

            return new IbkrOrderState(
                ibkrOrderId,
                PermId: 0,
                ClientOrderId: null,
                Status: OrderLifecycleStatus.Submitted,
                RawStatus: "CancelRequestedUntracked",
                Filled: 0,
                Remaining: 0,
                AverageFillPrice: 0,
                Fills: [],
                Commission: 0,
                Message: "Cancel sent for an order this gateway session does not track; confirm via GET /ibkr/orders/open.",
                UpdatedAt: DateTimeOffset.UtcNow);
        }

        logger.LogInformation("Cancelling order {OrderId}: {Reason}", ibkrOrderId, reason);
        await socket.CancelOrderAsync(ibkrOrderId, new IBApi.OrderCancel(), cancellationToken);

        var current = tracker.Get(ibkrOrderId);

        if (current is not null)
        {
            await orderIdStore.TryUpdateStatusAsync(ibkrOrderId, current.RawStatus, current.PermId, cancellationToken);
        }

        return current;
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
        var sweep = tracker.BeginOpenOrdersSweep();

        await socket.ReqAllOpenOrdersAsync(cancellationToken);

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
