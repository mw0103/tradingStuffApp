using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.Tests;

/// <summary>
/// The enumeration and range arithmetic <see cref="EsContractWalker"/> owns: no socket, no
/// database — everything here is a pure function of its fixture inputs.
/// </summary>
/// <remarks>
/// The one fact these tests exist to protect: an identical rerun of the walker must derive
/// byte-identical slices for every contract, and a contract's slices must never reach before its
/// own head or past its own last trading day. Getting either wrong either duplicates
/// <c>backfill_requests</c> rows on every restart (the exact defect
/// <see cref="BackfillPlannerTests"/> guards against for the single-conId planner) or, worse,
/// silently stitches one contract's request rows into a range that belongs to its neighbour on the
/// roll — which <c>research.bars</c>' <c>con_id</c> key would then have to disagree with silently.
/// </remarks>
public sealed class EsContractWalkerTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static BackfillJob EsJob(DateTimeOffset targetFrom, DateTimeOffset targetTo) => new(
        JobId: 42,
        Name: EsContractWalker.JobName,
        InstrumentId: 6,
        ConId: null,
        WhatToShow: "TRADES",
        BarSize: "1 min",
        UseRth: false,
        TargetFrom: targetFrom,
        TargetTo: targetTo,
        Priority: 60,
        Status: "running");

    private static SliceCadence CadenceOf(BackfillJob job) =>
        BackfillPlanner.CadenceFor(job) ?? throw new InvalidOperationException("The test job has no cadence.");

    /// <summary>
    /// The scan instant every window derivation below is anchored on. Named rather than inlined
    /// because the whole point of these tests is that a scan's slices are a function of the DAY it
    /// runs on, so "which now" is load-bearing in almost every one.
    /// </summary>
    private static readonly DateTimeOffset AnchorDay = Utc(2026, 7, 31);

    // ---- quarterly selection --------------------------------------------------------------------

    [Fact]
    public void Contracts_are_ordered_oldest_expiry_first()
    {
        var raw = new[]
        {
            new FuturesContractResolution(3, new DateOnly(2027, 3, 19), "ES", "CME", "USD"),
            new FuturesContractResolution(1, new DateOnly(2026, 9, 18), "ES", "CME", "USD"),
            new FuturesContractResolution(2, new DateOnly(2026, 12, 18), "ES", "CME", "USD"),
        };

        var selected = EsContractWalker.SelectContracts(raw);

        Assert.Equal([1, 2, 3], selected.Select(c => c.ConId));
    }

    [Fact]
    public void A_duplicate_conid_collapses_to_one_entry()
    {
        var raw = new[]
        {
            new FuturesContractResolution(1, new DateOnly(2026, 9, 18), "ES", "CME", "USD"),
            new FuturesContractResolution(1, new DateOnly(2026, 9, 18), "ES", "GLOBEX", "USD"),
        };

        var selected = EsContractWalker.SelectContracts(raw);

        Assert.Single(selected);
    }

    [Fact]
    public void An_empty_family_selects_nothing()
    {
        Assert.Empty(EsContractWalker.SelectContracts([]));
    }

    // ---- per-contract window derivation ----------------------------------------------------------

    [Fact]
    public void An_expired_contracts_slices_never_extend_past_its_own_last_trading_day()
    {
        // ESZ2 expired 2022-12-16; the job's declared target_to reaches all the way to today, so
        // only the contract's own expiry — not the job's, and not the scan's — may bound its newest
        // slice. The forward extension must not resurrect a contract that has stopped trading.
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var expiry = new DateOnly(2022, 12, 16);

        var slices = EsContractWalker.PlanContractWindow(
            job, conId: 555, expiry, Utc(2020, 6, 1), CadenceOf(job), AnchorDay.AddDays(400));

        Assert.NotEmpty(slices);
        var ceiling = new DateTimeOffset(expiry.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.All(slices, slice => Assert.True(slice.EndTimeUtc!.Value <= ceiling));
        Assert.Equal(ceiling, slices.Max(slice => slice.EndTimeUtc));
    }

    [Fact]
    public void A_contracts_slices_never_reach_before_its_own_head()
    {
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var head = Utc(2023, 8, 20); // the runtime-probed ESU6 head timestamp

        var slices = EsContractWalker.PlanContractWindow(
            job, conId: 495512563, new DateOnly(2026, 9, 18), head, CadenceOf(job), AnchorDay);

        var oldest = slices.Min(slice => slice.EndTimeUtc!.Value);
        Assert.True(oldest > head, "No slice should be planned entirely below the contract's own head.");
        Assert.True(oldest - TimeSpan.FromDays(1) <= head, "The oldest slice must still reach down to the head.");
    }

    [Fact]
    public void A_contract_that_expired_before_the_jobs_range_opens_plans_nothing()
    {
        // Not an inverted or negative range: zero slices. (Its mirror image — a contract still
        // listing when the job's frozen anchor was written — is NOT this case; see the forward-band
        // tests below, where planning nothing was the defect.)
        var job = EsJob(Utc(2020, 1, 1), Utc(2020, 6, 1));

        Assert.Empty(EsContractWalker.PlanContractWindow(
            job, conId: 1, new DateOnly(2019, 3, 15), Utc(2017, 1, 1), CadenceOf(job), Utc(2020, 6, 1)));
    }

    [Fact]
    public void A_null_head_still_clamps_to_the_jobs_declared_target_from()
    {
        var job = EsJob(Utc(2020, 1, 1), Utc(2020, 2, 1));

        var slices = EsContractWalker.PlanContractWindow(
            job, conId: 1, new DateOnly(2022, 12, 16), headTimestampUtc: null, CadenceOf(job), Utc(2020, 2, 1));

        Assert.NotEmpty(slices);
        Assert.True(slices.Min(slice => slice.EndTimeUtc!.Value) - TimeSpan.FromDays(1) <= job.TargetFrom);
    }

    // ---- the band in front of the frozen anchor ----------------------------------------------------
    //
    // target_to is fixed at the UTC midnight of the day the ES job row was created and never moves.
    // Clamping every contract to it meant the newest ES slice ever planned ended on creation day:
    // every minute bar after that was requested by nothing, forever, and the hole widened by a day
    // per day. PlanForward exists for exactly this and was wired into the coordinator's planner only,
    // which returns early for the NULL-conId job the ES walker owns — so ES never got it.

    [Fact]
    public void A_still_listing_contracts_newest_slice_advances_with_the_calendar()
    {
        // The reproduction, stated the way the failure was actually observed: the job's anchor is
        // frozen on creation day, the walker runs again 30 days later, and the question is whether
        // anything has asked for those 30 days.
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var cadence = CadenceOf(job);
        var frontMonth = new DateOnly(2026, 9, 18);

        var onCreationDay = EsContractWalker.PlanContractWindow(job, 495512563, frontMonth, Utc(2023, 8, 20), cadence, AnchorDay);
        var thirtyDaysLater = EsContractWalker.PlanContractWindow(
            job, 495512563, frontMonth, Utc(2023, 8, 20), cadence, AnchorDay.AddDays(30).AddHours(9));

        Assert.Equal(AnchorDay, onCreationDay.Max(slice => slice.EndTimeUtc));
        Assert.Equal(AnchorDay.AddDays(30), thirtyDaysLater.Max(slice => slice.EndTimeUtc));

        // ...and the earlier scan's slices are all still there, unchanged. A forward extension that
        // shifted existing boundaries would re-plan the whole job into a second, overlapping set of
        // request rows that the idempotency key cannot collapse.
        Assert.All(onCreationDay, slice => Assert.Contains(slice, thirtyDaysLater));
        Assert.Equal(30, thirtyDaysLater.Count - onCreationDay.Count);
    }

    [Fact]
    public void The_forward_band_stops_at_the_contracts_own_expiry_once_it_has_rolled()
    {
        // The roll. A contract that was still listing when the job row was created, and has since
        // expired, carries the band only as far as its own last trading day — the next quarter picks
        // it up from there. Getting this wrong would spend a paced request per day per dead contract.
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var expiry = new DateOnly(2026, 9, 18);

        var slices = EsContractWalker.PlanContractWindow(
            job, 495512563, expiry, Utc(2023, 8, 20), CadenceOf(job), Utc(2027, 1, 15));

        Assert.Equal(Utc(2026, 9, 19), slices.Max(slice => slice.EndTimeUtc));
    }

    [Fact]
    public void The_forward_band_never_reaches_below_a_head_that_postdates_the_anchor()
    {
        // A quarter that only STARTED trading after the job row was written. Everything between the
        // frozen anchor and its head is known-empty from the head timestamp alone, and planning it
        // would buy one confirmed-empty paced request per day to be told what we already knew.
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var head = Utc(2026, 9, 1);

        var slices = EsContractWalker.PlanContractWindow(
            job, 777, new DateOnly(2026, 12, 18), head, CadenceOf(job), Utc(2026, 10, 1));

        Assert.NotEmpty(slices);
        Assert.All(slices, slice => Assert.True(slice.EndTimeUtc!.Value > head));
        Assert.Equal(Utc(2026, 10, 1), slices.Max(slice => slice.EndTimeUtc));
    }

    // ---- determinism: the property that makes a rerun free -----------------------------------------

    [Fact]
    public void The_same_contract_plans_byte_identical_slices_twice()
    {
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var cadence = CadenceOf(job);
        var expiry = new DateOnly(2022, 12, 16);
        var head = Utc(2020, 6, 1);

        var first = EsContractWalker.PlanContractWindow(job, 555, expiry, head, cadence, AnchorDay);
        var second = EsContractWalker.PlanContractWindow(job, 555, expiry, head, cadence, AnchorDay);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(
            first.Select(s => (s.JobId, s.ConId, s.EndTimeUtc, s.Duration, s.WhatToShow, s.BarSize, s.UseRth)),
            second.Select(s => (s.JobId, s.ConId, s.EndTimeUtc, s.Duration, s.WhatToShow, s.BarSize, s.UseRth)));
    }

    [Fact]
    public void Two_scans_on_the_same_UTC_day_derive_the_identical_forward_band()
    {
        // The constraint the forward extension had to satisfy to be allowed at all: the ceiling is a
        // floored UTC midnight, never the scan instant, so a six-hourly rescan adds zero rows within
        // a day and exactly one slice across one.
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var cadence = CadenceOf(job);
        var frontMonth = new DateOnly(2026, 9, 18);

        var earlyScan = EsContractWalker.PlanContractWindow(
            job, 495512563, frontMonth, Utc(2023, 8, 20), cadence, AnchorDay.AddDays(3).AddHours(1));
        var lateScan = EsContractWalker.PlanContractWindow(
            job, 495512563, frontMonth, Utc(2023, 8, 20), cadence, AnchorDay.AddDays(3).AddHours(23).AddMinutes(59));

        Assert.Equal(earlyScan, lateScan);
    }

    [Fact]
    public void Different_contracts_never_share_a_conid_on_their_planned_slices()
    {
        // The invariant that keeps two contracts' backfill_requests rows from ever colliding under
        // the (job_id, con_id, end_time_utc, ...) idempotency key, and the same invariant that keeps
        // research.bars from ever attributing one contract's bar to another.
        var job = EsJob(Utc(2008, 1, 1), AnchorDay);
        var cadence = CadenceOf(job);

        var older = EsContractWalker.PlanContractWindow(job, 111, new DateOnly(2022, 12, 16), Utc(2020, 6, 1), cadence, AnchorDay);
        var newer = EsContractWalker.PlanContractWindow(job, 222, new DateOnly(2026, 9, 18), Utc(2023, 8, 20), cadence, AnchorDay);

        Assert.All(older, slice => Assert.Equal(111, slice.ConId));
        Assert.All(newer, slice => Assert.Equal(222, slice.ConId));
    }
}
