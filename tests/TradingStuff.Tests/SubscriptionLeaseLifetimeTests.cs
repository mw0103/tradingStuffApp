using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.IbkrGateway.Subscriptions;
using TradingStuff.ResearchContracts;

namespace TradingStuff.Tests;

/// <summary>
/// Stands in for the request registry plus <see cref="PacedSocket"/>, with one property a plain mock
/// would not have: <see cref="SubscribeAsync"/> takes a REAL market-data line from a REAL
/// <see cref="IbkrPacingGovernor"/> before it can be made to park, in the same order
/// <c>PacedSocket.ReqMktDataAsync</c> does (line first, then the message budget, which is where it
/// queues). That ordering is the entire hazard these tests are about: a lease torn down while a
/// subscription is parked has already had a line charged against the ledger on its behalf.
/// </summary>
internal sealed class FakeSubscriptionTransport(IbkrPacingGovernor governor) : ISubscriptionTransport
{
    private readonly ConcurrentDictionary<int, int> _subscribeCounts = new();
    private readonly ConcurrentDictionary<(int ConId, int Occurrence), TaskCompletionSource> _reached = new();
    private readonly ConcurrentDictionary<(int ConId, int Occurrence), TaskCompletionSource> _resume = new();
    private int _nextTicker;

    public ConcurrentDictionary<int, ITickSink> Registered { get; } = new();

    public ConcurrentQueue<(int ConId, int TickerId)> Subscribed { get; } = new();

    public ConcurrentQueue<int> Unsubscribed { get; } = new();

    /// <summary>Holds the <paramref name="occurrence"/>-th subscribe for <paramref name="conId"/> open, line already taken.</summary>
    public void ParkSubscribe(int conId, int occurrence) => Gate(_resume, (conId, occurrence));

    public Task SubscribeReachedAsync(int conId, int occurrence) => Gate(_reached, (conId, occurrence)).Task;

    public void ResumeSubscribe(int conId, int occurrence) => Gate(_resume, (conId, occurrence)).TrySetResult();

    public int NextTickerId() => Interlocked.Increment(ref _nextTicker);

    public void RegisterSink(int tickerId, ITickSink sink) => Registered[tickerId] = sink;

    public void RemoveSink(int tickerId) => Registered.TryRemove(tickerId, out _);

    public async Task<LineLease> SubscribeAsync(
        int tickerId, int conId, string exchange, string genericTickList, CancellationToken cancellationToken)
    {
        var occurrence = _subscribeCounts.AddOrUpdate(conId, 1, (_, count) => count + 1);

        // Always asynchronous, like the real one. A transport that completed synchronously would
        // quietly serialise the very interleavings these tests exist to produce.
        await Task.Yield();

        var lease = await governor.AcquireLineAsync(LineClass.Research, cancellationToken);

        Subscribed.Enqueue((conId, tickerId));
        Gate(_reached, (conId, occurrence)).TrySetResult();

        if (_resume.TryGetValue((conId, occurrence), out var resume))
        {
            await resume.Task;
        }

        return lease;
    }

    public Task UnsubscribeAsync(int tickerId, LineLease lineLease)
    {
        lineLease.Dispose();
        Unsubscribed.Enqueue(tickerId);
        return Task.CompletedTask;
    }

    private static TaskCompletionSource Gate(
        ConcurrentDictionary<(int ConId, int Occurrence), TaskCompletionSource> gates,
        (int ConId, int Occurrence) key) =>
        gates.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// The <see cref="SubscriptionManager"/> lease lifetime under the interleavings that actually
/// happen: a reconnect replay landing on top of an eviction, a DELETE landing on top of a replay,
/// and a grant that completes across a replay trigger.
/// </summary>
/// <remarks>
/// All one class of defect — an object whose acquire/complete/release is driven from two or more
/// interleaving paths — and none of it is reachable from a live smoke test, because every one of
/// them fails silently: the recorder keeps reporting healthy leases while recording nothing, or the
/// pacing ledger drifts out of step with TWS's real line count with no error at all until the
/// account's ~80 research lines are gone and grants start timing out.
/// </remarks>
public sealed class SubscriptionLeaseLifetimeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private static (SubscriptionManager Manager, FakeSubscriptionTransport Transport, IbkrPacingGovernor Governor)
        Create(IbkrPacingOptions? pacing = null)
    {
        var governor = new IbkrPacingGovernor(
            Options.Create(new IbkrOptions { Pacing = pacing ?? new IbkrPacingOptions() }),
            TimeProvider.System,
            new TestMeterFactory(),
            NullLogger<IbkrPacingGovernor>.Instance);

        var transport = new FakeSubscriptionTransport(governor);

        // No connection string, so the recorder is disabled and gap bookkeeping is a no-op here. The
        // gap half of this work is asserted against real rows in SubscriptionLeaseGapPostgresTests.
        var recorder = new ObservationRecorder(
            new ConfigurationBuilder().Build(), new TestMeterFactory(), NullLogger<ObservationRecorder>.Instance);

        return (new SubscriptionManager(transport, recorder, NullLogger<SubscriptionManager>.Instance),
            transport, governor);
    }

    internal static SubscriptionLeaseRequest Request(int conId) => new(
        conId, LeasePriority.CoreRecording, RecordToDatabase: true, IsOption: false,
        GenericTickList: null, HeartbeatIntervalSeconds: 5, Exchange: "CBOE");

    [Fact]
    public async Task A_lease_released_while_its_reissue_is_in_flight_leaves_nothing_behind()
    {
        // The race: RunReplayPassAsync used to test _leases.ContainsKey and then await IssueAsync,
        // which parks inside AcquireLineAsync (up to 30s) and the message throttle. A DELETE landing
        // in that window removed the lease and tore down the ticker and line it held at that moment,
        // and the reissue then stored a SECOND ticker and line on an ActiveLease nobody held any
        // more. Nothing ever cancelled that ticker or released that line.
        //
        // Asserted on subscribe/unsubscribe pairing rather than on the pacing ledger deliberately:
        // ReleaseLine clamps at zero, so a stranded line surfaces there only as the ledger drifting
        // out of step with TWS's real count — silently, and in whichever direction the clamp happens
        // to swallow. The pairing is exact.
        var (manager, transport, governor) = Create();

        var lease = await manager.GrantAsync(Request(11), CancellationToken.None);
        Assert.Equal(1, governor.GetLineBudget().ResearchInUse);

        // TWS 1101: the ledger is zeroed exactly as IbkrConnection does before raising the replay
        // event, and the reissue is then held inside the budget.
        governor.ResetLineLedgerForReconnect();
        transport.ParkSubscribe(conId: 11, occurrence: 2);

        var replay = manager.ReplayAsync(CancellationToken.None);
        await transport.SubscribeReachedAsync(11, 2).WaitAsync(TestTimeout);

        Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));

        transport.ResumeSubscribe(11, 2);
        await replay.WaitAsync(TestTimeout);

        var subscribed = transport.Subscribed.Select(call => call.TickerId).Order().ToArray();
        Assert.Equal(2, subscribed.Length);
        Assert.Equal(subscribed, transport.Unsubscribed.Order().ToArray());
        Assert.Empty(transport.Registered);
    }

    [Fact]
    public async Task A_lease_evicted_while_its_reissue_is_in_flight_leaves_nothing_behind()
    {
        // The identical race through the other termination path. Both reach TerminateAsync now,
        // which is the point — but the sweep gets there from a timer rather than from a request, so
        // it is pinned separately.
        var (manager, transport, _) = Create();

        await manager.GrantAsync(Request(12), CancellationToken.None);

        transport.ParkSubscribe(conId: 12, occurrence: 2);
        var replay = manager.ReplayAsync(CancellationToken.None);
        await transport.SubscribeReachedAsync(12, 2).WaitAsync(TestTimeout);

        // Far enough past the deadline to satisfy the 5s heartbeat interval's three-strike rule.
        await manager.SweepExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        transport.ResumeSubscribe(12, 2);
        await replay.WaitAsync(TestTimeout);

        var subscribed = transport.Subscribed.Select(call => call.TickerId).Order().ToArray();
        Assert.Equal(2, subscribed.Length);
        Assert.Equal(subscribed, transport.Unsubscribed.Order().ToArray());
        Assert.Empty(transport.Registered);
        Assert.Empty(manager.ActiveLeases());
    }

    [Fact]
    public async Task An_evicted_lease_gives_its_line_back()
    {
        // The uncomplicated case, kept because it is the one where the pacing ledger IS a clean
        // oracle: no reconnect reset has happened, so every acquire must show up as a release.
        var (manager, transport, governor) = Create();

        await manager.GrantAsync(Request(13), CancellationToken.None);
        Assert.Equal(1, governor.GetLineBudget().ResearchInUse);

        await manager.SweepExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        Assert.Equal(0, governor.GetLineBudget().ResearchInUse);
        Assert.Single(transport.Unsubscribed);
        Assert.Empty(transport.Registered);
    }

    [Fact]
    public async Task Releasing_the_same_lease_twice_terminates_it_once()
    {
        var (manager, transport, _) = Create();

        var lease = await manager.GrantAsync(Request(14), CancellationToken.None);

        Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));
        Assert.False(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));
        Assert.Single(transport.Unsubscribed);
    }

    [Fact]
    public async Task A_release_racing_the_sweep_tears_the_lease_down_exactly_once()
    {
        // Two terminators, one lease, started together. Whichever wins _leases.TryRemove owns the
        // unwind; the loser must do nothing at all rather than unwind a second time with stale state.
        var (manager, transport, governor) = Create();

        var lease = await manager.GrantAsync(Request(15), CancellationToken.None);
        using var barrier = new Barrier(2);

        // Task.Run is essential: an async method runs synchronously to its first await, and
        // Barrier.SignalAndWait is a BLOCKING call rather than an await — invoking these directly as
        // Task.WhenAll's arguments would block the test thread inside evaluating the first one,
        // forever, since the barrier's second participant would never be reached. (Same reasoning as
        // ResearchRecordingPostgresTests.Concurrent_assignment_of_the_same_node_never_leaves_two_current_rows.)
        Task<bool> ReleaseRaceAsync() => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None);
        });

        Task SweepRaceAsync() => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await manager.SweepExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        });

        await Task.WhenAll(ReleaseRaceAsync(), SweepRaceAsync()).WaitAsync(TestTimeout);

        Assert.Single(transport.Unsubscribed);
        Assert.Equal(0, governor.GetLineBudget().ResearchInUse);
        Assert.Empty(manager.ActiveLeases());
    }

    [Fact]
    public async Task A_lease_granted_across_a_replay_trigger_is_reissued()
    {
        // A replay pass snapshots _leases when it starts. A grant whose reqMktData is still in
        // flight at that instant is invisible to the pass — and _replayPending only coalesces later
        // TRIGGERS, which a grant is not, so no later pass covered it either. Its subscription was
        // issued against socket state TWS had already discarded, so the lease recorded nothing for
        // the rest of the session while continuing to look healthy in ActiveLeases().
        var (manager, transport, governor) = Create();

        transport.ParkSubscribe(conId: 22, occurrence: 1);
        var grant = manager.GrantAsync(Request(22), CancellationToken.None);
        await transport.SubscribeReachedAsync(22, 1).WaitAsync(TestTimeout);

        governor.ResetLineLedgerForReconnect();
        await manager.ReplayAsync(CancellationToken.None).WaitAsync(TestTimeout);

        // The pass genuinely could not see it: _leases is still empty.
        Assert.Single(transport.Subscribed);

        transport.ResumeSubscribe(22, 1);
        await grant.WaitAsync(TestTimeout);

        await WaitUntilAsync(() => transport.Subscribed.Count == 2);
    }

    [Fact]
    public async Task A_lease_granted_with_no_replay_trigger_in_flight_is_not_reissued()
    {
        // The negative half of the test above, and the reason the fix uses an epoch rather than
        // "replay after every grant": RecorderOrchestrator grants leases constantly (node rotation,
        // expiry roll, every strike crossing), and a pass re-issues all ~54 of them against the line
        // and message budgets.
        var (manager, transport, _) = Create();

        await manager.GrantAsync(Request(23), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Single(transport.Subscribed);
        Assert.False(manager.ReplayPassOwed);
    }

    [Fact]
    public async Task A_trigger_arriving_as_the_replay_gate_is_released_is_not_dropped()
    {
        // The window the outer loop exists for. A trigger landing after the do/while's last read of
        // _replayPending but before Release() finds the gate held — so it cannot run the pass — while
        // the loop it hands its obligation to has already decided to exit. Without the re-check after
        // the release, that trigger is simply lost, and since a trigger means "TWS discarded every
        // streaming subscription", losing one leaves every lease dead until the next reconnect.
        //
        // Driven from BeforeReplayGateRelease rather than from concurrency: see that property's
        // remarks — a stress test does not reach this window, and one that pretends to would report
        // the defect as absent.
        var (manager, transport, _) = Create(new IbkrPacingOptions { LineCap = 10_000 });

        await manager.GrantAsync(Request(32), CancellationToken.None);
        var subscribesBeforeReplay = transport.Subscribed.Count;

        var fired = 0;
        manager.BeforeReplayGateRelease = () =>
        {
            // Once only: every pass passes through here, and a trigger per pass would never settle.
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                _ = manager.ReplayAsync(CancellationToken.None);
            }
        };

        await manager.ReplayAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(1, fired);
        Assert.False(manager.ReplayPassOwed);

        // Two passes, not one: the straggler was served rather than swallowed.
        Assert.Equal(subscribesBeforeReplay + 2, transport.Subscribed.Count);
    }

    [Fact]
    public async Task No_replay_pass_is_owed_once_every_trigger_has_returned()
    {
        // The coalescing invariant. A trigger that loses the race to _replayGate hands its
        // obligation to the holder; if that handoff is lost the flag stays raised with nobody
        // running, and every lease stays dead until the next reconnect. Both halves of the handoff
        // are load-bearing: the flag is raised BEFORE contending (a loser raising it afterwards can
        // be overtaken by the holder's last read of it) and re-checked AFTER the gate is released (a
        // trigger arriving in that gap finds the gate held by a loop that has already decided to
        // exit).
        //
        // A high line cap because a replay deliberately drops the displaced LineLease without
        // disposing it — correct in production, where ResetLineLedgerForReconnect has already zeroed
        // the ledger, but across a few hundred synthetic passes it would exhaust the default budget.
        var (manager, transport, _) = Create(new IbkrPacingOptions { LineCap = 10_000 });

        await manager.GrantAsync(Request(31), CancellationToken.None);

        for (var round = 0; round < 40; round++)
        {
            using var barrier = new Barrier(4);

            Task TriggerAsync() => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await manager.ReplayAsync(CancellationToken.None);
            });

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TriggerAsync())).WaitAsync(TestTimeout);

            Assert.False(manager.ReplayPassOwed);
        }

        // Sanity: the passes really did run, so the assertion above was not vacuous.
        Assert.True(transport.Subscribed.Count > 40, $"only {transport.Subscribed.Count} subscribes happened");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;

        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.True(condition());
    }
}
