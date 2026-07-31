using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>Knobs for the backfill coordinator, bound from the <c>Backfill</c> configuration section.</summary>
public sealed class BackfillOptions
{
    /// <summary>
    /// Off by default. The historical drain is on the order of days of continuously paced TWS
    /// requests, so it is an operator decision the same way <c>Execution:Router</c> and
    /// <c>Portfolio:Source</c> are — and <c>GET /research/backfill</c> reports this flag explicitly
    /// so "disabled" can never be mistaken for "nothing to do".
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How many times a slice may be attempted before it is left alone and reported as exhausted.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// How long a claim stays believable. Must comfortably exceed the gateway's own historical
    /// request timeout (60 s) plus HTTP overhead, or a live request gets reclaimed underneath its
    /// owner and re-issued for nothing.
    /// </summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>How long to wait after finding nothing claimable.</summary>
    public int IdlePollSeconds { get; set; } = 15;

    /// <summary>How often top-up slices are (re)planned and job completion re-evaluated.</summary>
    public int PlanIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How often a historical job is re-planned. Re-planning is a no-op unless the job's range or
    /// head timestamp changed, so this only needs to be often enough to pick up an operator
    /// deepening <c>target_from</c>.
    /// </summary>
    public int HistoricalPlanIntervalHours { get; set; } = 6;

    /// <summary>How long a cached head timestamp is trusted before re-probing.</summary>
    public int HeadTimestampMaxAgeDays { get; set; } = 30;

    /// <summary>
    /// How many attempts a slice that reports empty gets before the verdict is accepted, when its
    /// neighbours suggest it should not be empty. See
    /// <see cref="BackfillStore.HasDataBearingNeighboursAsync"/>.
    /// </summary>
    public int SuspiciousEmptyAttempts { get; set; } = 2;

    /// <summary>How long to wait when the gateway reports it is not connected to TWS.</summary>
    public int DisconnectedBackoffSeconds { get; set; } = 30;

    /// <summary>
    /// How long to wait after a failure that MAY have reached TWS.
    /// </summary>
    /// <remarks>
    /// Non-zero because the per-slice exponential backoff does not restrain this loop at all: it
    /// backs off the slice that just failed, so the next claim simply returns the NEXT slice and the
    /// loop walks down the job at HTTP-failure speed, spending one attempt per slice on a condition
    /// that has nothing to do with any of them. That is how a twenty-second gateway outage retired
    /// roughly a hundred distinct slices.
    /// </remarks>
    public int TransientBackoffSeconds { get; set; } = 15;

    /// <summary>
    /// How many 15-minute buckets behind the current one a top-up run re-plans, so a missed run is
    /// caught up rather than skipped. 16 buckets is four hours — long enough to cover a restart, a
    /// deploy, or a pacing storm, short enough that a longer outage is left to the daily forward
    /// extension rather than flooding the highest-priority queue in the system.
    /// </summary>
    public int TopUpCatchUpBuckets { get; set; } = 16;
}

/// <summary>
/// Drains <c>research.backfill_requests</c> against the IBKR gateway: plans slices, claims them one
/// at a time, lands their bars, and reclaims anything a dead instance left behind.
/// </summary>
/// <remarks>
/// <para>
/// The checkpoint table is the only state. This class holds nothing across a restart beyond caches
/// it can rebuild, and every decision it makes — what is left to do, what is in flight, whether a
/// job is finished — is a query, not a field. A restart mid-drain therefore costs at most the one
/// slice that was in the air, and even that is reclaimed rather than lost (see
/// <see cref="BackfillStore.ReclaimExpiredAsync"/>).
/// </para>
/// <para>
/// Slices are claimed <b>one at a time</b>. A batch would need its leases heartbeated while the
/// batch drains, and a coordinator that dies part-way through one leaves a mix of finished and
/// abandoned claims to untangle. Pacing, not the database, is the bottleneck here — the governor
/// admits roughly one historical request every eleven seconds — so a per-slice claim round trip
/// costs nothing measurable and removes an entire class of partial-batch states.
/// </para>
/// <para>
/// <b>A permanent error marks the slice, never the job.</b> A rejected contract or an invalid
/// parameter combination for one range says nothing about the other several thousand slices, and
/// failing the job would convert one bad request into a silently abandoned campaign.
/// </para>
/// </remarks>
public sealed class BackfillCoordinator(
    BackfillStore store,
    IbkrGatewayClient gateway,
    IOptions<BackfillOptions> options,
    ILogger<BackfillCoordinator> logger)
    : BackgroundService
{
    private readonly BackfillOptions _options = options.Value;

    /// <summary>
    /// Identifies this process instance for the lifetime of the process, and never again.
    /// </summary>
    /// <remarks>
    /// The GUID is the point. Machine and pid alone can repeat after a restart (pids are recycled,
    /// containers keep their hostname), and a restarted coordinator that reused its predecessor's
    /// token could complete a claim it never made — writing an outcome for a request the dead
    /// instance actually had in flight.
    /// </remarks>
    public string OwnerId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    private readonly Dictionary<short, InstrumentRow> _instruments = [];
    private readonly Dictionary<long, BackfillJob> _jobsById = [];
    private readonly Dictionary<string, int> _conIdsByName = [];
    private readonly Dictionary<long, DateTimeOffset> _historicalPlannedAt = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation(
                "Backfill is disabled; set Backfill__Enabled=true to start draining historical slices. " +
                "GET /research/backfill still reports job state.");
            return;
        }

        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            logger.LogWarning("No 'trading' connection string; the backfill coordinator cannot run.");
            return;
        }

        logger.LogInformation("Backfill coordinator starting as {OwnerId}.", OwnerId);

        var nextPlan = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var idleFor = TimeSpan.FromSeconds(_options.IdlePollSeconds);

            try
            {
                // Reclaiming runs first and every pass, including before the first plan: the rows it
                // frees may be the only claimable work there is, and a coordinator that plans and
                // claims without reaping would sit idle next to slices its own dead predecessor left
                // stranded.
                var reclaimed = await store.ReclaimExpiredAsync(stoppingToken);

                if (reclaimed > 0)
                {
                    logger.LogWarning(
                        "Reclaimed {Count} backfill slice(s) whose lease expired; they will be retried.", reclaimed);
                }

                if (DateTimeOffset.UtcNow >= nextPlan)
                {
                    await PlanAsync(stoppingToken);
                    nextPlan = DateTimeOffset.UtcNow.AddSeconds(_options.PlanIntervalSeconds);
                }

                var claimed = await store.ClaimAsync(
                    OwnerId, TimeSpan.FromSeconds(_options.LeaseSeconds), _options.MaxAttempts, limit: 1, stoppingToken);

                if (claimed.Count > 0)
                {
                    idleFor = await ExecuteSliceAsync(claimed[0], stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backfill pass failed; retrying.");
            }

            if (idleFor <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await Task.Delay(idleFor, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- planning -----------------------------------------------------------------------------

    private async Task PlanAsync(CancellationToken cancellationToken)
    {
        var seeded = new HashSet<long>();

        foreach (var definition in BackfillJobCatalog.Definitions)
        {
            try
            {
                // Resolution is a paced socket round trip, so it happens once per job per process
                // and then never again: a job row that already carries a conId is passed a null,
                // which EnsureJobAsync coalesces into "keep what is there".
                var conId = _conIdsByName.ContainsKey(definition.Name)
                    ? null
                    : await ResolveConIdAsync(definition, cancellationToken);

                var job = await store.EnsureJobAsync(definition, conId, cancellationToken);

                if (job is null)
                {
                    continue;
                }

                _jobsById[job.JobId] = job;
                seeded.Add(job.JobId);

                if (job.ConId is { } resolved)
                {
                    _conIdsByName[job.Name] = resolved;
                }

                // Catalog jobs are planned regardless of status (except when an operator has paused
                // them). Restricting this to "active" jobs would leave a COMPLETE job unreachable:
                // deepening its target_from — the roadmap's "probe toward the 2004 head" — would
                // then quietly do nothing, because a completed job never gets planned again.
                if (job.Status != "paused")
                {
                    await PlanJobAsync(job, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One job's failure must not stop the others. The Phase 1 review found exactly this
                // shape in PartitionMaintainer, where one un-creatable date blocked every other date
                // on every retry, forever.
                logger.LogError(ex, "Planning job {Job} failed; other jobs are unaffected.", definition.Name);
            }
        }

        // Jobs this coordinator did not seed — an ES walk from package 2e, say — are just as
        // claimable, and their slices must be executed and reported even though nothing here plans
        // them. Reading them keeps the job cache complete for the execution path.
        foreach (var job in await store.GetActiveJobsAsync(cancellationToken))
        {
            _jobsById[job.JobId] = job;

            if (seeded.Contains(job.JobId))
            {
                continue;
            }

            try
            {
                await PlanJobAsync(job, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Planning job {Job} failed; other jobs are unaffected.", job.Name);
            }
        }
    }

    private async Task PlanJobAsync(BackfillJob job, CancellationToken cancellationToken)
    {
        if (job.ConId is not { } conId)
        {
            // Either the gateway has not resolved it yet, or this is a contract-walked job whose
            // slices come from elsewhere (package 2e's ES walker). Either way there is nothing for
            // this planner to derive, and its request rows — if any — still drain normally.
            logger.LogDebug("Job {Job} has no conId; nothing to plan for it.", job.Name);
            return;
        }

        if (job.Kind == BackfillJobKinds.TopUp)
        {
            var topUps = BackfillPlanner.PlanTopUp(
                job, conId, DateTimeOffset.UtcNow, _options.TopUpCatchUpBuckets);

            if (await store.InsertSlicesAsync(topUps, cancellationToken) > 0)
            {
                await store.SetJobStatusAsync(job.JobId, "running", cancellationToken);
                logger.LogDebug("Top-up slice for {Job} anchored at {End:O}.", job.Name, topUps[0].EndTimeUtc);
            }

            // A top-up job is never complete by construction, so it is never marked so — doing it
            // even briefly between buckets would drop it out of the claim query's job filter.
            return;
        }

        if (_historicalPlannedAt.TryGetValue(job.JobId, out var plannedAt) &&
            DateTimeOffset.UtcNow - plannedAt < TimeSpan.FromHours(_options.HistoricalPlanIntervalHours))
        {
            await RefreshJobStatusAsync(job, cancellationToken);
            return;
        }

        if (BackfillPlanner.CadenceFor(job) is not { } cadence)
        {
            // Refusing beats guessing: a job planned at a duration nobody asked for lands request
            // rows that permanently mismatch the operator's intent, and the mismatch is invisible.
            logger.LogError(
                "Job {Job} names a slice duration ('{Duration}') or bar size ('{BarSize}') this planner " +
                "cannot put on a boundary grid; refusing to plan it.",
                job.Name, job.SliceDuration ?? "(derived)", job.BarSize);

            await store.SetJobStatusAsync(job.JobId, "failed", cancellationToken);
            return;
        }

        var head = await ResolveHeadTimestampAsync(job, conId, cancellationToken);

        if (head.Outcome == HeadResolution.Retry)
        {
            logger.LogInformation("Head timestamp for {Job} is not available yet; deferring its plan.", job.Name);
            return;
        }

        var slices = BackfillPlanner.PlanHistorical(job, conId, head.HeadUtc, cadence);

        if (slices.Count >= BackfillPlanner.MaxSlicesPerJob)
        {
            logger.LogError(
                "Job {Job} planned the maximum {Max} slices and was truncated at {Oldest:O}; its target range " +
                "is larger than one job should cover at a '{Duration}' slice.",
                job.Name, BackfillPlanner.MaxSlicesPerJob, slices[^1].EndTimeUtc, cadence.Duration);
        }

        // The band after the job's frozen target_to. Planned every pass alongside the backward walk
        // rather than in a job of its own, because it is the same grid, the same conId, and the same
        // idempotency key — see BackfillPlanner.PlanForward for why nothing covered it before.
        var forward = BackfillPlanner.PlanForward(job, conId, DateTimeOffset.UtcNow, cadence);

        var inserted = await store.InsertSlicesAsync(slices, cancellationToken)
                     + await store.InsertSlicesAsync(forward, cancellationToken);

        _historicalPlannedAt[job.JobId] = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "Planned {Total} slice(s) for {Job} ({New} new, {Forward} of them forward of target_to {TargetTo:O}) " +
            "from {From:O} at a '{Duration}' cadence.",
            slices.Count + forward.Count, job.Name, inserted, forward.Count, job.TargetTo,
            head.HeadUtc ?? job.TargetFrom, cadence.Duration);

        await RefreshJobStatusAsync(job, cancellationToken);
    }

    /// <summary>
    /// Re-derives this job's status from its checkpoint counts.
    /// </summary>
    /// <remarks>
    /// Replaces a one-way "settled ⇒ complete" transition. That version could only ever move a job
    /// forward, so a job that reached <c>complete</c> with exhausted slices in it was stuck there:
    /// raising the attempt cap made its rows claimable while the job itself stayed outside the claim
    /// query's status filter, and re-planning could not rescue it either because an unchanged job
    /// re-derives identical slices and inserts nothing. Deriving the status every pass instead means
    /// the job follows its own rows in both directions, and <c>complete_with_gaps</c> keeps the
    /// distinction an operator actually needs: finished, versus finished with holes in it.
    /// </remarks>
    private async Task RefreshJobStatusAsync(BackfillJob job, CancellationToken cancellationToken)
    {
        var status = await store.RefreshJobStatusAsync(
            job.JobId, _options.MaxAttempts, planningComplete: true, cancellationToken);

        switch (status)
        {
            case "complete":
                logger.LogInformation("Job {Job} has no outstanding slices; marking it complete.", job.Name);
                break;

            case "complete_with_gaps":
                logger.LogWarning(
                    "Job {Job} has no outstanding slices, but some exhausted their {Max} attempts and will never " +
                    "be fetched. Marking it complete_with_gaps — GET /research/backfill/gaps names the ranges, and " +
                    "raising Backfill__MaxAttempts makes them claimable again.",
                    job.Name, _options.MaxAttempts);
                break;

            case { } reopened:
                logger.LogInformation("Job {Job} has outstanding slices again; back to '{Status}'.", job.Name, reopened);
                break;
        }
    }

    // ---- execution ----------------------------------------------------------------------------

    /// <returns>How long to idle before the next pass.</returns>
    private async Task<TimeSpan> ExecuteSliceAsync(ClaimedSlice slice, CancellationToken cancellationToken)
    {
        if (!_jobsById.TryGetValue(slice.JobId, out var job))
        {
            foreach (var active in await store.GetActiveJobsAsync(cancellationToken))
            {
                _jobsById[active.JobId] = active;
            }

            if (!_jobsById.TryGetValue(slice.JobId, out job))
            {
                logger.LogWarning("Claimed a slice for unknown job {JobId}; releasing it.", slice.JobId);
                await store.ReleaseAsync(slice.RequestId, OwnerId, cancellationToken);
                return TimeSpan.FromSeconds(_options.IdlePollSeconds);
            }
        }

        var instrument = await GetInstrumentAsync(job.InstrumentId, cancellationToken);

        if (instrument is null)
        {
            logger.LogError(
                "Job {Job} names instrument {InstrumentId}, which is not in research.instruments; releasing the slice.",
                job.Name, job.InstrumentId);
            await store.ReleaseAsync(slice.RequestId, OwnerId, cancellationToken);
            return TimeSpan.FromSeconds(_options.IdlePollSeconds);
        }

        var request = new HistoricalBarsRequestDto(
            instrument.ContractFor(slice.ConId),
            slice.EndTimeUtc,
            slice.Duration,
            slice.BarSize,
            slice.WhatToShow,
            slice.UseRth);

        var result = await gateway.GetHistoricalBarsAsync(request, cancellationToken);

        switch (result.Outcome)
        {
            case GatewayOutcome.Ok:
                await LandAsync(job, slice, result, cancellationToken);
                return TimeSpan.Zero;

            case GatewayOutcome.Empty:
                await SettleEmptyAsync(job, slice, cancellationToken);
                return TimeSpan.Zero;

            case GatewayOutcome.Paced:
                // The governor's backpressure signal. The slice never reached TWS, so it goes back
                // on the queue with its attempt refunded, and we wait exactly as long as we were told.
                await store.ReleaseAsync(slice.RequestId, OwnerId, cancellationToken);
                var wait = result.RetryAfter ?? TimeSpan.FromSeconds(60);
                logger.LogInformation("Pacing budget exhausted; backing off {Seconds:F0}s.", wait.TotalSeconds);
                return wait;

            case GatewayOutcome.NotConnected:
                await store.ReleaseAsync(slice.RequestId, OwnerId, cancellationToken);
                logger.LogWarning("The gateway is not connected to TWS; backfill is paused.");
                return TimeSpan.FromSeconds(_options.DisconnectedBackoffSeconds);

            case GatewayOutcome.Unreachable:
                // The same treatment as Paced and NotConnected, and for the identical reason: the
                // request never reached TWS, so charging the slice a retry for it is charging it for
                // something it did not do. `attempts` has NO reset path — the only two writers are
                // +1 at claim and -1 at release — so an attempt burned here is burned forever, and a
                // gateway that is down long enough to cost five of them retires the slice
                // permanently while the job goes on to report itself finished.
                await store.ReleaseAsync(slice.RequestId, OwnerId, cancellationToken);
                logger.LogWarning(
                    "The gateway could not be reached ({Detail}); backing off without spending the slice's attempt.",
                    result.Detail);
                return TimeSpan.FromSeconds(_options.DisconnectedBackoffSeconds);

            case GatewayOutcome.Permanent:
                logger.LogWarning(
                    "Slice {RequestId} of {Job} ending {End:O} failed permanently (IBKR {Code}): {Detail}. " +
                    "The slice is retired; the job continues.",
                    slice.RequestId, job.Name, slice.EndTimeUtc, result.IbkrErrorCode, result.Detail);

                await store.MarkOutcomeAsync(
                    slice.RequestId, OwnerId, BackfillRequestState.Permanent, result.IbkrErrorCode, result.Detail,
                    cancellationToken);
                return TimeSpan.Zero;

            default:
                // The attempt IS spent here, unlike the branches above: a 502, a 504, or a client
                // timeout may well have consumed a paced TWS request slot, and refunding on that
                // basis would let a request that genuinely reaches TWS and genuinely fails retry
                // without limit. The backoff is what stops the loop walking the whole job at
                // failure speed — MarkOutcomeAsync backs off only THIS slice, so returning zero here
                // meant the next pass immediately claimed the next one.
                logger.LogInformation(
                    "Slice {RequestId} of {Job} failed transiently on attempt {Attempt}: {Detail}",
                    slice.RequestId, job.Name, slice.Attempts, result.Detail);

                await store.MarkOutcomeAsync(
                    slice.RequestId, OwnerId, BackfillRequestState.Failed, result.IbkrErrorCode, result.Detail,
                    cancellationToken);
                return TimeSpan.FromSeconds(_options.TransientBackoffSeconds);
        }
    }

    private async Task LandAsync(
        BackfillJob job, ClaimedSlice slice, HistoricalBarsResult result, CancellationToken cancellationToken)
    {
        var source = job.Kind == BackfillJobKinds.TopUp ? "topup" : "backfill";
        var landed = await store.LandBarsAsync(slice, OwnerId, job.InstrumentId, result.Bars, source, cancellationToken);

        if (!landed)
        {
            // Our lease expired while the request was in flight and a reaper took the row back. The
            // bars were discarded with the transaction; whoever holds the row now will re-fetch them.
            logger.LogWarning(
                "Lost the lease on slice {RequestId} of {Job} while it was in flight; its {Count} bar(s) were " +
                "discarded and the slice will be re-requested.",
                slice.RequestId, job.Name, result.Bars.Count);
            return;
        }

        logger.LogDebug(
            "Landed {Count} bar(s) for {Job} slice ending {End:O}.", result.Bars.Count, job.Name, slice.EndTimeUtc);
    }

    /// <summary>
    /// Records a confirmed-empty slice — unless its neighbours say it should not be empty.
    /// </summary>
    /// <remarks>
    /// The gateway maps TWS error 162 to <c>HasData: false</c>, and 162 is also how some pacing
    /// violations surface, distinguished only by message text. Retiring a slice permanently on that
    /// basis alone would, when the text check is wrong, leave a hole nothing can later find: the
    /// checkpoint would insist the range was legitimately empty. A slice bracketed on BOTH sides by
    /// slices of the same contract that DID return data gets one extra attempt before the verdict
    /// stands — one paced request against permanently losing the data.
    /// </remarks>
    private async Task SettleEmptyAsync(BackfillJob job, ClaimedSlice slice, CancellationToken cancellationToken)
    {
        if (slice.Attempts < _options.SuspiciousEmptyAttempts)
        {
            var proximity = BackfillPlanner.ApproximateSpanOf(slice.Duration) * 3;

            if (await store.HasDataBearingNeighboursAsync(
                    job.JobId, slice.ConId, slice.EndTimeUtc, proximity, cancellationToken))
            {
                logger.LogInformation(
                    "Slice {RequestId} of {Job} ending {End:O} reported no data but sits between slices that " +
                    "returned data; re-verifying before retiring it.",
                    slice.RequestId, job.Name, slice.EndTimeUtc);

                await store.MarkOutcomeAsync(
                    slice.RequestId, OwnerId, BackfillRequestState.Failed, null,
                    "no data reported, but neighbouring slices have data; re-verifying", cancellationToken);
                return;
            }
        }

        await store.MarkOutcomeAsync(
            slice.RequestId, OwnerId, BackfillRequestState.Empty, null, null, cancellationToken);
    }

    // ---- resolution ---------------------------------------------------------------------------

    private async Task<int?> ResolveConIdAsync(BackfillJobDefinition definition, CancellationToken cancellationToken)
    {
        var resolution = await gateway.ResolveUnderlyingAsync(definition.Symbol, cancellationToken);

        if (resolution is null)
        {
            // EnsureJobAsync keeps whatever conId the row already carries when handed a null, so a
            // gateway outage cannot erase a resolution that already succeeded.
            logger.LogDebug("Could not resolve {Symbol} for job {Job} this pass.", definition.Symbol, definition.Name);
            return null;
        }

        return resolution.ConId;
    }

    private enum HeadResolution
    {
        /// <summary>A head timestamp is available (or is known not to exist); plan now.</summary>
        Known,

        /// <summary>Nothing conclusive yet; try again next pass rather than planning blind.</summary>
        Retry,
    }

    private async Task<(HeadResolution Outcome, DateTimeOffset? HeadUtc)> ResolveHeadTimestampAsync(
        BackfillJob job, int conId, CancellationToken cancellationToken)
    {
        var probeKey = $"head_timestamp:{job.Name}";
        var cached = await store.GetCachedHeadTimestampAsync(probeKey, cancellationToken);

        if (cached is { } hit &&
            DateTimeOffset.UtcNow - hit.ProbedAt < TimeSpan.FromDays(_options.HeadTimestampMaxAgeDays))
        {
            return (HeadResolution.Known, hit.Head);
        }

        var instrument = await GetInstrumentAsync(job.InstrumentId, cancellationToken);

        if (instrument is null)
        {
            return (HeadResolution.Retry, null);
        }

        var result = await gateway.GetHeadTimestampAsync(
            instrument.ContractFor(conId), job.WhatToShow, job.UseRth, cancellationToken);

        switch (result.Outcome)
        {
            case GatewayOutcome.Ok when result.HeadTimestampUtc is { } head:
                await store.RecordHeadTimestampAsync(
                    probeKey, conId, head, $"planned by the backfill coordinator for job {job.Name}", cancellationToken);
                return (HeadResolution.Known, head);

            case GatewayOutcome.Permanent:
            case GatewayOutcome.Empty:
                // TWS will not tell us where the data starts. Planning the job's full declared range
                // unclamped is the honest degradation: slices below the real floor come back empty,
                // which is a first-class outcome that is recorded once and never retried — far better
                // than a job that silently never plans at all.
                logger.LogWarning(
                    "No head timestamp for {Job} (IBKR {Code}: {Detail}); planning its full declared range instead.",
                    job.Name, result.IbkrErrorCode, result.Detail);
                return (HeadResolution.Known, null);

            default:
                // Pacing, disconnection, transient failure — say nothing and try again. Falling back
                // to a stale cached value would be fine, so use one if we have it.
                return cached is { } stale
                    ? (HeadResolution.Known, stale.Head)
                    : (HeadResolution.Retry, null);
        }
    }

    private async Task<InstrumentRow?> GetInstrumentAsync(short instrumentId, CancellationToken cancellationToken)
    {
        if (_instruments.TryGetValue(instrumentId, out var cached))
        {
            return cached;
        }

        var row = await store.GetInstrumentAsync(instrumentId, cancellationToken);

        if (row is not null)
        {
            _instruments[instrumentId] = row;
        }

        return row;
    }
}
