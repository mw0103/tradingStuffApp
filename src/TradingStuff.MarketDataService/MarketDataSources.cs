namespace TradingStuff.MarketDataService;

/// <summary>
/// Values accepted by the <c>MarketData:Source</c> setting, which selects the quote provider.
/// </summary>
public static class MarketDataSources
{
    /// <summary>Deterministic generated quotes. The default, and what the test suite relies on.</summary>
    public const string DeterministicPaperFeed = "ibkr-deterministic-paper-feed";

    /// <summary>Real IBKR data via the gateway, live market data type.</summary>
    public const string IbkrLive = "ibkr-live";

    /// <summary>Real IBKR data via the gateway, delayed market data type — needs no OPRA subscription.</summary>
    public const string IbkrDelayed = "ibkr-delayed";

    /// <summary>
    /// True only for sources that route to the IBKR gateway. Anything unrecognised — including null
    /// — falls back to the deterministic feed, so a typo degrades to safe fake data rather than
    /// silently failing against a broker.
    /// </summary>
    public static bool UsesIbkrGateway(string? source) =>
        string.Equals(source, IbkrLive, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, IbkrDelayed, StringComparison.OrdinalIgnoreCase);
}
