namespace TradingStuff.IbkrGateway;

/// <summary>Connection and behaviour settings for the single TWS/IB Gateway socket.</summary>
public sealed class IbkrOptions
{
    public const string SectionName = "IBKR";

    /// <summary>Host running TWS or IB Gateway. It must list this machine under Trusted IPs.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>7497 TWS paper, 7496 TWS live, 4002 Gateway paper, 4001 Gateway live.</summary>
    public int Port { get; set; } = 7497;

    /// <summary>
    /// Must be unique per connected API client. Only client 0 (or TWS's configured Master API
    /// client ID) receives events for orders placed elsewhere.
    /// </summary>
    public int ClientId { get; set; } = 11;

    /// <summary>Expected account. A <c>DU</c> prefix is paper; <c>U</c> is live money.</summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Master switch for real order routing. Defaults to false and must never be defaulted to true
    /// in AppHost, appsettings, or test fixtures. Data paths ignore it; order placement requires it.
    /// </summary>
    public bool AllowLiveTrading { get; set; }

    /// <summary>1 live, 2 frozen, 3 delayed, 4 delayed-frozen. Delayed needs no OPRA subscription.</summary>
    public int MarketDataType { get; set; } = 3;

    /// <summary>Ceiling for contract-detail and chain round trips.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// How long to wait for a quote to become complete (bid, ask, and model Greeks) before returning
    /// whatever ticks did arrive. Illiquid option series can legitimately never fill every field.
    /// </summary>
    public int QuoteTimeoutSeconds { get; set; } = 8;

    public int ReconnectDelaySeconds { get; set; } = 5;

    public int MaxReconnectDelaySeconds { get; set; } = 60;

    /// <summary>Default half-width, in strikes, of the chain window returned around the spot price.</summary>
    public int ChainStrikeWindow { get; set; } = 5;

    /// <summary>
    /// How long a placed order is awaited before its current working state is returned. Not a
    /// cancel — a resting limit order legitimately stays working past this.
    /// </summary>
    public int OrderSettleTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Lets SMART fill combo legs independently. Fills more readily, but accepts leg risk: the order
    /// can end up partially legged into a position that is not the intended spread. Off by default.
    /// </summary>
    public bool NonGuaranteedCombos { get; set; }

    /// <summary>
    /// Marks orders eligible to execute outside 09:30-16:15 ET.
    /// </summary>
    /// <remarks>
    /// Needed to trade index options such as SPXW during global trading hours; without it an order
    /// placed pre-market is held until the regular session. Off by default, because enabling it on
    /// an instrument that only trades regular hours changes nothing, but enabling it unknowingly on
    /// one that trades overnight means orders can fill while nobody is watching.
    /// </remarks>
    public bool OutsideRegularTradingHours { get; set; }
}
