using TradingStuff.EarningsStudy;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The v2 deadline fallback through the compute verb: what the memo says it is, what gate 07b
/// removes, and what the exit code is when there is nothing to deliver.
///
/// The property these exist for is the one docs/LESSONS.md section 3 keeps catching: a partially
/// fetched window must not read as a complete one. A subset is a claim about what WAS fetched, so
/// the absent rows have to produce a visible removal — a gate row, a labelled memo, an event-table
/// stop gate — rather than a slightly smaller sample nobody notices.
/// </summary>
public sealed class ComputeCoverageTests
{
    private static DateOnly In(int year, int quarter) => new(year, ((quarter - 1) * 3) + 2, 15);

    [Fact]
    public async Task A_fully_fetched_window_is_final_and_gate_07b_removes_nothing()
    {
        using var study = new StudyHarness();
        study.AddEvent("a", printDate: In(2022, 1), week: "2022-W07");
        study.AddEvent("b", printDate: In(2024, 3), week: "2024-W33");
        study.AddEvent("c", printDate: In(2025, 4), week: "2025-W46");

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        Assert.Equal("# C1 — implied vs realized earnings moves (v2)", study.Memo.Split('\n')[0]);
        Assert.Contains("**Deliverable subset: 2022Q1..2025Q4 — the full registered window (16 quarters). This memo is FINAL.**", study.Memo);
        Assert.Contains("Measured on the PRIMARY (tradable) sample over the full registered window.", study.Memo);

        var gate = study.WrittenGateCounts.Single(c => c.Gate == Gates.QuarterCoverage);
        Assert.Equal(3, gate.Considered);
        Assert.Equal(0, gate.Removed);
        Assert.Equal(3, gate.Remaining);
        Assert.All(study.EventTable, r => Assert.NotEqual(Gates.QuarterCoverage, r.StopGate));
        Assert.Contains("| events in sample | 3 | |", study.Memo);
    }

    [Fact]
    public async Task A_partly_fetched_window_delivers_the_recent_run_and_labels_the_memo_provisional()
    {
        // 2025Q2 is short a chain row; 2025Q3 and 2025Q4 are whole. Everything before 2025Q2 is
        // either empty or fetched, but the run ending at 2025Q4 stops at 2025Q2.
        using var study = new StudyHarness();
        study.AddEvent("old", printDate: In(2023, 1), week: "2023-W07");
        study.AddEvent("gap", printDate: In(2025, 2), week: "2025-W20", withMeasures: false);
        study.AddEvent("q3", printDate: In(2025, 3), week: "2025-W33");
        study.AddEvent("q4-a", printDate: In(2025, 4), week: "2025-W46");
        study.AddEvent("q4-b", printDate: In(2025, 4), week: "2025-W47");

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        Assert.Equal($"# C1 — implied vs realized earnings moves (v2) — {MemoWriter.ProvisionalLabel}", study.Memo.Split('\n')[0]);
        Assert.Contains("**Deliverable subset: 2025Q3..2025Q4 (2 of 16 quarters). This memo is PROVISIONAL.**", study.Memo);
        Assert.Contains(
            "Every statistic and split below is restricted to those quarters, and the full-window run remains owed\n" +
            "whatever the verdict says.",
            study.Memo);
        Assert.Contains("| 2025Q2 | 1 | 1 | 0 | 0 | 0 | no | no |", study.Memo);
        Assert.Contains("| 2025Q3 | 1 | 0 | 0 | 0 | 0 | yes | yes |", study.Memo);
        Assert.Contains("| 2023Q1 | 1 | 0 | 0 | 0 | 0 | yes | no |", study.Memo);

        // The 07b tally reconciles, and feeds gate 08 exactly what it left standing.
        var gate = study.WrittenGateCounts.Single(c => c.Gate == Gates.QuarterCoverage);
        Assert.Equal(5, gate.Considered);
        Assert.Equal(2, gate.Removed);
        Assert.Equal(3, gate.Remaining);
        Assert.Equal(gate.Considered - gate.Removed, gate.Remaining);
        Assert.Equal(gate.Remaining, study.WrittenGateCounts.Single(c => c.Gate == Gates.ClosesPresent).Considered);

        // The out-of-subset events are rows in the event table with 07b as their stop, not absences.
        Assert.Equal(Gates.QuarterCoverage, study.Row("old").StopGate);
        Assert.Equal(Gates.QuarterCoverage, study.Row("gap").StopGate);
        Assert.Contains("print date in 2023Q1, outside the deliverable subset", study.Row("old").Note);
        Assert.Contains("print date in 2025Q2, outside the deliverable subset", study.Row("gap").Note);
        Assert.Equal("", study.Row("q3").StopGate);

        // Every statistic is restricted to the subset: three events in, two of the five out.
        Assert.Contains("| events in sample | 3 | |", study.Memo);
        Assert.Contains("Measured on the PRIMARY (tradable) sample, restricted to the deliverable subset (section 1.3).", study.Memo);
        Assert.Contains("PROVISIONAL memo: only 2 of 16 registered quarters are fully fetched", study.Memo);
    }

    [Fact]
    public async Task A_gap_in_the_middle_leaves_only_the_run_that_ends_at_the_most_recent_quarter()
    {
        using var study = new StudyHarness();
        study.AddEvent("early-a", printDate: In(2022, 1), week: "2022-W07");
        study.AddEvent("early-b", printDate: In(2022, 2), week: "2022-W20");
        study.AddEvent("hole", printDate: In(2023, 2), week: "2023-W20", withCloses: false);
        study.AddEvent("after", printDate: In(2023, 3), week: "2023-W33");
        study.AddEvent("last", printDate: In(2025, 4), week: "2025-W46");

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        // The two covered 2022 quarters are earlier than the hole, so they are NOT deliverable even
        // though they are complete: the subset has to be one contiguous run ending at 2025Q4.
        Assert.Contains("**Deliverable subset: 2023Q3..2025Q4 (10 of 16 quarters). This memo is PROVISIONAL.**", study.Memo);
        Assert.Equal(Gates.QuarterCoverage, study.Row("early-a").StopGate);
        Assert.Equal(Gates.QuarterCoverage, study.Row("early-b").StopGate);
        Assert.Equal(Gates.QuarterCoverage, study.Row("hole").StopGate);
        Assert.Equal("", study.Row("after").StopGate);
        Assert.Equal("", study.Row("last").StopGate);
        Assert.Contains("| events in sample | 2 | |", study.Memo);
    }

    [Fact]
    public async Task With_the_most_recent_quarter_unfetched_there_is_no_verdict_and_the_run_exits_non_zero()
    {
        using var study = new StudyHarness();
        study.AddEvent("done", printDate: In(2024, 1), week: "2024-W07");
        study.AddEvent("undone", printDate: In(2025, 4), week: "2025-W46", withMeasures: false);

        Assert.Equal(1, await study.RunAsync("--reps", "50"));

        // The coverage table is still written — that is the point of the run — and so is the memo.
        Assert.Equal($"# C1 — implied vs realized earnings moves (v2) — {MemoWriter.NoSubsetLabel}", study.Memo.Split('\n')[0]);
        Assert.Contains("**Deliverable subset: NONE.**", study.Memo);
        Assert.Contains("| 2025Q4 | 1 | 1 | 0 | 0 | 0 | no | no |", study.Memo);
        Assert.Contains("| 2024Q1 | 1 | 0 | 0 | 0 | 0 | yes | no |", study.Memo);

        // No verdict, and the memo says so rather than printing a FAIL nobody measured.
        Assert.Contains("**VERDICT: NOT COMPUTED**", study.Memo);
        Assert.DoesNotContain("VERDICT: FAIL", study.Memo);
        Assert.DoesNotContain("VERDICT: PASS", study.Memo);
        Assert.Contains("NO DELIVERABLE SUBSET.", study.Memo);

        var gate = study.WrittenGateCounts.Single(c => c.Gate == Gates.QuarterCoverage);
        Assert.Equal(2, gate.Considered);
        Assert.Equal(2, gate.Removed);
        Assert.Equal(0, gate.Remaining);
        Assert.Equal(Gates.QuarterCoverage, study.Row("done").StopGate);
        Assert.Contains("no deliverable subset and no verdict", study.Output.ToString());
    }

    [Fact]
    public async Task Require_full_window_refuses_a_provisional_run_and_writes_nothing()
    {
        using var study = new StudyHarness();
        study.AddEvent("fine", printDate: In(2025, 4), week: "2025-W46");
        study.AddEvent("short", printDate: In(2025, 1), week: "2025-W07", withCloses: false);

        Assert.Equal(1, await study.RunAsync("--reps", "50", ComputeOptions.RequireFullWindowFlag));

        Assert.False(File.Exists(study.Paths.Memo));
        Assert.False(File.Exists(study.Paths.EventTable));
        var output = study.Output.ToString();
        Assert.Contains("2025Q1 is not fully fetched", output);
        Assert.Contains("1 of 16 quarter(s) in the registered window are not fully fetched", output);
        Assert.Contains("this run would be PROVISIONAL", output);
        Assert.Contains("Nothing was written.", output);
    }

    [Fact]
    public async Task Require_full_window_lets_a_fully_fetched_run_through_unchanged()
    {
        using var study = new StudyHarness();
        study.AddEvent("a", printDate: In(2025, 4), week: "2025-W46");
        study.AddEvent("b", printDate: In(2022, 1), week: "2022-W07");

        Assert.Equal(0, await study.RunAsync("--reps", "50", ComputeOptions.RequireFullWindowFlag));

        Assert.Contains("This memo is FINAL.", study.Memo);
        Assert.Equal(0, study.WrittenGateCounts.Single(c => c.Gate == Gates.QuarterCoverage).Removed);
    }

    /// <summary>
    /// A quarantined event still has to have been fetched. It is not in the sample either way, but a
    /// missing row for it means the chains run did not finish that quarter, and a subset that
    /// overlooked it would claim a completeness nobody has.
    /// </summary>
    [Fact]
    public async Task An_event_quarantined_at_timing_still_counts_toward_its_quarters_coverage()
    {
        using var study = new StudyHarness();
        study.AddEvent("kept", printDate: In(2025, 4), week: "2025-W46");
        study.AddEvent("quarantined", printDate: In(2025, 4), week: "2025-W46",
            quarantinedAtTiming: true, withMeasures: false);

        Assert.Equal(1, await study.RunAsync("--reps", "50"));

        Assert.Contains("| 2025Q4 | 2 | 1 | 0 | 0 | 0 | no | no |", study.Memo);
        Assert.Contains("**Deliverable subset: NONE.**", study.Memo);
    }

    [Fact]
    public async Task The_provisional_memo_reproduces_byte_for_byte()
    {
        using var study = new StudyHarness();
        study.AddEvent("old", printDate: In(2023, 1), week: "2023-W07");
        study.AddEvent("gap", printDate: In(2025, 2), week: "2025-W20", withMeasures: false);
        for (var i = 0; i < 6; i++) study.AddEvent($"q4-{i}", printDate: In(2025, 4), week: $"2025-W{40 + i}");

        Assert.Equal(0, await study.RunAsync("--reps", "200"));
        var first = File.ReadAllBytes(study.Paths.Memo);

        Assert.Equal(0, await study.RunAsync("--reps", "200"));
        Assert.Equal(first, File.ReadAllBytes(study.Paths.Memo));
        Assert.Contains(MemoWriter.ProvisionalLabel, study.Memo.Split('\n')[0]);
    }
}
