namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>The four model keys this development run computes, exactly as the API contract names them.</summary>
public static class VolResidualModelKeys
{
    public const string Har = "HAR";
    public const string Vix = "VIX";
    public const string HarX = "HARX";
    public const string Corrected = "CORRECTED";

    /// <summary>
    /// Ladder rung 4, gradient-boosted trees. Present ONLY on an exploratory run: the registration
    /// runs rung 4 only if rung 3 passes the H1 gate, and it has not. See
    /// <see cref="VolResidualExploratoryRung"/>.
    /// </summary>
    public const string Gbt = "GBT";

    /// <summary>
    /// Equal-weight average of the fitted HAR-X and calibrated-VIX forecasts — candidate B1 in
    /// <c>docs/research/model-candidates.md</c>. Exploratory until registered.
    /// </summary>
    public const string EqualWeight = "EW-HARX-VIX";

    /// <summary>
    /// Quarticity-attenuated HAR-X — candidate A1 (HARQ-X). Exploratory until registered.
    /// </summary>
    public const string HarqX = "HARQX";

    /// <summary>The primary gate — H1 is measured against this model, never HAR.</summary>
    public const string Gate = HarX;

    /// <summary>The registered candidate H1 adjudicates. Never <see cref="Gbt"/>.</summary>
    public const string RegisteredCandidate = Corrected;
}
