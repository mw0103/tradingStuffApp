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
        // SPX 1-min is RTH-only at the source (an index price is computed during the regular session).
        new("spx-1min-trades", BackfillJobKinds.Historical, InstrumentId: 1, "SPX", "TRADES", "1 min",
            UseRth: true, SpxIntradayFrom, TargetTo: null, Priority: 100),
        new("spy-1min-trades", BackfillJobKinds.Historical, InstrumentId: 5, "SPY", "TRADES", "1 min",
            UseRth: true, SpyIntradayFrom, TargetTo: null, Priority: 90),
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
            UseRth: false, VixFrom, TargetTo: null, Priority: 70),
    ];
}
