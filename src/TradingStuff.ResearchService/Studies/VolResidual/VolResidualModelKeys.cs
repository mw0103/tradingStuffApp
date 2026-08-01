namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>The four model keys this development run computes, exactly as the API contract names them.</summary>
public static class VolResidualModelKeys
{
    public const string Har = "HAR";
    public const string Vix = "VIX";
    public const string HarX = "HARX";
    public const string Corrected = "CORRECTED";

    /// <summary>The primary gate — H1 is measured against this model, never HAR.</summary>
    public const string Gate = HarX;
}
