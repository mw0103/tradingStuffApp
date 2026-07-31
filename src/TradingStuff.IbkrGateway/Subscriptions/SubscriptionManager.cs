using System.Collections.Concurrent;
using IBApi;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.ResearchContracts;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway.Subscriptions;

/// <summary>
/// Standing market-data subscriptions, leased rather than fire-and-forget: a caller acquires a
/// lease, heartbeats it, and the manager releases (and, if it never comes back, evicts) it.
/// </summary>
/// <remarks>
/// Runs as a hosted service so it can both sweep expired leases on a timer and react to
/// <see cref="IbkrConnection.SubscriptionsMustReplay"/> without a separate wiring point. Every
/// subscription goes through <see cref="Pacing.PacedSocket"/>, which is the actual pacing/line
/// enforcement — this class owns lease semantics (priority, heartbeat, replay), not budgets.
/// </remarks>
public sealed class SubscriptionManager : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly IbkrConnection _connection;
    private readonly PacedSocket _socket;
    private readonly ObservationRecorder _recorder;
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveLease> _leases = new();
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private int _replayPending;

    public SubscriptionManager(
        IbkrConnection connection,
        PacedSocket socket,
        ObservationRecorder recorder,
        ILogger<SubscriptionManager> logger)
    {
        _connection = connection;
        _socket = socket;
        _recorder = recorder;
        _logger = logger;

        // Fired from whatever thread raised the underlying TWS event (often the EReader pump) —
        // must not block here.
        _connection.SubscriptionsMustReplay += () => _ = ReplayAsync(CancellationToken.None);
    }

    public IReadOnlyList<SubscriptionLease> ActiveLeases() =>
        [.. _leases.Values.Select(lease => lease.ToRecord())];

    public async Task<SubscriptionLease> GrantAsync(SubscriptionLeaseRequest request, CancellationToken cancellationToken)
    {
        if (request.Priority == LeasePriority.ExecutionReserved)
        {
            throw new ArgumentException(
                "LeasePriority.ExecutionReserved is reserved for the gateway's own transient " +
                "execution-path quotes and cannot be requested through the standing-lease API.",
                nameof(request));
        }

        var leaseId = Guid.NewGuid();
        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, request.HeartbeatIntervalSeconds));
        var genericTicks = request.GenericTickList ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(request.Exchange))
        {
            // TWS rejects a conId with no exchange outright (error 321, "Please enter exchange"),
            // so refuse here with a message that names the cause rather than letting it surface as
            // an opaque broker error on a subscription that then silently records nothing.
            throw new ArgumentException(
                $"An exchange is required to subscribe to conId {request.ConId}; TWS rejects a " +
                "conId-only request. Supply the instrument's real exchange (index conIds are NOT " +
                "reachable via SMART).",
                nameof(request));
        }

        var active = new ActiveLease
        {
            LeaseId = leaseId,
            ConId = request.ConId,
            Exchange = request.Exchange,
            Priority = request.Priority,
            RecordToDatabase = request.RecordToDatabase,
            IsOption = request.IsOption,
            GenericTickList = genericTicks,
            HeartbeatInterval = heartbeatInterval,
            GrantedAt = now,
            LastHeartbeat = now,
        };

        await IssueAsync(active, markFirstTickAsReplay: false, cancellationToken);

        _leases[leaseId] = active;

        _logger.LogInformation(
            "Granted subscription lease {LeaseId} for conId {ConId} (priority {Priority}, recording {Recording}).",
            leaseId, request.ConId, request.Priority, request.RecordToDatabase);

        return active.ToRecord();
    }

    /// <summary>Renews a lease. Returns false when the lease id is unknown (already released or evicted).</summary>
    public bool Heartbeat(Guid leaseId)
    {
        if (!_leases.TryGetValue(leaseId, out var active))
        {
            return false;
        }

        lock (active.Gate)
        {
            active.LastHeartbeat = DateTimeOffset.UtcNow;
        }

        return true;
    }

    public async Task<bool> ReleaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (!_leases.TryRemove(leaseId, out var active))
        {
            return false;
        }

        await TeardownAsync(active, cancellationToken);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before anything is subscribed: any gap still open in the table belongs to a process that
        // is gone, and left alone it would count against every future coverage window.
        await _recorder.ReconcileOrphanedGapsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepExpiredAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Subscription lease sweep failed.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var active in _leases.Values)
        {
            bool expired;

            lock (active.Gate)
            {
                // 3 missed heartbeats, per the roadmap's lease semantics.
                expired = now - active.LastHeartbeat > active.HeartbeatInterval * 3;
            }

            if (!expired || !_leases.TryRemove(active.LeaseId, out _))
            {
                continue;
            }

            _logger.LogWarning(
                "Lease {LeaseId} (conId {ConId}) expired without a heartbeat; evicting.",
                active.LeaseId, active.ConId);

            var scope = ObservationRecorder.LeaseScope(active.LeaseId);

            if (active.RecordToDatabase)
            {
                await _recorder.OpenGapAsync(scope, "line_evicted", cancellationToken);
            }

            await TeardownAsync(active, cancellationToken);

            if (active.RecordToDatabase)
            {
                // Bounded at teardown, NOT left open. An earlier version left it open on the
                // reasoning that this lease is finished permanently, which is true — but a gap row
                // is not read as "this lease ended", it is read by CoverageMonitor as "recording is
                // missing, and still missing", against every window from here to eternity. Since
                // ResearchService is expected to redeploy constantly (that is the whole reason the
                // recorder lives in the gateway), each redeploy abandons its ~54 node leases, and
                // one redeploy would otherwise poison coverage forever. Observed live: 80 immortal
                // gaps from a single afternoon's restarts.
                //
                // 'inferred', because nothing watched recording resume — after teardown no data was
                // owed on this scope at all. The genuine question ("was this conId covered?") is
                // answered by CoverageMonitor's per-conId tick counts, which span lease changes;
                // this row only explains where one lease's stream stopped.
                await _recorder.CloseGapAsync(scope, observed: false);
            }
        }
    }

    /// <summary>
    /// Re-issues every active lease's <c>reqMktData</c> against the (possibly brand-new) socket, in
    /// priority order.
    /// </summary>
    /// <remarks>
    /// A trigger arriving while a pass is already running does not run a second pass concurrently
    /// — but it must not be silently discarded either: a lease that failed to reissue partway
    /// through the in-flight pass has no other retry path (<see cref="SweepExpiredAsync"/> only
    /// evicts on missed heartbeats, unrelated to replay success), so a dropped trigger could leave
    /// that lease dead for the rest of the session. <see cref="_replayPending"/> coalesces instead
    /// of dropping: a trigger that loses the race to <see cref="_replayGate"/> flags that one more
    /// full pass is owed, and the in-flight pass checks that flag before releasing the gate and
    /// loops for another pass if it was set — guaranteeing every trigger's lease set is eventually
    /// revisited.
    /// </remarks>
    internal async Task ReplayAsync(CancellationToken cancellationToken)
    {
        if (!await _replayGate.WaitAsync(0, cancellationToken))
        {
            Interlocked.Exchange(ref _replayPending, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _replayPending, 0);
                await RunReplayPassAsync(cancellationToken);
            }
            while (Volatile.Read(ref _replayPending) == 1 && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private async Task RunReplayPassAsync(CancellationToken cancellationToken)
    {
        var ordered = _leases.Values.OrderBy(lease => lease.Priority).ToArray();

        foreach (var active in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!_leases.ContainsKey(active.LeaseId))
            {
                continue; // released while this pass was running
            }

            try
            {
                var previousTicker = active.TickerId;

                await IssueAsync(active, markFirstTickAsReplay: true, cancellationToken);

                // The old ticker's registration (if this lease had one) is only cleaned up here,
                // on a SUCCESSFUL reissue — not by registry.FailAll, which only fires on the
                // socket dropping outright (connectionClosed / error 1100). TWS's 1101 notice
                // ("connectivity restored, data lost") replays without the socket ever dropping,
                // so without this the dead sink's registry entry would leak forever.
                if (previousTicker != 0 && previousTicker != active.TickerId)
                {
                    _connection.Registry.Remove(previousTicker);
                }

                _logger.LogInformation(
                    "Replayed subscription lease {LeaseId} for conId {ConId}.", active.LeaseId, active.ConId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Could not replay lease {LeaseId} for conId {ConId}; will retry on the next trigger.",
                    active.LeaseId, active.ConId);

                // The lease's LineLease (if any) predates the reconnect's ResetLineLedgerForReconnect
                // and no longer corresponds to anything real. Left in place, a later teardown of
                // this lease would dispose it anyway, decrementing the pacing ledger for a line that
                // isn't actually held — silently under-reporting real usage and letting the governor
                // over-admit. Clearing it here means teardown finds nothing to (wrongly) release.
                lock (active.Gate)
                {
                    active.LineLease = null;
                }
            }
        }
    }

    /// <summary>Issues (or re-issues) the <c>reqMktData</c> call for one lease and records the new ticker/line.</summary>
    private async Task IssueAsync(ActiveLease active, bool markFirstTickAsReplay, CancellationToken cancellationToken)
    {
        var ticker = _connection.Registry.NextRequestId();

        RecordingTickSink? sink = active.RecordToDatabase
            ? new RecordingTickSink(
                active.ConId, active.LeaseId, active.IsOption, markFirstTickAsReplay, _recorder,
                onFailed: ex => OnSinkFailed(active, ex))
            : null;

        if (sink is not null)
        {
            _connection.Registry.Register(ticker, sink);
        }

        // Exchange comes from the lease, never a hardcoded "SMART": verified against live paper TWS
        // that an index conId on SMART is rejected with error 200 and streams zero ticks, while the
        // same conId on its native exchange (CBOE) streams normally. See SubscriptionLeaseRequest.
        var contract = new IbContract { ConId = active.ConId, Exchange = active.Exchange };

        LineLease lineLease;

        try
        {
            lineLease = await _socket.ReqMktDataAsync(
                ticker, contract, active.GenericTickList, snapshot: false, regulatorySnapshot: false,
                mktDataOptions: null, LineClass.Research, cancellationToken);
        }
        catch
        {
            if (sink is not null)
            {
                _connection.Registry.Remove(ticker);
            }

            throw;
        }

        lock (active.Gate)
        {
            // Deliberately not disposing the previous LineLease on a replay: after a reconnect the
            // pacing governor's ledger has already been zeroed (IbkrConnection.ResetLineLedgerForReconnect),
            // so the old lease object no longer corresponds to anything real and disposing it would
            // just be a harmless no-op at best — dropping the reference is simpler and equally safe.
            active.TickerId = ticker;
            active.LineLease = lineLease;
        }
    }

    private void OnSinkFailed(ActiveLease active, Exception error)
    {
        _logger.LogDebug(error, "Recording sink for lease {LeaseId} failed; the connection is down.", active.LeaseId);

        if (active.RecordToDatabase)
        {
            _ = _recorder.OpenGapAsync(ObservationRecorder.LeaseScope(active.LeaseId), "disconnect", CancellationToken.None);
        }
    }

    private async Task TeardownAsync(ActiveLease active, CancellationToken cancellationToken)
    {
        _connection.Registry.Remove(active.TickerId);

        LineLease? lineLease;

        lock (active.Gate)
        {
            lineLease = active.LineLease;
            active.LineLease = null;
        }

        if (lineLease is not null)
        {
            await _socket.CancelMktDataAsync(active.TickerId, lineLease);
        }
    }

    private sealed class ActiveLease
    {
        public readonly Lock Gate = new();

        public required Guid LeaseId { get; init; }
        public required int ConId { get; init; }
        public required string Exchange { get; init; }
        public required LeasePriority Priority { get; init; }
        public required bool RecordToDatabase { get; init; }
        public required bool IsOption { get; init; }
        public required string GenericTickList { get; init; }
        public required TimeSpan HeartbeatInterval { get; init; }
        public required DateTimeOffset GrantedAt { get; init; }

        public DateTimeOffset LastHeartbeat;
        public int TickerId;
        public LineLease? LineLease;

        public SubscriptionLease ToRecord()
        {
            lock (Gate)
            {
                return new SubscriptionLease(
                    LeaseId, ConId, Priority, RecordToDatabase, GrantedAt, LastHeartbeat + (HeartbeatInterval * 3));
            }
        }
    }
}
