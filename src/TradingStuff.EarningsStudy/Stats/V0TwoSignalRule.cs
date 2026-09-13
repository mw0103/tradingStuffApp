using System.Globalization;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// The RETIRED v0-era two-signal price QA, evaluated as a POST-HOC DIAGNOSTIC and nothing else
/// (<c>docs/research/c1-preregistration-v2.md</c> section 2: "may be computed as a POST-HOC
/// diagnostic only; it never affects inclusion").
///
/// <b>The retired rule.</b> Quarantine when the pre-entry move exceeds BOTH the event move AND the
/// trailing scale — <c>TimingQa.ScaleMultiple</c> x the 20-day median absolute daily return when
/// that is positive, and <c>TimingQa.FallbackFloor</c> otherwise. The second conjunct is the
/// registered gate-09 rule; the first is the one v2 removed.
///
/// <b>Why it is still computed.</b> The event move is RF, the numerator of the study's primary
/// statistic, so the first conjunct conditions inclusion on the outcome. On a clean synthetic
/// population the removed set carried P(RF&lt;IM) 0.946 against a population 0.620 — the strongest
/// PASS evidence in the sample — and at the verdict boundary it flipped PASS to FAIL
/// (docs/LESSONS.md §13). That measurement was made on a synthetic null population. This diagnostic
/// is the same measurement ON REAL DATA: the memo prints how many events the retired rule WOULD
/// have removed and what the mean(RF/IM) of that would-have-been-removed set is, beside the
/// sample's own. A removed set whose mean sits far from the sample's is the same tell, now with the
/// real distribution behind it.
///
/// <b>It cannot affect inclusion.</b> Nothing here is wired to a gate; the only consumers are the
/// event table's <c>v0_two_signal_would_quarantine</c> column and section 9.2 of the memo.
/// </summary>
public static class V0TwoSignalRule
{
    /// <summary>
    /// What the retired rule would have decided for one event. Null when it cannot be evaluated —
    /// the event never reached gate 09, or one of the two moves is missing — because an unevaluable
    /// rule has no decision, and rendering that as "would not quarantine" would understate the
    /// diagnostic exactly where the data is thinnest.
    /// </summary>
    public static bool? WouldQuarantine(decimal? preEntryMove, decimal? eventMove, decimal? medianAbsReturn20)
    {
        if (preEntryMove is not { } preEntry || eventMove is not { } evented) return null;

        var threshold = medianAbsReturn20 is { } median && median > 0m
            ? TimingQa.ScaleMultiple * median
            : TimingQa.FallbackFloor;

        return preEntry > evented && preEntry > threshold;
    }

    /// <summary>
    /// The rule in one sentence, for the memo. The multiple and the floor are read from
    /// <see cref="TimingQa"/> rather than typed here, so the sentence cannot drift from the rule.
    /// </summary>
    public static string Describe() =>
        "Retired v0 two-signal rule (POST-HOC DIAGNOSTIC, never applied): quarantine when the absolute pre-entry " +
        "move exceeds BOTH the absolute event move AND the trailing scale (" +
        TimingQa.ScaleMultiple.ToString("0.##", CultureInfo.InvariantCulture) +
        " x the 20-day median absolute daily return, or the " +
        (TimingQa.FallbackFloor * 100m).ToString("0.00", CultureInfo.InvariantCulture) +
        "% floor when that scale is unavailable). The first conjunct reads the event move, which is RF itself, so " +
        "it conditions inclusion on the study's own outcome variable; v2 section 2 retired it for that reason and " +
        "gate 09 applies the pre-entry-only rule instead.";
}

/// <summary>
/// The post-hoc readout: what the retired rule would have done to this study, measured on the events
/// gate 09 actually decided and on the primary sample the verdict is measured on.
/// </summary>
/// <param name="Considered">Events that reached gate 09 — the set the retired rule would have decided on had it been the gate.</param>
/// <param name="WouldQuarantine">Of those, how many it would have removed.</param>
/// <param name="NotEvaluable">Of those, how many it could not decide (a missing pre-entry or event move). A column, not a silent drop.</param>
/// <param name="PrimaryMembers">Primary-sample events with a computable RF/IM — the denominator of <paramref name="PrimaryMean"/>.</param>
/// <param name="PrimaryFlagged">Of those, how many the retired rule would have removed.</param>
/// <param name="PrimaryFlaggedMean">mean(RF/IM) over the would-have-been-removed set. Null when it is empty.</param>
/// <param name="PrimaryMean">mean(RF/IM) over the whole primary sample, repeated here so the two are read side by side.</param>
public sealed record V0TwoSignalDiagnostic(
    int Considered,
    int WouldQuarantine,
    int NotEvaluable,
    int PrimaryMembers,
    int PrimaryFlagged,
    decimal? PrimaryFlaggedMean,
    decimal? PrimaryMean);
