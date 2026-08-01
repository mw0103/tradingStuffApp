using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Trials;

/// <summary>
/// The arithmetic of the pre-registered trial protocol: how many variants a study may run, and
/// what significance threshold applies once N of them have been registered.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of SQL deliberately. Both rules are properties of a study's state at a moment rather
/// than of a row, and a CHECK constraint encoding a workflow has to be migrated every time the
/// protocol is clarified. What the database guarantees is narrower and more useful: that the count
/// these rules operate on is honest, because the rows were written before their own results
/// existed and cannot be edited afterwards.
/// </para>
/// <para>
/// Pure functions, so the rules are pinned by ordinary unit tests rather than only by a
/// database-backed suite that a machine without Postgres will skip.
/// </para>
/// </remarks>
public static class TrialProtocol
{
    /// <summary>
    /// Variants a study may register before the holdout opens. Exhausting it is a negative
    /// result, not a prompt to raise the cap.
    /// </summary>
    public const int VariantCap = 10;

    /// <summary>The undeflated gate, before correction for multiple registered variants.</summary>
    public const double BasePThreshold = 0.05;

    /// <summary>
    /// Registered variants above which the registration additionally requires an SPA test of the
    /// family against HAR, because Bonferroni alone gets conservative and the family-wise question
    /// stops being answerable one variant at a time.
    /// </summary>
    public const int SuperiorPredictiveAbilityTrigger = 5;

    /// <summary>
    /// The threshold a gate is judged against once <paramref name="registeredVariants"/> have been
    /// registered: 0.05/N.
    /// </summary>
    /// <remarks>
    /// Bonferroni over the registered count, not over the variants that happened to produce a
    /// result. A variant declared and then abandoned still consumed a look at the data, which is
    /// precisely what the correction is for — and is why the registry counts declarations rather
    /// than outcomes.
    /// </remarks>
    public static double DeflatedPThreshold(int registeredVariants)
    {
        if (registeredVariants < 1)
            throw new ArgumentOutOfRangeException(nameof(registeredVariants),
                "A threshold applies to at least one registered variant.");

        return BasePThreshold / registeredVariants;
    }

    /// <summary>Whether an SPA test of the family against HAR is additionally required.</summary>
    public static bool RequiresSuperiorPredictiveAbilityTest(int registeredVariants) =>
        registeredVariants > SuperiorPredictiveAbilityTrigger;

    /// <summary>Whether another variant may be registered for a study.</summary>
    public static bool CanRegisterAnother(int registeredVariants) => registeredVariants < VariantCap;

    /// <summary>
    /// The registration's H1 gate, applied to one variant's measured outcome.
    /// </summary>
    /// <remarks>
    /// Every clause is required, and each rules out a different way of being wrong: the gain
    /// threshold rules out a real but negligible effect, the p-value rules out noise, the fold
    /// count rules out a result carried by one period, and the concentration check rules out a
    /// result carried by one year. The registration states them conjunctively and so does this.
    /// </remarks>
    public static string Judge(
        double pooledQlikeGain,
        double dieboldMarianoPValue,
        int foldsImproved,
        int foldsTotal,
        double largestYearShareOfGain,
        double pThresholdApplied)
    {
        if (foldsTotal < 1) throw new ArgumentOutOfRangeException(nameof(foldsTotal));

        // Falsification: any one of these makes the variant negative regardless of the others.
        var significant = dieboldMarianoPValue < pThresholdApplied;
        var enoughFolds = foldsImproved >= 2;
        var notOneYear = largestYearShareOfGain <= 0.50;

        if (pooledQlikeGain < 0.01 || !significant || !enoughFolds || !notOneYear)
        {
            return TrialVerdicts.Negative;
        }

        // Between 1% and 2% the registration treats the result as not clearing H1 at all.
        if (pooledQlikeGain < 0.02) return TrialVerdicts.Negative;

        // Statistically real but, by the registration's own account, likely unactionable.
        return pooledQlikeGain < 0.05
            ? TrialVerdicts.InsufficientMagnitude
            : TrialVerdicts.Validated;
    }
}
