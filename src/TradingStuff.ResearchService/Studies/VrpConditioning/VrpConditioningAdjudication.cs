using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// The companion study's inference: the same Diebold-Mariano comparison computed on the overlapping
/// daily series and on a non-overlapping subsample, plus a block-bootstrap interval on the mean loss
/// advantage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is a gate.</b> The parent study's H1 is a five-condition conjunction with a
/// registered materiality margin; this companion has no such gate, produces no verdict, and is
/// permitted "bootstrap CIs only, no significance claims". The p-values below exist for exactly one
/// purpose: to make the gap between the overlapping and non-overlapping samplings visible. A reader
/// who quotes one of them as a result has been told, in the response body, not to.
/// </para>
/// <para>
/// <b>Why tau is zero here.</b> <see cref="DieboldMariano.CompareWithMargin"/> takes a materiality
/// margin because the parent study registered one. Reusing a non-zero tau here would imply this
/// companion has a registered materiality threshold, which it does not. Tau = 0 is the plain
/// comparison, and it is labelled as descriptive.
/// </para>
/// </remarks>
public static class VrpConditioningAdjudication
{
    public const string Overlapping = "overlapping-daily";
    public const string NonOverlapping = "non-overlapping-subsample";

    private const string OverlappingNote =
        "All daily decision dates. Adjacent observations share up to 20 of their 21 label days, so " +
        "this series is far smaller than its length suggests even after the HAC correction. Reported " +
        "because it uses all the data, NOT because it is the trustworthy number.";

    private const string NonOverlappingNote =
        "Every 21st decision date within each fold's test block (stride = the label horizon, offset 0, " +
        "fixed in advance), so no two observations share a label day. This is the honest inference. It " +
        "is also much less powerful, and a disagreement with the overlapping row means the overlapping " +
        "row was over-precise, not that the effect vanished.";

    /// <summary>
    /// Compares every arm against the gate, twice. Returns an empty list when there is nothing to
    /// compare — an absent comparison is honest; a fabricated one is not.
    /// </summary>
    public static List<VrpConditioningDmComparison> Compare(
        IReadOnlyList<VrpConditioningFoldResult> foldResults, string gateArm = VrpConditioningArms.Gate)
    {
        ArgumentNullException.ThrowIfNull(foldResults);

        var ordered = foldResults.SelectMany(f => f.DailyResults).OrderBy(d => d.Date).ToList();
        var thinned = NonOverlappingSubsample(foldResults);

        if (ordered.Count < 2) return [];

        var comparisons = new List<VrpConditioningDmComparison>();

        foreach (var arm in VrpConditioningArms.All)
        {
            if (arm == gateArm) continue;

            var overlapping = Run(ordered, arm, gateArm, VrpConditioningHorizon.OverlappingHacLag, Overlapping, honest: false, OverlappingNote);

            var nonOverlapping = thinned.Count >= 2
                ? Run(thinned, arm, gateArm, VrpConditioningHorizon.NonOverlappingHacLag, NonOverlapping, honest: true, NonOverlappingNote)
                : new VrpConditioningDm(
                    NonOverlapping, Honest: true,
                    "Fewer than two non-overlapping windows exist in this run, so no non-overlapping " +
                    "comparison was made. Nothing is being asserted about it.",
                    double.NaN, double.NaN, double.NaN, double.NaN, thinned.Count,
                    VrpConditioningHorizon.NonOverlappingHacLag, Degenerate: true);

            var advantages = new double[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
                advantages[i] = ordered[i].Qlike[gateArm] - ordered[i].Qlike[arm];

            var interval = BootstrapMeanInterval(advantages);

            var signsDisagree =
                !double.IsNaN(nonOverlapping.MeanLossAdvantage) &&
                Math.Sign(overlapping.MeanLossAdvantage) != Math.Sign(nonOverlapping.MeanLossAdvantage);

            var significanceDisagrees =
                !double.IsNaN(nonOverlapping.PValueOneSided) &&
                overlapping.PValueOneSided < 0.05 != nonOverlapping.PValueOneSided < 0.05;

            comparisons.Add(new VrpConditioningDmComparison(
                arm, gateArm, overlapping, nonOverlapping,
                signsDisagree || significanceDisagrees, interval));
        }

        return comparisons;
    }

    /// <summary>
    /// Every 21st scored day WITHIN each fold's test block, concatenated in fold order.
    /// </summary>
    /// <remarks>
    /// Striding within folds rather than across the pooled series matters: the pooled series has
    /// seams where consecutive rows are two years apart, and a stride laid over the seam would keep a
    /// pair that is non-overlapping for the wrong reason while silently changing the phase of the
    /// stride in the following fold. Offset 0 is fixed in advance; choosing among the 21 possible
    /// offsets after seeing which one is friendlier is exactly the discretion a pre-registration
    /// removes.
    /// </remarks>
    internal static List<VrpConditioningDailyResult> NonOverlappingSubsample(
        IReadOnlyList<VrpConditioningFoldResult> foldResults)
    {
        var thinned = new List<VrpConditioningDailyResult>();

        foreach (var fold in foldResults.OrderBy(f => f.TestFrom))
        {
            var days = fold.DailyResults.OrderBy(d => d.Date).ToList();
            for (var i = 0; i < days.Count; i += VrpConditioningHorizon.LabelTradingDays)
            {
                thinned.Add(days[i]);
            }
        }

        return thinned;
    }

    private static VrpConditioningDm Run(
        IReadOnlyList<VrpConditioningDailyResult> days, string arm, string gateArm,
        int hacLag, string sampling, bool honest, string note)
    {
        var armLosses = days.Select(d => d.Qlike[arm]).ToList();
        var gateLosses = days.Select(d => d.Qlike[gateArm]).ToList();

        var result = DieboldMariano.CompareWithMargin(armLosses, gateLosses, tau: 0.0, hacLag);

        return new VrpConditioningDm(
            sampling, honest, note,
            result.MeanLossAdvantage,
            result.Statistic,
            result.OneSidedPValue,
            result.LongRunVariance,
            result.Observations,
            result.HacLag,
            result.Degenerate);
    }

    /// <summary>
    /// Two-sided block-bootstrap percentile interval on the mean loss advantage, at the block length
    /// this study's overlap requires.
    /// </summary>
    private static VrpConditioningInterval BootstrapMeanInterval(double[] advantages)
    {
        if (advantages.Length < 2) return new VrpConditioningInterval(double.NaN, double.NaN, 0.10, 0);

        var draws = new List<double>(StationaryBlockBootstrap.RegisteredResamples);

        StationaryBlockBootstrap.ForEachResample(
            advantages.Length,
            StationaryBlockBootstrap.RegisteredResamples,
            VrpConditioningHorizon.BootstrapMeanBlockLength,
            VrpConditioningHorizon.BootstrapSeed,
            indices =>
            {
                double sum = 0.0;
                for (var t = 0; t < indices.Length; t++) sum += advantages[indices[t]];
                draws.Add(sum / indices.Length);
            });

        return VrpConditioningQuintiles.Interval(draws);
    }
}
