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
    public async Task A_grant_across_a_replay_trigger_does_not_double_charge_the_line_ledger()
    {
        // The multi-lease sibling of the test above, and the reason it needs one: with a single
        // lease there is nothing for a redundant re-issue to double-charge, so that test passes
        // just as happily against a fix that re-issues the whole book and leaks a line for every
        // lease it touches. Production scale (57 node leases, the default 90-line cap with 10
        // reserved for execution) is load-bearing here — below ~23 leases the leak fits inside the
        // budget and every assertion still passes.
        //
        // The interleaving, which is the ordinary two-minute cadence of RecorderOrchestrator
        // meeting an ordinary 1101:
        //   thread A  GrantAsync samples _replayEpoch, then parks inside AcquireLineAsync
        //   thread B  the EReader pump raises SubscriptionsMustReplay: ledger zeroed, epoch bumped,
        //             pass P1 re-issues all 57 standing leases against the fresh ledger
        //   thread A  resumes, publishes, joins _leases, re-reads the epoch, sees it moved
        // Whatever thread A does next runs with a ledger that is NOT zeroed and 57 lines that are
        // genuinely held. A re-issue that displaces a live LineLease without disposing it therefore
        // charges a second line for every subscription it replaces, and the 80-line research budget
        // is gone in one pass.
        const int standingLeases = 57;

        // Only the acquire timeout is moved off its default: a leak drives later re-issues into
        // that timeout, and 30s each would put this test in the minutes.
        var (manager, transport, governor) = Create(new IbkrPacingOptions { AcquireTimeoutSeconds = 1 });

        for (var index = 0; index < standingLeases; index++)
        {
            await manager.GrantAsync(Request(1000 + index), CancellationToken.None);
        }

        Assert.Equal(standingLeases, governor.GetLineBudget().ResearchInUse);

        transport.ParkSubscribe(conId: 2000, occurrence: 1);
        var grant = manager.GrantAsync(Request(2000), CancellationToken.None);
        await transport.SubscribeReachedAsync(2000, 1).WaitAsync(TestTimeout);

        governor.ResetLineLedgerForReconnect();
        await manager.ReplayAsync(CancellationToken.None).WaitAsync(TestTimeout);

        // P1 could not see the parked grant, so it re-issued exactly the 57 it could: one line each
        // against the zeroed ledger. The parked grant's own line predates the reset and was zeroed
        // with everything else.
        Assert.Equal(standingLeases, governor.GetLineBudget().ResearchInUse);

        transport.ResumeSubscribe(2000, 1);
        await grant.WaitAsync(TestTimeout);

        await SettleSubscribesAsync(transport);

        Assert.True(
            governor.GetLineBudget().ResearchInUse == standingLeases + 1,
            $"one line per live lease, but {Describe(transport, governor)}");

        foreach (var lease in manager.ActiveLeases())
        {
            Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));
        }

        Assert.True(
            governor.GetLineBudget().ResearchInUse == 0,
            $"every line handed back, but {Describe(transport, governor)}");

        // Exact pairing rather than a count: it is the difference between "as many lines came back
        // as went out" and "the subscription TWS is still streaming is the one nobody cancelled".
        Assert.Equal(
            transport.Subscribed.Select(call => call.TickerId).Order().ToArray(),
            transport.Unsubscribed.Order().ToArray());

        Assert.Empty(transport.Registered);
    }

    [Fact]
    public async Task A_replay_that_fails_with_no_reconnect_behind_it_keeps_the_line_the_lease_still_holds()
    {
        // The failure branch of the same false assumption, and it has to be pinned separately
        // because the success branch above never reaches it. ForgetLineLease used to drop the
        // lease's LineLease in RunReplayPassAsync's catch, documented as safe because "the lease's
        // LineLease predates the reconnect's ResetLineLedgerForReconnect and no longer corresponds
        // to anything real". A replay pass does not need a reset behind it — ReplayAsync is callable
        // on its own, and the grant path used to fire one — so on that path the reference dropped
        // was a CURRENT-epoch line the lease was genuinely holding, over a ticker TWS was genuinely
        // still streaming. TerminateAsync then found lineLease == null, skipped UnsubscribeAsync,
        // and neither the line nor the subscription ever came back. Silent: the lease keeps its old
        // ticker, keeps recording, keeps heartbeating, and still reports healthy.
        //
        // Nothing forgets any more. LineLease carries its ledger epoch and ReleaseLine ignores a
        // superseded one, so retaining the reference is a no-op after a real reset and the correct
        // release without one — the precondition is enforced where it is knowable instead of
        // assumed at a call site three removes away.

        // Research cap 2 (cap 3 less the execution reserve), exactly consumed by the two standing
        // leases, so every re-issue in the pass below runs out its acquire timeout and throws.
        var (manager, transport, governor) = Create(
            new IbkrPacingOptions { LineCap = 3, ExecutionReservedLines = 1, AcquireTimeoutSeconds = 1 });

        var first = await manager.GrantAsync(Request(41), CancellationToken.None);
        var second = await manager.GrantAsync(Request(42), CancellationToken.None);
        Assert.Equal(2, governor.GetLineBudget().ResearchInUse);

        var originalTickers = transport.Subscribed.Select(call => call.TickerId).Order().ToArray();

        // No ResetLineLedgerForReconnect anywhere: a same-epoch pass, both of whose re-issues fail.
        await manager.ReplayAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(2, governor.GetLineBudget().ResearchInUse);

        Assert.True(await manager.ReleaseAsync(first.LeaseId, CancellationToken.None));
        Assert.True(await manager.ReleaseAsync(second.LeaseId, CancellationToken.None));

        Assert.True(
            governor.GetLineBudget().ResearchInUse == 0,
            $"a failed reissue stranded the line its lease still held: {Describe(transport, governor)}");

        // And the subscription, not merely the ledger entry: what TWS is still streaming are the
        // tickers from the original grants, so those are the ones that have to be cancelled.
        Assert.Equal(originalTickers, transport.Unsubscribed.Order().ToArray());
        Assert.Empty(transport.Registered);
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
        var (manager, transport, governor) = Create();

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

        // And two passes cost one line, not three. No reconnect reset happened here, so every line
        // both passes displaced was a live one.
        Assert.Equal(1, governor.GetLineBudget().ResearchInUse);
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
        // On the DEFAULT line cap, which is now itself an assertion. This test used to run with
        // LineCap = 10_000 because a replay dropped the displaced LineLease without disposing it,
        // and a few hundred synthetic passes exhausted the real budget — the leak was seen here,
        // worked around, and written up in a comment as correct-in-production. It was not: the grant
        // path re-issued with no ledger reset in front of it, and one pass over 57 leases consumed
        // the entire research budget (A_grant_across_a_replay_trigger_does_not_double_charge_the_
        // line_ledger). A test that raises the ceiling to walk past the thing it tripped over is
        // worse than no test, because it reports the defect as absent.
        var (manager, transport, governor) = Create();

        var lease = await manager.GrantAsync(Request(31), CancellationToken.None);

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

            // One lease, one line, however many passes have run. No reconnect reset happens in this
            // test, so every displaced line is a current-epoch line the ledger really is holding and
            // a pass that fails to hand it back shows up here immediately rather than 90 passes
            // later as an acquire timeout.
            Assert.Equal(1, governor.GetLineBudget().ResearchInUse);
        }

        // Sanity: the passes really did run, so the assertions above were not vacuous.
        Assert.True(transport.Subscribed.Count > 40, $"only {transport.Subscribed.Count} subscribes happened");

        // Every subscribe but the live one has already been cancelled by the reissue that displaced
        // it; releasing the lease accounts for the last.
        Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));

        Assert.Equal(0, governor.GetLineBudget().ResearchInUse);
        Assert.Equal(
            transport.Subscribed.Select(call => call.TickerId).Order().ToArray(),
            transport.Unsubscribed.Order().ToArray());
        Assert.Empty(transport.Registered);
    }

    private static string Describe(FakeSubscriptionTransport transport, IbkrPacingGovernor governor) =>
        $"researchInUse={governor.GetLineBudget().ResearchInUse} subscribes={transport.Subscribed.Count} " +
        $"unsubscribes={transport.Unsubscribed.Count} registeredSinks={transport.Registered.Count}";

    /// <summary>Waits until the transport has been quiet for a moment, or gives up and lets the assertions speak.</summary>
    /// <remarks>
    /// The re-issue a grant fires is deliberately not awaited — the caller must not be held behind
    /// it — so there is no handle to join on, and no count to wait FOR either: a test that waits for
    /// the number it expects reports a leak as a timeout rather than as the leak it is. Quiescence
    /// is the only condition that describes both outcomes, and it deliberately does not assert.
    /// </remarks>
    private static async Task SettleSubscribesAsync(FakeSubscriptionTransport transport)
    {
        var quiet = TimeSpan.FromMilliseconds(400);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        var lastCount = -1;
        var lastChanged = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var count = transport.Subscribed.Count;

            if (count != lastCount)
            {
                lastCount = count;
                lastChanged = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastChanged > quiet)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
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
