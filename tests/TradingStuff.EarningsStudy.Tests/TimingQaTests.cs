using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class TimingQaTests
{
    private const string EventId = "320193:0000320193-24-000006";

    private static EventTimingRow Timing() => new(
        EventId, TimingResolver.Amc, new DateOnly(2024, 2, 1), new DateOnly(2024, 1, 31),
        new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 2), "2024-W05", false, null);

    private static ClosesRow Closes(decimal? preEntry, decimal? entry, decimal? exit, decimal? medianAbsReturn20) =>
        new(EventId, "AAPL", preEntry, entry, exit, medianAbsReturn20, "ok", "ibkr", null);

    [Fact]
    public void A_move_that_landed_before_the_entry_snapshot_is_quarantined()
    {
        // The late-filing signature: 8% on the pre-entry-to-entry leg, 0.5% on the event leg, against
        // a name whose ordinary day is 1%.
        var result = TimingQa.Evaluate(Timing(), Closes(100m, 108m, 108.54m, 0.01m));

        Assert.True(result.Quarantined);
        Assert.Contains("late_filing_suspected", result.Reason);
        Assert.Equal(0.08m, Assert.IsType<decimal>(result.PreEntryMove), 6);

        // EventMove is still computed and reported — the memo prints it — it just decides nothing.
        Assert.Equal(0.005m, Assert.IsType<decimal>(result.EventMove), 6);
        Assert.DoesNotContain("event move", result.Reason);
    }

    [Fact]
    public void An_ordinary_event_is_not_quarantined()
    {
        var result = TimingQa.Evaluate(Timing(), Closes(100m, 100.4m, 107.428m, 0.01m));

        Assert.False(result.Quarantined);
        Assert.Null(result.Reason);
        Assert.Equal(0.004m, Assert.IsType<decimal>(result.PreEntryMove), 6);
        Assert.Equal(0.07m, Assert.IsType<decimal>(result.EventMove), 6);
    }

    [Fact]
    public void A_big_pre_entry_move_is_quarantined_however_large_the_event_move_was()
    {
        // The retired conjunct kept this row: 6% pre-entry against a 12% event move, so the event day
        // "still carried the larger move". That reasoning reads RF to decide inclusion. The rule now
        // sees only the pre-entry leg and the name's own scale, and 6% against a 3% threshold goes.
        var result = TimingQa.Evaluate(Timing(), Closes(100m, 106m, 118.72m, 0.01m));

        Assert.True(result.Quarantined);
        Assert.Contains("late_filing_suspected", result.Reason);

        // Still computed, still reported, just not consulted.
        Assert.Equal(0.12m, Assert.IsType<decimal>(result.EventMove), 6);

        // And the reason may not cite the event move, because the event move did not decide anything.
        Assert.DoesNotContain("event move", result.Reason);
    }

    [Fact]
    public void The_decision_cannot_see_anything_that_happens_after_the_entry_close()
    {
        // The defect this pins: gate 09 used to require preEntryMove > eventMove, which conditions
        // INCLUSION on RF — the numerator of the study's primary statistic. Two rows identical up to
        // and including the entry close, differing only in what happened afterwards, must be decided
        // identically. (docs/LESSONS.md 2: reintroducing the conjunct makes this test fail.)
        var quiet = TimingQa.Evaluate(Timing(), Closes(100m, 106m, 106.2968m, 0.01m));   // event +0.28%
        var violent = TimingQa.Evaluate(Timing(), Closes(100m, 106m, 96.0042m, 0.01m));  // event -9.43%

        // The reviewer's executed pair: identical pre-entry move, opposite outcomes.
        Assert.Equal(0.06m, Assert.IsType<decimal>(quiet.PreEntryMove), 6);
        Assert.Equal(0.06m, Assert.IsType<decimal>(violent.PreEntryMove), 6);
        Assert.Equal(0.0028m, Assert.IsType<decimal>(quiet.EventMove), 6);
        Assert.Equal(0.0943m, Assert.IsType<decimal>(violent.EventMove), 6);
        Assert.NotEqual(quiet.EventMove, violent.EventMove);

        Assert.Equal(quiet.Quarantined, violent.Quarantined);
        Assert.Equal(quiet.Reason, violent.Reason);

        // Sweep the exit close across two orders of magnitude and both signs: the decision is flat.
        foreach (var exit in new[] { 1m, 53m, 100m, 105.9m, 106m, 106.01m, 120m, 500m, 10_000m })
        {
            var swept = TimingQa.Evaluate(Timing(), Closes(100m, 106m, exit, 0.01m));

            Assert.True(swept.Quarantined, $"exit close {exit} changed the verdict");
            Assert.Equal(quiet.Reason, swept.Reason);
        }

        // Same sweep below the threshold: still flat, and still the other way.
        foreach (var exit in new[] { 1m, 100m, 102m, 500m })
        {
            Assert.False(TimingQa.Evaluate(Timing(), Closes(100m, 102m, exit, 0.01m)).Quarantined);
        }
    }

    [Fact]
    public void The_one_threshold_is_strict_at_the_boundary()
    {
        // Trailing median 1% ⇒ threshold exactly 3%. A pre-entry move of exactly 3% does not clear it.
        Assert.False(TimingQa.Evaluate(Timing(), Closes(100m, 103m, 100.5m, 0.01m)).Quarantined);

        // A hair over does.
        Assert.True(TimingQa.Evaluate(Timing(), Closes(100m, 103.001m, 100.5m, 0.01m)).Quarantined);

        // Downwards too: the move is absolute, so -3% is on the same side of the boundary as +3%.
        Assert.False(TimingQa.Evaluate(Timing(), Closes(100m, 97m, 100.5m, 0.01m)).Quarantined);
        Assert.True(TimingQa.Evaluate(Timing(), Closes(100m, 96.999m, 100.5m, 0.01m)).Quarantined);
    }

    [Fact]
    public void The_floor_stands_in_when_the_trailing_scale_is_missing()
    {
        var quarantined = TimingQa.Evaluate(Timing(), Closes(100m, 105m, 101m, null));

        Assert.True(quarantined.Quarantined);
        Assert.Contains("fallback floor", quarantined.Reason);

        // 3.5% is above where a 1%-a-day name's scaled threshold would be, but below the floor, and
        // the floor is what applies when the scale is unmeasurable.
        var kept = TimingQa.Evaluate(Timing(), Closes(100m, 103.5m, 101m, null));

        Assert.False(kept.Quarantined);
        Assert.Contains("fallback floor", kept.Reason);
    }

    [Fact]
    public void A_zero_trailing_scale_does_not_become_a_zero_threshold()
    {
        // A name that has not moved for twenty sessions has a degenerate scale, not a strict one.
        // Three times zero would quarantine on any pre-entry move at all.
        var result = TimingQa.Evaluate(Timing(), Closes(100m, 101m, 100.1m, 0m));

        Assert.False(result.Quarantined);
        Assert.Contains("fallback floor", result.Reason);
    }

    [Fact]
    public void A_missing_close_is_not_evaluable_and_names_what_is_missing()
    {
        Assert.Equal(
            "not_evaluable: close_pre_entry is missing",
            TimingQa.Evaluate(Timing(), Closes(null, 100m, 101m, 0.01m)).Reason);

        Assert.Equal(
            "not_evaluable: close_entry is missing",
            TimingQa.Evaluate(Timing(), Closes(100m, null, 101m, 0.01m)).Reason);

        Assert.Equal(
            "not_evaluable: close_exit is missing",
            TimingQa.Evaluate(Timing(), Closes(100m, 100m, null, 0.01m)).Reason);

        foreach (var missing in new[]
        {
            Closes(null, 100m, 101m, 0.01m), Closes(100m, null, 101m, 0.01m), Closes(100m, 100m, null, 0.01m)
        })
        {
            var result = TimingQa.Evaluate(Timing(), missing);

            Assert.False(result.Quarantined);
            Assert.Null(result.PreEntryMove);
            Assert.Null(result.EventMove);
        }
    }

    [Fact]
    public void A_zero_close_is_refused_rather_than_divided_by()
    {
        Assert.Equal(
            "not_evaluable: close_pre_entry is not positive",
            TimingQa.Evaluate(Timing(), Closes(0m, 100m, 101m, 0.01m)).Reason);

        Assert.Equal(
            "not_evaluable: close_entry is not positive",
            TimingQa.Evaluate(Timing(), Closes(100m, 0m, 101m, 0.01m)).Reason);
    }

    [Fact]
    public void An_unresolved_event_is_not_evaluable()
    {
        var unresolved = new EventTimingRow(
            EventId, TimingResolver.Unresolved, new DateOnly(2024, 2, 1), null, null, null, "2024-W05", true, "why");

        var result = TimingQa.Evaluate(unresolved, Closes(100m, 108m, 108.54m, 0.01m));

        Assert.False(result.Quarantined);
        Assert.Contains("not_evaluable", result.Reason);
    }

    [Fact]
    public void Rows_for_two_different_events_are_refused_rather_than_scored()
    {
        var otherEvent = new ClosesRow("someone-else", "MSFT", 100m, 108m, 108.54m, 0.01m, "ok", "ibkr", null);

        var ex = Assert.Throws<ArgumentException>(() => TimingQa.Evaluate(Timing(), otherEvent));

        Assert.Contains(EventId, ex.Message);
        Assert.Contains("someone-else", ex.Message);
    }

    [Fact]
    public void The_memo_sentence_states_the_rule_and_is_one_sentence()
    {
        var sentence = TimingQa.Describe();

        Assert.Contains("3 times the trailing 20-day median absolute daily return", sentence);
        Assert.Contains("4.00%", sentence);
        Assert.Contains("absolute pre-entry move", sentence);
        Assert.EndsWith(".", sentence);
        Assert.DoesNotContain(". ", sentence);

        // The memo quotes this verbatim, so it is where a reader learns whether the gate can see the
        // outcome. It states one threshold and does not mention the event move at all.
        Assert.DoesNotContain("event move", sentence);
        Assert.DoesNotContain("exit close", sentence);
        Assert.DoesNotContain("both", sentence);
    }

    [Fact]
    public void The_options_side_diagnostic_measures_the_collapse_into_the_entry_snapshot()
    {
        // Straddle worth 5% of spot the session before, 1.5% at entry: the event is already priced out.
        var collapsed = Measures(straddlePreEntry: 5m, spotPreEntry: 100m, straddleEntry: 1.5m, spotEntry: 100m);

        var ratio = TimingQa.ImCollapseDiagnostic(collapsed);

        Assert.Equal(0.3m, Assert.IsType<decimal>(ratio), 6);
        Assert.True(ratio < TimingQa.ImCollapseThreshold);

        // Unchanged IM in spot terms reads as 1, even when the spot itself moved.
        var steady = Measures(straddlePreEntry: 5m, spotPreEntry: 100m, straddleEntry: 5.5m, spotEntry: 110m);

        Assert.Equal(1m, Assert.IsType<decimal>(TimingQa.ImCollapseDiagnostic(steady)), 6);

        Assert.Null(TimingQa.ImCollapseDiagnostic(
            Measures(straddlePreEntry: null, spotPreEntry: 100m, straddleEntry: 1.5m, spotEntry: 100m)));
        Assert.Null(TimingQa.ImCollapseDiagnostic(
            Measures(straddlePreEntry: 5m, spotPreEntry: 0m, straddleEntry: 1.5m, spotEntry: 100m)));
    }

    private static OptionMeasuresRow Measures(
        decimal? straddlePreEntry, decimal? spotPreEntry, decimal? straddleEntry, decimal? spotEntry) =>
        new(EventId, "AAPL", new DateOnly(2024, 2, 2), 1, "eod", "16:00:00",
            spotEntry, null, 100m, null, null, null, null, straddleEntry, null,
            straddlePreEntry, spotPreEntry, null, null, "ok", null);
}
