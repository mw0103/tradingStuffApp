using IBApi;
using TradingStuff.IbkrGateway.History;

namespace TradingStuff.IbkrGateway;

/// <summary>One segment of a <c>reqSecDefOptParams</c> response — TWS sends one per exchange/class.</summary>
internal sealed record OptionChainSegment(
    string Exchange,
    int UnderlyingConId,
    string TradingClass,
    string Multiplier,
    IReadOnlyList<string> Expirations,
    IReadOnlyList<double> Strikes);

/// <summary>
/// Routes TWS callbacks to the pending requests that are waiting on them.
/// </summary>
/// <remarks>
/// Derives from <see cref="DefaultEWrapper"/> so only the handful of callbacks this adapter uses are
/// overridden rather than all ~170 members of <see cref="EWrapper"/>.
/// <para>
/// Every method here runs on the EReader pump thread. Do no blocking work and take no long locks —
/// stalling this thread stalls every in-flight request on the connection.
/// </para>
/// </remarks>
public sealed class IbkrClientWrapper(
    IbkrRequestRegistry registry,
    IbkrOrderTracker orderTracker,
    ILogger<IbkrClientWrapper> logger)
    : DefaultEWrapper
{
    public event Action<int>? NextValidIdReceived;
    public event Action<string>? ManagedAccountsReceived;
    public event Action? ConnectionClosedReceived;
    public event Action<int>? ConnectivityChanged;

    // ---- connection lifecycle -------------------------------------------------------------

    public override void nextValidId(int orderId)
    {
        logger.LogInformation("TWS assigned next valid order id {OrderId}.", orderId);
        NextValidIdReceived?.Invoke(orderId);
    }

    public override void managedAccounts(string accountsList)
    {
        logger.LogInformation("TWS reported managed accounts.");
        ManagedAccountsReceived?.Invoke(accountsList);
    }

    public override void connectionClosed()
    {
        logger.LogWarning("TWS connection closed.");
        registry.FailAll(new IbkrConnectionException("The TWS connection closed while requests were in flight."));
        ConnectionClosedReceived?.Invoke();
    }

    public override void marketDataType(int reqId, int marketDataType) =>
        logger.LogInformation("Market data type for request {ReqId} is {MarketDataType}.", reqId, marketDataType);

    // ---- errors ---------------------------------------------------------------------------

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        if (IbkrErrorCodes.IsInformational(errorCode))
        {
            logger.LogDebug("TWS notice {Code} (request {ReqId}): {Message}", errorCode, id, errorMsg);

            if (IbkrErrorCodes.IsConnectionLevel(errorCode))
            {
                ConnectivityChanged?.Invoke(errorCode);
            }

            return;
        }

        if (IbkrErrorCodes.IsConnectionLevel(errorCode))
        {
            logger.LogWarning("TWS connectivity event {Code}: {Message}", errorCode, errorMsg);

            if (errorCode == IbkrErrorCodes.ConnectivityLost)
            {
                registry.FailAll(new IbkrConnectionException($"TWS connectivity lost ({errorCode}): {errorMsg}"));
            }

            ConnectivityChanged?.Invoke(errorCode);
            return;
        }

        // id is -1 for connection-scoped messages; anything else targets a specific request or order
        // and must fault it, or the caller waits forever for a reply that will never come. Requests
        // and orders share one id sequence, so at most one of these two claims it.
        if (id >= 0 && registry.Fail(id, new IbkrRequestException(errorCode, errorMsg)))
        {
            logger.LogWarning("TWS error {Code} faulted request {ReqId}: {Message}", errorCode, id, errorMsg);
            return;
        }

        if (id >= 0)
        {
            orderTracker.ApplyError(id, errorCode, errorMsg);
        }

        logger.LogWarning("TWS error {Code} (request {ReqId}): {Message}", errorCode, id, errorMsg);
    }

    public override void error(Exception e) =>
        logger.LogError(e, "TWS client raised an exception.");

    public override void error(string str) =>
        logger.LogError("TWS client error: {Message}", str);

    // ---- contract resolution --------------------------------------------------------------

    public override void contractDetails(int reqId, ContractDetails contractDetails) =>
        registry.Get<ListRequest<ContractDetails>>(reqId)?.Add(contractDetails);

    public override void contractDetailsEnd(int reqId)
    {
        registry.Get<ListRequest<ContractDetails>>(reqId)?.Complete();
        registry.Remove(reqId);
    }

    // ---- option chains --------------------------------------------------------------------

    public override void securityDefinitionOptionParameter(
        int reqId,
        string exchange,
        int underlyingConId,
        string tradingClass,
        string multiplier,
        HashSet<string> expirations,
        HashSet<double> strikes) =>
        registry.Get<ListRequest<OptionChainSegment>>(reqId)?.Add(new OptionChainSegment(
            exchange,
            underlyingConId,
            tradingClass,
            multiplier,
            [.. expirations],
            [.. strikes]));

    public override void securityDefinitionOptionParameterEnd(int reqId)
    {
        registry.Get<ListRequest<OptionChainSegment>>(reqId)?.Complete();
        registry.Remove(reqId);
    }

    // ---- historical data --------------------------------------------------------------------

    public override void historicalData(int reqId, Bar bar) =>
        registry.Get<ListRequest<Bar>>(reqId)?.Add(bar);

    public override void historicalDataEnd(int reqId, string start, string end)
    {
        registry.Get<ListRequest<Bar>>(reqId)?.Complete();
        registry.Remove(reqId);
    }

    public override void headTimestamp(int reqId, string headTimestamp) =>
        registry.Get<HeadTimestampSink>(reqId)?.Apply(headTimestamp);

    // ---- market data ----------------------------------------------------------------------

    public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs) =>
        registry.Get<ITickSink>(tickerId)?.ApplyPrice(field, price);

    public override void tickSize(int tickerId, int field, decimal size) =>
        registry.Get<ITickSink>(tickerId)?.ApplySize(field, size);

    public override void tickOptionComputation(
        int tickerId,
        int field,
        int tickAttrib,
        double impliedVolatility,
        double delta,
        double optPrice,
        double pvDividend,
        double gamma,
        double vega,
        double theta,
        double undPrice) =>
        registry.Get<ITickSink>(tickerId)?.ApplyOptionComputation(
            field, impliedVolatility, delta, gamma, vega, theta, undPrice);

    public override void tickSnapshotEnd(int tickerId) =>
        registry.Get<ITickSink>(tickerId)?.CompletePartial();

    // ---- account and positions --------------------------------------------------------------

    // These three stay registered for the life of the connection. Removing them on the ...End
    // callback — as a one-shot request would — drops every subsequent push on the floor, and
    // re-subscribing per read exhausts TWS's account-summary cap. See AccountSubscription.

    public override void accountSummary(int reqId, string account, string tag, string value, string currency) =>
        registry.Get<AccountSummarySubscription>(reqId)?
            .Apply(new AccountSummaryValue(account, tag, value, currency));

    public override void accountSummaryEnd(int reqId) =>
        registry.Get<AccountSummarySubscription>(reqId)?.CompleteSnapshot();

    public override void positionMulti(
        int requestId,
        string account,
        string modelCode,
        Contract contract,
        decimal pos,
        double avgCost) =>
        registry.Get<PositionsSubscription>(requestId)?
            .Apply(new AccountPositionRow(account, contract, pos, avgCost));

    public override void positionMultiEnd(int requestId) =>
        registry.Get<PositionsSubscription>(requestId)?.CompleteSnapshot();

    public override void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) =>
        registry.Get<PnLSubscription>(reqId)?.Apply(dailyPnL, unrealizedPnL, realizedPnL);

    // ---- orders ---------------------------------------------------------------------------

    public override void orderStatus(
        int orderId,
        string status,
        decimal filled,
        decimal remaining,
        double avgFillPrice,
        long permId,
        int parentId,
        double lastFillPrice,
        int clientId,
        string whyHeld,
        double mktCapPrice) =>
        orderTracker.ApplyOrderStatus(orderId, status, filled, remaining, avgFillPrice, permId, whyHeld);

    public override void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
    {
        logger.LogDebug(
            "Open order {OrderId}: {Status} {OrderType} on {Symbol}.",
            orderId,
            orderState.Status,
            order.OrderType,
            contract.Symbol);

        orderTracker.AddOpenOrder(new OpenOrderSummary(
            orderId,
            contract.Symbol ?? string.Empty,
            contract.SecType ?? string.Empty,
            order.Action ?? string.Empty,
            order.TotalQuantity,
            order.OrderType ?? string.Empty,
            order.LmtPrice,
            orderState.Status ?? string.Empty,
            order.Account ?? string.Empty));
    }

    public override void openOrderEnd() => orderTracker.CompleteOpenOrdersSweep();

    public override void execDetails(int reqId, Contract contract, Execution execution)
    {
        logger.LogInformation(
            "Execution {ExecId} on order {OrderId}: {Shares} @ {Price}.",
            execution.ExecId,
            execution.OrderId,
            execution.Shares,
            execution.Price);

        orderTracker.ApplyExecution(contract, execution);
    }

    public override void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport) =>
        orderTracker.ApplyCommission(commissionAndFeesReport);
}

/// <summary>The socket dropped, or was never up, when a request needed it.</summary>
public sealed class IbkrConnectionException(string message) : Exception(message);

/// <summary>TWS rejected a specific request.</summary>
/// <param name="permanent">
/// Overrides the code-only classification. Needed because TWS overloads its error codes: 162 is both
/// "no data" and "pacing violation", and even among the genuine no-data ones permanence depends on
/// the question asked — no data for one historical SLICE says nothing about the contract, while no
/// data for its head timestamp is a standing fact about the contract and whatToShow. Only the call
/// site that knows which request it issued, and can read the message text, can decide that; the code
/// alone cannot. Left null everywhere else, so the default stays
/// <see cref="IbkrErrorCodes.IsPermanentRequestFailure"/>.
/// </param>
public sealed class IbkrRequestException(int errorCode, string message, bool? permanent = null)
    : Exception($"TWS error {errorCode}: {message}")
{
    public int ErrorCode { get; } = errorCode;

    /// <summary>The TWS message text on its own, without this exception's "TWS error N:" prefix.</summary>
    public string TwsMessage { get; } = message;

    /// <summary>True when retrying the identical request cannot succeed.</summary>
    public bool IsPermanent { get; } = permanent ?? IbkrErrorCodes.IsPermanentRequestFailure(errorCode);
}
