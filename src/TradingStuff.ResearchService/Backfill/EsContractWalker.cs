using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>
/// One futures-family contract as the walker plans it: its conId and last trading day, independent
/// of where those facts came from — the live gateway enumeration, or a fixture in a test.
/// </summary>
public sealed record EsContractCandidate(int ConId, DateOnly LastTradeDateOrContractMonth);

/// <summary>One scan's outcome, for logging and for tests to assert on.</summary>
/// <param name="ContractCount">How many contracts this scan's family enumeration returned.</param>
/// <param name="UnplannedContractCount">
/// How many of those the scan could NOT plan <b>this pass but expects to plan on a later one</b> — a
/// head probe that was paced away or hit a disconnected gateway, or a per-contract failure. The
/// number that has to exist for this walker to be able to tell "finished" from "barely started": a
/// skipped contract writes no request rows, so it lowers no count in
/// <c>research.backfill_requests</c> and is invisible to every completion query.
/// <para>
/// Deliberately NOT the same thing as <paramref name="NoDataContractCount"/>. Conflating the two is
/// what made this counter useless in the steady state: CME lists ES quarters years before they
/// trade, TWS answers their head probe with a definitive "no data", and counting that as "could not
/// plan it" left the count permanently non-zero — so the job could never complete, and the warning
/// this drives looked identical whether four contracts were listed-but-untraded (normal) or
/// twenty-five had been paced away (the catastrophe the counter exists for).
/// </para>
/// </param>
/// <param name="NoDataContractCount">
/// How many contracts TWS answered definitively: there is no history to plan. Terminal for this
/// probe, not for the contract — the verdict is cached for
/// <see cref="BackfillOptions.HeadTimestampMaxAgeDays"/> and then re-probed, because a listed
/// quarter does eventually start trading. Reported rather than silently dropped, so "26 of 29
/// contracts have no data" cannot pass for a finished walk.
/// </param>
/// <param name="ForgottenContractCount">
/// How many contracts have request rows from an earlier scan but were absent from THIS scan's
/// enumeration. Non-zero means the family listing came back incomplete, which is the one way this
/// walker's expected-contract set can shrink without anything being wrong with the contracts.
/// </param>
/// <param name="NewestPlannedEndUtc">
/// The newest <c>end_time_utc</c> this job has in <c>research.backfill_requests</c> after the scan,
/// read back from the table rather than inferred from what this pass planned. NULL means the job has
/// no request rows at all, which is a shortfall, never health.
/// </param>
/// <param name="ForwardCoverageComplete">
/// Whether <paramref name="NewestPlannedEndUtc"/> reaches the newest instant any contract planned in
/// this scan could cover — the last completed UTC day for a contract that is still listing, its own
/// expiry for one that is not. The negative claim "nothing after the job's frozen <c>target_to</c>
/// is missing", measured on the request table. See <see cref="EsContractWalker.PlanContractWindow"/>.
/// </param>
/// <param name="PlanningComplete">
/// True only when every contract this walker knows about — enumerated now, or planned before — was
/// planned or definitively explained in this scan, AND the planned rows reach forward to the present.
/// The gate on declaring the job finished.
/// </param>
public sealed record EsWalkResult(
    int ContractCount,
    int SlicesPlanned,
    int SlicesInserted,
    int UnplannedContractCount = 0,
    int NoDataContractCount = 0,
    int ForgottenContractCount = 0,
    DateTimeOffset? NewestPlannedEndUtc = null,
    bool ForwardCoverageComplete = false,
    bool PlanningComplete = false)
{
    public static readonly EsWalkResult Empty = new(0, 0, 0);
}

/// <summary>
/// Seeds the ES backfill job's per-contract request rows by walking every quarterly contract IBKR
/// lists for the ES futures family, expired and current alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a family walk, not the CONTFUT job every other instrument gets.</b> A CONTFUT rejects a
/// past <c>endDateTime</c> with error 10339 (RUNTIME-verified — see
/// docs/research/ibkr-data-capability-matrix.md constraint 3) and cannot page backward at all. Deep
/// ES intraday history is therefore only reachable by requesting each individual quarterly contract
/// directly, with <c>Contract.IncludeExpired = true</c>, each within its own listing window. IBKR
/// documents roughly two years of servable history per contract post-expiry; a runtime probe of
/// ESU6 measured its head timestamp at 2023-08-20 (~3 years) — treat two years as the floor and
/// discover the real head per contract, which is exactly what <see cref="ResolveContractHeadAsync"/>
/// does rather than assuming either number.
/// </para>
/// <para>
/// <b>This class only ever writes rows the coordinator already knows how to drain.</b> It never
/// claims, leases, or executes a slice — <see cref="BackfillCoordinator"/> owns that machinery
/// unmodified. A request row's contract is rebuilt from <c>research.instruments</c> plus the row's
/// own <c>con_id</c> (see <see cref="InstrumentRow.ContractFor"/>), not from a per-job template, and
/// <see cref="BackfillJobCatalog"/> already documents the other half: "a job row with a NULL conId
/// is skipped by [the coordinator's] planner ... The walker adds a job row and its request rows; no
/// code here needs to change." This class is that walker. Integration is structural — seed the
/// rows, and the existing drain loop does the rest — not a hook into the coordinator's loop.
/// </para>
/// <para>
/// <b>Per-contract head discovery gates planning; it does not merely clamp it.</b>
/// <see cref="BackfillCoordinator"/>'s per-job fallback — plan the full declared range unclamped
/// when a head timestamp cannot be resolved — is correct for a job with one fixed conId, but wrong
/// here: CME lists ES quarters roughly two years ahead of their own expiry, and a not-yet-traded
/// contract with an unclamped floor reaching back to <see cref="BackfillJob.TargetFrom"/> would plan
/// thousands of slices this walker already knows will all come back empty. A contract whose head
/// cannot be resolved this pass is skipped entirely — not planned with a wide-open floor — and
/// retried on the next scan.
/// </para>
/// <para>
/// <b>Forward planning belongs HERE, not in the coordinator.</b>
/// <see cref="BackfillCoordinator.PlanJobAsync"/> returns before it reaches
/// <see cref="BackfillPlanner.PlanForward"/> for any job with a NULL <c>con_id</c> — which is exactly
/// this job — so the fix that closed the "nothing planned the band after <c>target_to</c>" hole for
/// SPX/SPY/VIX never reached ES at all: the newest ES slice ever planned ended at the frozen anchor,
/// and the missing band widened by a day per day, indefinitely, surviving the contract roll. It could
/// not be fixed on the coordinator's side either, because forward planning for a walked job is
/// per-contract: the band belongs to whichever contract is still listing when it elapses, and only
/// this class knows which one that is. <see cref="PlanContractWindow"/> therefore extends each
/// contract's own window forward, bounded by that contract's expiry, and
/// <see cref="SeedAsync"/> then asserts the result against
/// <c>research.backfill_requests</c> rather than trusting it.
/// </para>
/// <para>
/// <b>ES gets no top-up job, deliberately.</b> The catalog's 15-minute top-ups all resolve one fixed
/// conId by symbol, which is precisely what a rolling futures family cannot do — the front month is a
/// different contract every quarter, and resolving it is this walker's job, not
/// <c>BackfillJobCatalog</c>'s. Adding one would mean either a second front-month resolver or top-up
/// rows inside a historical job, and it buys only the current partial day: forward planning already
/// covers every completed UTC day, so ES minute bars are at worst one day plus one
/// <see cref="RescanInterval"/> behind. ES is a deep-history input to the futures-vs-index study, not
/// a live tail — the live tail is what the Track B recorder is for. If ES latency ever matters, the
/// honest fix is a front-month top-up owned by this class, not a catalog entry.
/// </para>
/// <para>
/// <b>Never stitches bars across contract boundaries.</b> This is enforced structurally, not by
/// anything in this class: <c>research.bars</c> is keyed on <c>con_id</c>, so continuity across
/// rolled contracts is an explicit join a later reader performs, never an implicit splice here. This
/// walker never invents a synthetic conId and never attributes one contract's bars to another.
/// </para>
/// </remarks>
public sealed class EsContractWalker(
    BackfillStore store,
    IbkrGatewayClient gateway,
    IOptions<BackfillOptions> options,
    ILogger<EsContractWalker> logger)
    : BackgroundService
{
    private readonly BackfillOptions _options = options.Value;

    public const string JobName = "es-1min-trades";

    private const short EsInstrumentId = 6; // research.instruments seed row: ES, future_family, CME/USD.
    private const string EsSymbol = "ES";
    private const string EsExchange = "CME";
    private const string EsCurrency = "USD";

    /// <summary>How often the family is re-enumerated and every contract's plan re-derived.</summary>
    /// <remarks>
    /// Re-deriving is cheap and safe to repeat: <see cref="BackfillStore.InsertSlicesAsync"/>'s
    /// <c>ON CONFLICT DO NOTHING</c> is the entire resumability story, so a re-scan of an unchanged
    /// family costs a handful of read queries and lands zero new rows. The cadence only needs to be
    /// often enough to notice a newly-listed CME quarter or a head timestamp that has aged out of
    /// <see cref="BackfillOptions.HeadTimestampMaxAgeDays"/>; matches
    /// <see cref="BackfillOptions.HistoricalPlanIntervalHours"/>'s default.
    /// </remarks>
    public static readonly TimeSpan RescanInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Deliberately deep: 2008 predates every ES contract this account's HMDS retention could
    /// plausibly still serve. Nothing about correctness depends on this being tight — each
    /// contract's own <see cref="ResolveContractHeadAsync"/> probe is what actually decides where
    /// its slices start (the "probe to the floor" pattern <c>BackfillJobCatalog</c> already applies
    /// to VIX's intraday job: clamping to the head IS the discovery, not a separate mode).
    /// </summary>
    private static readonly DateTimeOffset EsIntradayFrom = new(2008, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly BackfillJobDefinition JobDefinition = new(
        JobName, BackfillJobKinds.Historical, EsInstrumentId, EsSymbol, "TRADES", "1 min",
        // ES trades nearly continuously and the capability matrix records overnight/GTH bars
        // present for it; useRth=true would discard them — the same reasoning BackfillJobCatalog
        // already applies to VIX's intraday job.
        UseRth: false, EsIntradayFrom, TargetTo: null, Priority: 60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation(
                "Backfill is disabled; the ES contract walker will not seed request rows. " +
                "Set Backfill__Enabled=true to start it.");
            return;
        }

        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            logger.LogWarning("No 'trading' connection string; the ES contract walker cannot run.");
            return;
        }

        logger.LogInformation("ES contract walker starting; rescanning every {Interval}.", RescanInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PlanOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ES contract walker scan failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(RescanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One full scan: ensure the job row, enumerate the family via the gateway, plan and insert every contract.</summary>
    internal async Task<EsWalkResult> PlanOnceAsync(CancellationToken cancellationToken)
    {
        var job = await store.EnsureJobAsync(JobDefinition, conId: null, cancellationToken);

        if (job is null)
        {
            logger.LogWarning("Could not ensure the {Job} job row; skipping this scan.", JobName);
            return EsWalkResult.Empty;
        }

        // An operator pause is respected the same way BackfillCoordinator.PlanAsync respects it for
        // catalog jobs — everything except 'paused' still gets (re)planned.
        if (job.Status == "paused")
        {
            return EsWalkResult.Empty;
        }

        var raw = await gateway.GetFuturesFamilyAsync(EsSymbol, EsExchange, EsCurrency, cancellationToken);
        var contracts = SelectContracts(raw);

        if (contracts.Count == 0)
        {
            logger.LogDebug("No contracts returned for the {Symbol} futures family this scan.", EsSymbol);
            return EsWalkResult.Empty;
        }

        return await SeedAsync(job, contracts, DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>
    /// Plans and inserts every contract's slices for an already-enumerated contract list.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="PlanOnceAsync"/> so the seeding pipeline and its rerun-idempotency
    /// guarantee can be exercised against a real Postgres instance with a fixture contract list and
    /// no TWS socket in the loop: as long as every contract's head timestamp is already cached in
    /// <c>research.capability_probes</c> (via <see cref="BackfillStore.RecordHeadTimestampAsync"/>,
    /// exactly as a prior scan would have left it), <see cref="ResolveContractHeadAsync"/> never
    /// calls the gateway at all.
    /// </remarks>
    /// <param name="nowUtc">
    /// The scan instant, passed in rather than read here so a test can advance days without waiting
    /// them out — and so the ONE clock reading a scan makes is shared by the forward planning and by
    /// the check that verifies it, which would otherwise be able to disagree across a midnight.
    /// </param>
    internal async Task<EsWalkResult> SeedAsync(
        BackfillJob job,
        IReadOnlyList<EsContractCandidate> contracts,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (contracts.Count == 0)
        {
            return EsWalkResult.Empty;
        }

        var instrument = await store.GetInstrumentAsync(job.InstrumentId, cancellationToken);

        if (instrument is null)
        {
            logger.LogError(
                "Job {Job} names instrument {InstrumentId}, which is not in research.instruments; skipping this scan.",
                job.Name, job.InstrumentId);
            return EsWalkResult.Empty;
        }

        if (BackfillPlanner.CadenceFor(job) is not { } cadence)
        {
            // Refusing beats guessing, the same rule BackfillCoordinator.PlanJobAsync applies: a job
            // planned at a duration nobody asked for lands request rows that permanently mismatch
            // the operator's intent, invisibly.
            logger.LogError(
                "Job {Job} names a slice duration ('{Duration}') or bar size ('{BarSize}') this planner " +
                "cannot put on a boundary grid; refusing to plan it.",
                job.Name, job.SliceDuration ?? "(derived)", job.BarSize);
            return EsWalkResult.Empty;
        }

        var planned = 0;
        var inserted = 0;
        var unplanned = new List<int>();
        var noData = new List<int>();

        // The newest instant this scan's PLANNED contracts could reach between them. Built from the
        // contracts that actually got planned, never from the enumeration: a quarter that is listed
        // but has never traded has a ceiling years out and no slices at all, and letting it into this
        // figure would demand coverage nothing can supply and hold the job open forever — the very
        // conflation this scan's tri-state head verdict exists to undo.
        DateTimeOffset? reachable = null;

        foreach (var contract in contracts)
        {
            try
            {
                var head = await ResolveContractHeadAsync(job, instrument, contract, cancellationToken);

                if (head.Verdict == HeadVerdict.Unresolved)
                {
                    // Pacing or a disconnected gateway: TWS has said nothing about this contract yet,
                    // so the next scan tries again. Recorded, not merely skipped: this is the ONLY
                    // record that the contract exists at all, since it writes no request rows.
                    unplanned.Add(contract.ConId);
                    continue;
                }

                if (head.Verdict == HeadVerdict.NoDataYet)
                {
                    // TWS answered, and the answer was "nothing here" — for ES, almost always a
                    // quarter CME lists years ahead of its own expiry that has not traded yet. That
                    // is a conclusion about this contract, not a failure of this scan, so it must not
                    // hold the job open; the verdict is cached and re-probed monthly by
                    // ResolveContractHeadAsync rather than re-asked every six hours.
                    noData.Add(contract.ConId);
                    continue;
                }

                var slices = PlanContractWindow(
                    job, contract.ConId, contract.LastTradeDateOrContractMonth, head.HeadUtc, cadence, nowUtc);

                // Zero slices with a RESOLVED head is a conclusion, not a skip: the contract's own
                // window falls entirely outside the job's declared range. It is planned as far as
                // this walker is ever going to plan it, so it must not hold the job open forever.
                planned += slices.Count;
                inserted += await store.InsertSlicesAsync(slices, cancellationToken);

                var ceiling = PlannableCeiling(contract.LastTradeDateOrContractMonth, nowUtc);

                if (reachable is not { } current || ceiling > current)
                {
                    reachable = ceiling;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One contract's failure must not stop the others — the same isolation
                // BackfillCoordinator.PlanAsync applies per catalog job, applied here per contract.
                unplanned.Add(contract.ConId);
                logger.LogError(
                    ex, "Planning {Job} contract {ConId} failed; other contracts are unaffected.", job.Name, contract.ConId);
            }
        }

        // The durable half of the expectation. A contract this walker planned on an earlier scan and
        // that today's family enumeration did not return means the LISTING came back short, not that
        // the contract stopped existing — and without this query the walker's idea of "every
        // contract" is only ever the list it was handed a moment ago.
        var enumerated = contracts.Select(c => c.ConId).ToHashSet();
        var forgotten = (await store.GetPlannedConIdsAsync(job.JobId, cancellationToken))
            .Where(conId => !enumerated.Contains(conId))
            .ToArray();

        // The negative claim — "nothing this walker was supposed to request is missing from the
        // recent end" — measured on research.backfill_requests, the table the claim is ABOUT, and
        // read back rather than inferred from the slice list this pass just built. Inferring it
        // would only re-assert PlanContractWindow's own arithmetic; reading it also covers an insert
        // that silently landed nothing.
        //
        // NULL is the case that matters: a job with no request rows at all cannot produce a maximum,
        // and a missing maximum must read as a shortfall. That is the same absent-row trap the Phase 1
        // review found three times over, and the reason this is a comparison against `reachable`
        // rather than a `newest is not null` check.
        var newestPlanned = await store.GetNewestPlannedEndAsync(job.JobId, cancellationToken);
        var forwardCoverageComplete = reachable is not { } required
                                   || (newestPlanned is { } newest && newest >= required);

        var planningComplete = unplanned.Count == 0 && forgotten.Length == 0 && forwardCoverageComplete;

        // planningComplete is the whole point of this call. IsJobSettledAsync counts rows in
        // research.backfill_requests, and a contract that was skipped produces NO rows — so it
        // cannot lower any count, and "total > 0" proves that ONE contract was planned, never that
        // all of them were. The previous version inferred completion from exactly that: with 29 ES
        // quarterlies and this job holding the lowest priority in the system, four contracts
        // planning while head probes for the other 25 were paced away was enough to report the job
        // complete at 100% with ~85% of the intended history never requested.
        await store.RefreshJobStatusAsync(job.JobId, _options.MaxAttempts, planningComplete, cancellationToken);

        if (!forwardCoverageComplete)
        {
            // Loud on its own, and separate from the warning below, because this is the failure that
            // is otherwise indistinguishable from health: every planned slice succeeds, the job reads
            // 100% complete, and the band nothing requested simply widens. GapDetector cannot see it
            // either — it refuses a NULL-conId job outright — so this log and the job status it
            // forces are the whole alarm.
            logger.LogCritical(
                "ES contract walk for {Job}: the newest planned slice ends {Newest:O}, which is short of the " +
                "{Required:O} its planned contracts can reach. Every minute bar after that instant is requested " +
                "by nothing, and the shortfall grows by a day per day until this is resolved.",
                job.Name, newestPlanned, reachable);
        }

        if (planningComplete)
        {
            logger.LogInformation(
                "ES contract walk for {Job}: all {ContractCount} contract(s) accounted for ({NoData} listed but " +
                "not yet trading), {Planned} slice(s), {Inserted} new, planned through {Newest:O}.",
                job.Name, contracts.Count, noData.Count, planned, inserted, newestPlanned);
        }
        else
        {
            logger.LogWarning(
                "ES contract walk for {Job}: {Planned} slice(s) planned ({Inserted} new), but {Unplanned} of " +
                "{ContractCount} enumerated contract(s) could not be probed this scan ({NoData} more have no data " +
                "yet, which is expected) and {Forgotten} previously planned contract(s) were missing from the " +
                "family listing. The job stays open; it will be retried in {Interval}.",
                job.Name, planned, inserted, unplanned.Count, contracts.Count, noData.Count, forgotten.Length,
                RescanInterval);
        }

        return new EsWalkResult(
            contracts.Count, planned, inserted, unplanned.Count, noData.Count, forgotten.Length,
            newestPlanned, forwardCoverageComplete, planningComplete);
    }

    /// <summary>
    /// The distinct contracts to walk, sorted oldest-expiry-first for deterministic, readable scan
    /// logs. TWS should never return two rows for the same conId from one <c>reqContractDetails</c>
    /// call, but folding a duplicate rather than trusting that is a one-line defensive cost.
    /// </summary>
    internal static IReadOnlyList<EsContractCandidate> SelectContracts(IReadOnlyList<FuturesContractResolution> contracts) =>
        contracts
            .Select(c => new EsContractCandidate(c.ConId, c.LastTradeDateOrContractMonth))
            .DistinctBy(c => c.ConId)
            .OrderBy(c => c.LastTradeDateOrContractMonth)
            .ThenBy(c => c.ConId)
            .ToArray();

    /// <summary>
    /// The slices covering one contract's own window: from its real head timestamp (or the job's
    /// declared floor, if later) through the day after its own last trading day, or — for a contract
    /// that has not expired — through the last completed UTC day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and deterministic given its arguments — nothing here reads the clock, the database, or
    /// configuration — which is what lets an identical rerun of the walker add zero new
    /// <c>backfill_requests</c> rows: the same (job, contract, head, cadence, day) always produces
    /// the same slices. Reuses <see cref="BackfillPlanner.PlanHistorical"/> unmodified by handing it
    /// a job record whose <c>TargetTo</c> is clamped to this contract's own ceiling; the
    /// head-timestamp clamp on the low end is already exactly what this needs, so the walker adds
    /// only the expiry clamp on the high end rather than re-deriving slice arithmetic
    /// <c>BackfillPlanner</c> already owns.
    /// </para>
    /// <para>
    /// <b>The forward half is why <paramref name="nowUtc"/> exists.</b> A job's <c>target_to</c> is
    /// frozen at the UTC midnight of its creation day and never moves — that is what makes lowering
    /// <c>target_from</c> a pure extension of the grid — so clamping every contract to it means the
    /// newest slice this walker ever plans ends on the day the job row was written. For an expired
    /// contract that is right and the clamp does nothing; for one still listing it left every bar
    /// after the creation day requested by NOTHING, forever, growing by a day per day and surviving
    /// the roll onto the next quarter. <see cref="BackfillPlanner.PlanForward"/> closed exactly this
    /// hole for single-conId jobs and could never reach ES, because the coordinator's planner returns
    /// early for a NULL-conId job. It is applied here per contract instead, bounded by that
    /// contract's own expiry, so a rolled contract stops at its last trading day and only whichever
    /// contract is still listing carries the band up to the present.
    /// </para>
    /// </remarks>
    /// <param name="nowUtc">
    /// The scan instant. Only its UTC DATE is used (via <see cref="BackfillPlanner.PlanForward"/>'s
    /// own flooring), so two scans on the same day derive the identical slice set and add zero rows,
    /// and crossing midnight adds exactly one day's worth.
    /// </param>
    internal static IReadOnlyList<BackfillSlice> PlanContractWindow(
        BackfillJob job, int conId, DateOnly lastTradeDate, DateTimeOffset? headTimestampUtc, SliceCadence cadence,
        DateTimeOffset nowUtc)
    {
        // Inclusive of the whole last trading day: a contract can print right up to its close, and a
        // slice reaching a little past midnight the day after costs nothing — TWS returns whatever
        // it actually has up to the requested end, exactly like BackfillPlanner's own leading slice
        // that over-reaches backward past its grid boundary.
        var contractCeiling = ContractCeiling(lastTradeDate);

        var targetTo = job.TargetTo.ToUniversalTime();
        var targetFrom = job.TargetFrom.ToUniversalTime();
        var clampedTargetTo = contractCeiling < targetTo ? contractCeiling : targetTo;

        var backward = clampedTargetTo > targetFrom
            ? BackfillPlanner.PlanHistorical(job with { TargetTo = clampedTargetTo }, conId, headTimestampUtc, cadence)
            : [];

        if (contractCeiling <= targetTo)
        {
            // Already expired when this job row was created: its window closed before the frozen
            // anchor, so there is no band in front of it and PlanForward would have nothing to add.
            return backward;
        }

        // PlanForward floors its own ceiling to a UTC midnight, so handing it the contract ceiling
        // (itself a midnight) for an already-rolled contract stops the band exactly at expiry.
        var forwardCeiling = contractCeiling < nowUtc ? contractCeiling : nowUtc;

        var forward = BackfillPlanner.PlanForward(job, conId, forwardCeiling, cadence)
            // A contract whose head is LATER than the job's frozen target_to — a quarter that only
            // started trading after this job row was written — would otherwise get one confirmed-empty
            // slice per day between the two, which for a newly-trading ES quarter is hundreds of paced
            // requests spent proving what the head timestamp already said. A slice ending at or below
            // the head lies entirely beneath it.
            .Where(slice => headTimestampUtc is not { } head || slice.EndTimeUtc > head.ToUniversalTime())
            .ToArray();

        return forward.Length == 0 ? backward : [.. backward, .. forward];
    }

    /// <summary>The instant one contract's own data stops: midnight after its last trading day.</summary>
    private static DateTimeOffset ContractCeiling(DateOnly lastTradeDate) =>
        new(lastTradeDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// The newest instant a planned contract can be expected to cover: its own expiry once it has
    /// rolled, else the last completed UTC day. The per-contract term of the forward-coverage check
    /// <see cref="SeedAsync"/> measures against <c>research.backfill_requests</c>.
    /// </summary>
    private static DateTimeOffset PlannableCeiling(DateOnly lastTradeDate, DateTimeOffset nowUtc)
    {
        var contractCeiling = ContractCeiling(lastTradeDate);
        var today = BackfillPlanner.FloorToBucket(nowUtc, TimeSpan.FromDays(1));

        return contractCeiling < today ? contractCeiling : today;
    }

    /// <summary>What a head-timestamp probe settled about one contract.</summary>
    /// <remarks>
    /// Three states, not two, and the distinction is the whole point:
    /// <see cref="Unresolved"/> means nobody has answered yet and the scan must stay open over it,
    /// while <see cref="NoDataYet"/> means TWS answered definitively and the scan is finished with
    /// this contract until the verdict ages out. Collapsing both into "null head" made the second
    /// masquerade as the first — and since ES normally has several listed-but-untraded quarters at
    /// any time, the job could never once reach <c>complete</c>.
    /// </remarks>
    private enum HeadVerdict
    {
        /// <summary>A usable data floor (possibly a stale cached one, which is fine to plan from).</summary>
        Resolved,

        /// <summary>TWS says there is no history here. Terminal for this probe; re-probed when the cached verdict ages out.</summary>
        NoDataYet,

        /// <summary>Pacing, a disconnect, a transport failure — nothing has been established. Try again next scan.</summary>
        Unresolved,
    }

    private readonly record struct ContractHead(HeadVerdict Verdict, DateTimeOffset? HeadUtc);

    /// <summary>
    /// This contract's real data floor, from the cache if fresh, else a live <c>reqHeadTimeStamp</c>
    /// probe recorded into the cache for next time.
    /// </summary>
    /// <remarks>
    /// Deliberately per-contract, unlike <see cref="BackfillCoordinator"/>'s per-job probe key: this
    /// job walks many conIds, and each one's data floor is a genuinely different fact.
    /// <para>
    /// <b>A "no data" verdict is cached too, and that is not an optimisation.</b> Only a successful
    /// probe used to be written, so a quarter TWS had already told us has no history was re-probed on
    /// every six-hourly scan for the life of the job — one paced request per contract per scan,
    /// spent to be told the same thing, against a job holding the lowest priority in the system. The
    /// verdict expires on the same <see cref="BackfillOptions.HeadTimestampMaxAgeDays"/> clock as a
    /// positive one, because a listed quarter does eventually start trading and this must not become
    /// a permanent exclusion.
    /// </para>
    /// </remarks>
    private async Task<ContractHead> ResolveContractHeadAsync(
        BackfillJob job, InstrumentRow instrument, EsContractCandidate contract, CancellationToken cancellationToken)
    {
        var probeKey = $"head_timestamp:{job.Name}:{contract.ConId}";
        var cached = await store.GetCachedHeadProbeAsync(probeKey, cancellationToken);

        if (cached is { } hit && DateTimeOffset.UtcNow - hit.ProbedAt < TimeSpan.FromDays(_options.HeadTimestampMaxAgeDays))
        {
            return hit.Head is { } cachedHead
                ? new ContractHead(HeadVerdict.Resolved, cachedHead)
                : new ContractHead(HeadVerdict.NoDataYet, null);
        }

        var result = await gateway.GetHeadTimestampAsync(
            instrument.ContractFor(contract.ConId), job.WhatToShow, job.UseRth, cancellationToken);

        switch (result.Outcome)
        {
            case GatewayOutcome.Ok when result.HeadTimestampUtc is { } headUtc:
                await store.RecordHeadTimestampAsync(
                    probeKey, contract.ConId, headUtc, $"planned by the ES contract walker for {job.Name}", cancellationToken);
                return new ContractHead(HeadVerdict.Resolved, headUtc);

            case GatewayOutcome.Permanent:
            case GatewayOutcome.Empty:
                // TWS will not tell us where this contract's data starts — most often a quarter CME
                // lists for open interest years before it actually trades. Skip it rather than
                // falling back to BackfillCoordinator's per-job "plan the full range unclamped":
                // that fallback is right for one fixed conId, but here it would enqueue thousands of
                // slices this walker already has good reason to expect will all come back empty.
                logger.LogDebug(
                    "No head timestamp for {Job} contract {ConId} (IBKR {Code}: {Detail}); recording the verdict " +
                    "and re-probing in {Days} day(s).",
                    job.Name, contract.ConId, result.IbkrErrorCode, result.Detail, _options.HeadTimestampMaxAgeDays);

                await store.RecordNoHeadTimestampAsync(
                    probeKey, contract.ConId,
                    $"the ES contract walker for {job.Name} found no history (IBKR {result.IbkrErrorCode}: {result.Detail})",
                    cancellationToken);

                return new ContractHead(HeadVerdict.NoDataYet, null);

            default:
                // Pacing, disconnection, transient failure. A stale cached value is still fine to
                // plan from — but a stale "no data" is not a licence to declare this scan finished
                // with the contract, because nothing was established THIS pass either.
                return cached?.Head is { } stale
                    ? new ContractHead(HeadVerdict.Resolved, stale)
                    : new ContractHead(HeadVerdict.Unresolved, null);
        }
    }
}
