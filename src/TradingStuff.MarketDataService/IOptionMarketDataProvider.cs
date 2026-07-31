using TradingStuff.Contracts;

namespace TradingStuff.MarketDataService;

/// <summary>
/// Source of option quotes and chains. Implementations are selected by the <c>MarketData:Source</c>
/// setting, which keeps the deterministic generator available for tests and offline work while a
/// broker-backed provider serves real data.
/// </summary>
public interface IOptionMarketDataProvider
{
    /// <summary>Identifies the provider on every snapshot it produces, for audit provenance.</summary>
    string Source { get; }

    Task<MarketDataQuoteResponse> GetQuotesAsync(
        MarketDataQuoteRequest request,
        CancellationToken cancellationToken);

    /// <param name="expiration">Null asks the provider for its own default expiration.</param>
    /// <param name="strikeWindow">Half-width, in strikes, of the window around spot; null uses the provider default.</param>
    /// <param name="tradingClass">
    /// Option series to select where the underlying lists more than one — SPX (AM-settled
    /// monthlies) vs SPXW (PM-settled weeklies/dailies) being the case that matters. Null lets the
    /// provider pick its standard class. Providers without the concept ignore it.
    /// </param>
    Task<IReadOnlyList<OptionContract>> GetOptionChainAsync(
        string underlying,
        DateOnly? expiration,
        int? strikeWindow,
        string? tradingClass,
        CancellationToken cancellationToken);
}
