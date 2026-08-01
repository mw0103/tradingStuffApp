namespace TradingStuff.RiskService;

/// <summary>
/// Every reason code the risk engine can put on a <see cref="Contracts.RiskLimitBreach"/>.
/// </summary>
/// <remarks>
/// These are operator vocabulary: they end up in audit records, alerts, and whatever an operator
/// greps at 09:29. They were previously literals scattered across the evaluator and the endpoint,
/// so there was no single place that said what the engine can refuse for — which is also what
/// <c>/risk/breach-codes</c> now serves.
/// <para>A code is refusing to price something, or a limit being exceeded. There is no code for
/// "priced, but we are not sure": if the engine cannot stand behind a number it must not return
/// one.</para>
/// </remarks>
public static class RiskBreachCodes
{
    // Configured limits.
    public const string MaxContracts = "MAX_CONTRACTS";
    public const string MaxLossPerOrder = "MAX_LOSS_PER_ORDER";
    public const string BuyingPower = "BUYING_POWER";
    public const string MaxBuyingPowerUsage = "MAX_BUYING_POWER_USAGE";
    public const string MaxDailyLoss = "MAX_DAILY_LOSS";
    public const string MaxDelta = "MAX_DELTA";
    public const string MaxGamma = "MAX_GAMMA";
    public const string MaxTheta = "MAX_THETA";
    public const string MaxVega = "MAX_VEGA";

    // Shapes v1 has no bounded-loss formula for.
    public const string UncoveredShortOption = "UNCOVERED_SHORT_OPTION";
    public const string UncoveredShortVolatilitySpread = "UNCOVERED_SHORT_VOLATILITY_SPREAD";
    public const string UnsupportedStrategy = "UNSUPPORTED_STRATEGY";
    public const string UnsupportedLegCount = "UNSUPPORTED_LEG_COUNT";
    public const string NonPositiveLegQuantity = "NON_POSITIVE_LEG_QUANTITY";
    public const string NonPositiveMultiplier = "NON_POSITIVE_CONTRACT_MULTIPLIER";
    public const string UnequalLegQuantities = "UNEQUAL_LEG_QUANTITIES";

    // Inputs the engine refuses to guess at.
    public const string UnpriceableLeg = "UNPRICEABLE_LEG";

    // Submission control.
    public const string DuplicateOrder = "DUPLICATE_ORDER";

    /// <summary>The whole vocabulary, for operator surfaces that enumerate it.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MaxContracts,
        MaxLossPerOrder,
        BuyingPower,
        MaxBuyingPowerUsage,
        MaxDailyLoss,
        MaxDelta,
        MaxGamma,
        MaxTheta,
        MaxVega,
        UncoveredShortOption,
        UncoveredShortVolatilitySpread,
        UnsupportedStrategy,
        UnsupportedLegCount,
        NonPositiveLegQuantity,
        NonPositiveMultiplier,
        UnequalLegQuantities,
        UnpriceableLeg,
        DuplicateOrder,
    ];
}
