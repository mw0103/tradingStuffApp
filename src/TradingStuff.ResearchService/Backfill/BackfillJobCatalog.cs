using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>
/// A job the platform wants filled in, before it has a row in <c>research.backfill_jobs</c>.
/// </summary>
/// <param name="Symbol">
/// Resolved to a conId through the gateway once, then persisted on the job row. Contract SHAPE
/// (secType, exchange, currency) comes from <c>research.instruments</c>, not from here — see
/// <see cref="InstrumentRow.ContractFor"/> for why that indirection is load-bearing.
/// </param>
/// <param name="TargetTo">
/// NULL means "the UTC midnight of the day this job row is first created", fixed from then on.
/// Every slice boundary is measured back from this instant, so it must never move: if it did, the
/// whole grid would shift and re-planning would land a second, overlapping set of request rows that
/// the idempotency key cannot collapse.
/// </param>
public sealed record BackfillJobDefinition(
    string Name,
    string Kind,
    short InstrumentId,
    string Symbol,
    string WhatToShow,
    string BarSize,
    bool UseRth,
    DateTimeOffset TargetFrom,
    DateTimeOffset? TargetTo,
    int Priority,
    string? SliceDuration = null);

/// <summary>
/// The jobs this phase seeds, in the roadmap's priority order.
/// </summary>
/// <remarks>
/// <para>
/// Priorities put the top-ups above everything historical on purpose. The historical drain is days
/// of paced requests; a single shared priority would let it starve the 15-minute tail for as long as
/// it runs, and the tail is the half that has to stay current.
/// </para>
/// <para>
/// <b>ES is deliberately absent.</b> Deep ES intraday cannot be requested as one contract — CONTFUT
/// rejects a past <c>endDateTime</c> with error 10339, so it must walk individual expired
/// quarterlies — and package 2e owns that walker. Nothing here precludes it: a job row with a NULL
/// <c>con_id</c> is skipped by this planner (its walker supplies the per-contract slices) but is
/// still claimed, executed, landed, and reported by everything else in this package, because a
/// request's contract is rebuilt from <c>research.instruments</c> plus the request row's own conId
/// rather than from a per-job template. The walker adds a job row and its request rows; no code
/// here needs to change.
/// </para>
/// <para>
/// <b>Weekends and holidays are not skipped.</b> A closed-market slice costs exactly one paced
/// request, once, and settles as <c>empty</c> — a first-class outcome that is never retried. The
/// alternative, teaching this planner which days a market trades, means either duplicating the
/// session calendar another package owns or hard-coding a rule; and a rule that is wrong in the
/// skipping direction silently loses data no gap report can find. Paying ~2/7 more requests on the
/// one-time historical drain is the cheaper mistake.
/// </para>
/// </remarks>
public static class BackfillJobCatalog
{
    /// <summary>SPX 1-min head is 2004-03-04, but only 2010 onward is runtime-verified to actually serve.</summary>
    private static readonly DateTimeOffset SpxIntradayFrom = new(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SpyIntradayFrom = new(2005, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset VixFrom = new(2005, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A top-up job has no walked range; its far end is nominal and only exists because the column is
    /// NOT NULL. 2035 matches the top of <c>research.bars</c>'s pre-created yearly partitions.
    /// </summary>
    private static readonly DateTimeOffset TopUpHorizon = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<BackfillJobDefinition> Definitions { get; } =
    [
        // ---- the recent tail, every 15 minutes -------------------------------------------------
        new("spx-1min-trades-topup", BackfillJobKinds.TopUp, InstrumentId: 1, "SPX", "TRADES", "1 min",
            UseRth: true, VixFrom, TopUpHorizon, Priority: 1000),
        new("spy-1min-trades-topup", BackfillJobKinds.TopUp, InstrumentId: 5, "SPY", "TRADES", "1 min",
            UseRth: true, VixFrom, TopUpHorizon, Priority: 1000),
        // VIX intraday is requested with useRth=false: the capability matrix records GTH values
        // present in VIX 1-min bars from 02:15 CT, which useRth=true would discard.
        new("vix-1min-trades-topup", BackfillJobKinds.TopUp, InstrumentId: 4, "VIX", "TRADES", "1 min",
            UseRth: false, VixFrom, TopUpHorizon, Priority: 1000),

        // ---- the historical drain, in roadmap order ---------------------------------------------
        //
        // SliceDuration "4 W" on every 1-minute historical drain, and the size is measured rather
        // than chosen. Against live paper TWS on 2026-08-02, SPX 1-minute TRADES at a 2019 anchor:
        //
        //     1 D  ->    390 bars ( 1 session)  in  0s
        //     5 D  ->  1,950 bars ( 5 sessions) in  1s
        //     1 M  ->  8,580 bars (22 sessions) in  0s
        //     2 M  -> 16,770 bars (43 sessions) in 42s
        //     6 M  -> 504, TWS did not answer in time
        //
        // The pacing limit counts REQUESTS, not bars, so a day per request was spending the scarce
        // resource ~20x faster than necessary: SPX 2010-> was 6,056 requests and becomes ~300. The
        // planner's own note said raising this was "a per-job slice_duration change, not a code
        // change" and deferred the maximum to a probe; this is that probe.
        //
        // "4 W" rather than the "1 M" that was measured, because TryParseCadence accepts only D, W
        // and Y — months name no fixed-length boundary grid the planner can walk deterministically,
        // and it refuses (marking the job failed) rather than silently substituting a cadence nobody
        // asked for. That refusal is correct and it caught this exact mistake. 4 W is 28 days,
        // bracketed by two measured-good durations, so it buys essentially the 1 M win.
        //
        // 2 M is rejected despite working. 43 sessions in 42s is worse throughput per unit time than
        // 22 in ~0s, and it sits next to the duration where TWS stops answering — a cliff worth
        // staying well clear of on a multi-hour unattended drain.
        //
        // What this gives up, stated because CadenceForBarSize was deliberate about it: a one-day
        // cadence put every slice boundary on a UTC midnight, which for Cboe products never splits a
        // session (SPX/SPXW overnight opens 19:15 CT, after UTC midnight). A month-long slice has no
        // such alignment, so a boundary can land mid-session. That is safe rather than merely
        // tolerable: adjacent requests still cover the whole range between them, and research.bars'
        // primary key makes any bar returned by both a no-op. Gap detection is what would catch it
        // if that reasoning is ever wrong.
        new("spx-1min-trades", BackfillJobKinds.Historical, InstrumentId: 1, "SPX", "TRADES", "1 min",
            UseRth: true, SpxIntradayFrom, TargetTo: null, Priority: 100, SliceDuration: "4 W"),
        new("spy-1min-trades", BackfillJobKinds.Historical, InstrumentId: 5, "SPY", "TRADES", "1 min",
            UseRth: true, SpyIntradayFrom, TargetTo: null, Priority: 90, SliceDuration: "4 W"),
        // Priority 200 — ABOVE the 1-minute drains, which is not a typo and not a preference.
        //
        // This job is 22 slices; spx-1min-trades is 6,056 and spy-1min-trades 7,882. At priority 80
        // it sat behind both and would not have run for days. Meanwhile the volatility-residual
        // study cannot score a single day without it: HAR-X and the calibrated-VIX baseline (B1)
        // both take a VIX close, so a complete SPX history with no VIX is worth exactly nothing to
        // the study. Twenty-two slices gating thousands is the wrong way round.
        //
        // Ordering cheap-and-blocking ahead of expensive-and-incremental is the general rule here;
        // priority is doing dependency work that nothing else expresses.
        new("vix-daily-trades", BackfillJobKinds.Historical, InstrumentId: 4, "VIX", "TRADES", "1 day",
            UseRth: true, VixFrom, TargetTo: null, Priority: 200),
        // The "probe to the floor" job: VIX intraday depth is UNKNOWN in the capability matrix, so
        // target_from is set optimistically deep and reqHeadTimeStamp decides where planning really
        // starts. Clamping to the head IS the probe — no separate mode, no separate code path.
        new("vix-1min-trades", BackfillJobKinds.Historical, InstrumentId: 4, "VIX", "TRADES", "1 min",
            UseRth: false, VixFrom, TargetTo: null, Priority: 70, SliceDuration: "4 W"),
    ];
}
