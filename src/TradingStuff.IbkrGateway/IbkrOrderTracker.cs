using System.Collections.Concurrent;
using IBApi;
using TradingStuff.Contracts;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// An order TWS currently considers open — including ones this process did not place (placed
/// manually in TWS, or left over from a previous run).
/// </summary>
public sealed record OpenOrderSummary(
    int IbkrOrderId,
    string Symbol,
    string SecType,
    string Action,
    decimal Quantity,
    string OrderType,
    double LimitPrice,
    string Status,
    string Account);

/// <summary>Live state of one order placed through this gateway.</summary>
public sealed record IbkrOrderState(
    int IbkrOrderId,
    long PermId,
    Guid? ClientOrderId,
    OrderLifecycleStatus Status,
    string RawStatus,
    decimal Filled,
    decimal Remaining,
    decimal AverageFillPrice,
    IReadOnlyList<FillReport> Fills,
    decimal Commission,
    string? Message,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Assembles the asynchronous order callbacks — <c>orderStatus</c>, <c>execDetails</c>,
/// <c>commissionAndFeesReport</c> — into per-order state.
/// </summary>
/// <remarks>
/// Fills arrive per leg and can be <em>replayed</em> after a reconnect, so executions are deduplicated
/// on <c>ExecId</c>; without that, a dropped socket double-counts every fill.
/// </remarks>
public sealed class IbkrOrderTracker(ILogger<IbkrOrderTracker> logger)
{
    private readonly ConcurrentDictionary<int, TrackedOrder> _orders = new();

    // ExecId -> IBKR order id, so a commission report arriving separately can find its order.
    private readonly ConcurrentDictionary<string, int> _executionOrderIds = new();

    // Internal order id -> IBKR order id. Enforces one broker order per internal order, and is the
    // claim that makes that check atomic against concurrent placement attempts.
    private readonly ConcurrentDictionary<Guid, int> _orderIdByInternalId = new();

    private ListRequest<OpenOrderSummary>? _openOrdersSweep;

    /// <summary>
    /// Claims an internal order id and registers the order, before it is transmitted.
    /// </summary>
    /// <remarks>
    /// Registering first means no callback can arrive unowned. Claiming first means the same internal
    /// order cannot be transmitted twice: the claim is a <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>,
    /// so two concurrent attempts cannot both win it. Returns false when the internal order already
    /// has a broker order, in which case the caller must not place.
    /// </remarks>
    /// <param name="legIndexByConId">
    /// Maps each leg's IBKR conId to its index in the original request, so executions can be
    /// attributed to the right leg.
    /// </param>
    public bool TryTrack(
        int ibkrOrderId,
        Guid? clientOrderId,
        Guid internalOrderId,
        IReadOnlyDictionary<int, int> legIndexByConId)
    {
        if (!_orderIdByInternalId.TryAdd(internalOrderId, ibkrOrderId))
        {
            return false;
        }

        _orders[ibkrOrderId] = new TrackedOrder(ibkrOrderId, clientOrderId, internalOrderId, legIndexByConId);
        return true;
    }

    /// <summary>The broker order already placed for an internal order, if there is one.</summary>
    public IbkrOrderState? FindByInternalOrderId(Guid internalOrderId) =>
        _orderIdByInternalId.TryGetValue(internalOrderId, out var ibkrOrderId) ? Get(ibkrOrderId) : null;

    /// <summary>
    /// Releases a claim taken by <see cref="TryTrack"/> when the order provably never reached the
    /// wire, so a retry of the same internal order may place. Only removes the exact pairing it is
    /// given — a transmitted order's claim can never be released by a stray compensation.
    /// </summary>
    internal void Untrack(int ibkrOrderId, Guid internalOrderId)
    {
        if (_orderIdByInternalId.TryRemove(KeyValuePair.Create(internalOrderId, ibkrOrderId)))
        {
            _orders.TryRemove(ibkrOrderId, out _);
        }
    }

    // ---- open order reconciliation ----------------------------------------------------------
    // reqAllOpenOrders carries no request id, and neither do openOrder/openOrderEnd, so this cannot
    // go through the id-keyed registry — it needs a single dedicated slot.

    internal ListRequest<OpenOrderSummary> BeginOpenOrdersSweep()
    {
        var request = new ListRequest<OpenOrderSummary>();
        Interlocked.Exchange(ref _openOrdersSweep, request)?.Complete();
        return request;
    }

    internal void AddOpenOrder(OpenOrderSummary summary) => Volatile.Read(ref _openOrdersSweep)?.Add(summary);

    internal void CompleteOpenOrdersSweep() => Interlocked.Exchange(ref _openOrdersSweep, null)?.Complete();

    public IbkrOrderState? Get(int ibkrOrderId) =>
        _orders.TryGetValue(ibkrOrderId, out var order) ? order.Snapshot() : null;

    public IReadOnlyList<IbkrOrderState> All() => [.. _orders.Values.Select(order => order.Snapshot())];

    /// <summary>Completes when the order reaches a terminal status, or the timeout elapses.</summary>
    /// <param name="fillGrace">
    /// How long to keep waiting AFTER terminal status for the per-leg executions to arrive. See the
    /// body — terminal status and complete fills are two different events on two different TWS
    /// callbacks, and treating them as one loses the fills.
    /// </param>
    public async Task<IbkrOrderState?> WaitForSettlementAsync(
        int ibkrOrderId,
        TimeSpan timeout,
        TimeSpan fillGrace,
        CancellationToken cancellationToken)
    {
        if (!_orders.TryGetValue(ibkrOrderId, out var order))
        {
            return null;
        }

        try
        {
            await order.Settled.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Not an error: a resting limit order legitimately stays working. Report current state.
            logger.LogInformation(
                "Order {OrderId} had not settled after {Timeout}s; returning its working state.",
                ibkrOrderId,
                timeout.TotalSeconds);
        }

        // Terminal status is NOT the end of the story. TWS delivers orderStatus="Filled" BEFORE the
        // execDetails that carry the per-leg fills, and commissionAndFeesReport after those again.
        // Returning on Settled alone hands the caller a filled order with an EMPTY fill list, and
        // ExecutionService then persists it that way — the per-leg fills are simply lost from the
        // system of record while the gateway holds them in memory.
        //
        // Observed live on the paper account, not theorised: a 1-lot SPY 745/747 vertical returned
        // filled=1, avgFillPrice=1.28, fills=[], commission=0; four seconds later the same order
        // read fills=2 (1.67 and 0.39, differencing to exactly 1.28) and commission=1.598693.
        if (order.ExpectsFills)
        {
            try
            {
                await order.FillsSettled.Task.WaitAsync(fillGrace, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Deliberately not fatal: the order really did fill, and reporting it with partial
                // fills beats failing a completed order. Loud, because a persisted fill record that
                // silently disagrees with `filled` is a reconciliation problem later.
                logger.LogWarning(
                    "Order {OrderId} reported filled but its per-leg executions and commissions did " +
                    "not all arrive within {Grace}s. The returned fills/commission are incomplete; " +
                    "reconcile against the broker before trusting them for cost analysis.",
                    ibkrOrderId,
                    fillGrace.TotalSeconds);
            }
        }

        return order.Snapshot();
    }

    public void ApplyOrderStatus(
        int orderId,
        string status,
        decimal filled,
        decimal remaining,
        double avgFillPrice,
        long permId,
        string whyHeld)
    {
        if (!_orders.TryGetValue(orderId, out var order))
        {
            // Orders placed from TWS directly, or by another client id, are not ours to track.
            logger.LogDebug("Ignoring status {Status} for untracked order {OrderId}.", status, orderId);
            return;
        }

        order.ApplyStatus(status, filled, remaining, avgFillPrice, permId, whyHeld);

        logger.LogInformation(
            "Order {OrderId} status {Status} (filled {Filled}, remaining {Remaining}).",
            orderId,
            status,
            filled,
            remaining);
    }

    public void ApplyExecution(IBApi.Contract contract, Execution execution)
    {
        if (!_orders.TryGetValue(execution.OrderId, out var order))
        {
            return;
        }

        // A combo produces an execution for the BAG itself carrying the net price, plus one per leg.
        // The BAG row is a summary of the other two, not a third fill — counting it invents a leg
        // that does not exist and reports the net as if it were a leg price.
        if (string.Equals(contract.SecType, "BAG", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Ignoring the BAG summary execution {ExecId} on order {OrderId}; the leg executions carry the fills.",
                execution.ExecId,
                execution.OrderId);
            return;
        }

        _executionOrderIds[execution.ExecId] = execution.OrderId;
        order.ApplyExecution(contract, execution);
    }

    public void ApplyCommission(CommissionAndFeesReport report)
    {
        if (!_executionOrderIds.TryGetValue(report.ExecId, out var orderId) ||
            !_orders.TryGetValue(orderId, out var order))
        {
            return;
        }

        order.ApplyCommission(report);
    }

    /// <summary>Records a TWS error against an order and settles it if the order is dead.</summary>
    public void ApplyError(int orderId, int errorCode, string message)
    {
        if (!_orders.TryGetValue(orderId, out var order))
        {
            return;
        }

        order.ApplyError(errorCode, message);
    }

    private sealed class TrackedOrder(
        int ibkrOrderId,
        Guid? clientOrderId,
        Guid internalOrderId,
        IReadOnlyDictionary<int, int> legIndexByConId)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, FillReport> _fillsByExecId = [];
        private readonly Dictionary<string, decimal> _commissionByExecId = [];

        private OrderLifecycleStatus _status = OrderLifecycleStatus.Submitted;
        private string _rawStatus = "PendingSubmit";
        private decimal _filled;
        private decimal _remaining;
        private decimal _averageFillPrice;
        private long _permId;
        private string? _message;
        private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes once every leg has an execution and every execution has its commission — the
        /// real "this order's cost is known" signal, distinct from <see cref="Settled"/> (which
        /// only means TWS reported a terminal status).
        /// </summary>
        public TaskCompletionSource FillsSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True once TWS says the order filled, so per-leg executions are owed.</summary>
        public bool ExpectsFills
        {
            get
            {
                lock (_gate)
                {
                    return _filled > 0m && legIndexByConId.Count > 0;
                }
            }
        }

        public void ApplyStatus(
            string status,
            decimal filled,
            decimal remaining,
            double avgFillPrice,
            long permId,
            string whyHeld)
        {
            lock (_gate)
            {
                // A terminal outcome is final; late status chatter must not resurrect the order.
                if (IbkrOrderBuilder.IsTerminal(_status))
                {
                    return;
                }

                _rawStatus = status;
                _status = IbkrOrderBuilder.ToLifecycleStatus(status, filled, remaining);
                _filled = filled;
                _remaining = remaining;
                _permId = permId;

                // Signed, not a plain price: a combo filled for a net credit reports a negative
                // average fill price, and the price converter would discard it as a missing quote.
                if (QuoteRequest.TryConvertSigned(avgFillPrice, out var average))
                {
                    _averageFillPrice = average;
                }

                if (!string.IsNullOrWhiteSpace(whyHeld))
                {
                    _message = $"held: {whyHeld}";
                }

                _updatedAt = DateTimeOffset.UtcNow;
            }

            if (IbkrOrderBuilder.IsTerminal(_status))
            {
                Settled.TrySetResult();
            }
        }

        public void ApplyExecution(IBApi.Contract contract, Execution execution)
        {
            bool complete;

            lock (_gate)
            {
                // Dedupe on ExecId: executions replay after a reconnect.
                if (_fillsByExecId.ContainsKey(execution.ExecId))
                {
                    return;
                }

                QuoteRequest.TryConvertPrice(execution.Price, out var price);

                // Attribute by conId. Legs do not fill in request order — one leg can fill in several
                // executions while another has not started — so a running counter would mislabel them.
                var legIndex = legIndexByConId.TryGetValue(contract.ConId, out var index)
                    ? index
                    : _fillsByExecId.Count;

                _fillsByExecId[execution.ExecId] = new FillReport(
                    Guid.NewGuid(),
                    internalOrderId,
                    legIndex,
                    (int)execution.Shares,
                    price,
                    FillLiquidity.BrokerReported,
                    DateTimeOffset.UtcNow);

                _updatedAt = DateTimeOffset.UtcNow;

                complete = IsFillReportingComplete();
            }

            if (complete)
            {
                FillsSettled.TrySetResult();
            }
        }

        public void ApplyCommission(CommissionAndFeesReport report)
        {
            bool complete;

            lock (_gate)
            {
                if (QuoteRequest.TryConvertGreek(report.CommissionAndFees, out var commission))
                {
                    _commissionByExecId[report.ExecId] = commission;
                }

                complete = IsFillReportingComplete();
            }

            if (complete)
            {
                FillsSettled.TrySetResult();
            }
        }

        /// <summary>
        /// True once every leg has an execution AND every execution has its commission — the point
        /// at which the order's cost is fully known.
        /// </summary>
        /// <remarks>
        /// Leg coverage counts DISTINCT leg indices rather than executions, which is what makes it
        /// correct when one leg fills in several pieces while another has not started.
        /// </remarks>
        private bool IsFillReportingComplete()
        {
            if (legIndexByConId.Count == 0 || _fillsByExecId.Count == 0)
            {
                return false;
            }

            var legsCovered = _fillsByExecId.Values.Select(fill => fill.LegIndex).Distinct().Count();

            return legsCovered >= legIndexByConId.Count
                   && _commissionByExecId.Count >= _fillsByExecId.Count;
        }

        public void ApplyError(int errorCode, string message)
        {
            lock (_gate)
            {
                // Once terminal, stay terminal. Trailing notices — "cancel attempted when order is
                // not in a cancellable state" being the classic — would otherwise overwrite the real
                // outcome with a confusing epilogue.
                if (IbkrOrderBuilder.IsTerminal(_status))
                {
                    return;
                }

                _message = $"TWS error {errorCode}: {message}";

                if (errorCode == IbkrErrorCodes.OrderCancelled)
                {
                    _status = OrderLifecycleStatus.Cancelled;
                }
                else if (IbkrErrorCodes.IsOrderRejection(errorCode))
                {
                    _status = OrderLifecycleStatus.Failed;
                }
                else
                {
                    // A non-fatal notice: record it but leave the order working.
                    _updatedAt = DateTimeOffset.UtcNow;
                    return;
                }

                _rawStatus = $"Error{errorCode}";
                _updatedAt = DateTimeOffset.UtcNow;
            }

            if (IbkrOrderBuilder.IsTerminal(_status))
            {
                Settled.TrySetResult();
            }
        }

        public IbkrOrderState Snapshot()
        {
            lock (_gate)
            {
                return new IbkrOrderState(
                    ibkrOrderId,
                    _permId,
                    clientOrderId,
                    _status,
                    _rawStatus,
                    _filled,
                    _remaining,
                    _averageFillPrice,
                    [.. _fillsByExecId.Values],
                    _commissionByExecId.Values.Sum(),
                    _message,
                    _updatedAt);
            }
        }
    }
}
