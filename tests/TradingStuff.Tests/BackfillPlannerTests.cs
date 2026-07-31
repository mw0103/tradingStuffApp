using System.Globalization;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;

namespace TradingStuff.Tests;

/// <summary>
/// Slice arithmetic and, above all, determinism.
/// </summary>
/// <remarks>
/// <c>research.backfill_requests</c>'s uniqueness key only makes a rerun free if the planner
/// re-derives byte-identical slice boundaries, and a planner that drifts fails in a way the obvious
/// acceptance test cannot see: <c>research.bars</c>'s primary key absorbs the duplicated bars, so
/// bar counts stay right while the request table silently doubles. These tests therefore assert on
/// the slices themselves — their exact instants, rendered — not on anything downstream of them.
/// </remarks>
public sealed class BackfillPlannerTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static BackfillJob Job(
        DateTimeOffset targetFrom,
        DateTimeOffset targetTo,
        string barSize = "1 min",
        string kind = BackfillJobKinds.Historical,
        string? sliceDuration = null) =>
        new(
            JobId: 7,
            Name: "spx-1min-trades",
            InstrumentId: 1,
            ConId: 416904,
            WhatToShow: "TRADES",
            BarSize: barSize,
            UseRth: true,
            TargetFrom: targetFrom,
            TargetTo: targetTo,
            Priority: 100,
            Status: "running",
            Kind: kind,
            SliceDuration: sliceDuration);

    /// <summary>Renders a slice the way the idempotency key sees it, so "identical" means identical.</summary>
    private static string Render(BackfillSlice slice) => string.Join(
        '|',
        slice.JobId.ToString(CultureInfo.InvariantCulture),
        slice.ConId.ToString(CultureInfo.InvariantCulture),
        slice.EndTimeUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "(null)",
        slice.Duration,
        slice.WhatToShow,
        slice.BarSize,
        slice.UseRth.ToString());

    private static SliceCadence CadenceOf(BackfillJob job) =>
        BackfillPlanner.CadenceFor(job) ?? throw new InvalidOperationException("The test job has no cadence.");

    // ---- determinism ---------------------------------------------------------------------------

    [Fact]
    public void The_same_job_plans_byte_identical_slices_twice()
    {
        var job = Job(Utc(2010, 1, 1), Utc(2026, 7, 31));
        var cadence = CadenceOf(job);

        var first = BackfillPlanner.PlanHistorical(job, 416904, Utc(2004, 3, 4), cadence);
        var second = BackfillPlanner.PlanHistorical(job, 416904, Utc(2004, 3, 4), cadence);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(Render), second.Select(Render));
    }

    [Fact]
    public void Nothing_in_a_historical_plan_depends_on_the_current_instant()
    {
        // The failure this guards against is a planner that walks back from `now`: it would produce
        // a different boundary set on every run, and every one of those is a brand new row under the
        // idempotency key. Two identical calls separated by a real clock tick must be identical.
        var job = Job(Utc(2024, 1, 1), Utc(2024, 3, 1));
        var cadence = CadenceOf(job);

        var first = BackfillPlanner.PlanHistorical(job, 416904, null, cadence);
        Thread.Sleep(5);
        var second = BackfillPlanner.PlanHistorical(job, 416904, null, cadence);

        Assert.Equal(first.Select(Render), second.Select(Render));
    }

    [Fact]
    public void Minute_bar_slice_boundaries_land_on_utc_midnights()
    {
        var job = Job(Utc(2024, 1, 1), Utc(2024, 1, 10));

        var slices = BackfillPlanner.PlanHistorical(job, 416904, null, CadenceOf(job));

        Assert.NotEmpty(slices);
        Assert.All(slices, slice =>
        {
            var end = slice.EndTimeUtc!.Value;
            Assert.Equal(TimeSpan.Zero, end.TimeOfDay);
            Assert.Equal(TimeSpan.Zero, end.Offset);
        });
    }

    [Fact]
    public void Adjacent_slices_are_contiguous_and_cover_the_whole_target_range()
    {
        var from = Utc(2024, 1, 1);
        var to = Utc(2024, 1, 10);
        var job = Job(from, to);
        var cadence = CadenceOf(job);

        // Oldest-first, so consecutive ends are exactly one cadence step apart and the union of
        // [end - span, end) has no hole in it.
        var ends = BackfillPlanner.PlanHistorical(job, 416904, null, cadence)
            .Select(slice => slice.EndTimeUtc!.Value)
            .OrderBy(end => end)
            .ToArray();

        for (var i = 1; i < ends.Length; i++)
        {
            Assert.Equal(ends[i - 1], cadence.Previous(ends[i]));
        }

        Assert.True(ends[0] - cadence.ApproximateSpan <= from, "The oldest slice must reach target_from.");
        Assert.True(ends[^1] >= to, "The newest slice must reach target_to.");
    }

    [Fact]
    public void No_planned_slice_ends_after_the_jobs_target_to()
    {
        // Rounding the anchor UP to the next boundary would put the newest slice's end in the
        // future, where TWS refuses it outright and the job stalls one boundary short of finished.
        var to = Utc(2026, 7, 31, 14, 37);
        var job = Job(Utc(2026, 7, 20), to);

        var slices = BackfillPlanner.PlanHistorical(job, 416904, null, CadenceOf(job));

        Assert.All(slices, slice => Assert.True(slice.EndTimeUtc!.Value <= to));
        Assert.Equal(to, slices[0].EndTimeUtc); // the leading, grid-off slice ends exactly at target_to
    }

    // ---- head clamping and deepening ------------------------------------------------------------

    [Fact]
    public void Planning_is_clamped_to_the_instruments_head_timestamp()
    {
        // "Probe to the floor" is not a separate mode: set target_from optimistically deep and let
        // reqHeadTimeStamp decide where planning actually starts.
        var job = Job(Utc(2000, 1, 1), Utc(2024, 1, 10));
        var head = Utc(2024, 1, 5);

        var slices = BackfillPlanner.PlanHistorical(job, 416904, head, CadenceOf(job));
        var oldest = slices.Min(slice => slice.EndTimeUtc!.Value);

        Assert.True(oldest > head, "No slice should be planned entirely below the head timestamp.");
        Assert.True(oldest - TimeSpan.FromDays(1) <= head, "The oldest slice must still reach down to the head.");
    }

    [Fact]
    public void Lowering_target_from_extends_the_plan_without_shifting_any_existing_slice()
    {
        // The property that makes "SPX from 2010, then probe toward the 2004 head" a configuration
        // change rather than a re-ingestion: because the walk is anchored at the FIXED far end,
        // deepening a job adds only the newly reachable older slices.
        var shallow = Job(Utc(2024, 2, 1), Utc(2024, 3, 1));
        var deep = Job(Utc(2024, 1, 1), Utc(2024, 3, 1));

        var shallowSlices = BackfillPlanner.PlanHistorical(shallow, 416904, null, CadenceOf(shallow)).Select(Render).ToArray();
        var deepSlices = BackfillPlanner.PlanHistorical(deep, 416904, null, CadenceOf(deep)).Select(Render).ToArray();

        Assert.True(deepSlices.Length > shallowSlices.Length);
        Assert.Equal(shallowSlices, deepSlices.Take(shallowSlices.Length));
        Assert.Empty(shallowSlices.Except(deepSlices));
    }

    [Fact]
    public void A_range_entirely_below_the_head_timestamp_plans_nothing()
    {
        var job = Job(Utc(1990, 1, 1), Utc(1995, 1, 1));

        Assert.Empty(BackfillPlanner.PlanHistorical(job, 416904, Utc(2004, 3, 4), CadenceOf(job)));
    }

    // ---- cadences ------------------------------------------------------------------------------

    [Fact]
    public void Daily_bars_are_sliced_a_year_at_a_time_on_january_boundaries()
    {
        var job = Job(Utc(2020, 1, 1), Utc(2023, 1, 1), barSize: "1 day");
        var cadence = CadenceOf(job);

        Assert.Equal("1 Y", cadence.Duration);

        var slices = BackfillPlanner.PlanHistorical(job, 13455763, null, cadence);

        Assert.All(slices, slice =>
        {
            var end = slice.EndTimeUtc!.Value;
            Assert.Equal(1, end.Month);
            Assert.Equal(1, end.Day);
            Assert.Equal(TimeSpan.Zero, end.TimeOfDay);
        });

        Assert.Equal([Utc(2023, 1, 1), Utc(2022, 1, 1), Utc(2021, 1, 1)], slices.Select(slice => slice.EndTimeUtc));
    }

    [Theory]
    [InlineData("1 min", "1 D")]
    [InlineData("5 mins", "1 D")]
    [InlineData("1 hour", "1 W")]
    [InlineData("1 day", "1 Y")]
    [InlineData("1 week", "1 Y")]
    public void Bar_sizes_map_to_their_documented_slice_cadence(string barSize, string expectedDuration)
    {
        Assert.Equal(expectedDuration, BackfillPlanner.CadenceForBarSize(barSize)!.Duration);
    }

    [Theory]
    [InlineData("5 secs")]
    [InlineData("30 secs")]
    [InlineData("nonsense")]
    public void An_unsupported_bar_size_yields_no_cadence_rather_than_a_guess(string barSize)
    {
        // The caller refuses to plan on null. Substituting a default here would land request rows at
        // a duration nobody asked for, permanently mismatching the operator's intent — invisibly,
        // because the rows themselves look perfectly well-formed.
        Assert.Null(BackfillPlanner.CadenceForBarSize(barSize));
    }

    [Fact]
    public void A_jobs_own_slice_duration_overrides_the_bar_size_derived_one()
    {
        var job = Job(Utc(2024, 1, 1), Utc(2024, 3, 1), sliceDuration: "1 W");

        Assert.Equal("1 W", CadenceOf(job).Duration);
    }

    [Fact]
    public void An_unparseable_slice_duration_override_yields_no_cadence()
    {
        Assert.Null(BackfillPlanner.CadenceFor(Job(Utc(2024, 1, 1), Utc(2024, 3, 1), sliceDuration: "4 fortnights")));
    }

    [Fact]
    public void Weekly_slice_boundaries_are_monday_anchored()
    {
        var cadence = new SliceCadence(SliceCadenceUnit.Week, 1);

        // 2026-07-31 is a Friday; the boundary at or before it is Monday 2026-07-27.
        Assert.Equal(Utc(2026, 7, 27), cadence.FloorBoundary(Utc(2026, 7, 31, 18, 5)));
        Assert.Equal(DayOfWeek.Monday, cadence.FloorBoundary(Utc(2026, 7, 31)).DayOfWeek);
        Assert.Equal(Utc(2026, 7, 20), cadence.Previous(Utc(2026, 7, 27)));
    }

    // ---- top-ups: the idempotency contradiction ------------------------------------------------

    [Fact]
    public void A_top_up_slice_is_anchored_to_a_concrete_instant_never_null()
    {
        // Migration 004 designed repeated top-ups to share a constant NULL end_time_utc, which under
        // UNIQUE NULLS NOT DISTINCT makes them collide by construction — every run after the first
        // silently no-ops while logging success. Nothing this planner emits is NULL-anchored.
        var job = Job(Utc(2026, 1, 1), Utc(2035, 1, 1), kind: BackfillJobKinds.TopUp);

        var slice = BackfillPlanner.PlanTopUp(job, 416904, Utc(2026, 7, 31, 14, 37));

        Assert.NotNull(slice.EndTimeUtc);
        Assert.Equal(Utc(2026, 7, 31, 14, 30), slice.EndTimeUtc);
    }

    [Fact]
    public void Two_top_up_runs_inside_one_bucket_produce_the_identical_slice()
    {
        var job = Job(Utc(2026, 1, 1), Utc(2035, 1, 1), kind: BackfillJobKinds.TopUp);

        var early = BackfillPlanner.PlanTopUp(job, 416904, Utc(2026, 7, 31, 14, 30));
        var late = BackfillPlanner.PlanTopUp(job, 416904, Utc(2026, 7, 31, 14, 44));

        Assert.Equal(Render(early), Render(late));
    }

    [Fact]
    public void The_next_bucket_produces_a_genuinely_different_slice_so_the_tail_advances()
    {
        var job = Job(Utc(2026, 1, 1), Utc(2035, 1, 1), kind: BackfillJobKinds.TopUp);

        var thisBucket = BackfillPlanner.PlanTopUp(job, 416904, Utc(2026, 7, 31, 14, 44));
        var nextBucket = BackfillPlanner.PlanTopUp(job, 416904, Utc(2026, 7, 31, 14, 45));

        Assert.NotEqual(Render(thisBucket), Render(nextBucket));
        Assert.Equal(BackfillPlanner.TopUpBucket, nextBucket.EndTimeUtc!.Value - thisBucket.EndTimeUtc!.Value);
    }

    [Fact]
    public void A_top_up_anchor_is_never_in_the_future()
    {
        // TWS rejects a future endDateTime outright, so flooring (never rounding) is what keeps the
        // top-up issuable at any instant within its bucket.
        var job = Job(Utc(2026, 1, 1), Utc(2035, 1, 1), kind: BackfillJobKinds.TopUp);

        foreach (var minute in Enumerable.Range(0, 60))
        {
            var now = Utc(2026, 7, 31, 14, minute);
            Assert.True(BackfillPlanner.PlanTopUp(job, 416904, now).EndTimeUtc!.Value <= now);
        }
    }

    [Fact]
    public void A_top_up_window_overlaps_several_buckets_so_a_missed_run_self_heals()
    {
        var job = Job(Utc(2026, 1, 1), Utc(2035, 1, 1), kind: BackfillJobKinds.TopUp);
        var slice = BackfillPlanner.PlanTopUp(job, 416904, Utc(2026, 7, 31, 14, 37));

        Assert.Equal(BackfillPlanner.DefaultTopUpDuration, slice.Duration);
        Assert.True(
            BackfillPlanner.ApproximateSpanOf(slice.Duration) >= BackfillPlanner.TopUpBucket * 2,
            "One top-up window must cover more than one bucket, or a skipped run leaves a permanent hole.");
    }

    [Fact]
    public void Bucket_flooring_is_stable_across_the_epoch_grid()
    {
        Assert.Equal(
            Utc(2026, 7, 31, 14, 45),
            BackfillPlanner.FloorToBucket(Utc(2026, 7, 31, 14, 59), TimeSpan.FromMinutes(15)));

        // Already on a boundary: flooring must be idempotent, or a run landing exactly on the
        // quarter hour would be attributed to the previous bucket and re-request the same window.
        var boundary = Utc(2026, 7, 31, 15, 0);
        Assert.Equal(boundary, BackfillPlanner.FloorToBucket(boundary, TimeSpan.FromMinutes(15)));
    }

    // ---- guards --------------------------------------------------------------------------------

    [Fact]
    public void A_runaway_target_range_is_truncated_rather_than_enqueuing_unbounded_work()
    {
        var job = Job(Utc(1800, 1, 1), Utc(2026, 7, 31));

        var slices = BackfillPlanner.PlanHistorical(job, 416904, null, CadenceOf(job));

        Assert.Equal(BackfillPlanner.MaxSlicesPerJob, slices.Count);
    }

    [Fact]
    public void An_inverted_target_range_plans_nothing_rather_than_looping()
    {
        var job = Job(Utc(2026, 1, 1), Utc(2024, 1, 1));

        Assert.Empty(BackfillPlanner.PlanHistorical(job, 416904, null, CadenceOf(job)));
    }
}
