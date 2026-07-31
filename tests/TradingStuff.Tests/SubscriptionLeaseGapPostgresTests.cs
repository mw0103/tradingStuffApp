using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.IbkrGateway.Subscriptions;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// The gap half of the lease lifetime, against real <c>gateway.recorder_gaps</c> rows.
/// </summary>
/// <remarks>
/// Real rows rather than a fake recorder because the property under test is a property of the
/// TABLE, not of a method call: CoverageMonitor treats <c>ended_at IS NULL</c> as an outage still in
/// progress, so an unbounded row overlaps every window from that instant to eternity. A test that
/// only asserted "CloseGapAsync was called" would be asserting the fix's shape rather than its
/// effect, and this is the failure class that has already shipped twice (docs/STATE.md, Phase 1:
/// 80 immortal gaps from one afternoon's restarts).
/// </remarks>
[Trait("Category", "RequiresPostgres")]
public sealed class SubscriptionLeaseGapPostgresTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static string ConnectionStringFor(string server) =>
        $"{server.TrimEnd(';')};Database=trading_test_{Guid.NewGuid():N}";

    private static Task MigrateAsync(string connectionString) =>
        new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance)
            .ApplyOnceAsync(connectionString, CancellationToken.None);

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    private static ObservationRecorder RecorderFor(string connectionString) =>
        new(ConfigurationFor(connectionString), new TestMeterFactory(), NullLogger<ObservationRecorder>.Instance);

    private static (SubscriptionManager Manager, FakeSubscriptionTransport Transport) ManagerFor(
        ObservationRecorder recorder)
    {
        var governor = new IbkrPacingGovernor(
            Options.Create(new IbkrOptions()), TimeProvider.System, new TestMeterFactory(),
            NullLogger<IbkrPacingGovernor>.Instance);

        var transport = new FakeSubscriptionTransport(governor);

        return (new SubscriptionManager(transport, recorder, NullLogger<SubscriptionManager>.Instance), transport);
    }

    [Fact]
    public async Task Releasing_a_lease_bounds_the_gap_its_disconnect_opened()
    {
        // The defect: SweepExpiredAsync was given CloseGapAsync(observed: false) and ReleaseAsync was
        // not, so the DELETE path left the row unbounded forever. Nothing could ever close it either
        // — after teardown the sink is unregistered, so no tick can reach NotifyGapClosed, and the
        // lease is out of _leases, so no sweep revisits it.
        //
        // And release is the hot path: RecorderOrchestrator re-derives node assignments every two
        // minutes and releases every lease whose conId moved (each expiry roll, every time spot
        // crosses a strike). One socket drop across 54 option nodes stranded up to 54 immortal rows.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = ConnectionStringFor(server);
        await MigrateAsync(connectionString);

        await using var recorder = RecorderFor(connectionString);
        var (manager, transport) = ManagerFor(recorder);

        var lease = await manager.GrantAsync(SubscriptionLeaseLifetimeTests.Request(101), CancellationToken.None);
        var scope = ObservationRecorder.LeaseScope(lease.LeaseId);

        // The socket drops: IbkrRequestRegistry.FailAll faults every in-flight sink, which is what
        // opens the lease's gap.
        Assert.Single(transport.Registered).Value.Fail(new IbkrConnectionException("socket dropped"));
        await WaitUntilAsync(async () => await OpenGapCountAsync(connectionString, scope) == 1);

        Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));

        Assert.Equal(0, await OpenGapCountAsync(connectionString, scope));

        // 'inferred', not 'observed': after teardown nothing watched recording resume on this scope,
        // so ended_at is an upper bound on the outage rather than a measurement of it.
        Assert.Equal("inferred", await SingleClosedByAsync(connectionString, scope));
    }

    [Fact]
    public async Task A_sink_failing_as_its_lease_is_released_still_leaves_the_gap_bounded()
    {
        // The same immortal row through a different door, and not a corner case: a socket drop
        // faults every sink at once (IbkrRequestRegistry.FailAll), which is precisely the moment the
        // sweep is evicting the leases that same outage abandoned. With the gap open fired loose
        // from the pump thread, its INSERT can land after the termination's close has already found
        // nothing to close — and then nothing ever closes it, because the lease is gone.
        //
        // No WaitUntilAsync here, deliberately: the wait in the test above is what makes that one
        // deterministic, and removing it is what makes this one the race.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = ConnectionStringFor(server);
        await MigrateAsync(connectionString);

        await using var recorder = RecorderFor(connectionString);
        var (manager, transport) = ManagerFor(recorder);

        var lease = await manager.GrantAsync(SubscriptionLeaseLifetimeTests.Request(103), CancellationToken.None);
        var scope = ObservationRecorder.LeaseScope(lease.LeaseId);

        Assert.Single(transport.Registered).Value.Fail(new IbkrConnectionException("socket dropped"));
        Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));

        // Whichever way the race fell, the table must not hold an unbounded row: either the open
        // landed and was closed, or it was refused and never happened.
        Assert.Equal(0, await OpenGapCountAsync(connectionString, scope));
    }

    [Fact]
    public async Task Evicting_a_lease_bounds_the_gap_it_opens()
    {
        // The path that was already correct, pinned so the move to a single TerminateAsync cannot
        // regress it.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = ConnectionStringFor(server);
        await MigrateAsync(connectionString);

        await using var recorder = RecorderFor(connectionString);
        var (manager, _) = ManagerFor(recorder);

        var lease = await manager.GrantAsync(SubscriptionLeaseLifetimeTests.Request(102), CancellationToken.None);
        var scope = ObservationRecorder.LeaseScope(lease.LeaseId);

        await manager.SweepExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        Assert.Equal(0, await OpenGapCountAsync(connectionString, scope));
        Assert.Equal("inferred", await SingleClosedByAsync(connectionString, scope));
    }

    [Fact]
    public async Task Orphan_reconciliation_reports_failure_so_the_caller_can_retry_it()
    {
        // The Aspire cold start, and the crash this routine exists for: the database is not there
        // yet when the gateway reaches its first attempt. Previously the failure was logged and
        // dropped, so every gap orphaned by the previous process stayed open for the life of the
        // platform — the routine failing in exactly the scenario it was written for.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = ConnectionStringFor(server);

        await using var recorder = RecorderFor(connectionString);
        Assert.False(await recorder.ReconcileOrphanedGapsAsync(CancellationToken.None));

        await MigrateAsync(connectionString);

        Assert.True(await recorder.ReconcileOrphanedGapsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Orphan_reconciliation_leaves_a_gap_this_process_still_holds_open()
    {
        // What makes retrying safe. The old statement closed EVERY unended row, justified by "this
        // process holds no subscriptions yet" — true of the first attempt at startup and of nothing
        // after it. A retry landing mid-session would otherwise mark a live outage as ended and hand
        // CoverageMonitor a window that reads as covered while nothing is being recorded.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = ConnectionStringFor(server);
        await MigrateAsync(connectionString);

        // The dead process's row: opened by a recorder that then goes away.
        await using (var abandoned = RecorderFor(connectionString))
        {
            await abandoned.OpenGapAsync("lease:abandoned", "disconnect", CancellationToken.None);
        }

        await using var live = RecorderFor(connectionString);
        await live.OpenGapAsync("lease:live", "disconnect", CancellationToken.None);

        Assert.True(await live.ReconcileOrphanedGapsAsync(CancellationToken.None));

        Assert.Equal(0, await OpenGapCountAsync(connectionString, "lease:abandoned"));
        Assert.Equal(1, await OpenGapCountAsync(connectionString, "lease:live"));

        // And the live one still closes normally when its own recording resumes — with the
        // provenance the recorder actually observed, not the reconciliation's bound.
        await live.CloseGapAsync("lease:live");

        Assert.Equal(0, await OpenGapCountAsync(connectionString, "lease:live"));
        Assert.Equal("observed", await SingleClosedByAsync(connectionString, "lease:live"));
    }

    private static async Task<int> OpenGapCountAsync(string connectionString, string scope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = $1 AND ended_at IS NULL", connection);
        command.Parameters.AddWithValue(scope);

        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> SingleClosedByAsync(string connectionString, string scope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT closed_by FROM gateway.recorder_gaps WHERE scope = $1", connection);
        command.Parameters.AddWithValue(scope);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;

        while (DateTime.UtcNow < deadline && !await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(await condition());
    }
}
