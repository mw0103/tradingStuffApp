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

    /// <summary>
    /// A configured decimal limit, or <paramref name="fallback"/> when the key is not set at all.
    /// </summary>
    /// <remarks>
    /// A key that is set but unreadable throws instead of falling back, and the service does not
    /// start. The fallbacks are <see cref="RiskLimits.DevelopmentDefaults"/> — the loosest limits in
    /// the system — so quietly substituting them for a mistyped production value ("2.5k", "$250", a
    /// stray unit) raises the ceiling the operator was trying to lower, and nothing in any response
    /// says which limits are actually in force.
    /// </remarks>
    private static decimal Decimal(IConfiguration configuration, string key, decimal fallback)
    {
        if (configuration[key] is not { } configured)
        {
            return fallback;
        }

        return decimal.TryParse(configured, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Unreadable(key, configured);
    }

    /// <inheritdoc cref="Decimal"/>
    private static int Int(IConfiguration configuration, string key, int fallback)
    {
        if (configuration[key] is not { } configured)
        {
            return fallback;
        }

        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Unreadable(key, configured);
    }

    private static InvalidOperationException Unreadable(string key, string configured) =>
        new($"Risk limit '{key}' is set to '{configured}', which is not a number. " +
            "A risk limit that is set wrongly is not defaulted — correct it or unset it.");
}
