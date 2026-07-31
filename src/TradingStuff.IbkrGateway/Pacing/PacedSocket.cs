using IBApi;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway.Pacing;

/// <summary>
/// The only type permitted to make outbound calls on the TWS socket. Every method acquires the
/// relevant pacing budget from <see cref="IbkrPacingGovernor"/> before touching the wire.
/// </summary>
/// <remarks>
/// Direct <c>connection.RequireClient().reqXxx(...)</c> calls elsewhere bypass pacing and are how
/// the ~50 msg/s and 100-line limits get violated. Connection lifecycle calls
/// (<c>eConnect</c>/<c>eDisconnect</c>/<c>reqMarketDataType</c>) remain in
/// <see cref="IbkrConnection"/>: they happen once per connection, before any budget contention.
/// </remarks>
public sealed class PacedSocket(
    IbkrConnection connection,
    IbkrPacingGovernor governor,
    ILogger<PacedSocket> logger)
{
    public async Task ReqContractDetailsAsync(int requestId, IbContract contract, CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqContractDetails(requestId, contract);
    }

    public async Task ReqSecDefOptParamsAsync(
        int requestId,
        string underlyingSymbol,
        string futFopExchange,
        string underlyingSecType,
        int underlyingConId,
        CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqSecDefOptParams(requestId, underlyingSymbol, futFopExchange, underlyingSecType, underlyingConId);
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
        var client = connection.RequireClient();
        var lease = await governor.AcquireLineAsync(lineClass, cancellationToken);

        try
        {
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
            client.reqMktData(tickerId, contract, genericTickList, snapshot, regulatorySnapshot, mktDataOptions);
            return lease;
        }
        catch
        {
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
            var client = connection.RequireClient();
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
            client.cancelMktData(tickerId);
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
        var client = connection.RequireClient();
        await governor.AcquireHistoricalAsync(pacingRequestKey, pacingContractKey, countsDouble, cancellationToken);
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqHistoricalData(
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
        var client = connection.RequireClient();
        await governor.AcquireHistoricalAsync(pacingRequestKey, pacingContractKey, countsDouble, cancellationToken);
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqHeadTimestamp(requestId, contract, whatToShow, useRth, formatDate);
    }

    /// <summary>
    /// Cancels a head-timestamp request. Never throws: freeing TWS's bookkeeping matters more than
    /// the cancel message landing, and a dead socket has already dropped the request.
    /// </summary>
    public async Task CancelHeadTimestampAsync(int requestId)
    {
        try
        {
            var client = connection.RequireClient();
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
            client.cancelHeadTimestamp(requestId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring failure to cancel head timestamp request {RequestId}.", requestId);
        }
    }

    public async Task PlaceOrderAsync(int orderId, IbContract contract, Order order, CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Order, cancellationToken);

        // Re-checked at the wire: the waits above can span a reconnect, and a reconnect can land on
        // an account the trading gate refuses. The check at the top of PlaceAsync is not enough.
        connection.EnsureTradingPermitted();
        client.placeOrder(orderId, contract, order);
    }

    public async Task CancelOrderAsync(int orderId, OrderCancel orderCancel, CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Order, cancellationToken);
        client.cancelOrder(orderId, orderCancel);
    }

    public async Task ReqAllOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqAllOpenOrders();
    }

    public async Task ReqAccountSummaryAsync(int requestId, string group, string tags, CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqAccountSummary(requestId, group, tags);
    }

    public async Task ReqPositionsMultiAsync(int requestId, string account, string modelCode, CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqPositionsMulti(requestId, account, modelCode);
    }

    public async Task ReqPnLAsync(int requestId, string account, string modelCode, CancellationToken cancellationToken)
    {
        var client = connection.RequireClient();
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, cancellationToken);
        client.reqPnL(requestId, account, modelCode);
    }
}
