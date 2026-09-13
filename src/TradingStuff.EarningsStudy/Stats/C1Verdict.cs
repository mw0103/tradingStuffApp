using System.Globalization;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// The PASS/FAIL of claim C1 under the v2 registration, and the sentence the memo prints. A FAIL
/// stops the program per spec v0.3, so the rule is implemented literally and its boundaries are
/// pinned by tests rather than left to reading.
///
/// v2 replaced the v0 criterion — median(RF/IM) &lt; 1 with the interval for P(RF &lt; IM) excluding
/// 0.5 — because an adversarial review proved it is satisfied by a fairly priced market: absolute
/// moves are right-skewed, so median(RF/IM) &lt; 1 and P(RF &lt; IM) &gt; 0.5 hold with no premium at
/// all. Those two measured the SHAPE of the ratio distribution. A premium is a statement about
/// means, and under fair per-event pricing (IM_i = E[RF_i]) the tower property puts mean(RF/IM) at
/// exactly 1, which is the null this criterion now tests against.
/// </summary>
/// <param name="Computed">False when the statistic or the interval does not exist (an empty sample, or a run that did not apply a registered gate). A verdict is never inferred from a missing number.</param>
/// <param name="Side">above | below | contains — where the interval sits relative to 1.</param>
public sealed record C1Verdict(
    bool Computed,
    bool Pass,
    bool MeanBelowOne,
    bool IntervalExcludesOne,
    string Side,
    decimal? Mean,
    BootstrapInterval? MeanInterval,
    string Line)
{
    /// <summary>The registered threshold: mean(RF/IM) = 1 is the fair-pricing null.</summary>
    public const decimal NullRatio = 1m;

    /// <summary>
    /// PASS iff mean(RF/IM) &lt; 1 AND the week-clustered interval for mean(RF/IM) excludes 1.
    ///
    /// Both boundaries are taken as written. "mean(RF/IM) &lt; 1" is strict, so a mean of exactly 1
    /// FAILS. "excludes 1" means 1 lies outside the CLOSED interval, so an endpoint of exactly 1 does
    /// NOT exclude it and FAILS. Neither boundary can be reached by accident in real data; they are
    /// fixed here so that if one ever is, the answer was decided before the data was seen.
    /// </summary>
    public static C1Verdict Decide(decimal? mean, BootstrapInterval? meanInterval, string? reasonNotComputed = null)
    {
        if (mean is null || meanInterval is null)
        {
            var why = reasonNotComputed ?? (mean is null
                ? "the primary sample has no computable mean"
                : "the primary sample has no bootstrap interval");
            return new C1Verdict(false, false, false, false, "unknown", mean, meanInterval,
                $"**VERDICT: NOT COMPUTED** — {why}.");
        }

        var meanBelowOne = mean.Value < NullRatio;
        var below = meanInterval.Upper < NullRatio;
        var above = meanInterval.Lower > NullRatio;
        var excludes = below || above;
        var side = above ? "above" : below ? "below" : "contains";
        var pass = meanBelowOne && excludes;

        var verdict = pass ? "**VERDICT: PASS**" : "**VERDICT: FAIL**";
        var meanClause = $"mean(RF/IM) = {Format(mean.Value, NullRatio)} {(meanBelowOne ? "<" : ">=")} 1";
        var bounds = $"[{Format(meanInterval.Lower, NullRatio)}, {Format(meanInterval.Upper, NullRatio)}]";
        var intervalClause = excludes
            ? $"the {Percent(meanInterval.Level)} week-clustered interval for mean(RF/IM) = {bounds} excludes 1 ({side} it)"
            : $"the {Percent(meanInterval.Level)} week-clustered interval for mean(RF/IM) = {bounds} contains 1";

        return new C1Verdict(true, pass, meanBelowOne, excludes, side, mean, meanInterval,
            $"{verdict} — {meanClause} and {intervalClause}.");
    }

    /// <summary>
    /// A compared value, formatted for the verdict sentence. Every comparison in this study is made
    /// on the unrounded <c>decimal</c>, but the sentence is the only evidence the reader gets, and at
    /// six places a value a hair off a boundary renders AS that boundary: 0.9999996 prints
    /// "1.000000", so the line reads "mean(RF/IM) = 1.000000 &lt; 1" — a correct verdict with printed
    /// evidence that contradicts it. When the rendering collides with the boundary the value is being
    /// compared against, and the value is not actually that boundary, the unrounded decimal is
    /// printed beside it. A value that IS the boundary needs no annotation: there the rendering is
    /// exact and the clause already says so (it prints "&gt;= 1", or "contains 1"). Under v2 the
    /// boundary is 1 for the mean AND for both interval endpoints, which is why one helper covers
    /// all three.
    /// </summary>
    private static string Format(decimal value, decimal comparedAgainst)
    {
        var rendered = Format(value);
        return rendered == Format(comparedAgainst) && value != comparedAgainst
            ? $"{value.ToString(CultureInfo.InvariantCulture)} (shown unrounded: F6 would print {rendered})"
            : rendered;
    }

    private static string Format(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Percent(decimal level) => (level * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// The economic cross-check of v2 section 1: the sign of the ATM straddle hold-through return at
/// mids against the verdict.
///
/// Convention, fixed here before any real data: the hold-through return is measured on the LONG
/// straddle, (exit mid - entry mid) / entry mid. A premium means the option was dear, so the long
/// side loses on average — a NEGATIVE mean hold-through return agrees with PASS, and a non-negative
/// one agrees with FAIL. Exactly zero is non-negative and therefore agrees with FAIL, for the same
/// reason mean(RF/IM) = 1 fails: no premium is not a premium.
/// </summary>
/// <param name="Agreement">agrees | disagrees | not computable.</param>
public sealed record StraddleCrossCheck(decimal? Mean, bool VerdictComputed, bool Pass, string Agreement)
{
    public const string Agrees = "agrees";
    public const string Disagrees = "disagrees";
    public const string NotComputable = "not computable";

    public static StraddleCrossCheck Evaluate(C1Verdict verdict, decimal? straddleMean)
    {
        if (!verdict.Computed || straddleMean is not { } mean)
        {
            return new StraddleCrossCheck(straddleMean, verdict.Computed, verdict.Pass, NotComputable);
        }

        var signAgreesWithPass = mean < 0m;
        return new StraddleCrossCheck(mean, true, verdict.Pass,
            signAgreesWithPass == verdict.Pass ? Agrees : Disagrees);
    }
}
