using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;

namespace TradingStuff.Tests;

/// <summary>
/// Pure unit tests for the gap-detection range arithmetic — no database, no wall clock. Mirrors
/// <c>CoverageSessionMinutesTests</c>'s posture: everything here is checkable by hand against the
/// values asserted, not against whatever the implementation happens to produce.
/// </summary>
public sealed class GapArithmeticTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ ClassifyBarSize

    [Theory]
    [InlineData("1 min", BarSizeKind.Intraday, 1)]
    [InlineData("5 mins", BarSizeKind.Intraday, 5)]
    [InlineData("1 hour", BarSizeKind.Intraday, 60)]
    [InlineData("4 hours", BarSizeKind.Intraday, 240)]
    [InlineData("1 day", BarSizeKind.Daily, null)]
    public void Recognised_bar_sizes_classify_correctly(string barSize, BarSizeKind kind, int? intervalMinutes)
    {
        var shape = GapArithmetic.ClassifyBarSize(barSize);

        Assert.Equal(kind, shape.Kind);
        Assert.Equal(intervalMinutes, shape.IntervalMinutes);
    }

    [Theory]
    [InlineData("5 secs")]  // sub-minute — not a backfill target at all (BackfillPlanner.CadenceForBarSize agrees)
    [InlineData("1 week")]  // no verified per-bar expectation formula
    [InlineData("1 month")]
    [InlineData("gibberish")]
    public void Unverifiable_bar_sizes_refuse_rather_than_guess(string barSize) =>
        Assert.Equal(BarSizeKind.Unsupported, GapArithmetic.ClassifyBarSize(barSize).Kind);

    // ------------------------------------------------------------------ ComputeRequestWindows

    [Fact]
    public void A_one_day_duration_window_is_exactly_one_day_back_from_its_end()
    {
        var end = Utc(2024, 3, 15);
        var rows = new[] { new BackfillRequestWindowRow(end, "1 D", "succeeded", 1) };

        var window = Assert.Single(GapArithmetic.ComputeRequestWindows(rows));

        Assert.Equal(Utc(2024, 3, 14), window.Start);
        Assert.Equal(end, window.End);
    }

    [Fact]
    public void A_one_year_duration_window_uses_calendar_years_not_a_flat_365_days()
    {
        // 2024 is a leap year; AddYears(-1) from 2024-03-01 lands on 2023-03-01 (366 days earlier),
        // not 2024-03-01 minus a flat 365 days (which would land on 2023-03-02). Exercising a leap
        // year is the point: a flat-day approximation is off by exactly one day here.
        var end = Utc(2024, 3, 1);
        var rows = new[] { new BackfillRequestWindowRow(end, "1 Y", "succeeded", 1) };

        var window = Assert.Single(GapArithmetic.ComputeRequestWindows(rows));

        Assert.Equal(Utc(2023, 3, 1), window.Start);
    }

    [Fact]
    public void A_seconds_duration_the_grid_parser_rejects_still_gets_an_exact_window()
    {
        // The top-up job's own duration shape ("3600 S") — BackfillPlanner.TryParseCadence names no
        // grid for "S", so this exercises the ApproximateSpanOf fallback, which IS exact for seconds.
        var end = Utc(2026, 7, 31, 14, 30);
        var rows = new[] { new BackfillRequestWindowRow(end, "3600 S", "succeeded", 1) };

        var window = Assert.Single(GapArithmetic.ComputeRequestWindows(rows));

        Assert.Equal(end.AddSeconds(-3600), window.Start);
    }

    [Fact]
    public void A_non_grid_aligned_end_time_still_derives_an_exact_start_the_leading_slice_case()
    {
        // BackfillPlanner.PlanHistorical's leading slice ends at the job's own target_to, which is
        // rarely on the "1 D"/"1 Y" grid boundary. SliceCadence.Previous is a plain subtraction, so it
        // is exact regardless of alignment — there is no separate "off-grid" branch to get wrong.
        var end = Utc(2024, 3, 15, 17, 42); // an arbitrary, non-midnight instant
        var rows = new[] { new BackfillRequestWindowRow(end, "1 D", "succeeded", 1) };

        var window = Assert.Single(GapArithmetic.ComputeRequestWindows(rows));

        Assert.Equal(end.AddDays(-1), window.Start);
    }

    [Fact]
    public void Windows_are_returned_sorted_by_start_regardless_of_input_order()
    {
        var rows = new[]
        {
            new BackfillRequestWindowRow(Utc(2024, 3, 3), "1 D", "succeeded", 1),
            new BackfillRequestWindowRow(Utc(2024, 3, 1), "1 D", "succeeded", 1),
            new BackfillRequestWindowRow(Utc(2024, 3, 2), "1 D", "succeeded", 1),
        };

        var windows = GapArithmetic.ComputeRequestWindows(rows);

        Assert.Equal([Utc(2024, 2, 29), Utc(2024, 3, 1), Utc(2024, 3, 2)], windows.Select(w => w.Start));
    }

    // ------------------------------------------------------------------ DetermineBasis

    [Fact]
    public void No_covering_request_is_not_requested() =>
        Assert.Equal(GapBasis.NotRequested, GapArithmetic.DetermineBasis([], maxAttempts: 5));

    [Theory]
    [InlineData("pending", GapBasis.Pending)]
    [InlineData("inflight", GapBasis.Inflight)]
    [InlineData("empty", GapBasis.Empty)]
    [InlineData("permanent", GapBasis.Permanent)]
    public void A_single_covering_state_maps_to_its_basis(string state, string expectedBasis)
    {
        var covering = new[] { new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), state, Attempts: 1) };

        Assert.Equal(expectedBasis, GapArithmetic.DetermineBasis(covering, maxAttempts: 5));
    }

    [Fact]
    public void A_failed_row_under_the_attempt_cap_is_retrying_not_exhausted()
    {
        var covering = new[] { new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "failed", Attempts: 2) };

        Assert.Equal(GapBasis.Retrying, GapArithmetic.DetermineBasis(covering, maxAttempts: 5));
    }

    [Fact]
    public void A_failed_row_at_the_attempt_cap_is_exhausted_not_permanent()
    {
        // Exhausted is deliberately distinct from Permanent: nobody confirmed the data does not exist,
        // the coordinator simply stopped trying.
        var covering = new[] { new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "failed", Attempts: 5) };

        Assert.Equal(GapBasis.Exhausted, GapArithmetic.DetermineBasis(covering, maxAttempts: 5));
    }

    [Fact]
    public void A_succeeded_covering_row_is_the_alarm_case_even_alongside_a_pending_one()
    {
        // The alarming basis must win regardless of what else covers the same range — an operator
        // reading succeeded_but_absent must never have it masked by a co-located pending row.
        var covering = new[]
        {
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "pending", Attempts: 0),
            new RequestWindow(Utc(2024, 1, 1, 12, 0), Utc(2024, 1, 2), "succeeded", Attempts: 1),
        };

        Assert.Equal(GapBasis.SucceededButAbsent, GapArithmetic.DetermineBasis(covering, maxAttempts: 5));
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("empty")]
    public void An_abandoned_range_is_not_hidden_behind_an_explained_one(string explainedState)
    {
        // `empty` and `permanent` are EXPLAINED — TWS said the data is not there, or said the request
        // can never succeed — and callers are told to treat both as resolved. `exhausted` means
        // nobody established anything: the coordinator tried N times and stopped. Ranking the
        // explained pair above it reported a range the platform had abandoned as benign.
        var covering = new[]
        {
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), explainedState, Attempts: 1),
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "failed", Attempts: 5),
        };

        Assert.Equal(GapBasis.Exhausted, GapArithmetic.DetermineBasis(covering, maxAttempts: 5));
    }

    [Fact]
    public void Succeeded_beats_permanent_and_exhausted_too()
    {
        var covering = new[]
        {
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "permanent", Attempts: 1),
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "failed", Attempts: 5),
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "succeeded", Attempts: 1),
        };

        Assert.Equal(GapBasis.SucceededButAbsent, GapArithmetic.DetermineBasis(covering, maxAttempts: 5));
    }

    // ------------------------------------------------------------------ RequestWindowSweep

    [Fact]
    public void The_sweep_finds_only_windows_that_genuinely_overlap_the_queried_range()
    {
        var windows = new[]
        {
            new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "succeeded", 1),
            new RequestWindow(Utc(2024, 1, 2), Utc(2024, 1, 3), "empty", 1),
            new RequestWindow(Utc(2024, 1, 5), Utc(2024, 1, 6), "pending", 0),
        };
        var sweep = new GapArithmetic.RequestWindowSweep(windows);

        // Half-open: querying exactly [Jan 2, Jan 3) must find the Jan2-3 window but not the Jan1-2
        // one (which ends exactly at Jan 2) — the same half-open discipline sessions use.
        var found = sweep.FindOverlapping(Utc(2024, 1, 2), Utc(2024, 1, 3));

        Assert.Single(found);
        Assert.Equal("empty", found[0].State);
    }

    [Fact]
    public void The_sweep_returns_nothing_for_a_range_no_window_covers()
    {
        var windows = new[] { new RequestWindow(Utc(2024, 1, 1), Utc(2024, 1, 2), "succeeded", 1) };
        var sweep = new GapArithmetic.RequestWindowSweep(windows);

        Assert.Empty(sweep.FindOverlapping(Utc(2024, 6, 1), Utc(2024, 6, 2)));
    }

    [Fact]
    public void The_sweep_handles_successive_queries_advancing_forward_through_many_windows()
    {
        var windows = Enumerable.Range(0, 10)
            .Select(i => new RequestWindow(Utc(2024, 1, 1).AddDays(i), Utc(2024, 1, 1).AddDays(i + 1), "succeeded", 1))
            .ToArray();
        var sweep = new GapArithmetic.RequestWindowSweep(windows);

        for (var i = 0; i < 10; i++)
        {
            var day = Utc(2024, 1, 1).AddDays(i);
            var found = Assert.Single(sweep.FindOverlapping(day, day.AddDays(1)));
            Assert.Equal(day, found.Start);
        }
    }

    // ------------------------------------------------------------------ BuildRanges: the absent-row cases

    [Fact]
    public void A_covered_unit_between_two_incomplete_units_breaks_the_run_rather_than_being_swallowed()
    {
        // The exact bug a naive "merge every consecutive same-basis incomplete unit" implementation
        // would have: without treating a COVERED unit as a hard break, a healthy day sandwiched
        // between two not_requested days would be reported as part of one continuous gap — which is
        // the "explained-looking range that actually hides a real day of missing data" failure mode in
        // miniature, just inverted (here it would hide a real day of PRESENT data inside a reported
        // gap). A test that only checks "a gap in the middle is found" does not exercise this at all.
        var sequence = new (DateTimeOffset From, DateTimeOffset To, string? Basis)[]
        {
            (Utc(2024, 1, 1), Utc(2024, 1, 2), GapBasis.NotRequested),
            (Utc(2024, 1, 2), Utc(2024, 1, 3), null), // covered — must break the run
            (Utc(2024, 1, 3), Utc(2024, 1, 4), GapBasis.NotRequested),
        };

        var (ranges, truncated) = GapArithmetic.BuildRanges(sequence, maxRanges: 100);

        Assert.False(truncated);
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new GapRange(Utc(2024, 1, 1), Utc(2024, 1, 2), GapBasis.NotRequested), ranges[0]);
        Assert.Equal(new GapRange(Utc(2024, 1, 3), Utc(2024, 1, 4), GapBasis.NotRequested), ranges[1]);
    }

    [Fact]
    public void Consecutive_incomplete_units_with_a_wall_clock_gap_between_them_still_merge()
    {
        // The ordinary case: two trading days' RTH sessions never touch in wall-clock time (the
        // market is shut overnight), and a multi-year unplanned backlog spans many weekends. Neither
        // should explode into one range per day — only an actually-covered or differently-explained
        // unit should ever start a new range.
        var sequence = new (DateTimeOffset From, DateTimeOffset To, string? Basis)[]
        {
            (Utc(2024, 1, 1, 13, 30), Utc(2024, 1, 1, 20, 0), GapBasis.NotRequested), // Monday RTH
            (Utc(2024, 1, 2, 13, 30), Utc(2024, 1, 2, 20, 0), GapBasis.NotRequested), // Tuesday RTH — does not touch Monday's
        };

        var (ranges, _) = GapArithmetic.BuildRanges(sequence, maxRanges: 100);

        var range = Assert.Single(ranges);
        Assert.Equal(Utc(2024, 1, 1, 13, 30), range.From);
        Assert.Equal(Utc(2024, 1, 2, 20, 0), range.To);
        Assert.Equal(GapBasis.NotRequested, range.Basis);
    }

    [Fact]
    public void A_change_in_basis_starts_a_new_range_even_with_no_covered_unit_between()
    {
        var sequence = new (DateTimeOffset From, DateTimeOffset To, string? Basis)[]
        {
            (Utc(2024, 1, 1), Utc(2024, 1, 2), GapBasis.Empty),
            (Utc(2024, 1, 2), Utc(2024, 1, 3), GapBasis.Permanent),
        };

        var (ranges, _) = GapArithmetic.BuildRanges(sequence, maxRanges: 100);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(GapBasis.Empty, ranges[0].Basis);
        Assert.Equal(GapBasis.Permanent, ranges[1].Basis);
    }

    [Fact]
    public void An_entirely_covered_sequence_produces_no_ranges_at_all()
    {
        var sequence = new (DateTimeOffset From, DateTimeOffset To, string? Basis)[]
        {
            (Utc(2024, 1, 1), Utc(2024, 1, 2), null),
            (Utc(2024, 1, 2), Utc(2024, 1, 3), null),
        };

        var (ranges, truncated) = GapArithmetic.BuildRanges(sequence, maxRanges: 100);

        Assert.Empty(ranges);
        Assert.False(truncated);
    }

    [Fact]
    public void More_merged_ranges_than_the_cap_are_truncated_rather_than_silently_all_returned()
    {
        // Force one range per unit by alternating basis so nothing merges, then cap below the count.
        var sequence = Enumerable.Range(0, 10)
            .Select(i => (Utc(2024, 1, 1).AddDays(i), Utc(2024, 1, 1).AddDays(i + 1),
                (string?)(i % 2 == 0 ? GapBasis.Empty : GapBasis.Permanent)));

        var (ranges, truncated) = GapArithmetic.BuildRanges(sequence, maxRanges: 3);

        Assert.Equal(3, ranges.Count);
        Assert.True(truncated);
    }

    // ------------------------------------------------------------------ TradingDatesInRange

    private static TradingSession RthSession(DateOnly date) => new(
        SessionId: date.DayNumber,
        Calendar: "CBOE_INDEX_RTH",
        TradingDate: date,
        OpenUtc: new DateTimeOffset(date.ToDateTime(new TimeOnly(14, 30)), TimeSpan.Zero),
        CloseUtc: new DateTimeOffset(date.ToDateTime(new TimeOnly(21, 15)), TimeSpan.Zero),
        Label: "RTH",
        IsHalfDay: false);

    [Fact]
    public void A_daily_expectation_uses_the_same_bound_convention_as_the_landed_query()
    {
        // The expected set and the landed set MUST agree on what "in range" means, because one is
        // measured against the other. BackfillStore.GetLandedTradingDatesAsync filters on the bar's
        // instant (ts_utc >= from AND ts_utc < to), and a daily bar's instant is its trading date's
        // UTC midnight. The previous rule here was whole-day OVERLAP, so a window starting mid-day —
        // which every head-clamped lower bound does — expected a date whose bar that query could not
        // return, and reported succeeded_but_absent over data that is present and correct.
        var sessions = new[]
        {
            RthSession(new DateOnly(2024, 1, 8)),
            RthSession(new DateOnly(2024, 1, 9)),
            RthSession(new DateOnly(2024, 1, 10)),
        };

        // Starts inside Monday, so Monday's midnight instant is BELOW the window: its bar is not in
        // range, and must not be expected.
        var dates = GapArithmetic.TradingDatesInRange(sessions, Utc(2024, 1, 8, 14, 30), Utc(2024, 1, 10, 12, 0));

        Assert.Equal([new DateOnly(2024, 1, 9), new DateOnly(2024, 1, 10)], dates);
    }

    [Fact]
    public void A_daily_expectation_is_half_open_at_both_ends()
    {
        var sessions = new[] { RthSession(new DateOnly(2024, 1, 8)), RthSession(new DateOnly(2024, 1, 9)) };

        // [Monday midnight, Tuesday midnight) — Monday in, Tuesday out.
        Assert.Equal(
            [new DateOnly(2024, 1, 8)],
            GapArithmetic.TradingDatesInRange(sessions, Utc(2024, 1, 8), Utc(2024, 1, 9)));
    }

    // ------------------------------------------------------------------ Union / Subtract

    [Fact]
    public void Overlapping_and_touching_spans_merge_into_one()
    {
        var union = GapArithmetic.Union(
        [
            new(Utc(2024, 1, 3), Utc(2024, 1, 5)),
            new(Utc(2024, 1, 1), Utc(2024, 1, 3)), // touches the first exactly
            new(Utc(2024, 1, 4), Utc(2024, 1, 6)), // overlaps
            new(Utc(2024, 1, 9), Utc(2024, 1, 9)), // empty — dropped
        ]);

        Assert.Equal([new GapArithmetic.Span(Utc(2024, 1, 1), Utc(2024, 1, 6))], union);
    }

    [Fact]
    public void The_seam_between_two_audited_windows_is_what_subtract_reports()
    {
        // The reconciliation the report was missing, in miniature: a historical job audited up to
        // Jan 10 and a top-up job audited from Jan 20, both of them cleanly. Nothing looked at the
        // ten days between, and no per-job check could ever say so.
        var unaudited = GapArithmetic.Subtract(
            [new(Utc(2024, 1, 1), Utc(2024, 1, 31))],
            [new(Utc(2024, 1, 1), Utc(2024, 1, 10)), new(Utc(2024, 1, 20), Utc(2024, 1, 31))]);

        Assert.Equal([new GapArithmetic.Span(Utc(2024, 1, 10), Utc(2024, 1, 20))], unaudited);
    }

    [Fact]
    public void A_fully_audited_claim_subtracts_to_nothing()
    {
        Assert.Empty(GapArithmetic.Subtract(
            [new(Utc(2024, 1, 1), Utc(2024, 1, 10))],
            [new(Utc(2023, 1, 1), Utc(2024, 6, 1))]));
    }

    [Fact]
    public void A_claim_nothing_audited_survives_subtraction_whole()
    {
        Assert.Equal(
            [new GapArithmetic.Span(Utc(2024, 1, 1), Utc(2024, 1, 10))],
            GapArithmetic.Subtract([new(Utc(2024, 1, 1), Utc(2024, 1, 10))], []));
    }
}
