using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Universe;

namespace TradingStuff.ResearchService.Recording;

/// <summary>
/// Keeps standing subscriptions alive for the core underlyings and the current option-node
/// assignments: acquires leases, heartbeats them, and re-attempts anything missing on a timer.
/// </summary>
/// <remarks>
/// Core underlyings for Phase 1: SPX, VIX, SPY. ES is deliberately deferred to Phase 2 — resolving
/// a live front-month futures contract needs expiry/roll logic (which Phase 2's ES contract walker
/// already owns for backfill), and duplicating that logic here would be exactly the kind of
/// premature abstraction the plan argues against. See docs/STATE.md.
/// <para>
/// This class only tracks lease ids in memory; it is not the source of truth for what SHOULD be
/// recorded (that is <c>research.option_nodes</c>/<c>node_assignments</c>, read fresh from
/// <see cref="NodeSelector"/> on every retry pass) or for what IS being recorded (the gateway's own
/// lease table). A restart of this process simply re-derives both and re-leases; nothing is lost
/// beyond the brief recording gap the restart itself causes.
/// </para>
/// </remarks>
public sealed class RecorderOrchestrator(
    IbkrGatewayClient gateway,
    NodeSelector nodeSelector,
    ILogger<RecorderOrchestrator> logger)
    : BackgroundService
{
    private static readonly string[] CoreUnderlyings = ["SPX", "VIX", "SPY"];
    private static readonly TimeSpan LeaseHeartbeatInterval = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, TrackedLease> _underlyingLeases = [];

    /// <summary>
    /// Keyed by nodeId, not conId: a node's assignment can change (its old conId simply stops
    /// appearing in <see cref="NodeSelector.GetCurrentAssignmentsAsync"/>'s result). Keying by
    /// conId alone made a reassignment unobservable — the old conId's dictionary entry was never
    /// visited again, never released, and kept being heartbeated forever, permanently leaking a
    /// research line per reassignment.
    /// </summary>
    private readonly Dictionary<short, TrackedLease> _nodeLeases = [];

    /// <summary>Test seam: how often the heartbeat pass runs. Production uses the default.</summary>
    internal TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Test seam: how often the bootstrap/lease-reconciliation pass runs.</summary>
    internal TimeSpan RetryEvery { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a lease may go without an ACKNOWLEDGED heartbeat before this process concludes the
    /// gateway has evicted it and stops treating its map entry as real.
    /// </summary>
    /// <remarks>
    /// Deliberately not "the first heartbeat that fails to reach the gateway". Dropping early and
    /// re-granting would double-book a line that the gateway still holds — the old lease keeps a TWS
    /// market-data line for up to three more missed heartbeats, and the line budget (90, of which
    /// ~57 are the recording grid) has no room to carry both copies. So the rule is: wait until the
    /// gateway has CERTAINLY released the line, then re-acquire.
    /// <para>
    /// <c>SubscriptionManager.ActiveLease.IsExpiredAt</c> expires a lease at three times its
    /// requested heartbeat interval measured from the last heartbeat it RECEIVED, noticed by a sweep
    /// that runs every 10 s. Measuring here from the last heartbeat we saw ACKNOWLEDGED puts this
    /// clock at or behind the gateway's, and the extra 15 s covers that sweep's latency — so when
    /// this elapses, either the gateway evicted the lease or the gateway process is gone. Both mean
    /// the same thing to us: the map entry is fiction and recording for that conId has stopped.
    /// </para>
    /// </remarks>
    internal TimeSpan AssumeEvictedAfter { get; init; } =
        (LeaseHeartbeatInterval * 3) + TimeSpan.FromSeconds(15);

    /// <summary>What a heartbeat attempt actually established.</summary>
    private enum HeartbeatOutcome
    {
        /// <summary>The gateway renewed the lease.</summary>
        Acknowledged,

        /// <summary>The gateway answered and does not know this lease — it is already gone.</summary>
        Refused,

        /// <summary>
        /// The request never got an answer. Says nothing about the lease, only about the network:
        /// distinct from <see cref="Refused"/> precisely because the gateway may still be holding it.
        /// </summary>
        Undeliverable,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRetry = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            // ONE try around the whole loop body, heartbeats included. Anything that escapes
            // ExecuteAsync faults the background service, and the default
            // BackgroundServiceExceptionBehavior.StopHost (nothing in src/ configures otherwise)
            // stops the ENTIRE ResearchService host — PartitionMaintainer,
            // SessionCalendarSynchronizer, BackfillCoordinator and EsContractWalker with it.
            //
            // That is not hypothetical: the heartbeat pass used to sit outside this try, and
            // IbkrGatewayClient.HeartbeatAsync posts without classifying transport failures (unlike
            // its history methods). A gateway restart — routine, it owns the TWS socket and this
            // service is designed to outlive it — therefore threw HttpRequestException at the next
            // 20 s tick, or BrokenCircuitException from the resilience handler, or
            // TaskCanceledException at the attempt timeout, straight out of here. The host stopped;
            // and a stopped PartitionMaintainer is not benign, because a row that lands in a DEFAULT
            // partition makes Postgres permanently refuse to create the real partition for that date
            // (docs/STATE.md, Phase 1). The only signal was one LogCritical and a clean-looking
            // shutdown, with recording continuing for ~180 s first, so it read as an orderly stop
            // rather than a crash — and local `aspire start` has no supervisor to restart it. The
            // prospective option data lost meanwhile is unrecoverable.
            try
            {
                if (DateTimeOffset.UtcNow >= nextRetry)
                {
                    await RunLeasePassAsync(stoppingToken);
                    nextRetry = DateTimeOffset.UtcNow + RetryEvery;
                }

                if (await HeartbeatAllAsync(DateTimeOffset.UtcNow, stoppingToken))
                {
                    // Something was dropped, so the maps no longer describe what the gateway holds.
                    // Re-derive immediately instead of recording nothing for the rest of the retry
                    // interval: a gateway restart drops every lease at once, and waiting out the
                    // full two minutes on top of the ~180 s it took to establish that would triple
                    // the outage for data that cannot be collected again.
                    nextRetry = DateTimeOffset.MinValue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recorder orchestration pass failed; will retry.");
                nextRetry = DateTimeOffset.UtcNow + RetryEvery;
            }

            try
            {
                await Task.Delay(HeartbeatEvery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One reconciliation pass: re-derive what SHOULD be recorded and lease anything not already
    /// leased. Bootstrap runs every pass, not just once: it is idempotent (a node already pointing
    /// at the right conId is left alone), and re-running it is how a node that failed to resolve on
    /// a prior pass — schema not ready yet, TWS briefly unreachable — gets picked up without a
    /// separate recovery path. Internal so a test can drive one pass without the loop's timers.
    /// </summary>
    internal async Task RunLeasePassAsync(CancellationToken cancellationToken)
    {
        await nodeSelector.BootstrapAssignmentsAsync(cancellationToken);
        await EnsureUnderlyingLeasesAsync(cancellationToken);
        await EnsureNodeLeasesAsync(cancellationToken);
    }

    /// <summary>Test seam: how many leases this process currently believes the gateway is holding.</summary>
    internal int TrackedLeaseCount => _underlyingLeases.Count + _nodeLeases.Count;

    private async Task EnsureUnderlyingLeasesAsync(CancellationToken cancellationToken)
    {
        foreach (var symbol in CoreUnderlyings)
        {
            if (_underlyingLeases.ContainsKey(symbol))
            {
                continue;
            }

            var resolution = await gateway.ResolveUnderlyingAsync(symbol, cancellationToken);

            if (resolution is null)
            {
                logger.LogWarning("Could not resolve underlying {Symbol}; will retry.", symbol);
                continue;
            }

            // resolution.Exchange, NOT "SMART": SPX and VIX are indices, and an index conId on
            // SMART is rejected by TWS with error 200 and streams zero ticks (verified live). The
            // resolver already returns the instrument's real exchange — passing it on is the whole
            // fix, and discarding it is what previously made SPX/VIX record nothing in silence.
            var request = new SubscriptionLeaseRequest(
                resolution.ConId, LeasePriority.CoreRecording, RecordToDatabase: true, IsOption: false,
                GenericTickList: string.Empty, HeartbeatIntervalSeconds: (int)LeaseHeartbeatInterval.TotalSeconds,
                Exchange: resolution.Exchange);

            var lease = await gateway.GrantSubscriptionAsync(request, cancellationToken);

            if (lease is null)
            {
                logger.LogWarning("Could not grant a subscription lease for underlying {Symbol}; will retry.", symbol);
                continue;
            }

            _underlyingLeases[symbol] = new TrackedLease(lease.LeaseId, resolution.ConId, DateTimeOffset.UtcNow);

            logger.LogInformation(
                "Recording underlying {Symbol} (conId {ConId}) under lease {LeaseId}.",
                symbol, resolution.ConId, lease.LeaseId);
        }
    }

    private async Task EnsureNodeLeasesAsync(CancellationToken cancellationToken)
    {
        var assignments = await nodeSelector.GetCurrentAssignmentsAsync(cancellationToken);
        var currentNodeIds = new HashSet<short>();

        foreach (var assignment in assignments)
        {
            currentNodeIds.Add(assignment.NodeId);

            if (_nodeLeases.TryGetValue(assignment.NodeId, out var existing))
            {
                if (existing.ConId == assignment.ConId)
                {
                    continue; // already leased under the right conId
                }

                // Reassigned since the last pass: the old lease's conId is stale — release it
                // before granting the new one so it stops occupying a research line for nothing.
                logger.LogInformation(
                    "Node {NodeId} reassigned from conId {OldConId} to {NewConId}; releasing the old lease.",
                    assignment.NodeId, existing.ConId, assignment.ConId);

                await gateway.ReleaseSubscriptionAsync(existing.LeaseId, cancellationToken);
                _nodeLeases.Remove(assignment.NodeId);
            }

            var request = new SubscriptionLeaseRequest(
                assignment.ConId, LeasePriority.CoreRecording, RecordToDatabase: true, IsOption: true,
                // 100 = per-contract volume, 101 = per-contract open interest, 106 = option IV.
                GenericTickList: "100,101,106", HeartbeatIntervalSeconds: (int)LeaseHeartbeatInterval.TotalSeconds,
                // Explicit rather than relying on the default: SMART is genuinely correct for SPX
                // options (verified live — an SPXW conId on SMART streamed 109 ticks with Greeks),
                // but it is correct here by fact, not by being the fallback.
                Exchange: "SMART");

            var lease = await gateway.GrantSubscriptionAsync(request, cancellationToken);

            if (lease is null)
            {
                logger.LogWarning(
                    "Could not grant a subscription lease for node {NodeId} (conId {ConId}); will retry.",
                    assignment.NodeId, assignment.ConId);
                continue;
            }

            _nodeLeases[assignment.NodeId] = new TrackedLease(lease.LeaseId, assignment.ConId, DateTimeOffset.UtcNow);
        }

        // A node that no longer appears in the current assignments at all (rather than merely
        // having moved to a new conId) would otherwise leak the same way — reconcile defensively.
        foreach (var staleNodeId in _nodeLeases.Keys.Except(currentNodeIds).ToArray())
        {
            var stale = _nodeLeases[staleNodeId];
            logger.LogWarning(
                "Node {NodeId} no longer has a current assignment; releasing its lease {LeaseId}.",
                staleNodeId, stale.LeaseId);
            await gateway.ReleaseSubscriptionAsync(stale.LeaseId, cancellationToken);
            _nodeLeases.Remove(staleNodeId);
        }
    }

    /// <summary>
    /// Renews every tracked lease. <paramref name="now"/> is a parameter rather than read inside so
    /// a test can reach the assumed-evicted path without waiting out a real three-minute deadline —
    /// the same seam <c>SubscriptionManager.SweepExpiredAsync</c> uses.
    /// </summary>
    /// <returns>
    /// True when at least one lease was dropped from the maps, so the caller knows the maps and the
    /// gateway have diverged and re-derives now rather than on the ordinary retry interval.
    /// </returns>
    internal async Task<bool> HeartbeatAllAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pass = new HeartbeatPass(now);

        await HeartbeatGroupAsync(_underlyingLeases, "underlying", pass, cancellationToken);
        await HeartbeatGroupAsync(_nodeLeases, "node", pass, cancellationToken);

        if (pass.Undeliverable > 0)
        {
            // One line per pass, not one per lease: with ~57 leases and a 20 s tick, per-lease
            // logging turns an unreachable gateway into ~170 warnings a minute, which buries the
            // drop messages below that actually change what is being recorded.
            logger.LogWarning(
                pass.FirstFailure,
                "Could not deliver {Count} of {Total} lease heartbeat(s); the gateway is unreachable. " +
                "Leases are kept until {Horizon} has passed without an acknowledged heartbeat, by " +
                "which point the gateway has certainly evicted them, and are then re-acquired.",
                pass.Undeliverable,
                _underlyingLeases.Count + _nodeLeases.Count,
                AssumeEvictedAfter);
        }

        return pass.Dropped;
    }

    private async Task HeartbeatGroupAsync<TKey>(
        Dictionary<TKey, TrackedLease> leases,
        string scope,
        HeartbeatPass pass,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        foreach (var (key, lease) in leases.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            switch (await TryHeartbeatAsync(lease, pass, cancellationToken))
            {
                case HeartbeatOutcome.Acknowledged:
                    lease.LastAcknowledgedAt = pass.Now;
                    break;

                case HeartbeatOutcome.Refused:
                    logger.LogWarning(
                        "Heartbeat refused for {Scope} {Key} (conId {ConId}) lease {LeaseId}; the gateway " +
                        "no longer knows it, so it was evicted or released. Re-acquiring.",
                        scope, key, lease.ConId, lease.LeaseId);
                    leases.Remove(key);
                    pass.Dropped = true;
                    break;

                case HeartbeatOutcome.Undeliverable
                    when pass.Now - lease.LastAcknowledgedAt > AssumeEvictedAfter:
                    logger.LogWarning(
                        "No heartbeat has reached the gateway for {Scope} {Key} (conId {ConId}) lease " +
                        "{LeaseId} since {LastAcknowledgedAt:O}; the gateway has evicted it by now, so " +
                        "recording for this conId has stopped. Dropping the lease and re-acquiring.",
                        scope, key, lease.ConId, lease.LeaseId, lease.LastAcknowledgedAt);
                    leases.Remove(key);
                    pass.Dropped = true;
                    break;

                // Undeliverable and still inside the horizon: keep it. The gateway may well still
                // hold the lease and be recording perfectly well; re-granting now would leave the
                // old subscription occupying a line until it expires anyway.
            }
        }
    }

    private async Task<HeartbeatOutcome> TryHeartbeatAsync(
        TrackedLease lease, HeartbeatPass pass, CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.HeartbeatAsync(lease.LeaseId, cancellationToken)
                ? HeartbeatOutcome.Acknowledged
                : HeartbeatOutcome.Refused;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // IbkrGatewayClient.HeartbeatAsync posts and reads IsSuccessStatusCode with no try/catch
            // of its own, so every transport failure arrives here as an exception: connection
            // refused as HttpRequestException, an open circuit as BrokenCircuitException, the
            // resilience handler's per-attempt timeout as TaskCanceledException. "Could not ask" is
            // NOT "the answer was no" — see the caller — so it gets its own outcome rather than
            // being folded into the false that HeartbeatAsync returns for a 404.
            pass.Undeliverable++;
            pass.FirstFailure ??= ex;

            return HeartbeatOutcome.Undeliverable;
        }
    }

    /// <summary>
    /// One lease this process believes the gateway is holding. Mutable rather than a record because
    /// <see cref="LastAcknowledgedAt"/> is the running measurement the assumed-evicted rule reads.
    /// </summary>
    private sealed class TrackedLease(Guid leaseId, int conId, DateTimeOffset acknowledgedAt)
    {
        public Guid LeaseId { get; } = leaseId;

        public int ConId { get; } = conId;

        /// <summary>
        /// When the gateway last confirmed this lease. Seeded at grant, which is itself a
        /// confirmation that the gateway had it at that instant.
        /// </summary>
        public DateTimeOffset LastAcknowledgedAt { get; set; } = acknowledgedAt;
    }

    /// <summary>Per-pass accumulator, so transport failures are reported once rather than per lease.</summary>
    private sealed class HeartbeatPass(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; } = now;

        public int Undeliverable { get; set; }

        public Exception? FirstFailure { get; set; }

        public bool Dropped { get; set; }
    }
}
