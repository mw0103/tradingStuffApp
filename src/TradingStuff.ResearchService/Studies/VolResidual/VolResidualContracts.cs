namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// The response body for both <c>POST /research/studies/vol-residual/run</c> and
/// <c>GET /research/studies/vol-residual/latest</c>. The shape is fixed by the task that specified
/// this endpoint — a UI is coded against it in parallel — and must not drift; see the plain string
/// constants in <see cref="VolResidualRunStatus"/> for the two legal <see cref="Status"/> values.
/// </summary>
public sealed record VolResidualRunResponse(
    Guid RunId,
    bool IsDevelopmentRun,
    DateTimeOffset GeneratedAt,
    string Status,
    string? InsufficientReason,
    VolResidualDataWindow DataWindow,
    VolResidualHoldoutInfo ReservedHoldout,
    string GateModelKey,
    IReadOnlyList<VolResidualModelSummary> Models,
    IReadOnlyList<VolResidualDailyRow> Daily,
    // <summary>
    // The H1 adjudication for the registered candidate against the gate. Null only when the run
    // produced no scoreable day — an absent verdict is honest; a fabricated "fail" would not be.
    // </summary>
    VolResidualH1Verdict? H1 = null,
    // <summary>
    // True when this run executed something OUTSIDE the registered ladder. Tagged at the top level
    // of both the API response and the persisted artifact so no consumer has to dig for it.
    // </summary>
    bool IsExploratory = false,
    // <summary>
    // False when nothing in this run may be entered in <c>research.registered_trials</c> or used to
    // support a registered claim.
    // </summary>
    bool Registrable = true,
    // <summary>Names the ladder rule the run sits outside. Null on a purely registered run.</summary>
    string? ExploratoryReason = null,
    // <summary>Detail of the exploratory rung, when one was run.</summary>
    VolResidualExploratoryRung? Exploratory = null);

public static class VolResidualRunStatus
{
    public const string Ok = "ok";
    public const string InsufficientData = "insufficient-data";
}

public sealed record VolResidualDataWindow(DateOnly From, DateOnly To, int SessionsAvailable, int SessionsUsed);

public sealed record VolResidualHoldoutInfo(DateOnly From, DateOnly To, bool Excluded)
{
    public static VolResidualHoldoutInfo Registered { get; } =
        new(ReservedHoldout.Start, ReservedHoldout.End, Excluded: true);
}

public sealed record VolResidualFoldSummary(
    int Fold, DateOnly TrainFrom, DateOnly TrainTo, DateOnly TestFrom, DateOnly TestTo, double Qlike, int Days);

public sealed record VolResidualModelSummary(
    string Key,
    string Label,
    string Role,
    double PooledQlike,
    double ImprovementVsGatePct,
    IReadOnlyList<VolResidualFoldSummary> Folds);

public sealed record VolResidualDailyRow(
    DateOnly Date,
    int Fold,
    double ActualRv,
    IReadOnlyDictionary<string, double> Forecasts,
    IReadOnlyDictionary<string, double> Qlike,
    double CumulativeQlikeDiffVsGate,
    // <summary>Prior-close VIX for the day, in index points — the level the regime split is made on.</summary>
    double PriorVix = 0.0,
    // <summary>
    // <c>"low"</c> or <c>"high"</c> relative to this day's OWN fold's TRAINING-window median prior
    // VIX. Train-defined, per the registration's regime rules; never a median of the evaluation
    // sample.
    // </summary>
    string VixRegime = "");

public static class VolResidualModelRoles
{
    public const string Reference = "reference";
    public const string Baseline = "baseline";
    public const string Gate = "gate";
    public const string Candidate = "candidate";

    /// <summary>Outside the registered ladder. Never eligible for a claim. See <see cref="VolResidualExploratoryRung"/>.</summary>
    public const string Exploratory = "exploratory";
}

public static class VolResidualVixRegimes
{
    public const string Low = "low";
    public const string High = "high";
}

// ---------------------------------------------------------------------------------------------
// H1 adjudication
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One Diebold-Mariano comparison, in the study's orientation: positive favours the candidate.
/// </summary>
/// <param name="Tau">
/// The materiality margin. 0.02 is the registered primary; 0 is the conventional test and carries a
/// restricted <paramref name="Interpretation"/> for exactly that reason.
/// </param>
/// <param name="Interpretation">
/// One of <see cref="VolResidualDmInterpretations"/>. Fixed strings, not free text — the whole point
/// of the tau = 0 row is that it is not allowed to be narrated as the materiality result.
/// </param>
public sealed record VolResidualDieboldMariano(
    double Tau,
    string Interpretation,
    double MeanLossAdvantage,
    double Statistic,
    double PValueOneSided,
    double LongRunVariance,
    int Observations,
    int HacLag);

public static class VolResidualDmInterpretations
{
    /// <summary>tau = 0.02: the registered gate.</summary>
    public const string Materiality =
        "margin-adjusted (tau = 0.02): tests the registered materiality gate, E[L_candidate] < 0.98 * E[L_gate]";

    /// <summary>tau = 0: reportable, never a substitute for the above.</summary>
    public const string SomeSuperiority =
        "evidence of some superiority (tau = 0): tests only that the candidate beats the gate by SOME " +
        "positive amount, and may never stand in for the materiality claim";
}

public sealed record VolResidualBootstrapCi(
    double SampleMeanAdvantage,
    double LowerBound,
    double Alpha,
    int Resamples,
    double MeanBlockLength,
    long Seed,
    bool ExcludesZero);

/// <summary>Candidate-versus-gate loss in one train-defined VIX half.</summary>
public sealed record VolResidualVixHalfResult(
    string Regime, int Days, double GateQlike, double CandidateQlike, double ImprovementPct, bool Positive);

/// <summary>Candidate-versus-gate loss in one walk-forward fold.</summary>
public sealed record VolResidualFoldAdjudication(
    int Fold, int Days, double GateQlike, double CandidateQlike, double ImprovementPct, bool Positive);

/// <summary>
/// The H1 gate, evaluated condition by condition so a partial pass reads as a partial pass.
/// </summary>
/// <remarks>
/// H1 requires ALL of: a pooled normalized-QLIKE margin of at least 2% versus the gate; a
/// margin-adjusted Diebold-Mariano p below 0.05; a positive improvement in at least two of the three
/// registered folds; a positive improvement in BOTH train-defined VIX halves; and a one-sided block
/// bootstrap lower bound above zero. Each is reported separately, and
/// <see cref="FailedConditions"/> names the ones that did not hold, because "fail" alone loses the
/// distinction between a design that is underpowered and one whose point estimate is wrong-signed.
/// </remarks>
public sealed record VolResidualH1Verdict(
    string GateModelKey,
    string CandidateModelKey,

    double MarginPct,
    bool MarginPasses,

    // <summary>The MARGIN-ADJUSTED statistic. The primary test is tau = 0.02, not tau = 0.</summary>
    double DmStatistic,
    // <summary>The MARGIN-ADJUSTED one-sided p-value.</summary>
    double DmPValue,
    bool DmPasses,

    int FoldsPositive,
    int FoldsTotal,
    bool FoldsPass,

    double BootstrapLower,
    bool BootstrapExcludesZero,

    bool VixHalvesPositive,

    string Verdict,
    IReadOnlyList<string> FailedConditions,

    VolResidualDieboldMariano MarginAdjusted,
    VolResidualDieboldMariano Unadjusted,
    VolResidualBootstrapCi Bootstrap,
    IReadOnlyList<VolResidualFoldAdjudication> Folds,
    IReadOnlyList<VolResidualVixHalfResult> VixHalves,

    // <summary>
    // The only sentence this outcome permits, taken from the pre-registration's fixed claim-language
    // table. Carried as data so a caller cannot narrate the result upward.
    // </summary>
    string PermittedClaim,
    // <summary>Which row of that table was selected, and on what basis.</summary>
    string ClaimBasis);

public static class VolResidualVerdicts
{
    public const string Pass = "pass";
    public const string Fail = "fail";
}

// ---------------------------------------------------------------------------------------------
// Exploratory (outside the registered ladder)
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A rung executed outside the registered ladder. Everything about this record exists so that the
/// status travels with the numbers rather than depending on anyone remembering it.
/// </summary>
public sealed record VolResidualExploratoryRung(
    string ModelKey,
    string Label,
    bool IsExploratory,
    bool Registrable,
    string Reason,
    string PermittedClaim,
    double PooledQlike,
    double ImprovementVsGatePct,
    VolResidualDieboldMariano MarginAdjusted,
    VolResidualDieboldMariano Unadjusted,
    IReadOnlyDictionary<string, string> FrozenHyperparameters,
    // <summary>How many test days had their forecast raised by the positivity floor.</summary>
    int PositivityFloorHits,
    // <summary>Why no retransformation was applied, stated rather than left to be inferred.</summary>
    string RetransformationNote);
