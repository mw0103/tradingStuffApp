using IBApi;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway.Pacing;

/// <summary>
/// The only type permitted to make outbound calls on the TWS socket. Every method acquires the
/// relevant pacing budget from <see cref="IbkrPacingGovernor"/> before touching the wire.
/// </summary>
/// <remarks>
/// <para>
/// Direct <c>connection.RequireClient().reqXxx(...)</c> calls elsewhere bypass pacing and are how
/// the ~50 msg/s and 100-line limits get violated. Connection lifecycle calls
/// (<c>eConnect</c>/<c>eDisconnect</c>/<c>reqMarketDataType</c>) remain in
/// <see cref="IbkrConnection"/>: they happen once per connection, before any budget contention.
/// </para>
/// <para>
/// <b>Every method resolves the socket AFTER its budgets, never before.</b> A budget wait can span
/// minutes (the historical window) and a reconnect replaces the <c>EClientSocket</c> outright, so a
/// reference captured before the wait belongs to a dead socket by the time the wait returns.
/// Writing to it throws or silently no-ops depending on where the old socket died — and for
/// <see cref="PlaceOrderAsync"/> a silent no-op is the worst outcome in the system: the order map
/// row and the tracker claim already exist, so the gateway reports a working order TWS never
/// received. Resolving last shrinks the window to the handful of instructions between
/// <see cref="IbkrConnection.RequireClient"/> and the write, and a connection that is already down
/// at that point fails the call so the caller's compensation runs.
/// </para>
/// <para>
/// Not sealed, and two methods are virtual, <b>for tests only</b>: the order client's
/// never-transmitted compensation is the most consequential branch in the gateway and is otherwise
/// unreachable without a live socket, since order placement depends on conId resolution which also
/// needs one. Production has exactly one implementation, and it must stay that way — a second one
/// would be a second <c>placeOrder</c> call site.
/// </para>
/// </remarks>
public class PacedSocket(
    IbkrConnection connection,
    IbkrPacingGovernor governor,
    ILogger<PacedSocket> logger)
{
    public virtual async Task ReqContractDetailsAsync(int requestId, IbContract contract, CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        connection.RequireClient().reqContractDetails(requestId, contract);
    }

    public async Task ReqSecDefOptParamsAsync(
        int requestId,
        string underlyingSymbol,
        string futFopExchange,
        string underlyingSecType,
        int underlyingConId,
        CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        connection.RequireClient()
            .reqSecDefOptParams(requestId, underlyingSymbol, futFopExchange, underlyingSecType, underlyingConId);
    }

    /// <summary>
    /// Opens a streaming market-data subscription, consuming one line from the ledger. The returned
    /// lease MUST be passed to <see cref="CancelMktDataAsync"/> (or disposed) or the line leaks.
    /// </summary>
    public async Task<LineLease> ReqMktDataAsync(
        int tickerId,
        IbContract contract,
        string genericTickList,
        bool snapshot,
        bool regulatorySnapshot,
        List<TagValue>? mktDataOptions,
        LineClass lineClass,
        CancellationToken cancellationToken)
    {
        var lease = await governor.AcquireLineAsync(lineClass, cancellationToken);

        try
        {
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);

            var client = connection.RequireClient();
            client.reqMktData(tickerId, contract, genericTickList, snapshot, regulatorySnapshot, mktDataOptions);

            return lease;
        }
        catch
        {
            // Covers the socket being gone as well as the message budget expiring: either way no
            // subscription exists at TWS, so the line must go straight back to the ledger.
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Cancels a subscription and releases its line. Never throws: freeing the line matters more
    /// than the cancel message landing, and a dead socket has already dropped the subscription.
    /// </summary>
    /// <remarks>
    /// The ledger line is released FIRST, so callers are never blocked on the cancel message; the
    /// message itself is Normal-class so a storm of cancels (a large quote batch unwinding) is
    /// paced like any other data traffic instead of bursting past the wire limit. The ledger can
    /// briefly run ahead of TWS's own count while cancels drain — the 10-line gap between the
    /// 90-line cap and the account's 100 exists to absorb exactly this.
    /// </remarks>
    public async Task CancelMktDataAsync(int tickerId, LineLease lease)
    {
        lease.Dispose();

        try
        {
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
            connection.RequireClient().cancelMktData(tickerId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring failure to cancel market data for ticker {TickerId}.", tickerId);
        }
    }

    /// <summary>
    /// Requests historical bars. Acquires the historical pacing window (its 15s identical-request
    /// cooldown, 5-per-2s per-contract limit, and 54-per-10min budget with BID_ASK costing double)
    /// before the general message-rate budget, since TWS paces historical data far more
    /// aggressively than the ~50 msg/s wire limit.
    /// </summary>
    /// <param name="pacingRequestKey">Exact request identity — see <see cref="IbkrPacingGovernor.AcquireHistoricalAsync"/>.</param>
    /// <param name="pacingContractKey">Contract identity, for the per-contract short window.</param>
    /// <param name="countsDouble">True for BID_ASK, which costs double against the window.</param>
    public async Task ReqHistoricalDataAsync(
        int requestId,
        IbContract contract,
        string endDateTime,
        string durationString,
        string barSizeSetting,
        string whatToShow,
        int useRth,
        int formatDate,
        bool keepUpToDate,
        List<TagValue>? chartOptions,
        string pacingRequestKey,
        string pacingContractKey,
        bool countsDouble,
        CancellationToken cancellationToken)
    {
        await governor.AcquireHistoricalAsync(pacingRequestKey, pacingContractKey, countsDouble, cancellationToken);
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);

        connection.RequireClient().reqHistoricalData(
            requestId, contract, endDateTime, durationString, barSizeSetting, whatToShow, useRth, formatDate,
            keepUpToDate, chartOptions);
    }

    /// <summary>
    /// Requests the earliest available timestamp for a contract. Counts as an ongoing historical
    /// request against the same pacing window as bars, so it draws from
    /// <see cref="IbkrPacingGovernor.AcquireHistoricalAsync"/> too.
    /// <see cref="CancelHeadTimestampAsync"/> MUST follow once the caller is done with it, or the
    /// request leaks against TWS's own bookkeeping.
    /// </summary>
    public async Task ReqHeadTimestampAsync(
        int requestId,
        IbContract contract,
        string whatToShow,
        int useRth,
        int formatDate,
        string pacingRequestKey,
        string pacingContractKey,
        bool countsDouble,
        CancellationToken cancellationToken)
    {
        await governor.AcquireHistoricalAsync(pacingRequestKey, pacingContractKey, countsDouble, cancellationToken);
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);

        connection.RequireClient().reqHeadTimestamp(requestId, contract, whatToShow, useRth, formatDate);
    }

    /// <summary>
    /// Cancels a head-timestamp request. Never throws: freeing TWS's bookkeeping matters more than
    /// the cancel message landing, and a dead socket has already dropped the request.
    /// </summary>
    public async Task CancelHeadTimestampAsync(int requestId)
    {
        try
        {
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
            connection.RequireClient().cancelHeadTimestamp(requestId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring failure to cancel head timestamp request {RequestId}.", requestId);
        }
    }

    /// <summary>
    /// Transmits an order. The one call site in the system that reaches <c>placeOrder</c>.
    /// </summary>
    /// <param name="onAboutToTransmit">
    /// Invoked immediately before the socket write, with nothing awaitable between the two. It is
    /// the caller's only reliable answer to "did this order reach TWS?": everything that can throw
    /// before it — the message budget, the trading-gate re-check, resolving the live socket — is
    /// provably never-transmitted, and everything from the write onward is not. Order compensation
    /// (undoing the order-map row and the tracker claim) depends on that distinction, and getting
    /// it wrong in the permissive direction risks a second live order for one internal id, so a
    /// failure raised from inside <c>placeOrder</c> itself deliberately counts as transmitted even
    /// though it may not be.
    /// </param>
    public virtual async Task PlaceOrderAsync(
        int orderId,
        IbContract contract,
        Order order,
        Action onAboutToTransmit,
        CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Order, cancellationToken);

        // Both re-checked at the wire: the wait above can span a reconnect, which can land on an
        // account the trading gate refuses and always replaces the socket. The checks at the top of
        // PlaceAsync, and any client reference resolved before the wait, are not enough.
        connection.EnsureTradingPermitted();
        var client = connection.RequireClient();

        onAboutToTransmit();
        client.placeOrder(orderId, contract, order);
    }

    public async Task CancelOrderAsync(int orderId, OrderCancel orderCancel, CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Order, cancellationToken);
        connection.RequireClient().cancelOrder(orderId, orderCancel);
    }

    public async Task ReqAllOpenOrdersAsync(CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        connection.RequireClient().reqAllOpenOrders();
    }

    public async Task ReqAccountSummaryAsync(int requestId, string group, string tags, CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        connection.RequireClient().reqAccountSummary(requestId, group, tags);
    }

    public async Task ReqPositionsMultiAsync(int requestId, string account, string modelCode, CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        connection.RequireClient().reqPositionsMulti(requestId, account, modelCode);
    }

    public async Task ReqPnLAsync(int requestId, string account, string modelCode, CancellationToken cancellationToken)
    {
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        connection.RequireClient().reqPnL(requestId, account, modelCode);
    }

    /// <summary>
    /// Desubscribes an account stream. Never throws — a rebuild must proceed whether or not the
    /// cancel lands, and a socket that has already gone has dropped the subscription anyway.
    /// </summary>
    /// <remarks>
    /// Measured against TWS 223 on the paper account: <c>cancelAccountSummary</c> does NOT free the
    /// slot the "maximum number of account summary requests exceeded" error (322) counts, so the
    /// cancel alone does not make a rebuild safe — see
    /// <see cref="IbkrAccountClient"/> for the request-id reuse that does. It is still issued
    /// because leaving the stream live means TWS keeps pushing into a sink nothing reads.
    /// </remarks>
    public async Task CancelAccountStreamsAsync(int? summaryId, int? positionsId, int? pnlId)
    {
        if (summaryId is { } summary)
        {
            await TryCancelAsync(summary, static (client, id) => client.cancelAccountSummary(id), "account summary");
        }

        if (positionsId is { } positions)
        {
            await TryCancelAsync(positions, static (client, id) => client.cancelPositionsMulti(id), "positions");
        }

        if (pnlId is { } pnl)
        {
            await TryCancelAsync(pnl, static (client, id) => client.cancelPnL(id), "P&L");
        }
    }

    private async Task TryCancelAsync(int requestId, Action<EClientSocket, int> cancel, string description)
    {
        try
        {
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
            cancel(connection.RequireClient(), requestId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex, "Ignoring failure to cancel the {Description} stream {RequestId}.", description, requestId);
        }
    }
}
