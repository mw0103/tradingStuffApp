using System.Globalization;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// The PASS/FAIL of claim C1, and the sentence the memo prints. A FAIL stops the program per spec
/// v0.3, so the rule is implemented literally and its boundaries are pinned by tests rather than
/// left to reading.
/// </summary>
/// <param name="Computed">False when the statistic or the interval does not exist (an empty sample, or a run that did not apply a registered gate). A verdict is never inferred from a missing number.</param>
/// <param name="Side">above | below | contains — where the interval sits relative to 0.5.</param>
public sealed record C1Verdict(
    bool Computed,
    bool Pass,
    bool MedianBelowOne,
    bool IntervalExcludesHalf,
    string Side,
    decimal? Median,
    BootstrapInterval? ProportionInterval,
    string Line)
{
    /// <summary>The registered threshold: P(RF &lt; IM) = 0.5 is the no-premium null.</summary>
    public const decimal NullProportion = 0.5m;

    /// <summary>
    /// PASS iff median(RF/IM) &lt; 1 AND the week-clustered interval for P(RF &lt; IM) excludes 0.5.
    ///
    /// Both boundaries are taken as written. "median(RF/IM) &lt; 1" is strict, so a median of exactly
    /// 1 FAILS. "excludes 0.5" means 0.5 lies outside the CLOSED interval, so an endpoint of exactly
    /// 0.5 does NOT exclude it and FAILS. Neither boundary can be reached by accident in real data;
    /// they are fixed here so that if one ever is, the answer was decided before the data was seen.
    /// </summary>
    public static C1Verdict Decide(decimal? median, BootstrapInterval? proportionInterval, string? reasonNotComputed = null)
    {
        if (median is null || proportionInterval is null)
        {
            var why = reasonNotComputed ?? (median is null
                ? "the primary sample has no computable median"
                : "the primary sample has no bootstrap interval");
            return new C1Verdict(false, false, false, false, "unknown", median, proportionInterval,
                $"**VERDICT: NOT COMPUTED** — {why}.");
        }

        var medianBelowOne = median.Value < 1m;
        var below = proportionInterval.Upper < NullProportion;
        var above = proportionInterval.Lower > NullProportion;
        var excludes = below || above;
        var side = above ? "above" : below ? "below" : "contains";
        var pass = medianBelowOne && excludes;

        var verdict = pass ? "**VERDICT: PASS**" : "**VERDICT: FAIL**";
        var medianClause = $"median(RF/IM) = {Format(median.Value)} {(medianBelowOne ? "<" : ">=")} 1";
        var intervalClause = excludes
            ? $"the {Percent(proportionInterval.Level)} week-clustered interval for P(RF < IM) = [{Format(proportionInterval.Lower)}, {Format(proportionInterval.Upper)}] excludes 0.5 ({side} it)"
            : $"the {Percent(proportionInterval.Level)} week-clustered interval for P(RF < IM) = [{Format(proportionInterval.Lower)}, {Format(proportionInterval.Upper)}] contains 0.5";

        return new C1Verdict(true, pass, medianBelowOne, excludes, side, median, proportionInterval,
            $"{verdict} — {medianClause} and {intervalClause}.");
    }

    private static string Format(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Percent(decimal level) => (level * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
