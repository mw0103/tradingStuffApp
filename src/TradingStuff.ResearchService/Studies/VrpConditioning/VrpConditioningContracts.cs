using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// The response body for <c>POST /research/studies/vrp-conditioning/run</c> and
/// <c>GET /research/studies/vrp-conditioning/latest</c>.
/// </summary>
/// <remarks>
/// <see cref="Limitations"/> is not decoration and is not optional. This study produces two numbers
/// that will be misread if they travel alone — a P&amp;L-shaped figure with no costs in it, and a
/// bucket ordering over a sample with roughly 120 effective independent observations. The
/// registration's own words for this companion are "conditioning knowledge, not P&amp;L claims" and
/// "bootstrap CIs only, no significance claims", so the limitations ride on the payload as data,
/// where a consumer cannot drop them by rendering a different field.
/// </remarks>
public sealed record VrpConditioningRunResponse(
    Guid RunId,
    bool IsDevelopmentRun,
    DateTimeOffset GeneratedAt,
    string Status,
    string? InsufficientReason,
    VrpConditioningDataWindow DataWindow,
    VolResidualHoldoutInfo ReservedHoldout,
    VrpConditioningDesign Design,
    string GateArmKey,
    IReadOnlyList<VrpConditioningArmSummary> Arms,
    IReadOnlyList<VrpConditioningArmConditioning> Conditioning,
    IReadOnlyList<VrpConditioningDmComparison> DieboldMariano,
    VrpConditioningEffectiveSample EffectiveSample,
    IReadOnlyList<VrpConditioningDailyRow> Daily,
    VrpConditioningLimitations Limitations,
    // <summary>
    // Always false. This companion produces conditioning knowledge, never a registered claim, and
    // nothing it computes is written to research.registered_trials or consumes a variant slot.
    // </summary>
    bool Registrable = false);

public static class VrpConditioningRunStatus
{
    public const string Ok = "ok";
    public const string InsufficientData = "insufficient-data";
}

public sealed record VrpConditioningDataWindow(
    DateOnly From, DateOnly To, int SessionsAvailable, int DecisionDates,
    DateOnly? FirstLabelFrom, DateOnly? LastLabelTo);

/// <summary>Every frozen design constant, echoed so a reader never has to trust the prose.</summary>
public sealed record VrpConditioningDesign(
    int LabelTradingDays,
    string LabelDefinition,
    string ImpliedConversion,
    string DecisionTimestamp,
    int OverlappingHacLag,
    int NonOverlappingHacLag,
    int NonOverlappingStride,
    double BootstrapMeanBlockLength,
    int BootstrapResamples,
    int PurgeRows,
    string QuintileBreakpointSource)
{
    public static VrpConditioningDesign Registered { get; } = new(
        VrpConditioningHorizon.LabelTradingDays,
        "Cumulative SPX session realized variance over trading days t+1 .. t+21 inclusive, from the " +
        "same 1-minute bars and the same subsampled-5-minute estimator as the parent study's session " +
        "label (VolatilityPresets.SpxStudyTarget). Exactly 21 completed sessions, never a short window.",
        "impliedVar_t = (VIX_t/100)^2 * 21/252. VIX is a 30-CALENDAR-day annualized volatility; 30 " +
        "calendar days is 30*252/365 = 20.7 trading days, within a third of a day of the 21-trading-day " +
        "label, so the conversion is a pure de-annualization with no maturity interpolation. VIX_t is " +
        "the close of the decision date — the last print before the label window opens, never one from " +
        "inside it.",
        "Close of decision date t. Every feature is computed from sessions closed at or before t; the " +
        "label spans t+1 .. t+21.",
        VrpConditioningHorizon.OverlappingHacLag,
        VrpConditioningHorizon.NonOverlappingHacLag,
        VrpConditioningHorizon.LabelTradingDays,
        VrpConditioningHorizon.BootstrapMeanBlockLength,
        TradingStuff.Volatility.Forecasting.StationaryBlockBootstrap.RegisteredResamples,
        VrpConditioningHorizon.PurgeRows,
        "Each fold's TRAINING-window spread distribution, frozen before the test block is scored. " +
        "Never the evaluation sample — an evaluation-defined bucket edge is a leak, and it shows up as " +
        "suspiciously even bucket counts.");
}

// ---------------------------------------------------------------------------------------------
// Forecast arms
// ---------------------------------------------------------------------------------------------

public sealed record VrpConditioningArmFold(
    string Fold, DateOnly TrainFrom, DateOnly TrainTo, DateOnly TestFrom, DateOnly TestTo, double Qlike, int Days);

public sealed record VrpConditioningArmSummary(
    string Key,
    string Label,
    string Role,
    double PooledQlike,
    double ImprovementVsGatePct,
    IReadOnlyList<VrpConditioningArmFold> Folds);

// ---------------------------------------------------------------------------------------------
// Conditioning (the deliverable)
// ---------------------------------------------------------------------------------------------

/// <summary>A bootstrap percentile interval. Never accompanied by a p-value in this study.</summary>
public sealed record VrpConditioningInterval(double Lower, double Upper, double Alpha, int Draws);

/// <summary>The shape of a bucket sequence, kept as structured data rather than a rendered sentence.</summary>
public sealed record VrpConditioningMonotonicity(
    string Shape, bool IsMonotone, string Direction, int Violations, int AdjacentPairs);

/// <param name="MeanRealizedVariance">Mean subsequent 21-session realized variance in this bucket.</param>
/// <param name="MeanPremiumCollected">Mean <c>implied - realized</c>: the variance premium actually collected.</param>
/// <param name="MeanPnlPerVegaNotional">
/// Mean variance-swap-style short payoff per unit vega notional, in annualized volatility points.
/// See <see cref="VrpConditioningLimitations.PnlProxy"/> before quoting this anywhere.
/// </param>
public sealed record VrpConditioningBucket(
    int Bucket,
    string Label,
    int Days,
    double MeanSpread,
    double MeanRealizedVariance,
    double MeanRealizedAnnualizedVolPct,
    double MeanImpliedVariance,
    double MeanPremiumCollected,
    VrpConditioningInterval PremiumInterval,
    double MeanPnlPerVegaNotional,
    VrpConditioningInterval PnlInterval,
    VrpConditioningInterval RealizedVarianceInterval);

/// <param name="BootstrapMonotoneFractionPnl">
/// Fraction of block-bootstrap resamples in which the five bucket means of the P&amp;L proxy are
/// monotone. A stability diagnostic for the shape — NOT a p-value, and not to be reported as one.
/// </param>
public sealed record VrpConditioningArmConditioning(
    string Arm,
    IReadOnlyList<double> TrainSpreadBreakpoints,
    IReadOnlyList<VrpConditioningBucket> Buckets,
    VrpConditioningMonotonicity PnlMonotonicity,
    VrpConditioningMonotonicity PremiumMonotonicity,
    VrpConditioningMonotonicity RealizedVarianceMonotonicity,
    double Q5MinusQ1Pnl,
    VrpConditioningInterval Q5MinusQ1PnlInterval,
    double BootstrapMonotoneFractionPnl,
    double BootstrapMonotoneFractionPremium,
    int UsableResamples);

// ---------------------------------------------------------------------------------------------
// Inference
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One arm against the gate, computed twice: once on the overlapping daily series and once on a
/// stride-thinned non-overlapping subsample.
/// </summary>
/// <remarks>
/// The two WILL disagree, and the disagreement is the point. Overlapping 21-day windows share up to
/// 20 of their 21 label days, so ~3,500 daily observations carry roughly 175 non-overlapping windows
/// and fewer effective ones. The overlapping row is reported because it uses all the data; the
/// non-overlapping row is the honest inference, and <see cref="Honest"/> marks which is which so a
/// reader cannot pick the friendlier number without noticing.
/// </remarks>
public sealed record VrpConditioningDm(
    string Sampling,
    bool Honest,
    string Note,
    double MeanLossAdvantage,
    double Statistic,
    double PValueOneSided,
    double LongRunVariance,
    int Observations,
    int HacLag,
    bool Degenerate);

public sealed record VrpConditioningDmComparison(
    string Arm,
    string GateArm,
    VrpConditioningDm Overlapping,
    VrpConditioningDm NonOverlapping,
    // <summary>True when the two samplings disagree about the SIGN of the advantage or about p &lt; 0.05.</summary>
    bool SamplingsDisagree,
    VrpConditioningInterval MeanAdvantageInterval);

public sealed record VrpConditioningEffectiveSample(
    int ScoredDecisionDates,
    int NonOverlappingWindows,
    int LabelTradingDays,
    string Note);

// ---------------------------------------------------------------------------------------------
// Daily
// ---------------------------------------------------------------------------------------------

public sealed record VrpConditioningDailyRow(
    DateOnly Date,
    DateOnly LabelFrom,
    DateOnly LabelTo,
    string Fold,
    double VixLevel,
    double ImpliedVariance,
    double RealizedVariance,
    double RealizedAnnualizedVolPct,
    double PremiumCollected,
    double PnlPerVegaNotional,
    IReadOnlyDictionary<string, double> Forecasts,
    IReadOnlyDictionary<string, double> Qlike,
    IReadOnlyDictionary<string, double> Spread,
    IReadOnlyDictionary<string, int> Bucket);

// ---------------------------------------------------------------------------------------------
// Limitations — carried as data, on every response, including the insufficient-data one
// ---------------------------------------------------------------------------------------------

public sealed record VrpConditioningLimitations(
    string Headline,
    string PnlProxy,
    string Inference,
    string Overlap,
    string LabelVersusImplied,
    string VixSource,
    string PermittedClaim)
{
    public static VrpConditioningLimitations Registered { get; } = new(
        "CONDITIONING KNOWLEDGE, NOT P&L CLAIMS. Nothing in this run is evidence of tradeable profit " +
        "and nothing in it is a significance test.",

        "The P&L column is a variance-swap-style payoff, (implied - realized) per unit vega notional, " +
        "and NOTHING ELSE. It contains no option execution detail, no bid-ask, no slippage, no delta " +
        "hedging, no replication error, no margin, no financing, no discounting and no capacity. A real " +
        "short-volatility position is not a variance swap and does not earn this number. It exists only " +
        "so that 'does conditioning on the spread sort outcomes?' can be answered in an economically " +
        "ordered unit. Quoting it as a return, an edge, or an expected profit is a misuse.",

        "BOOTSTRAP CONFIDENCE INTERVALS ONLY. NO SIGNIFICANCE CLAIMS. The pre-registration " +
        "(docs/research/volatility-forecast-residual-study.md, 'Companion study: VrpConditioningStudy') " +
        "permits this companion bootstrap CIs and no significance claims at all. The Diebold-Mariano " +
        "p-values below are descriptive diagnostics for comparing two samplings of the same data; they " +
        "are not a gate, no hypothesis is being tested here, and no p-value in this response may be " +
        "reported as establishing anything.",

        "21-day windows on daily data OVERLAP. Two decisions one day apart share 20 of their 21 label " +
        "days. Roughly 3,500 daily observations over 2010-2023 therefore carry only about 175 " +
        "non-overlapping windows and perhaps 120 effective ones. Every overlapping-sample statistic in " +
        "this response is correspondingly over-precise; where a non-overlapping figure is reported " +
        "beside it, the non-overlapping one is the honest inference.",

        "The implied leg prices CALENDAR time (VIX includes overnight and weekend variance); the " +
        "realized leg is SESSION variance with the overnight gap deliberately excluded, matching the " +
        "parent study's registered label. So 'implied - realized' here is structurally WIDER than a " +
        "true variance risk premium by the whole overnight component. That is a roughly constant level " +
        "shift: harmless for whether SORTING on the spread sorts outcomes, and fatal to any claim about " +
        "the LEVEL of the premium or the profitability of collecting it.",

        "VIX comes from the IBKR-recorded daily bar in research.bars, not from Cboe's official daily " +
        "index history. The registration requires the Cboe series and explains why; this development " +
        "runner has no Cboe feed. Stated here so it is never mistaken for the registered data source.",

        "PERMITTED: 'conditioning on the implied-minus-forecast spread does / does not sort subsequent " +
        "realized variance monotonically in this sample.' NOT PERMITTED: any statement about " +
        "significance, about profit, or about what a position would have earned.");
}
