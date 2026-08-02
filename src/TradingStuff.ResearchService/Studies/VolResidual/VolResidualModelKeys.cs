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

    /// <summary>Signed-semivariance HAR-X — candidate A2 (SHAR). Exploratory until registered.</summary>
    public const string SharX = "SHARX";

    /// <summary>Continuous/jump decomposition HAR-X — candidate A3 (HAR-CJ). Exploratory until registered.</summary>
    public const string HarCjX = "HARCJX";

    /// <summary>Granger-Ramanathan NNLS combination — candidate B2. Exploratory until registered.</summary>
    public const string GrNnls = "GR-NNLS";

    /// <summary>Regime-split NNLS combination — candidate B3. Exploratory until registered.</summary>
    public const string GrRegime = "GR-REGIME";

    /// <summary>Discounted-QLIKE adaptive weights — candidate B4. Exploratory until registered.</summary>
    public const string DiscountedQlike = "EW-DISC";

    /// <summary>
    /// Quarticity attenuation and the continuous/jump split together — candidate A5, suggested by
    /// A1 and A3 improving by similar amounts through different mechanisms. Exploratory.
    /// </summary>
    public const string HarqCjX = "HARQCJX";

    /// <summary>
    /// The registered residual correction applied over A5 rather than HAR-X — candidate A6.
    /// Exploratory.
    /// </summary>
    public const string CorrectedOverHarqCj = "CORRECTED-QCJ";

    /// <summary>The primary gate — H1 is measured against this model, never HAR.</summary>
    public const string Gate = HarX;

    /// <summary>The registered candidate H1 adjudicates. Never <see cref="Gbt"/>.</summary>
    public const string RegisteredCandidate = Corrected;
}
