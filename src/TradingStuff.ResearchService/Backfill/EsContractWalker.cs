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
/// How many of those the scan could NOT plan — a head timestamp that would not resolve this pass,
/// or a per-contract failure. The number that has to exist for this walker to be able to tell
/// "finished" from "barely started": a skipped contract writes no request rows, so it lowers no
/// count in <c>research.backfill_requests</c> and is invisible to every completion query.
/// </param>
/// <param name="ForgottenContractCount">
/// How many contracts have request rows from an earlier scan but were absent from THIS scan's
/// enumeration. Non-zero means the family listing came back incomplete, which is the one way this
/// walker's expected-contract set can shrink without anything being wrong with the contracts.
/// </param>
/// <param name="PlanningComplete">
/// True only when every contract this walker knows about — enumerated now, or planned before — was
/// planned in this scan. The gate on declaring the job finished.
/// </param>
public sealed record EsWalkResult(
    int ContractCount,
    int SlicesPlanned,
    int SlicesInserted,
    int UnplannedContractCount = 0,
    int ForgottenContractCount = 0,
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

        return await SeedAsync(job, contracts, cancellationToken);
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
    internal async Task<EsWalkResult> SeedAsync(
        BackfillJob job, IReadOnlyList<EsContractCandidate> contracts, CancellationToken cancellationToken)
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

        foreach (var contract in contracts)
        {
            try
            {
                var head = await ResolveContractHeadAsync(job, instrument, contract, cancellationToken);

                if (head is null)
                {
                    // Not resolvable this pass — pacing, a disconnect, or (most often for ES) a
                    // quarter CME lists years ahead of its own expiry that has not traded yet.
                    // Skip it; the next scan tries again. Recorded, not merely skipped: this is the
                    // ONLY record that the contract exists at all, since it writes no request rows.
                    unplanned.Add(contract.ConId);
                    continue;
                }

                var slices = PlanContractWindow(job, contract.ConId, contract.LastTradeDateOrContractMonth, head, cadence);

                // Zero slices with a RESOLVED head is a conclusion, not a skip: the contract's own
                // window falls entirely outside the job's declared range. It is planned as far as
                // this walker is ever going to plan it, so it must not hold the job open forever.
                planned += slices.Count;
                inserted += await store.InsertSlicesAsync(slices, cancellationToken);
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

        var planningComplete = unplanned.Count == 0 && forgotten.Length == 0;

        // planningComplete is the whole point of this call. IsJobSettledAsync counts rows in
        // research.backfill_requests, and a contract that was skipped produces NO rows — so it
        // cannot lower any count, and "total > 0" proves that ONE contract was planned, never that
        // all of them were. The previous version inferred completion from exactly that: with 29 ES
        // quarterlies and this job holding the lowest priority in the system, four contracts
        // planning while head probes for the other 25 were paced away was enough to report the job
        // complete at 100% with ~85% of the intended history never requested.
        await store.RefreshJobStatusAsync(job.JobId, _options.MaxAttempts, planningComplete, cancellationToken);

        if (planningComplete)
        {
            logger.LogInformation(
                "ES contract walk for {Job}: all {ContractCount} contract(s) planned, {Planned} slice(s), {Inserted} new.",
                job.Name, contracts.Count, planned, inserted);
        }
        else
        {
            logger.LogWarning(
                "ES contract walk for {Job}: {Planned} slice(s) planned ({Inserted} new), but {Unplanned} of " +
                "{ContractCount} enumerated contract(s) could not be planned this scan and {Forgotten} previously " +
                "planned contract(s) were missing from the family listing. The job stays open; it will be retried " +
                "in {Interval}.",
                job.Name, planned, inserted, unplanned.Count, contracts.Count, forgotten.Length, RescanInterval);
        }

        return new EsWalkResult(contracts.Count, planned, inserted, unplanned.Count, forgotten.Length, planningComplete);
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
    /// declared floor, if later) through the day after its own last trading day (or the job's
    /// declared ceiling, if earlier) — never beyond either.
    /// </summary>
    /// <remarks>
    /// Pure and deterministic given its arguments — nothing here reads the clock, the database, or
    /// configuration — which is what lets an identical rerun of the walker add zero new
    /// <c>backfill_requests</c> rows: the same (job, contract, head, cadence) always produces the
    /// same slices. Reuses <see cref="BackfillPlanner.PlanHistorical"/> unmodified by handing it a
    /// job record whose <c>TargetTo</c> is clamped to this contract's own ceiling; the head-timestamp
    /// clamp on the low end is already exactly what this needs, so the walker adds only the expiry
    /// clamp on the high end rather than re-deriving slice arithmetic <c>BackfillPlanner</c> already
    /// owns.
    /// </remarks>
    internal static IReadOnlyList<BackfillSlice> PlanContractWindow(
        BackfillJob job, int conId, DateOnly lastTradeDate, DateTimeOffset? headTimestampUtc, SliceCadence cadence)
    {
        // Inclusive of the whole last trading day: a contract can print right up to its close, and a
        // slice reaching a little past midnight the day after costs nothing — TWS returns whatever
        // it actually has up to the requested end, exactly like BackfillPlanner's own leading slice
        // that over-reaches backward past its grid boundary.
        var contractCeiling = new DateTimeOffset(lastTradeDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var targetTo = job.TargetTo.ToUniversalTime();
        var clampedTargetTo = contractCeiling < targetTo ? contractCeiling : targetTo;

        if (clampedTargetTo <= job.TargetFrom.ToUniversalTime())
        {
            return [];
        }

        return BackfillPlanner.PlanHistorical(job with { TargetTo = clampedTargetTo }, conId, headTimestampUtc, cadence);
    }

    /// <summary>
    /// This contract's real data floor, from the cache if fresh, else a live <c>reqHeadTimeStamp</c>
    /// probe recorded into the cache for next time.
    /// </summary>
    /// <remarks>
    /// Deliberately per-contract, unlike <see cref="BackfillCoordinator"/>'s per-job probe key: this
    /// job walks many conIds, and each one's data floor is a genuinely different fact.
    /// </remarks>
    private async Task<DateTimeOffset?> ResolveContractHeadAsync(
        BackfillJob job, InstrumentRow instrument, EsContractCandidate contract, CancellationToken cancellationToken)
    {
        var probeKey = $"head_timestamp:{job.Name}:{contract.ConId}";
        var cached = await store.GetCachedHeadTimestampAsync(probeKey, cancellationToken);

        if (cached is { } hit && DateTimeOffset.UtcNow - hit.ProbedAt < TimeSpan.FromDays(_options.HeadTimestampMaxAgeDays))
        {
            return hit.Head;
        }

        var result = await gateway.GetHeadTimestampAsync(
            instrument.ContractFor(contract.ConId), job.WhatToShow, job.UseRth, cancellationToken);

        switch (result.Outcome)
        {
            case GatewayOutcome.Ok when result.HeadTimestampUtc is { } headUtc:
                await store.RecordHeadTimestampAsync(
                    probeKey, contract.ConId, headUtc, $"planned by the ES contract walker for {job.Name}", cancellationToken);
                return headUtc;

            case GatewayOutcome.Permanent:
            case GatewayOutcome.Empty:
                // TWS will not tell us where this contract's data starts — most often a quarter CME
                // lists for open interest years before it actually trades. Skip it this pass rather
                // than falling back to BackfillCoordinator's per-job "plan the full range unclamped":
                // that fallback is right for one fixed conId, but here it would enqueue thousands of
                // slices this walker already has good reason to expect will all come back empty.
                logger.LogDebug(
                    "No head timestamp for {Job} contract {ConId} (IBKR {Code}: {Detail}); skipping it this scan.",
                    job.Name, contract.ConId, result.IbkrErrorCode, result.Detail);
                return null;

            default:
                // Pacing, disconnection, transient failure. A stale cached value is still fine to use.
                return cached?.Head;
        }
    }
}
