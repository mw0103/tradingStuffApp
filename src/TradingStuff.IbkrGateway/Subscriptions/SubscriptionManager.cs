using System.Collections.Concurrent;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.ResearchContracts;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway.Subscriptions;

/// <summary>
/// Everything the lease lifetime needs from the TWS socket, behind one seam.
/// </summary>
/// <remarks>
/// Exists so the lease state machine — grant, replay, terminate, and the interleavings between them
/// — can be exercised without a socket. Those interleavings are where this area's defects have
/// actually been, and they are invisible to a live smoke test: a lease issued into a torn-down state
/// leaks a market-data line silently, and there are only ~80 research lines before the recorder can
/// no longer subscribe to anything at all.
/// </remarks>
internal interface ISubscriptionTransport
{
    int NextTickerId();

    void RegisterSink(int tickerId, ITickSink sink);

    void RemoveSink(int tickerId);

    Task<LineLease> SubscribeAsync(
        int tickerId, int conId, string exchange, string genericTickList, CancellationToken cancellationToken);

    Task UnsubscribeAsync(int tickerId, LineLease lineLease);
}

/// <summary>The real transport: the request registry plus the paced socket.</summary>
internal sealed class PacedSocketTransport(IbkrConnection connection, PacedSocket socket) : ISubscriptionTransport
{
    public int NextTickerId() => connection.Registry.NextRequestId();

    public void RegisterSink(int tickerId, ITickSink sink) => connection.Registry.Register(tickerId, sink);

    public void RemoveSink(int tickerId) => connection.Registry.Remove(tickerId);

    public Task<LineLease> SubscribeAsync(
        int tickerId, int conId, string exchange, string genericTickList, CancellationToken cancellationToken) =>
        // Exchange comes from the lease, never a hardcoded "SMART": verified against live paper TWS
        // that an index conId on SMART is rejected with error 200 and streams zero ticks, while the
        // same conId on its native exchange (CBOE) streams normally. See SubscriptionLeaseRequest.
        socket.ReqMktDataAsync(
            tickerId,
            new IbContract { ConId = conId, Exchange = exchange },
            genericTickList,
            snapshot: false,
            regulatorySnapshot: false,
            mktDataOptions: null,
            LineClass.Research,
            cancellationToken);

    public Task UnsubscribeAsync(int tickerId, LineLease lineLease) => socket.CancelMktDataAsync(tickerId, lineLease);
}

/// <summary>
/// Standing market-data subscriptions, leased rather than fire-and-forget: a caller acquires a
/// lease, heartbeats it, and the manager releases (and, if it never comes back, evicts) it.
/// </summary>
/// <remarks>
/// Runs as a hosted service so it can both sweep expired leases on a timer and react to
/// <see cref="IbkrConnection.SubscriptionsMustReplay"/> without a separate wiring point. Every
/// subscription goes through <see cref="Pacing.PacedSocket"/>, which is the actual pacing/line
/// enforcement — this class owns lease semantics (priority, heartbeat, replay), not budgets.
/// <para>
/// A lease's life is driven from four paths that interleave freely: the HTTP grant, the HTTP
/// release, the sweep timer, and replay from the TWS pump thread. None of them is quick — issuing a
/// subscription can park for tens of seconds inside the line and message budgets — so every
/// transition is written as a single atomic claim on <see cref="ActiveLease"/> rather than a check
/// followed by an act. See <see cref="TerminateAsync"/> and <see cref="IssueAsync"/>.
/// </para>
/// </remarks>
public sealed class SubscriptionManager : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly ISubscriptionTransport _transport;
    private readonly ObservationRecorder _recorder;
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveLease> _leases = new();
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private int _replayPending;
    private long _replayEpoch;

    public SubscriptionManager(
        IbkrConnection connection,
        PacedSocket socket,
        ObservationRecorder recorder,
        ILogger<SubscriptionManager> logger)
        : this(new PacedSocketTransport(connection, socket), recorder, logger)
    {
        // Fired from whatever thread raised the underlying TWS event (often the EReader pump) —
        // must not block here.
        connection.SubscriptionsMustReplay += () => _ = ReplayAsync(CancellationToken.None);
    }

    /// <summary>Test seam. DI only ever sees the public constructor (it enumerates public ones).</summary>
    internal SubscriptionManager(
        ISubscriptionTransport transport,
        ObservationRecorder recorder,
        ILogger<SubscriptionManager> logger)
    {
        _transport = transport;
        _recorder = recorder;
        _logger = logger;
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

        var leaseId = Guid.NewGuid();

        var active = new ActiveLease(
            leaseId,
            request.ConId,
            request.Exchange,
            request.Priority,
            request.RecordToDatabase,
            request.IsOption,
            request.GenericTickList ?? string.Empty,
            TimeSpan.FromSeconds(Math.Max(5, request.HeartbeatIntervalSeconds)),
            DateTimeOffset.UtcNow);

        // Sampled BEFORE the socket call and re-read after this lease is visible in _leases. A
        // replay pass snapshots _leases when it starts, so a grant whose reqMktData is still in
        // flight at that instant is invisible to that pass — and nothing else ever revisits it:
        // _replayPending only coalesces further TRIGGERS, and this lease is not a trigger, so the
        // pass finishes, the gate releases, and the subscription TWS already discarded is never
        // re-issued. The lease then records nothing for the rest of the session while looking
        // perfectly healthy in ActiveLeases().
        var epochAtIssue = Volatile.Read(ref _replayEpoch);

        await IssueAsync(active, markFirstTickAsReplay: false, cancellationToken);

        _leases[leaseId] = active;

        _logger.LogInformation(
            "Granted subscription lease {LeaseId} for conId {ConId} (priority {Priority}, recording {Recording}).",
            leaseId, request.ConId, request.Priority, request.RecordToDatabase);

        if (Volatile.Read(ref _replayEpoch) != epochAtIssue)
        {
            // The epoch is bumped by the trigger, i.e. at or before any pass takes its snapshot, so
            // this test is conservative in the safe direction: a grant may ask for one redundant
            // pass it did not strictly need, but a grant that a pass missed can never fail to ask.
            _logger.LogInformation(
                "Lease {LeaseId} was granted across a replay trigger; requesting a pass so it is re-issued " +
                "against the current socket.",
                leaseId);

            // Not awaited: the grant itself has succeeded either way, and the caller must not be
            // held behind a full ~54-lease replay. RunCoalescedReplayAsync either runs the pass or
            // hands the obligation to the pass already in flight.
            _ = RunCoalescedReplayAsync(CancellationToken.None);
        }

        return active.ToRecord();
    }

    /// <summary>Renews a lease. Returns false when the lease id is unknown (already released or evicted).</summary>
    public bool Heartbeat(Guid leaseId)
    {
        if (!_leases.TryGetValue(leaseId, out var active))
        {
            return false;
        }

        active.Renew();
        return true;
    }

    public Task<bool> ReleaseAsync(Guid leaseId, CancellationToken cancellationToken) =>
        TerminateAsync(leaseId, evictionReason: null, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retried every sweep until it succeeds rather than attempted once at startup. Any gap still
        // open in the table belongs to a process that is gone, and left alone it counts against every
        // future coverage window — but this runs during startup, when Postgres may still be coming
        // up (Aspire starts containers alongside services) and when whatever killed the previous
        // process may well have taken the database with it. A one-shot here fails exactly in the
        // scenario it exists for, and its failure was previously logged and then forgotten.
        var orphansReconciled = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!orphansReconciled)
            {
                try
                {
                    orphansReconciled = await _recorder.ReconcileOrphanedGapsAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                await SweepExpiredAsync(DateTimeOffset.UtcNow, stoppingToken);
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

    /// <summary>
    /// Evicts every lease past its heartbeat deadline. <paramref name="now"/> is a parameter so a
    /// test can reach the eviction path without waiting out a real 15-second deadline.
    /// </summary>
    internal async Task SweepExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var active in _leases.Values)
        {
            if (!active.IsExpiredAt(now))
            {
                continue;
            }

            if (await TerminateAsync(active.LeaseId, evictionReason: "line_evicted", cancellationToken))
            {
                _logger.LogWarning(
                    "Lease {LeaseId} (conId {ConId}) expired without a heartbeat; evicted.",
                    active.LeaseId, active.ConId);
            }
        }
    }

    /// <summary>
    /// The one and only way a lease ends: it leaves <see cref="_leases"/>, whatever TWS state it
    /// holds is unwound, and its recording gap is bounded.
    /// </summary>
    /// <param name="evictionReason">
    /// Non-null opens a gap for the lease first, so an eviction leaves a row saying where this
    /// lease's stream stopped. Null — an orderly release — only closes whatever is already open.
    /// </param>
    /// <remarks>
    /// Every termination path funnels through here because the two that existed before did NOT do
    /// the same thing: the sweep closed the lease's gap and <c>DELETE /ibkr/subscriptions/{id}</c>
    /// did not. An unended row in <c>recorder_gaps</c> is not read as "this lease is over"; it is
    /// read by CoverageMonitor as an outage still in progress, overlapping every window from that
    /// moment to eternity. And after teardown nothing can ever close it: the sink is unregistered so
    /// no tick can arrive to call <see cref="ObservationRecorder.NotifyGapClosed"/>, and the lease
    /// is out of <see cref="_leases"/> so no later sweep revisits it. Release is the HOT path, not
    /// the exceptional one — RecorderOrchestrator re-derives node assignments every two minutes and
    /// releases every lease whose conId moved, which is every expiry roll and every time spot
    /// crosses a strike. One socket drop across 54 option nodes could strand 54 immortal rows, the
    /// same shape as the 80 already observed live (docs/STATE.md, Phase 1).
    /// <para>
    /// Structural rather than a second copy of the call: <see cref="ActiveLease"/> hands out the
    /// ticker and line to unwind ONLY through <see cref="ActiveLease.TryClaimTermination"/>, and
    /// this is its only caller. A future third termination path cannot tear a lease down without
    /// coming through here, and so cannot forget the close.
    /// </para>
    /// </remarks>
    private async Task<bool> TerminateAsync(Guid leaseId, string? evictionReason, CancellationToken cancellationToken)
    {
        if (!_leases.TryRemove(leaseId, out var active))
        {
            return false;
        }

        // The claim happens BEFORE the transport state is read, under the same lock IssueAsync
        // publishes with, so a reissue racing this termination resolves one of two ways and no
        // other: it published first and its ticker/line come back here to be unwound, or it lost
        // and unwinds its own acquisition. Neither order can leave a live subscription owned by a
        // lease nobody holds.
        //
        // The result is deliberately discarded: TryRemove above already made this call single-entry
        // (a ConcurrentDictionary hands the value to exactly one caller), and were that ever to stop
        // being true, a lost claim yields (0, null) — nothing to unwind — while the gap close below
        // still runs. There is no interleaving in which skipping that close is the right answer.
        _ = active.TryClaimTermination(out var tickerId, out var lineLease);

        var scope = ObservationRecorder.LeaseScope(leaseId);

        // Any gap this lease's sink began opening before that claim has to LAND before the close
        // below, or the close finds nothing to close and the insert outlives it — the same immortal
        // row, arrived at through a different door. Drainable rather than unbounded because
        // TryChainGapWork refuses once the claim is in: the set cannot grow while it is drained.
        await active.DrainGapWorkAsync();

        if (evictionReason is not null && active.RecordToDatabase)
        {
            await _recorder.OpenGapAsync(scope, evictionReason, cancellationToken);
        }

        _transport.RemoveSink(tickerId);

        if (lineLease is not null)
        {
            await _transport.UnsubscribeAsync(tickerId, lineLease);
        }

        // Bounded at teardown, NOT left open. An earlier version left it open on the reasoning that
        // this lease is finished permanently, which is true — but a gap row is not read as "this
        // lease ended", it is read by CoverageMonitor as "recording is missing, and still missing",
        // against every window from here to eternity. Since ResearchService is expected to redeploy
        // constantly (that is the whole reason the recorder lives in the gateway), each redeploy
        // abandons its ~54 node leases, and one redeploy would otherwise poison coverage forever.
        //
        // 'inferred', because nothing watched recording resume — after teardown no data was owed on
        // this scope at all. The genuine question ("was this conId covered?") is answered by
        // CoverageMonitor's per-conId tick counts, which span lease changes; this row only explains
        // where one lease's stream stopped.
        //
        // Unconditional, including for leases that never recorded: CloseGapAsync is a dictionary
        // miss when nothing is open, and "no gap can exist for a lease with RecordToDatabase=false"
        // is exactly the sort of assumption that stops holding the next time a gap reason is added.
        await _recorder.CloseGapAsync(scope, observed: false);

        return true;
    }

    /// <summary>
    /// Re-issues every active lease's <c>reqMktData</c> against the (possibly brand-new) socket, in
    /// priority order. The entry point for "TWS says the subscriptions it held are gone".
    /// </summary>
    internal Task ReplayAsync(CancellationToken cancellationToken)
    {
        // Bumped by the TRIGGER — before any pass takes its lease snapshot — so a grant that samples
        // the epoch before its reqMktData and re-reads it after joining _leases can tell whether a
        // snapshot might have been taken without it. See GrantAsync.
        Interlocked.Increment(ref _replayEpoch);

        return RunCoalescedReplayAsync(cancellationToken);
    }

    /// <summary>
    /// True while a replay pass is still owed to a trigger that has already arrived. Exposed for
    /// the coalescing regression test: once every triggering call has returned, nothing may be
    /// owed. A dropped trigger leaves this stuck on until the next reconnect, and the leases it was
    /// meant to re-issue record nothing in the meantime.
    /// </summary>
    internal bool ReplayPassOwed => Volatile.Read(ref _replayPending) == 1;

    /// <summary>
    /// Fired immediately before the replay gate is released — the one instant at which a trigger
    /// can arrive and find both the gate still held AND the loop that would have served it already
    /// finished deciding.
    /// </summary>
    /// <remarks>
    /// Test-only, and here rather than in the test because the window is two adjacent statements
    /// wide: nothing outside this class can schedule into it. Measured, not assumed — a stress test
    /// firing 160 concurrent triggers against the unfixed version never reproduced it, which is
    /// worse than no test, since it would have reported the defect as absent.
    /// </remarks>
    internal Action? BeforeReplayGateRelease { get; set; }

    /// <summary>Runs a replay pass, or makes sure whoever is already running one runs another.</summary>
    /// <remarks>
    /// A trigger arriving while a pass is already running does not run a second pass concurrently
    /// — but it must not be silently discarded either: a lease that failed to reissue partway
    /// through the in-flight pass has no other retry path (<see cref="SweepExpiredAsync"/> only
    /// evicts on missed heartbeats, unrelated to replay success), so a dropped trigger could leave
    /// that lease dead for the rest of the session. <see cref="_replayPending"/> coalesces instead
    /// of dropping.
    /// <para>
    /// Two details make that actually hold, and the flag is worthless without both. First, it is
    /// raised BEFORE contending for the gate, not after losing: a loser that raises it afterwards
    /// can be overtaken by the holder's own last read of it, and then neither of them runs the pass
    /// the trigger is owed. Second, the holder re-checks the flag AFTER releasing the gate (the
    /// outer loop), because a trigger arriving between the do/while's last read and
    /// <c>Release()</c> still finds the gate held and would otherwise be relying on a loop that has
    /// already decided to exit. Losing the re-contention is fine — it means a new holder exists,
    /// and the flag it re-raised is that holder's obligation.
    /// </para>
    /// <para>
    /// The flag is cleared immediately before each pass and read only after it, which is what makes
    /// the coverage argument work: any trigger whose flag-raise precedes a pass's clear is covered
    /// by that pass's lease snapshot, and any that follows it leaves the flag raised for the two
    /// reads that come after.
    /// </para>
    /// </remarks>
    private async Task RunCoalescedReplayAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _replayPending, 1);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await _replayGate.WaitAsync(0, cancellationToken))
            {
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
                BeforeReplayGateRelease?.Invoke();
                _replayGate.Release();
            }

            if (Volatile.Read(ref _replayPending) == 0)
            {
                return;
            }
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

            // A cheap skip, nothing more. What actually guarantees a lease terminated DURING the
            // await below does not end up owning a live subscription is IssueAsync's
            // publish-or-roll-back — IssueAsync can park for tens of seconds inside the line and
            // message budgets, so no check-then-act here could cover that window. Testing
            // _leases.ContainsKey here, as this used to, bought nothing at all.
            if (active.IsTerminated)
            {
                continue;
            }

            try
            {
                await IssueAsync(active, markFirstTickAsReplay: true, cancellationToken);

                _logger.LogInformation(
                    "Replayed subscription lease {LeaseId} for conId {ConId}.", active.LeaseId, active.ConId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Could not replay lease {LeaseId} for conId {ConId}; will retry on the next trigger.",
                    active.LeaseId, active.ConId);

                active.ForgetLineLease();
            }
        }
    }

    /// <summary>Issues (or re-issues) the <c>reqMktData</c> call for one lease and records the new ticker/line.</summary>
    /// <remarks>
    /// The conditional publish at the end is the whole point of this method's shape. Between the
    /// ticker allocation at the top and the publish, it awaits the line budget (which queues, up to
    /// the 30s acquire timeout) and the message budget (which throttles after a 45-message burst) —
    /// a window in which the sweep or a DELETE can terminate this very lease. Writing the ticker and
    /// line onto a terminated lease strands both: nothing ever disposes the
    /// <see cref="LineLease"/>, so the governor's research-line count is permanently one higher than
    /// reality (and there are only ~80 before <c>AcquireLineAsync</c> times out, grants start
    /// failing, and RecorderOrchestrator quietly stops recording), and nothing removes the registry
    /// entry. So the publish can refuse, and its refusal path unwinds what this call acquired —
    /// which only this call knows about.
    /// </remarks>
    private async Task IssueAsync(ActiveLease active, bool markFirstTickAsReplay, CancellationToken cancellationToken)
    {
        var ticker = _transport.NextTickerId();

        if (active.RecordToDatabase)
        {
            _transport.RegisterSink(
                ticker,
                new RecordingTickSink(
                    active.ConId, active.LeaseId, active.IsOption, markFirstTickAsReplay, _recorder,
                    onFailed: ex => OnSinkFailed(active, ex)));
        }

        LineLease lineLease;

        try
        {
            lineLease = await _transport.SubscribeAsync(
                ticker, active.ConId, active.Exchange, active.GenericTickList, cancellationToken);
        }
        catch
        {
            _transport.RemoveSink(ticker);
            throw;
        }

        if (!active.TryPublishIssued(ticker, lineLease, out var replacedTicker))
        {
            _logger.LogInformation(
                "Lease {LeaseId} was terminated while its subscription was being issued; releasing ticker {TickerId}.",
                active.LeaseId, ticker);

            _transport.RemoveSink(ticker);
            await _transport.UnsubscribeAsync(ticker, lineLease);
            return;
        }

        // The displaced ticker's registration is only cleaned up here, on a SUCCESSFUL reissue —
        // not by registry.FailAll, which only fires on the socket dropping outright
        // (connectionClosed / error 1100). TWS's 1101 notice ("connectivity restored, data lost")
        // replays without the socket ever dropping, so without this the dead sink's registry entry
        // would leak forever. It comes back FROM the publish rather than being read around it: a
        // ticker read outside that lock can be one a concurrent termination has already cancelled.
        if (replacedTicker != 0 && replacedTicker != ticker)
        {
            _transport.RemoveSink(replacedTicker);
        }
    }

    /// <remarks>
    /// Runs on the EReader pump thread, so it starts the gap open and returns — but chained onto the
    /// lease rather than fired loose, and refused outright once the lease has terminated. A socket
    /// drop faults every sink at once (<see cref="IbkrRequestRegistry.FailAll"/>), which is exactly
    /// when the sweep is evicting the leases the same outage abandoned, so "a gap opening while a
    /// lease is torn down" is the ordinary case here rather than a corner. Loose, that insert can
    /// land after the termination's close has already found nothing to close, leaving the row
    /// unbounded forever.
    /// </remarks>
    private void OnSinkFailed(ActiveLease active, Exception error)
    {
        _logger.LogDebug(error, "Recording sink for lease {LeaseId} failed; the connection is down.", active.LeaseId);

        if (!active.RecordToDatabase)
        {
            return;
        }

        var scope = ObservationRecorder.LeaseScope(active.LeaseId);

        if (!active.TryChainGapWork(() => _recorder.OpenGapAsync(scope, "disconnect", CancellationToken.None)))
        {
            // Not a loss: after termination no data is owed on this scope, and a row opened now is
            // one nothing would ever close.
            _logger.LogDebug(
                "Sink for already-terminated lease {LeaseId} failed; no gap opened.", active.LeaseId);
        }
    }

    /// <summary>
    /// One lease's mutable state, and the only place its transport state (ticker id and line lease)
    /// can be read or written.
    /// </summary>
    /// <remarks>
    /// Every mutable field lives behind <see cref="_gate"/> and is reachable only through a method
    /// that performs its check and its mutation as one step, because four code paths drive this
    /// object concurrently and none of them is fast. The pair that matters is
    /// <see cref="TryPublishIssued"/> and <see cref="TryClaimTermination"/>: they are the two sides
    /// of a race that, split back into separate reads and writes, leaks one market-data line per
    /// occurrence with no recovery short of a process restart.
    /// </remarks>
    private sealed class ActiveLease(
        Guid leaseId,
        int conId,
        string exchange,
        LeasePriority priority,
        bool recordToDatabase,
        bool isOption,
        string genericTickList,
        TimeSpan heartbeatInterval,
        DateTimeOffset grantedAt)
    {
        private readonly Lock _gate = new();

        // A field rather than a captured primary-constructor parameter so that `grantedAt` is used
        // only to seed fields — capturing it as well would make the seeding silently ambiguous
        // (CS9124).
        private readonly DateTimeOffset _grantedAt = grantedAt;

        private DateTimeOffset _lastHeartbeat = grantedAt;
        private int _tickerId;
        private LineLease? _lineLease;
        private bool _terminated;
        private Task _gapWork = Task.CompletedTask;

        public Guid LeaseId => leaseId;

        public int ConId => conId;

        public string Exchange => exchange;

        public LeasePriority Priority => priority;

        public bool RecordToDatabase => recordToDatabase;

        public bool IsOption => isOption;

        public string GenericTickList => genericTickList;

        /// <summary>
        /// Stale the moment it is read — usable as a skip hint, never as a guard. The only safe
        /// tests of terminality are the two claim methods, which decide it under the lock.
        /// </summary>
        public bool IsTerminated
        {
            get
            {
                lock (_gate)
                {
                    return _terminated;
                }
            }
        }

        public void Renew()
        {
            lock (_gate)
            {
                _lastHeartbeat = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>3 missed heartbeats, per the roadmap's lease semantics.</summary>
        public bool IsExpiredAt(DateTimeOffset now)
        {
            lock (_gate)
            {
                return now - _lastHeartbeat > heartbeatInterval * 3;
            }
        }

        /// <summary>
        /// Publishes the ticker and line a fresh <c>reqMktData</c> produced, and reports the ticker
        /// it displaced (0 if there was none). Returns false once the lease has been terminated, in
        /// which case the caller owns unwinding what it just acquired — nothing else knows it exists.
        /// </summary>
        /// <remarks>
        /// The displaced <see cref="LineLease"/> is deliberately dropped rather than disposed: a
        /// republish only happens on a replay, and by then the pacing governor's ledger has already
        /// been zeroed (<see cref="IbkrPacingGovernor.ResetLineLedgerForReconnect"/>), so the old
        /// lease object no longer corresponds to a line anything holds. Disposing it would decrement
        /// the ledger for a line that was never counted.
        /// </remarks>
        public bool TryPublishIssued(int tickerId, LineLease lineLease, out int replacedTickerId)
        {
            lock (_gate)
            {
                if (_terminated)
                {
                    replacedTickerId = 0;
                    return false;
                }

                replacedTickerId = _tickerId;
                _tickerId = tickerId;
                _lineLease = lineLease;
                return true;
            }
        }

        /// <summary>
        /// Marks the lease terminated and hands back the transport state to unwind, as one step.
        /// </summary>
        /// <remarks>
        /// Claim and read have to be atomic: the claim is what a concurrent
        /// <see cref="TryPublishIssued"/> tests against, so anything published before it is returned
        /// here to be unwound and anything published after is refused and unwound by its own issuer.
        /// Splitting them is the defect this replaces — a teardown that read <c>TickerId</c> twice
        /// outside the lock, while an in-flight reissue wrote it under the lock, could unregister one
        /// ticker, cancel a second, and dispose a third one's line.
        /// </remarks>
        public bool TryClaimTermination(out int tickerId, out LineLease? lineLease)
        {
            lock (_gate)
            {
                if (_terminated)
                {
                    tickerId = 0;
                    lineLease = null;
                    return false;
                }

                _terminated = true;
                tickerId = _tickerId;
                lineLease = _lineLease;
                _tickerId = 0;
                _lineLease = null;
                return true;
            }
        }

        /// <summary>
        /// Chains gap bookkeeping onto this lease's own sequence. Returns false once the lease has
        /// terminated, and the caller must then NOT do the work: after termination nothing is left
        /// that could undo it.
        /// </summary>
        public bool TryChainGapWork(Func<Task> work)
        {
            lock (_gate)
            {
                if (_terminated)
                {
                    return false;
                }

                _gapWork = ContinueAsync(_gapWork, work);
                return true;
            }
        }

        /// <summary>Waits for the gap bookkeeping that was chained before termination.</summary>
        /// <remarks>
        /// Awaitable from inside a termination only because <see cref="TryChainGapWork"/> refuses
        /// once <see cref="TryClaimTermination"/> has run, so what is being drained cannot grow
        /// while it drains.
        /// </remarks>
        public async Task DrainGapWorkAsync()
        {
            Task work;

            lock (_gate)
            {
                work = _gapWork;
            }

            try
            {
                await work;
            }
            catch (Exception)
            {
                // The chained work is ObservationRecorder's, which logs and swallows its own storage
                // failures; anything reaching here is unexpected. Swallowed because this is awaited
                // from inside a termination, and failing to record a gap must not also abort the
                // teardown that frees a market-data line and unregisters the sink.
            }
        }

        // ForceYielding so the chained work never starts on the caller's stack — TryChainGapWork
        // holds _gate, and the caller is usually the EReader pump thread.
        private static async Task ContinueAsync(Task previous, Func<Task> work)
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            await work().ConfigureAwait(false);
        }

        /// <summary>Drops the line reference after a failed replay, without releasing it.</summary>
        /// <remarks>
        /// The lease's <see cref="LineLease"/> predates the reconnect's
        /// <see cref="IbkrPacingGovernor.ResetLineLedgerForReconnect"/> and no longer corresponds to
        /// anything real. Left in place, a later termination would dispose it anyway, decrementing
        /// the pacing ledger for a line that is not actually held — silently under-reporting real
        /// usage and letting the governor over-admit. Clearing it here means termination finds
        /// nothing to (wrongly) release.
        /// </remarks>
        public void ForgetLineLease()
        {
            lock (_gate)
            {
                _lineLease = null;
            }
        }

        public SubscriptionLease ToRecord()
        {
            lock (_gate)
            {
                return new SubscriptionLease(
                    leaseId, conId, priority, recordToDatabase, _grantedAt, _lastHeartbeat + (heartbeatInterval * 3));
            }
        }
    }
}
