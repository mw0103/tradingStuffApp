using System.Globalization;
using TradingStuff.Contracts;

namespace TradingStuff.RiskService;

public static class RiskLimitFactory
{
    public static RiskLimits FromConfiguration(IConfiguration configuration)
    {
        var defaults = RiskLimits.DevelopmentDefaults;

        return new RiskLimits(
            Decimal(configuration, "RiskLimits:MaxLossPerOrder", defaults.MaxLossPerOrder),
            Decimal(configuration, "RiskLimits:MaxBuyingPowerUsage", defaults.MaxBuyingPowerUsage),
            Int(configuration, "RiskLimits:MaxContractsPerOrder", defaults.MaxContractsPerOrder),
            Decimal(configuration, "RiskLimits:MaxDailyLoss", defaults.MaxDailyLoss),
            new GreeksVector(
                Decimal(configuration, "RiskLimits:MaxAbsDelta", defaults.MaxAbsGreeks.Delta),
                Decimal(configuration, "RiskLimits:MaxAbsGamma", defaults.MaxAbsGreeks.Gamma),
                Decimal(configuration, "RiskLimits:MaxAbsTheta", defaults.MaxAbsGreeks.Theta),
                Decimal(configuration, "RiskLimits:MaxAbsVega", defaults.MaxAbsGreeks.Vega)));
    }

    private static decimal Decimal(IConfiguration configuration, string key, decimal fallback) =>
        decimal.TryParse(configuration[key], NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static int Int(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
