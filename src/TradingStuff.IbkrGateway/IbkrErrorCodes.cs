namespace TradingStuff.IbkrGateway;

/// <summary>
/// Classification of TWS <c>error</c> callback codes. TWS delivers status notices down the same
/// channel as genuine failures, so a naive adapter faults perfectly healthy requests on
/// "market data farm connection is OK".
/// </summary>
public static class IbkrErrorCodes
{
    /// <summary>Connectivity between TWS and IB's servers has been lost.</summary>
    public const int ConnectivityLost = 1100;

    /// <summary>Connectivity restored, subscriptions lost — everything must be re-subscribed.</summary>
    public const int ConnectivityRestoredDataLost = 1101;

    /// <summary>Connectivity restored with subscriptions intact.</summary>
    public const int ConnectivityRestoredDataMaintained = 1102;

    /// <summary>No security definition found — the contract does not exist. Never retry.</summary>
    public const int NoSecurityDefinition = 200;

    /// <summary>Requested market data is not subscribed, and no delayed fallback is being sent.</summary>
    public const int MarketDataNotSubscribed = 354;

    /// <summary>
    /// "HMDS query returned no data" — this historical slice is empty, not a transport failure. A
    /// different date range on the same contract can still have data, so this is deliberately NOT
    /// classified as a permanent failure; callers should mark only the requested slice as empty.
    /// </summary>
    public const int NoHistoricalData = 162;

    /// <summary>
    /// "Setting end date/time for continuous future security type is not allowed" — a live
    /// <c>CONTFUT</c> rejects a past <c>endDateTime</c>. Deep futures history requires walking
    /// individual expired contracts with <c>IncludeExpired</c> set instead of retrying this request.
    /// </summary>
    public const int ContinuousFutureEndDateNotAllowed = 10339;

    /// <summary>Market data not subscribed, but TWS is sending delayed data instead. Informational.</summary>
    public const int DisplayingDelayedData = 10167;

    /// <summary>Not subscribed and delayed data is NOT enabled — nothing will arrive. Genuinely fatal.</summary>
    public const int DelayedDataNotEnabled = 10168;

    /// <summary>
    /// Status notices, not failures. Faulting a request on any of these is a bug.
    /// </summary>
    private static readonly HashSet<int> Informational =
    [
        1102, // connectivity restored, data maintained
        2100, // account update unsubscribed
        2103, // market data farm connection is broken (transient; 2104 follows)
        2104, // market data farm connection is OK
        2105, // historical data farm connection is broken
        2106, // historical data farm connection is OK
        2107, // historical data farm connection is inactive
        2108, // market data farm connection is inactive but should be available
        2109, // outside regular trading hours order attribute
        2110, // connectivity between TWS and server is broken (transient)
        2119, // market data farm is connecting
        2137, // cancel order size warning
        2158, // security definition data farm connection is OK

        // The delayed-data family. TWS reports these as errors on a market-data request while still
        // streaming usable (delayed, or subscription-independent) ticks, so faulting the request
        // throws away data that is about to arrive. 10168 is deliberately NOT here: it means delayed
        // data is not enabled either, so nothing will ever come.
        10090, // part of requested data not subscribed; subscription-independent ticks still active
        10091, // part of requested data needs a subscription; delayed data is available
        10167, // not subscribed; displaying delayed market data
    ];

    public static bool IsInformational(int errorCode) => Informational.Contains(errorCode);

    /// <summary>
    /// True when retrying the identical request could plausibly succeed. A missing security
    /// definition or a malformed request never becomes valid on retry.
    /// </summary>
    public static bool IsPermanentRequestFailure(int errorCode) => errorCode switch
    {
        NoSecurityDefinition => true,
        201 => true, // order rejected
        202 => true, // order cancelled
        321 => true, // server error validating the request
        ContinuousFutureEndDateNotAllowed => true, // CONTFUT + past endDateTime never becomes valid
        _ => false,
    };

    /// <summary>Codes that mean the socket or upstream link is down rather than one request failing.</summary>
    public static bool IsConnectionLevel(int errorCode) =>
        errorCode is ConnectivityLost or ConnectivityRestoredDataLost or ConnectivityRestoredDataMaintained;

    /// <summary>TWS cancelled the order.</summary>
    public const int OrderCancelled = 202;

    /// <summary>
    /// The order will not work: TWS refused it outright. Distinct from a request failure because it
    /// must drive the order's own lifecycle to a terminal state.
    /// </summary>
    /// <remarks>
    /// 163 is the one that surprises people — TWS's *Precautionary Settings* reject any price more
    /// than a configured percentage from the market (3% by default), so a deliberately far-from-market
    /// limit is refused by TWS before it ever reaches the exchange. Without this mapping the order
    /// sits at <c>PendingSubmit</c> forever while being, in fact, dead.
    /// </remarks>
    public static bool IsOrderRejection(int errorCode) => errorCode switch
    {
        201 => true, // order rejected — see message
        110 => true, // price does not conform to the minimum price variation
        163 => true, // price exceeds a precautionary percentage constraint
        387 => true, // unsupported order type for this exchange/security
        388 => true, // order size does not conform to market rule
        _ => false,
    };
}
