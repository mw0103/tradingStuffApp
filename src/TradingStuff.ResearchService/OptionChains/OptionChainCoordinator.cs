using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.ResearchService.OptionChains;

/// <summary>Knobs for the option-chain ingestion coordinator, bound from the <c>OptionChains</c> configuration section.</summary>
public sealed class OptionChainOptions
{
    /// <summary>
    /// Off by default, exactly like <c>Backfill:Enabled</c> and for the same reason: a historical
    /// option-chain drain is an operator decision, and <c>GET /research/options/status</c> reports
    /// this flag explicitly so "disabled" is never mistaken for "nothing left to do".
    /// </summary>
    public bool Enabled { get; set; }

    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// How long a claim stays believable. ThetaData answered 8 concurrent requests in under a
    /// second when measured live (2026-08-01/02), so this is generous headroom for a slow Terminal
    /// restart rather than a reflection of typical latency.
    /// </summary>
    public int LeaseSeconds { get; set; } = 180;

    public int IdlePollSeconds { get; set; } = 5;

    public int PlanIntervalSeconds { get; set; } = 300;

    /// <summary>How long to wait after a request that may or may not have reached the Terminal.</summary>
    public int TransientBackoffSeconds { get; set; } = 15;
}

/// <summary>
/// Drains <c>research.option_chain_requests</c> against a local ThetaData Terminal: plans one
/// request row per (job, expiration), claims them one at a time, lands their quotes, and reclaims
/// anything a dead instance left behind.
/// </summary>
/// <remarks>
/// Deliberately parallel to <c>TradingStuff.ResearchService.Backfill.BackfillCoordinator</c> — same
/// "the checkpoint table is the only state" discipline, same one-at-a-time claim (a Terminal
/// round trip is fast enough that batching would buy nothing and cost partial-batch crash states),
/// same "a permanent error marks the request, never the job" rule. It is simpler than
/// <c>BackfillCoordinator</c> in one real way: the request granularity IS the planning granularity —
/// one checkpoint row per (job, expiration), with no separate slice cadence or head-timestamp probe
/// to cache. <see cref="MonthlyDateRangeChunker"/> exists because a SINGLE vendor call cannot answer
/// a whole job's date range for one expiration (ThetaData caps a bulk quote call's span at roughly a
/// month, measured live and undocumented), so <see cref="ExecuteRequestAsync"/> walks that range in
/// several calls per attempt — but that chunking is invisible to the checkpoint: a request row still
/// stands for one expiration, landed or not, and a crash mid-chunk-walk just re-fetches all of them.
/// </remarks>
public sealed class OptionChainCoordinator(
    OptionChainStore store,
    ThetaDataClient client,
    IOptions<OptionChainOptions> options,
    ILogger<OptionChainCoordinator> logger)
    : BackgroundService
{
    private readonly OptionChainOptions _options = options.Value;

    /// <summary>
    /// Identifies this process instance for the lifetime of the process, and never again — same
    /// reasoning as <c>BackfillCoordinator.OwnerId</c>: a restarted coordinator that reused its
    /// predecessor's token could complete a claim it never made.
    /// </summary>
    public string OwnerId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    private readonly Dictionary<long, OptionChainJob> _jobsById = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation(
                "Option-chain ingestion is disabled; set OptionChains__Enabled=true to start draining " +
                "expirations. GET /research/options/status still reports job state.");
            return;
        }

        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            logger.LogWarning("No 'trading' connection string; the option-chain coordinator cannot run.");
            return;
        }

        logger.LogInformation("Option-chain coordinator starting as {OwnerId}.", OwnerId);

        var nextPlan = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var idleFor = TimeSpan.FromSeconds(_options.IdlePollSeconds);

            try
            {
                var reclaimed = await store.ReclaimExpiredAsync(stoppingToken);
                if (reclaimed > 0)
                {
                    logger.LogWarning(
                        "Reclaimed {Count} option-chain request(s) whose lease expired; they will be retried.",
                        reclaimed);
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
                    idleFor = await ExecuteRequestAsync(claimed[0], stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Option-chain ingestion pass failed; retrying.");
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

    // ---- planning -------------------------------------------------------------------------------

    internal async Task PlanAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await store.GetActiveJobsAsync(cancellationToken))
        {
            _jobsById[job.JobId] = job;

            // Belt-and-braces: EnsureJobAsync already creates a 'tick' job already-paused, and
            // GetActiveJobsAsync's status filter excludes 'paused'. This check documents the
            // invariant at the one place a bug in either of those would otherwise silently start
            // planning tick requests, rather than relying on the two of them staying in sync forever.
            if (job.Interval == OptionChainIntervals.Tick)
            {
                logger.LogError(
                    "Job {Job} carries interval='tick' but is active; refusing to plan it. Bulk tick " +
                    "ingestion is out of scope for this coordinator (docs/FOLLOWUP.md §4.5) — this indicates " +
                    "a job was created or promoted to active status outside OptionChainStore.EnsureJobAsync.",
                    job.Name);
                continue;
            }

            try
            {
                await PlanJobAsync(job, cancellationToken);
            }
            catch (ThetaDataSubscriptionException ex)
            {
                logger.LogWarning(
                    "Job {Job}'s symbol is not covered by this ThetaData subscription: {Message}", job.Name, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One job's planning failure must not stop the others — the same reasoning
                // BackfillCoordinator.PlanAsync gives for the identical try/catch per job.
                logger.LogError(ex, "Planning job {Job} failed; other jobs are unaffected.", job.Name);
            }
        }
    }

    internal async Task PlanJobAsync(OptionChainJob job, CancellationToken cancellationToken)
    {
        var vendorSymbol = ThetaSymbolMap.VendorSymbolFor(job.Underlying, job.TradingClass);

        var table = await client.ListExpirationsAsync(vendorSymbol);
        var expirationColumn = table.RequireColumn("expiration");

        var expirations = table.Rows
            .Select(row => DateOnly.Parse(CsvTable.GetString(row, expirationColumn)))
            .Where(expiration => expiration >= job.TargetFrom && expiration <= job.TargetTo)
            .Distinct()
            .ToList();

        var inserted = await store.PlanExpirationsAsync(job.JobId, expirations, cancellationToken);

        logger.LogInformation(
            "Planned {Total} expiration(s) for {Job} in [{From:yyyy-MM-dd}, {To:yyyy-MM-dd}] ({New} new).",
            expirations.Count, job.Name, job.TargetFrom, job.TargetTo, inserted);

        await RefreshJobStatusAsync(job, cancellationToken);
    }

    private async Task RefreshJobStatusAsync(OptionChainJob job, CancellationToken cancellationToken)
    {
        var status = await store.RefreshJobStatusAsync(job.JobId, _options.MaxAttempts, cancellationToken);

        switch (status)
        {
            case "complete":
                logger.LogInformation("Job {Job} has no outstanding expirations; marking it complete.", job.Name);
                break;
            case "complete_with_gaps":
                logger.LogWarning(
                    "Job {Job} has no outstanding expirations, but some exhausted their {Max} attempts and will " +
                    "never be fetched.", job.Name, _options.MaxAttempts);
                break;
            case { } reopened:
                logger.LogInformation("Job {Job} has outstanding expirations again; back to '{Status}'.", job.Name, reopened);
                break;
        }
    }

    // ---- execution ------------------------------------------------------------------------------

    internal async Task<TimeSpan> ExecuteRequestAsync(ClaimedChainRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<OptionChainQuoteRow>? fetched = null;

        try
        {
            return await ExecuteClaimedRequestAsync(request, rows => fetched = rows, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await AbandonClaimAsync(request, fetched, ex, cancellationToken);
        }
    }

    private async Task<TimeSpan> AbandonClaimAsync(
        ClaimedChainRequest request, IReadOnlyList<OptionChainQuoteRow>? fetched, Exception failure,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            failure,
            "Bookkeeping for request {RequestId} (job {JobId}, expiration {Expiration:yyyy-MM-dd}) failed after " +
            "the request was already claimed; {Rows} fetched quote row(s) were discarded. Releasing the claim.",
            request.RequestId, request.JobId, request.Expiration, fetched?.Count ?? 0);

        try
        {
            await store.ReleaseAsync(request.RequestId, OwnerId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex, "Could not even release the claim on request {RequestId}; it stays inflight until its lease expires.",
                request.RequestId);
        }

        return TimeSpan.FromSeconds(_options.TransientBackoffSeconds);
    }

    private async Task<TimeSpan> ExecuteClaimedRequestAsync(
        ClaimedChainRequest request, Action<IReadOnlyList<OptionChainQuoteRow>> onFetched, CancellationToken cancellationToken)
    {
        if (!_jobsById.TryGetValue(request.JobId, out var job))
        {
            foreach (var active in await store.GetActiveJobsAsync(cancellationToken))
            {
                _jobsById[active.JobId] = active;
            }

            if (!_jobsById.TryGetValue(request.JobId, out job))
            {
                logger.LogWarning("Claimed a request for unknown job {JobId}; releasing it.", request.JobId);
                await store.ReleaseAsync(request.RequestId, OwnerId, cancellationToken);
                return TimeSpan.FromSeconds(_options.IdlePollSeconds);
            }
        }

        var vendorSymbol = ThetaSymbolMap.VendorSymbolFor(job.Underlying, job.TradingClass);

        try
        {
            // The vendor caps a single bulk-quote call's [start_date, end_date] span at roughly a
            // month (measured live, undocumented — see MonthlyDateRangeChunker's remarks); a job's
            // full target range is very often wider than that, so one expiration's worth of history
            // is fetched in chunks and landed together. A no-data chunk (e.g. the tail end of the
            // job's range, past this expiration's own settlement) is treated as zero rows for that
            // chunk rather than aborting the whole expiration — the expiration as a whole is still
            // very much real, just not active for that particular slice of the range.
            var rows = new List<OptionChainQuoteRow>();

            foreach (var (chunkStart, chunkEnd) in MonthlyDateRangeChunker.Split(
                         job.TargetFrom.ToDateTime(TimeOnly.MinValue), job.TargetTo.ToDateTime(TimeOnly.MinValue)))
            {
                try
                {
                    var table = await client.GetDailyChainQuotesAsync(
                        vendorSymbol, request.Expiration.ToDateTime(TimeOnly.MinValue), chunkStart, chunkEnd);
                    rows.AddRange(OptionChainQuoteCsvParser.Parse(table, job.Underlying, job.TradingClass, request.Expiration));
                }
                catch (ThetaDataNoDataException)
                {
                    // Normal: most of a multi-month range predates or postdates any one expiration's
                    // active trading window.
                }
            }

            onFetched(rows);

            if (rows.Count == 0)
            {
                // Every chunk came back empty: preserve the 'empty' vs 'succeeded' distinction
                // (BackfillCoordinator.SettleEmptyAsync makes the identical call for bars) rather
                // than recording a zero-quote 'succeeded' outcome that reads the same as a real one.
                logger.LogInformation(
                    "No quotes for {Job} expiration {Expiration:yyyy-MM-dd} across its whole range.",
                    job.Name, request.Expiration);
                await store.MarkOutcomeAsync(
                    request.RequestId, OwnerId, OptionChainRequestState.Empty,
                    "No quotes in any chunk of the job's date range.", cancellationToken);
                return TimeSpan.Zero;
            }

            var landed = await store.LandQuotesAsync(request, OwnerId, rows, vendorSymbol, job.Interval, cancellationToken);

            if (!landed)
            {
                logger.LogWarning(
                    "Lost the lease on request {RequestId} of {Job} (expiration {Expiration:yyyy-MM-dd}) while it " +
                    "was in flight; its {Count} quote row(s) were discarded and it will be re-requested.",
                    request.RequestId, job.Name, request.Expiration, rows.Count);
                return TimeSpan.Zero;
            }

            logger.LogDebug(
                "Landed {Count} quote row(s) for {Job} expiration {Expiration:yyyy-MM-dd}.",
                rows.Count, job.Name, request.Expiration);
            return TimeSpan.Zero;
        }
        catch (ThetaDataNoDataException ex)
        {
            logger.LogInformation(
                "No quotes for {Job} expiration {Expiration:yyyy-MM-dd} in range: {Message}",
                job.Name, request.Expiration, ex.Message);
            await store.MarkOutcomeAsync(request.RequestId, OwnerId, OptionChainRequestState.Empty, ex.Message, cancellationToken);
            return TimeSpan.Zero;
        }
        catch (ThetaDataSubscriptionException ex)
        {
            logger.LogWarning(
                "Request {RequestId} of {Job} failed permanently — subscription does not cover this symbol: {Message}",
                request.RequestId, job.Name, ex.Message);
            await store.MarkOutcomeAsync(request.RequestId, OwnerId, OptionChainRequestState.Permanent, ex.Message, cancellationToken);
            return TimeSpan.Zero;
        }
        catch (ThetaDataVersionException ex)
        {
            logger.LogError(
                "Request {RequestId} of {Job} failed permanently — the Terminal rejected this client's API " +
                "version: {Message}. This needs a human (Terminal or client upgrade), not a retry.",
                request.RequestId, job.Name, ex.Message);
            await store.MarkOutcomeAsync(request.RequestId, OwnerId, OptionChainRequestState.Permanent, ex.Message, cancellationToken);
            return TimeSpan.Zero;
        }
        catch (InvalidOperationException ex)
        {
            // Unreachable Terminal, malformed response, or a genuine parsing defect all land here.
            // Treated as retryable/transient rather than permanent: the same request against a
            // Terminal that has come back up, or after a defect fix, may well succeed, and marking
            // it permanent on a transient condition would silently and irreversibly lose the
            // expiration the way the backfill coordinator's remarks warn against.
            logger.LogWarning(
                "Request {RequestId} of {Job} (expiration {Expiration:yyyy-MM-dd}) failed on attempt {Attempt}: {Message}",
                request.RequestId, job.Name, request.Expiration, request.Attempts, ex.Message);
            await store.MarkOutcomeAsync(request.RequestId, OwnerId, OptionChainRequestState.Failed, ex.Message, cancellationToken);
            return TimeSpan.FromSeconds(_options.TransientBackoffSeconds);
        }
    }
}
