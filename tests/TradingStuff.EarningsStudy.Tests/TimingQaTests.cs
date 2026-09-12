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
        Assert.Equal(0.005m, Assert.IsType<decimal>(result.EventMove), 6);
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
    public void A_big_pre_entry_move_survives_when_the_event_move_is_bigger()
    {
        // A run-up into the print is not a late filing: the event day still carries the larger move.
        var result = TimingQa.Evaluate(Timing(), Closes(100m, 106m, 118.72m, 0.01m));

        Assert.False(result.Quarantined);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Both_thresholds_are_strict_at_the_boundary()
    {
        // Trailing median 1% ⇒ threshold exactly 3%. A pre-entry move of exactly 3% does not clear it.
        Assert.False(TimingQa.Evaluate(Timing(), Closes(100m, 103m, 100.5m, 0.01m)).Quarantined);

        // A hair over does.
        Assert.True(TimingQa.Evaluate(Timing(), Closes(100m, 103.001m, 100.5m, 0.01m)).Quarantined);

        // And equality on the first conjunct does not clear it either: 5% against 5%.
        Assert.False(TimingQa.Evaluate(Timing(), Closes(100m, 105m, 110.25m, 0.01m)).Quarantined);
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
        Assert.Contains("larger than the absolute event move", sentence);
        Assert.EndsWith(".", sentence);
        Assert.DoesNotContain(". ", sentence);
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
