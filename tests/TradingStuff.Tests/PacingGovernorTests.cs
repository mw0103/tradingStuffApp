using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;

namespace TradingStuff.Tests;

/// <summary>
/// The pacing governor under a fake clock. These budgets are what stand between the gateway and a
/// TWS pacing disconnect, so every limit is pinned: rates, cooldowns, double-counting, the line
/// cap, and the execution reserve.
/// </summary>
public sealed class PacingGovernorTests
{
    private static readonly TimeSpan RealWait = TimeSpan.FromSeconds(5);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private static (IbkrPacingGovernor Governor, FakeTimeProvider Time) Create(IbkrPacingOptions pacing)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T14:00:00Z"));
        var options = Options.Create(new IbkrOptions { Pacing = pacing });
        var governor = new IbkrPacingGovernor(
            options, time, new TestMeterFactory(), NullLogger<IbkrPacingGovernor>.Instance);

        return (governor, time);
    }

    // ---- socket message rate ------------------------------------------------------------------

    [Fact]
    public async Task Messages_within_the_burst_complete_immediately()
    {
        var (governor, _) = Create(new IbkrPacingOptions { MessagesPerSecond = 10, MessageBurst = 2 });

        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None)
            .WaitAsync(RealWait);
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None)
            .WaitAsync(RealWait);
    }

    [Fact]
    public async Task A_message_past_the_burst_waits_for_the_refill()
    {
        var (governor, time) = Create(new IbkrPacingOptions { MessagesPerSecond = 10, MessageBurst = 2 });

        await governor.AcquireMessagesAsync(2, SocketMessageClass.Normal, CancellationToken.None)
            .WaitAsync(RealWait);

        var third = governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
        Assert.False(third.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(150));
        await third.WaitAsync(RealWait);
    }

    [Fact]
    public async Task Order_messages_never_wait_but_still_count()
    {
        var (governor, time) = Create(new IbkrPacingOptions { MessagesPerSecond = 10, MessageBurst = 2 });

        await governor.AcquireMessagesAsync(2, SocketMessageClass.Normal, CancellationToken.None)
            .WaitAsync(RealWait);

        // Bucket is empty; the order jumps anyway, driving the balance to -1.
        await governor.AcquireMessagesAsync(1, SocketMessageClass.Order, CancellationToken.None)
            .WaitAsync(RealWait);

        // A normal message now owes the order's debt too: 2 tokens at 10/s = 200ms, not 100ms.
        var normal = governor.AcquireMessagesAsync(1, SocketMessageClass.Normal, CancellationToken.None);
        Assert.False(normal.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(120));
        Assert.False(normal.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(120));
        await normal.WaitAsync(RealWait);
    }

    // ---- historical window --------------------------------------------------------------------

    [Fact]
    public async Task The_historical_window_caps_requests_until_old_ones_age_out()
    {
        var (governor, time) = Create(new IbkrPacingOptions
        {
            HistoricalWindowRequests = 3,
            HistoricalWindowMinutes = 10,
            AcquireTimeoutSeconds = 3600, // long enough that the test exercises WAITING, not rejection
        });

        await governor.AcquireHistoricalAsync("a", "SPX", false, CancellationToken.None).WaitAsync(RealWait);
        await governor.AcquireHistoricalAsync("b", "SPY", false, CancellationToken.None).WaitAsync(RealWait);
        await governor.AcquireHistoricalAsync("c", "VIX", false, CancellationToken.None).WaitAsync(RealWait);

        var fourth = governor.AcquireHistoricalAsync("d", "ES", false, CancellationToken.None);
        Assert.False(fourth.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        await fourth.WaitAsync(RealWait);
    }

    [Fact]
    public async Task Bid_ask_requests_count_double_against_the_window()
    {
        var (governor, time) = Create(new IbkrPacingOptions
        {
            HistoricalWindowRequests = 3,
            HistoricalWindowMinutes = 10,
            AcquireTimeoutSeconds = 3600,
        });

        await governor.AcquireHistoricalAsync("a", "SPX", countsDouble: true, CancellationToken.None)
            .WaitAsync(RealWait);
        await governor.AcquireHistoricalAsync("b", "SPY", countsDouble: false, CancellationToken.None)
            .WaitAsync(RealWait);

        // 2 + 1 = 3 consumed; a second BID_ASK needs 2 slots and must wait.
        var overBudget = governor.AcquireHistoricalAsync("c", "VIX", countsDouble: true, CancellationToken.None);
        Assert.False(overBudget.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        await overBudget.WaitAsync(RealWait);
    }

    [Fact]
    public async Task A_wait_beyond_the_acquire_timeout_is_rejected_immediately_with_retry_after()
    {
        // Default AcquireTimeoutSeconds (30) is far below the 10-minute window: the over-budget
        // request must fail fast with backoff guidance, not park the caller in-process for minutes.
        var (governor, _) = Create(new IbkrPacingOptions
        {
            HistoricalWindowRequests = 1,
            HistoricalWindowMinutes = 10,
        });

        await governor.AcquireHistoricalAsync("a", "SPX", false, CancellationToken.None).WaitAsync(RealWait);

        var rejected = await Assert.ThrowsAsync<IbkrPacingRejectedException>(
            () => governor.AcquireHistoricalAsync("b", "SPY", false, CancellationToken.None).WaitAsync(RealWait));

        Assert.True(rejected.RetryAfter > TimeSpan.FromMinutes(9));
        Assert.True(rejected.RetryAfter <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task An_identical_request_inside_the_cooldown_waits_it_out()
    {
        var (governor, time) = Create(new IbkrPacingOptions { IdenticalRequestCooldownSeconds = 15 });

        await governor.AcquireHistoricalAsync("same", "SPX", false, CancellationToken.None).WaitAsync(RealWait);

        var repeat = governor.AcquireHistoricalAsync("same", "SPX", false, CancellationToken.None);
        Assert.False(repeat.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(16));
        await repeat.WaitAsync(RealWait);
    }

    [Fact]
    public async Task The_same_contract_short_window_is_enforced()
    {
        var (governor, time) = Create(new IbkrPacingOptions
        {
            SameContractWindowSeconds = 2,
            SameContractWindowRequests = 2,
        });

        await governor.AcquireHistoricalAsync("a", "SPX", false, CancellationToken.None).WaitAsync(RealWait);
        await governor.AcquireHistoricalAsync("b", "SPX", false, CancellationToken.None).WaitAsync(RealWait);

        var third = governor.AcquireHistoricalAsync("c", "SPX", false, CancellationToken.None);
        Assert.False(third.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(3));
        await third.WaitAsync(RealWait);
    }

    // ---- line ledger --------------------------------------------------------------------------

    [Fact]
    public async Task Research_lines_stop_short_of_the_execution_reserve()
    {
        var (governor, _) = Create(new IbkrPacingOptions { LineCap = 3, ExecutionReservedLines = 1 });

        await governor.AcquireLineAsync(LineClass.Research, CancellationToken.None).WaitAsync(RealWait);
        await governor.AcquireLineAsync(LineClass.Research, CancellationToken.None).WaitAsync(RealWait);

        // Research is capped at 2 of 3; the reserved line stays free for execution.
        var blocked = governor.AcquireLineAsync(LineClass.Research, CancellationToken.None);
        Assert.False(blocked.IsCompleted);

        var execution = await governor.AcquireLineAsync(LineClass.Execution, CancellationToken.None)
            .WaitAsync(RealWait);
        Assert.Equal(LineClass.Execution, execution.Class);

        var budget = governor.GetLineBudget();
        Assert.Equal(3, budget.ExecutionInUse + budget.ResearchInUse);
        Assert.Equal(1, budget.Waiting);
    }

    [Fact]
    public async Task Releasing_a_line_grants_the_first_eligible_waiter()
    {
        var (governor, _) = Create(new IbkrPacingOptions { LineCap = 2, ExecutionReservedLines = 0 });

        var first = await governor.AcquireLineAsync(LineClass.Research, CancellationToken.None).WaitAsync(RealWait);
        await governor.AcquireLineAsync(LineClass.Research, CancellationToken.None).WaitAsync(RealWait);

        var waiting = governor.AcquireLineAsync(LineClass.Research, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        first.Dispose();

        var granted = await waiting.WaitAsync(RealWait);
        Assert.Equal(LineClass.Research, granted.Class);
        Assert.Equal(2, governor.GetLineBudget().ResearchInUse);
    }

    [Fact]
    public async Task A_line_acquisition_times_out_rather_than_queueing_forever()
    {
        var (governor, time) = Create(new IbkrPacingOptions
        {
            LineCap = 1,
            ExecutionReservedLines = 0,
            AcquireTimeoutSeconds = 1,
        });

        await governor.AcquireLineAsync(LineClass.Execution, CancellationToken.None).WaitAsync(RealWait);

        var starved = governor.AcquireLineAsync(LineClass.Execution, CancellationToken.None);
        Assert.False(starved.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() => starved.WaitAsync(RealWait));
        Assert.Equal(0, governor.GetLineBudget().Waiting);
    }

    [Fact]
    public async Task A_cancelled_queued_waiter_does_not_leak_lines_when_later_granted()
    {
        // Regression: HTTP aborts cancel queued quote waiters routinely (resilience-handler attempt
        // timeouts). A cancelled waiter left in the queue used to receive a grant nobody awaited,
        // permanently inflating the ledger.
        var (governor, _) = Create(new IbkrPacingOptions { LineCap = 1, ExecutionReservedLines = 0 });

        var held = await governor.AcquireLineAsync(LineClass.Execution, CancellationToken.None)
            .WaitAsync(RealWait);

        using var cancellation = new CancellationTokenSource();
        var queued = governor.AcquireLineAsync(LineClass.Execution, cancellation.Token);
        Assert.False(queued.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(RealWait));
        Assert.Equal(0, governor.GetLineBudget().Waiting);

        // The release must NOT be captured by the abandoned waiter.
        held.Dispose();

        var budget = governor.GetLineBudget();
        Assert.Equal(0, budget.ExecutionInUse + budget.ResearchInUse);

        // And the freed line is genuinely acquirable again.
        var reacquired = await governor.AcquireLineAsync(LineClass.Execution, CancellationToken.None)
            .WaitAsync(RealWait);
        reacquired.Dispose();
    }

    [Fact]
    public async Task Order_class_admission_is_bounded_by_one_burst_of_debt()
    {
        var (governor, time) = Create(new IbkrPacingOptions { MessagesPerSecond = 10, MessageBurst = 2 });

        // Burst (2) + debt floor (2) = 4 immediate Order-class admissions; the 5th must wait.
        for (var i = 0; i < 4; i++)
        {
            await governor.AcquireMessagesAsync(1, SocketMessageClass.Order, CancellationToken.None)
                .WaitAsync(RealWait);
        }

        var fifth = governor.AcquireMessagesAsync(1, SocketMessageClass.Order, CancellationToken.None);
        Assert.False(fifth.IsCompleted);

        // Balance is -2; one token past the -2 floor needs 3 tokens of refill = 300ms at 10/s.
        time.Advance(TimeSpan.FromMilliseconds(350));
        await fifth.WaitAsync(RealWait);
    }

    [Fact]
    public async Task Double_dispose_releases_a_line_only_once()
    {
        var (governor, _) = Create(new IbkrPacingOptions { LineCap = 2, ExecutionReservedLines = 0 });

        var lease = await governor.AcquireLineAsync(LineClass.Execution, CancellationToken.None)
            .WaitAsync(RealWait);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, governor.GetLineBudget().ExecutionInUse);
    }
}
