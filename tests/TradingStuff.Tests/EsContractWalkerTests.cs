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
        // only the contract's own expiry — not the job's — may bound its newest slice.
        var job = EsJob(Utc(2008, 1, 1), Utc(2026, 7, 31));
        var expiry = new DateOnly(2022, 12, 16);

        var slices = EsContractWalker.PlanContractWindow(job, conId: 555, expiry, Utc(2020, 6, 1), CadenceOf(job));

        Assert.NotEmpty(slices);
        var ceiling = new DateTimeOffset(expiry.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.All(slices, slice => Assert.True(slice.EndTimeUtc!.Value <= ceiling));
        Assert.Equal(ceiling, slices.Max(slice => slice.EndTimeUtc));
    }

    [Fact]
    public void A_contracts_slices_never_reach_before_its_own_head()
    {
        var job = EsJob(Utc(2008, 1, 1), Utc(2026, 7, 31));
        var head = Utc(2023, 8, 20); // the runtime-probed ESU6 head timestamp

        var slices = EsContractWalker.PlanContractWindow(
            job, conId: 495512563, new DateOnly(2026, 9, 18), head, CadenceOf(job));

        var oldest = slices.Min(slice => slice.EndTimeUtc!.Value);
        Assert.True(oldest > head, "No slice should be planned entirely below the contract's own head.");
        Assert.True(oldest - TimeSpan.FromDays(1) <= head, "The oldest slice must still reach down to the head.");
    }

    [Fact]
    public void A_currently_active_contracts_ceiling_is_the_jobs_target_to_not_its_own_future_expiry()
    {
        // The current front-month quarter has not expired yet; its own "ceiling" is nominally years
        // away, so the job's declared target_to is what actually bounds it — the same clamp an
        // ordinary single-conId job gets from BackfillPlanner directly.
        var targetTo = Utc(2026, 7, 31);
        var job = EsJob(Utc(2008, 1, 1), targetTo);

        var slices = EsContractWalker.PlanContractWindow(
            job, conId: 495512563, new DateOnly(2026, 9, 18), Utc(2023, 8, 20), CadenceOf(job));

        Assert.Equal(targetTo, slices.Max(slice => slice.EndTimeUtc));
    }

    [Fact]
    public void A_contract_whose_window_is_entirely_outside_the_jobs_range_plans_nothing()
    {
        // A quarter CME lists years ahead of its own expiry, with a job target_to that predates it
        // entirely (or a contract that expired before target_from) plans zero slices, not a negative
        // or inverted range.
        var job = EsJob(Utc(2008, 1, 1), Utc(2010, 1, 1));

        Assert.Empty(EsContractWalker.PlanContractWindow(
            job, conId: 1, new DateOnly(2026, 9, 18), Utc(2023, 8, 20), CadenceOf(job)));
    }

    [Fact]
    public void A_null_head_still_clamps_to_the_jobs_declared_target_from()
    {
        var job = EsJob(Utc(2020, 1, 1), Utc(2020, 2, 1));

        var slices = EsContractWalker.PlanContractWindow(
            job, conId: 1, new DateOnly(2022, 12, 16), headTimestampUtc: null, CadenceOf(job));

        Assert.NotEmpty(slices);
        Assert.True(slices.Min(slice => slice.EndTimeUtc!.Value) - TimeSpan.FromDays(1) <= job.TargetFrom);
    }

    // ---- determinism: the property that makes a rerun free -----------------------------------------

    [Fact]
    public void The_same_contract_plans_byte_identical_slices_twice()
    {
        var job = EsJob(Utc(2008, 1, 1), Utc(2026, 7, 31));
        var cadence = CadenceOf(job);
        var expiry = new DateOnly(2022, 12, 16);
        var head = Utc(2020, 6, 1);

        var first = EsContractWalker.PlanContractWindow(job, 555, expiry, head, cadence);
        var second = EsContractWalker.PlanContractWindow(job, 555, expiry, head, cadence);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(
            first.Select(s => (s.JobId, s.ConId, s.EndTimeUtc, s.Duration, s.WhatToShow, s.BarSize, s.UseRth)),
            second.Select(s => (s.JobId, s.ConId, s.EndTimeUtc, s.Duration, s.WhatToShow, s.BarSize, s.UseRth)));
    }

    [Fact]
    public void Different_contracts_never_share_a_conid_on_their_planned_slices()
    {
        // The invariant that keeps two contracts' backfill_requests rows from ever colliding under
        // the (job_id, con_id, end_time_utc, ...) idempotency key, and the same invariant that keeps
        // research.bars from ever attributing one contract's bar to another.
        var job = EsJob(Utc(2008, 1, 1), Utc(2026, 7, 31));
        var cadence = CadenceOf(job);

        var older = EsContractWalker.PlanContractWindow(job, 111, new DateOnly(2022, 12, 16), Utc(2020, 6, 1), cadence);
        var newer = EsContractWalker.PlanContractWindow(job, 222, new DateOnly(2026, 9, 18), Utc(2023, 8, 20), cadence);

        Assert.All(older, slice => Assert.Equal(111, slice.ConId));
        Assert.All(newer, slice => Assert.Equal(222, slice.ConId));
    }
}
