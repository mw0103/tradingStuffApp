namespace TradingStuff.EarningsStudy;

/// <summary>
/// The frozen parameters of C1 v0. Every value is quoted from
/// <c>docs/research/c1-preregistration-v0.md</c>; changing one is a C1-PREREG v2 ledger event with a
/// stated reason, not a code edit.
/// </summary>
public static class C1Registration
{
    /// <summary>v0 subsets by TIME ONLY: the most recent four complete calendar years at the freeze.</summary>
    public static readonly DateOnly WindowFrom = new(2022, 1, 1);
    public static readonly DateOnly WindowTo = new(2025, 12, 31);

    /// <summary>Price >= $10 at entry close.</summary>
    public const decimal MinimumEntryPrice = 10m;

    /// <summary>Tradable tier: combined ATM spread &lt;= 15% of straddle mid. The PRIMARY sample.</summary>
    public const decimal TradableSpreadFraction = 0.15m;

    /// <summary>Week-clustered bootstrap, 10,000 replications, 95% interval.</summary>
    public const int BootstrapReplications = 10_000;
    public const decimal ConfidenceLevel = 0.95m;

    /// <summary>Fixed seed so the memo's numbers reproduce exactly. Reproducibility, not a registered parameter.</summary>
    public const int BootstrapSeed = 20260912;

    /// <summary>Pre-registered split: DTE &lt;= 7 vs &gt; 7, calendar days from entry to expiration.</summary>
    public const int ShortDteThresholdDays = 7;

    /// <summary>Secondary statistic only: mean log(RF/IM) with 1% symmetric trimming.</summary>
    public const decimal TrimFraction = 0.01m;

    /// <summary>The platform calendar every entry/exit date is resolved on.</summary>
    public const string Calendar = "NYSE";

    /// <summary>Primary listings admitted by the universe rule, as the SEC exchange field spells them.</summary>
    public static readonly IReadOnlySet<string> AdmittedExchanges =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NYSE", "Nasdaq", "NYSE American" };
}
