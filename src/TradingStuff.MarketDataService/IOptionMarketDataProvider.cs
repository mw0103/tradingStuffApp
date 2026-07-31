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
    Task<IReadOnlyList<OptionContract>> GetOptionChainAsync(
        string underlying,
        DateOnly? expiration,
        CancellationToken cancellationToken);
}
