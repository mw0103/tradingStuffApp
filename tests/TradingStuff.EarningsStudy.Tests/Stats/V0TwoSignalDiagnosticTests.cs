using TradingStuff.EarningsStudy.Stats;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The retired v0 two-signal rule as a POST-HOC diagnostic (v2 section 2), and the section 9.2
/// readout it feeds.
///
/// docs/LESSONS.md section 13: a selection rule's direction is invisible by reading, and the tell is
/// the removed set's OWN statistic against the population's. That measurement was made on a
/// synthetic null population before the freeze; here it is wired into the memo so the same
/// measurement lands on real data. The property under test is twofold — the flag is computed
/// correctly per event, and it removes nothing.
/// </summary>
public sealed class V0TwoSignalDiagnosticTests
{
    [Fact]
    public void The_retired_rule_needs_both_conjuncts_and_says_so_when_it_cannot_decide()
    {
        // Scale 0.02 -> threshold 3 x 0.02 = 0.06.
        Assert.True(V0TwoSignalRule.WouldQuarantine(0.09m, 0.01m, 0.02m));

        // Over the scale but NOT over the event move: the retired rule keeps it, the registered one cuts it.
        Assert.False(V0TwoSignalRule.WouldQuarantine(0.09m, 0.12m, 0.02m));

        // Over the event move but not the scale.
        Assert.False(V0TwoSignalRule.WouldQuarantine(0.05m, 0.01m, 0.02m));

        // Both comparisons are strict, so equality on either side keeps the event.
        Assert.False(V0TwoSignalRule.WouldQuarantine(0.06m, 0.01m, 0.02m));
        Assert.False(V0TwoSignalRule.WouldQuarantine(0.09m, 0.09m, 0.02m));

        // No usable scale falls back to the registered floor, exactly as gate 09 does.
        Assert.Equal(0.04m, TimingQa.FallbackFloor);
        Assert.True(V0TwoSignalRule.WouldQuarantine(0.05m, 0.01m, null));
        Assert.True(V0TwoSignalRule.WouldQuarantine(0.05m, 0.01m, 0m));
        Assert.False(V0TwoSignalRule.WouldQuarantine(0.04m, 0.01m, null));

        // A missing move is not a decision: null, never a defaulted "would not quarantine".
        Assert.Null(V0TwoSignalRule.WouldQuarantine(null, 0.01m, 0.02m));
        Assert.Null(V0TwoSignalRule.WouldQuarantine(0.09m, null, 0.02m));
    }

    /// <summary>
    /// A hand-built table: six events reach gate 09, one does not, and the flagged ones are the small
    /// -RF events the retired rule preferentially removed. The memo must print the count and the two
    /// means side by side.
    /// </summary>
    [Fact]
    public async Task The_column_and_the_section_9_2_counts_match_a_hand_built_table()
    {
        using var study = new StudyHarness();

        // The registered gate 09 keeps everything here, so every difference below is the retired
        // rule's doing and not a gate's.
        study.PriceQa = (timing, closes) => new TimingQaResult(false, null, PreEntry(timing.EventId), EventMove(timing.EventId));

        // Trailing scale 0.02 in the harness, so the retired rule's threshold is 0.06.
        //   flag-a   pre-entry 0.09 > event 0.01 and > 0.06  -> WOULD quarantine, ratio 0.25
        //   flag-b   pre-entry 0.08 > event 0.02 and > 0.06  -> WOULD quarantine, ratio 0.25
        //   keep-big pre-entry 0.09 > 0.06 but event 0.30    -> would NOT, ratio 1.50
        //   keep-a   pre-entry 0.01                          -> would NOT, ratio 1.00
        //   keep-b   pre-entry 0.01                          -> would NOT, ratio 1.00
        //   unknown  no pre-entry move recorded              -> NOT EVALUABLE, ratio 1.00
        // and one event that never reaches gate 09, which must carry no flag at all.
        study.AddEvent("flag-a", rf: 0.02m, im: 0.08m, week: "2024-W01");
        study.AddEvent("flag-b", rf: 0.02m, im: 0.08m, week: "2024-W02");
        study.AddEvent("keep-big", rf: 0.12m, im: 0.08m, week: "2024-W03");
        study.AddEvent("keep-a", rf: 0.08m, im: 0.08m, week: "2024-W04");
        study.AddEvent("keep-b", rf: 0.08m, im: 0.08m, week: "2024-W05");
        study.AddEvent("unknown", rf: 0.08m, im: 0.08m, week: "2024-W06");
        study.AddEvent("never-reaches-gate-09", closePreEntry: null, week: "2024-W07");

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        Assert.True(study.Row("flag-a").V0TwoSignalWouldQuarantine);
        Assert.True(study.Row("flag-b").V0TwoSignalWouldQuarantine);
        Assert.False(study.Row("keep-big").V0TwoSignalWouldQuarantine);
        Assert.False(study.Row("keep-a").V0TwoSignalWouldQuarantine);
        Assert.Null(study.Row("unknown").V0TwoSignalWouldQuarantine);
        Assert.Null(study.Row("never-reaches-gate-09").V0TwoSignalWouldQuarantine);

        // It removed nothing: every one of the six still stands, flagged or not.
        Assert.Equal("", study.Row("flag-a").StopGate);
        Assert.True(study.Row("flag-a").InPrimary);
        Assert.Equal(6, study.EventTable.Count(r => r.InPrimary));

        // The section 9.2 readout. Primary ratios are 0.25, 0.25, 1.5, 1, 1, 1 -> mean 0.833333;
        // the flagged pair's own mean is 0.25, which is the selection effect made visible.
        Assert.Contains("Over the 6 event(s) gate 09 decided on, it would have removed **2**; it could not be", study.Memo);
        Assert.Contains("evaluated for 1 of them", study.Memo);
        Assert.Contains("Over the primary sample's 6 event(s) with a computable RF/IM it flags **2**, whose", study.Memo);
        Assert.Contains("mean(RF/IM) is **0.250000** against the sample's **0.833333**.", study.Memo);
        Assert.Contains("Retired v0 two-signal rule, computed as a POST-HOC DIAGNOSTIC and applied to nothing (v2 section 2).", study.Memo);
        Assert.Contains("Inclusion in this study is decided by gate 09 and gate 09 alone; this line removed nothing.", study.Memo);

        // The rule's own words are quoted rather than paraphrased into the memo.
        Assert.Contains(V0TwoSignalRule.Describe(), study.Memo.Replace("\n  ", "\n").Replace("  Retired", "Retired"));
    }

    [Fact]
    public async Task A_run_the_retired_rule_would_have_left_alone_says_so_rather_than_printing_a_missing_mean()
    {
        using var study = new StudyHarness();
        study.PriceQa = (_, _) => new TimingQaResult(false, null, 0.001m, 0.02m);
        study.AddEvent("a", rf: 0.04m, im: 0.08m).AddEvent("b", rf: 0.04m, im: 0.08m);

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        Assert.All(study.EventTable, r => Assert.False(r.V0TwoSignalWouldQuarantine));
        Assert.Contains("it would have removed **0**", study.Memo);
        Assert.Contains("it flags none, so there is no", study.Memo);
        Assert.Contains("removed-set mean to compare against the sample's 0.500000.", study.Memo);
    }

    private static decimal? PreEntry(string eventId) => eventId switch
    {
        "flag-a" => 0.09m,
        "flag-b" => 0.08m,
        "keep-big" => 0.09m,
        "unknown" => null,
        _ => 0.01m
    };

    private static decimal? EventMove(string eventId) => eventId switch
    {
        "flag-a" => 0.01m,
        "flag-b" => 0.02m,
        "keep-big" => 0.30m,
        "unknown" => 0.02m,
        _ => 0.02m
    };
}
