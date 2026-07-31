using TradingStuff.Contracts;

namespace TradingStuff.MarketDataService;

public sealed class DeterministicOptionMarketDataProvider(IConfiguration configuration)
{
    public MarketDataQuoteResponse GetQuotes(MarketDataQuoteRequest request)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var source = configuration["MarketData:Source"] ?? "ibkr-deterministic-paper-feed";

        var quotes = request.Legs
            .Select(leg => CreateQuote(leg.Contract, capturedAt, source))
            .ToArray();

        return new MarketDataQuoteResponse(quotes, capturedAt, source);
    }

    public IReadOnlyList<OptionContract> GetOptionChain(string underlying, DateOnly expiration)
    {
        var normalizedUnderlying = underlying.ToUpperInvariant();
        var strikes = Enumerable.Range(-5, 11)
            .Select(offset => 100m + (offset * 5m));

        return strikes
            .SelectMany(strike => new[]
            {
                CreateContract(normalizedUnderlying, expiration, strike, OptionRight.Call),
                CreateContract(normalizedUnderlying, expiration, strike, OptionRight.Put)
            })
            .ToArray();
    }

    private static QuoteSnapshot CreateQuote(OptionContract contract, DateTimeOffset capturedAt, string source)
    {
        var intrinsicSeed = Math.Abs(contract.Symbol.GetHashCode(StringComparison.Ordinal)) % 100;
        var daysToExpiry = Math.Max(1, contract.Expiration.DayNumber - DateOnly.FromDateTime(capturedAt.UtcDateTime).DayNumber);
        var timeValue = Math.Clamp(daysToExpiry / 30m, 0.25m, 18m);
        var strikeSkew = Math.Abs(contract.Strike - 100m) / 25m;
        var mid = Math.Round(1.15m + timeValue + strikeSkew + (intrinsicSeed / 100m), 2);
        var spread = Math.Max(0.05m, Math.Round(mid * 0.04m, 2));
        var bid = Math.Max(0.01m, mid - spread / 2m);
        var ask = mid + spread / 2m;

        return new QuoteSnapshot(
            Guid.NewGuid(),
            contract,
            Math.Round(bid, 2),
            Math.Round(ask, 2),
            mid,
            CreateGreeks(contract, daysToExpiry),
            capturedAt,
            source);
    }

    private static OptionGreeks CreateGreeks(OptionContract contract, int daysToExpiry)
    {
        var expiryWeight = Math.Clamp(daysToExpiry / 45m, 0.25m, 4m);
        var moneyness = Math.Clamp((100m - contract.Strike) / 100m, -0.35m, 0.35m);
        var rightDirection = contract.Right == OptionRight.Call ? 1m : -1m;
        var baseDelta = contract.Right == OptionRight.Call ? 0.50m + moneyness : -0.50m + moneyness;

        return new OptionGreeks(
            Math.Round(Math.Clamp(baseDelta, -0.95m, 0.95m), 3),
            Math.Round(0.025m / expiryWeight, 4),
            Math.Round(-0.035m * expiryWeight, 4),
            Math.Round((0.08m + Math.Abs(moneyness)) * rightDirection, 4));
    }

    private static OptionContract CreateContract(string underlying, DateOnly expiration, decimal strike, OptionRight right)
    {
        var rightCode = right == OptionRight.Call ? "C" : "P";
        var symbol = $"{underlying}{expiration:yyyyMMdd}{rightCode}{strike:0.##}";

        return new OptionContract(symbol, underlying, expiration, strike, right);
    }
}
