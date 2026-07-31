using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public interface IPortfolioProvider
{
    Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken);
}

public sealed class DevelopmentPortfolioProvider(IConfiguration configuration) : IPortfolioProvider
{
    public Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken)
    {
        var buyingPower = Decimal("Portfolio:BuyingPower", 25_000m);
        var dailyPnL = Decimal("Portfolio:DailyPnL", 0m);

        var snapshot = new PortfolioSnapshot(
            accountId,
            buyingPower,
            dailyPnL,
            GreeksVector.Zero,
            []);

        return Task.FromResult(snapshot);
    }

    private decimal Decimal(string key, decimal fallback) =>
        decimal.TryParse(configuration[key], out var value) ? value : fallback;
}
