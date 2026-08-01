namespace TradingStuff.ResearchContracts;

/// <summary>
/// A variant declared before it is executed, as the study pre-registration requires.
/// </summary>
/// <param name="Study">
/// Which pre-registration this variant runs under. Separate studies count their variants
/// independently, so the cap and the p-threshold deflation are per-study, not global.
/// </param>
/// <param name="VariantOrdinal">Position within that study's cap. Assigned by the registry.</param>
/// <param name="FeatureSetHash">
/// Identifies the feature set exactly. A hash rather than a list so two variants can be compared
/// at a glance, and so a silent change to a feature definition shows up as a different variant
/// rather than as the same one behaving differently.
/// </param>
/// <param name="GitSha">
/// The commit executed from. Without it the rest describes a configuration but not the code that
/// interpreted it, and a change in the estimator is indistinguishable from a change in the variant.
/// </param>
/// <param name="Rationale">Why this variant exists, for the human reconstructing the sequence later.</param>
public sealed record RegisteredTrial(
    long TrialId,
    string Study,
    int VariantOrdinal,
    DateTimeOffset RegisteredAt,
    string FeatureSetHash,
    string ModelFamily,
    string Hyperparameters,
    string FoldConfig,
    long Seed,
    string GitSha,
    string Rationale);

/// <summary>A declaration not yet assigned an ordinal or written.</summary>
public sealed record TrialDeclaration(
    string Study,
    string FeatureSetHash,
    string ModelFamily,
    string Hyperparameters,
    string FoldConfig,
    long Seed,
    string GitSha,
    string Rationale);

/// <summary>
/// What a registered variant produced. Written after the run, never merged into the declaration.
/// </summary>
/// <param name="PThresholdApplied">
/// The deflated threshold this outcome was judged against. Stored rather than recomputed: N grows
/// as later variants register, so recomputing at read time would restate what an earlier decision
/// was actually made against.
/// </param>
public sealed record TrialOutcome(
    long TrialId,
    DateTimeOffset RecordedAt,
    double PooledQlike,
    double PooledQlikeGain,
    double ReportedLogMse,
    double DieboldMarianoStatistic,
    double DieboldMarianoPValue,
    double PThresholdApplied,
    int FoldsImproved,
    int FoldsTotal,
    double LargestYearShare,
    string Verdict);

/// <summary>The verdicts the registration's falsification rules produce.</summary>
public static class TrialVerdicts
{
    /// <summary>Cleared the H1 gate on every registered criterion.</summary>
    public const string Validated = "validated";

    /// <summary>
    /// Statistically real but below the registration's economic bar — a 2–5% pooled QLIKE gain.
    /// Used only to tighten the variance-gap study's uncertainty band, never traded.
    /// </summary>
    public const string InsufficientMagnitude = "insufficient-economic-magnitude";

    /// <summary>Failed a gate. Frozen and recorded; the registration forbids retrying it as a new variant.</summary>
    public const string Negative = "negative";
}
