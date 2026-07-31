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
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LeaseHeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, Guid> _underlyingLeases = [];

    /// <summary>
    /// Keyed by nodeId, not conId: a node's assignment can change (its old conId simply stops
    /// appearing in <see cref="NodeSelector.GetCurrentAssignmentsAsync"/>'s result). Keying by
    /// conId alone made a reassignment unobservable — the old conId's dictionary entry was never
    /// visited again, never released, and kept being heartbeated forever, permanently leaking a
    /// research line per reassignment.
    /// </summary>
    private readonly Dictionary<short, (Guid LeaseId, int ConId)> _nodeLeases = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRetry = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextRetry)
            {
                try
                {
                    // Bootstrap runs every retry pass, not just once: it is idempotent (a node
                    // already pointing at the right conId is left alone), and re-running it is how
                    // a node that failed to resolve on a prior pass — schema not ready yet, TWS
                    // briefly unreachable — gets picked up without a separate recovery path.
                    await nodeSelector.BootstrapAssignmentsAsync(stoppingToken);
                    await EnsureUnderlyingLeasesAsync(stoppingToken);
                    await EnsureNodeLeasesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Recorder orchestration pass failed; will retry.");
                }

                nextRetry = DateTimeOffset.UtcNow + RetryEvery;
            }

            await HeartbeatAllAsync(stoppingToken);

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

            _underlyingLeases[symbol] = lease.LeaseId;

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

            _nodeLeases[assignment.NodeId] = (lease.LeaseId, assignment.ConId);
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

    private async Task HeartbeatAllAsync(CancellationToken cancellationToken)
    {
        foreach (var (symbol, leaseId) in _underlyingLeases.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!await gateway.HeartbeatAsync(leaseId, cancellationToken))
            {
                logger.LogWarning(
                    "Heartbeat failed for underlying {Symbol} lease {LeaseId}; assuming it was evicted.",
                    symbol, leaseId);
                _underlyingLeases.Remove(symbol);
            }
        }

        foreach (var (nodeId, lease) in _nodeLeases.ToArray())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!await gateway.HeartbeatAsync(lease.LeaseId, cancellationToken))
            {
                logger.LogWarning(
                    "Heartbeat failed for node {NodeId} (conId {ConId}) lease {LeaseId}; assuming it was evicted.",
                    nodeId, lease.ConId, lease.LeaseId);
                _nodeLeases.Remove(nodeId);
            }
        }
    }
}
