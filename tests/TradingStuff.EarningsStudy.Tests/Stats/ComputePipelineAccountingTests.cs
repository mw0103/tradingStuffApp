using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Stats;
using TradingStuff.EarningsStudy.Timing;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// Where <c>compute</c>'s idea of the study's population has to match the timing step's, and what it
/// must say when the two disagree.
///
/// The defect these exist for: <c>compute</c> selected events on <c>kept_after_dedup</c> alone while
/// the timing step selects on <c>in_window AND kept_after_dedup</c>. An out-of-window row therefore
/// arrived here with no <c>event_timing.csv</c> row, and the verb reported its ABSENCE as a gate-07
/// quarantine — a missing row rendering as a decision somebody made, with the memo header claiming
/// a removal count the gate-07 tally did not support and no warning anywhere
/// (docs/STATE.md class (c), docs/LESSONS.md §3).
/// </summary>
public sealed class ComputePipelineAccountingTests
{
    /// <summary>
    /// The reviewer's chain, executed: the REAL timing step writes <c>event_timing.csv</c> and the
    /// gate-07 tally, and the REAL compute step reads them. Nothing here restates either filter — the
    /// assertion is that the two steps agree about the same set, which is the only property that
    /// makes the exclusion table's arithmetic mean anything.
    /// </summary>
    [Fact]
    public async Task An_out_of_window_row_is_the_events_verbs_business_and_never_reads_as_a_gate_07_removal()
    {
        using var study = new StudyHarness();
        study.GateCounts.RemoveAll(c => c.Gate == Gates.TimingClassified);   // the real timing step appends its own
        study.AddEvent("in-a", withTiming: false)
             .AddEvent("in-b", withTiming: false)
             .AddEvent("out-of-window", inWindow: false, withTiming: false);
        study.Write();

        Assert.Equal(0, await RunRealTimingStepAsync(study));

        // The timing step carried two events and wrote two rows; the out-of-window one is not its business.
        var timings = CsvFile.Read<EventTimingRow>(study.Paths.EventTiming);
        Assert.Equal(["in-a", "in-b"], timings.Select(t => t.EventId).Order(StringComparer.Ordinal));

        // The compute step directly, NOT through the harness's RunAsync: re-writing the tables would
        // overwrite the event_timing.csv the timing step just produced, which is the artifact under test.
        var compute = new ComputeStep(study.PriceQa, study.SharesAsOf, study.DescribePriceQa);
        Assert.Equal(0, await compute.RunAsync(study.Context, ["--reps", "20"], CancellationToken.None));

        var gate07 = study.WrittenGateCounts.Single(c => c.Gate == Gates.TimingClassified);
        Assert.Equal(2, gate07.Considered);
        Assert.Equal(0, gate07.Removed);
        Assert.Equal(2, gate07.Remaining);

        // The header's population is gate 07's population, and the discontinuity count is a stated zero.
        Assert.Contains("Events in window and kept after dedup: 2", study.Memo);
        Assert.Contains("Entering `compute`: 2. Quarantined by the timing step: 0", study.Memo);
        Assert.Contains("With no row at all in `event_timing.csv`: 0.", study.Memo);

        // The out-of-window row leaves no event-table row and invents no quarantine.
        Assert.DoesNotContain(study.EventTable, r => r.EventId == "out-of-window");
        Assert.DoesNotContain("pipeline discontinuity, not a gate-07 quarantine:", study.Memo);
        Assert.DoesNotContain("but 2 entered compute", study.Memo);
    }

    /// <summary>
    /// The genuine discontinuity — in window, kept, and no timing row — is counted apart from the
    /// gate-07 quarantines and warned about. It must never be added to them: gate 07's tally is a
    /// record of decisions, and nobody decided this.
    /// </summary>
    [Fact]
    public async Task An_event_with_no_timing_row_is_warned_about_and_counted_apart_from_the_gate_07_quarantines()
    {
        using var study = new StudyHarness();
        study.GateCounts.RemoveAll(c => c.Gate == Gates.TimingClassified);
        study.GateCounts.Add(new GateCountRow("timing", 7, Gates.TimingClassified, 3, 1, 2, null));

        study.AddEvent("good")
             .AddEvent("quarantined-by-timing", quarantinedAtTiming: true)
             .AddEvent("no-timing-row", withTiming: false);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("Events in window and kept after dedup: 3", study.Memo);
        Assert.Contains("Entering `compute`: 1. Quarantined by the timing step: 1", study.Memo);
        Assert.Contains("With no row at all in `event_timing.csv`: 1.", study.Memo);

        // The discontinuity is named as one, and the ledger disagreement is named separately.
        Assert.Contains("1 event(s) in window and kept after dedup have NO row in event_timing.csv.", study.Memo);
        Assert.Contains("event_timing.csv is not the table this events.csv implies", study.Memo);
        Assert.Contains("gate 07 records 2 event(s) remaining, but 1 entered compute.", study.Memo);
        Assert.Contains("WARNING 1 event(s) in window and kept after dedup have NO row", study.Output.ToString());

        // Gate 07's own tally is untouched: this verb counts nothing into another step's row.
        var gate07 = study.WrittenGateCounts.Single(c => c.Gate == Gates.TimingClassified);
        Assert.Equal(1, gate07.Removed);

        // And the event is still a row with a reason, not an absence.
        Assert.Equal(Gates.TimingClassified, study.Row("no-timing-row").StopGate);
        Assert.Contains("no row in event_timing.csv", study.Row("no-timing-row").Note);
    }

    /// <summary>
    /// The reconciliation warning fires on the counts alone, with no discontinuity present: a gate-07
    /// row left over from a run against a different <c>events.csv</c> is the same defect wearing
    /// different clothes, and silently preferring either number is what the warning exists to stop.
    /// </summary>
    [Fact]
    public async Task A_stale_gate_07_row_is_reported_as_a_disagreement_rather_than_reconciled_away()
    {
        using var study = new StudyHarness();
        study.GateCounts.RemoveAll(c => c.Gate == Gates.TimingClassified);
        study.GateCounts.Add(new GateCountRow("timing", 7, Gates.TimingClassified, 96, 4, 92, "a run against an older events.csv"));
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("gate 07 records 92 event(s) remaining, but 2 entered compute.", study.Memo);
        Assert.DoesNotContain("have NO row in event_timing.csv", study.Memo);
    }

    /// <summary>A run in which everything lines up says so and warns about nothing.</summary>
    [Fact]
    public async Task A_reconciling_run_emits_neither_warning()
    {
        using var study = new StudyHarness();
        study.GateCounts.RemoveAll(c => c.Gate == Gates.TimingClassified);
        study.GateCounts.Add(new GateCountRow("timing", 7, Gates.TimingClassified, 3, 1, 2, null));
        study.AddEvent("a").AddEvent("b").AddEvent("q", quarantinedAtTiming: true);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.DoesNotContain("have NO row in event_timing.csv", study.Memo);
        Assert.DoesNotContain("event(s) remaining, but", study.Memo);
        Assert.DoesNotContain("WARNING", study.Output.ToString());
    }

    private static async Task<int> RunRealTimingStepAsync(StudyHarness study)
    {
        var context = new StudyContext(
            new StudyPaths(study.Directory), new SessionClock(), study.Output, TimeProvider.System);
        return await new TimingStep().RunAsync(context, [], CancellationToken.None);
    }
}
