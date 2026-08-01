using System.Diagnostics.Metrics;
using IBApi;
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
/// What TWS reported about a lease's data regime, end to end: through the sink, the re-keying a
/// replay performs, the binary COPY, and the <c>recorder_gaps</c> row that makes a non-live session
/// visible after the fact.
/// </summary>
/// <remarks>
/// Against real rows rather than a fake recorder for the same reason
/// <see cref="SubscriptionLeaseGapPostgresTests"/> is: the property under test is a property of the
/// TABLE. Recorded ticks cannot be re-collected, so "was this surface live?" is a question that can
/// only ever be answered from what was written down at the time — and a test asserting that a
/// method was called would be asserting the fix's shape rather than its effect.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class MarketDataTypeProvenancePostgresTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    private static async Task<string> PrepareAsync(string server)
    {
        var connectionString = PostgresCollection.FreshDatabase(server);

        await new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance)
            .ApplyOnceAsync(connectionString, CancellationToken.None);

        return connectionString;
    }

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

    /// <summary>The single sink the transport currently routes to, with its ticker id.</summary>
    private static (int TickerId, ITickSink Sink) OnlySink(FakeSubscriptionTransport transport)
    {
        var registered = Assert.Single(transport.Registered);
        return (registered.Key, registered.Value);
    }

    [Fact]
    public async Task A_replay_re_keys_the_ticker_and_the_new_subscription_starts_unmeasured()
    {
        // The split-path hazard this whole design is shaped around. A replay after TWS's 1101 notice
        // allocates a BRAND-NEW ticker id and constructs a BRAND-NEW RecordingTickSink at it, and
        // deregisters the old ticker (SubscriptionManager.IssueAsync). Because the reported type
        // lives INSIDE the sink instance rather than in a dictionary keyed by ticker or lease id,
        // there is nothing to re-key and nothing to leak: the old sink's answer becomes garbage with
        // the sink, and the new subscription is genuinely unmeasured until TWS answers for IT.
        //
        // The failure this pins is the plausible-looking alternative — carrying the type forward, or
        // holding it in a map keyed by lease id — which would stamp the previous ticker's answer
        // onto rows recorded under a subscription that was never asked about. That is a fabricated
        // measurement in a row nobody can ever re-collect.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await using var recorder = RecorderFor(connectionString);
        var (manager, transport) = ManagerFor(recorder);

        var lease = await manager.GrantAsync(SubscriptionLeaseLifetimeTests.Request(701), CancellationToken.None);
        var (firstTicker, firstSink) = OnlySink(transport);

        firstSink.ApplyMarketDataType(3);                // delayed, for the FIRST ticker only
        firstSink.ApplyPrice(TickType.LAST, 4321.50d);

        await manager.ReplayAsync(CancellationToken.None).WaitAsync(TestTimeout);

        var (secondTicker, secondSink) = OnlySink(transport);
        Assert.NotEqual(firstTicker, secondTicker);
        Assert.NotSame(firstSink, secondSink);

        // The old ticker is gone from the registry, which is the mechanism: a late marketDataType
        // callback for it resolves to no sink at all inside IbkrClientWrapper and is dropped. Probed
        // through the registry exactly as the wrapper does, NOT by calling the orphaned object —
        // calling it directly would bypass the very lookup that is the protection.
        Assert.False(transport.Registered.ContainsKey(firstTicker));
        transport.Registered.GetValueOrDefault(firstTicker)?.ApplyMarketDataType(1);

        secondSink.ApplyPrice(TickType.LAST, 4322.00d);  // the first tick on the new ticker
        secondSink.ApplyPrice(TickType.LAST, 4322.25d);

        Assert.True(await WaitUntilAsync(async () =>
            (await MarketDataTypesAsync(connectionString, lease.LeaseId)).Length == 3));

        // 3 for the ticks the measurement covered; NULL — never a stale 3, and never "assume live"
        // — for the ones recorded before TWS had answered for the replacement ticker.
        Assert.Equal([(short?)3, null, null], await MarketDataTypesAsync(connectionString, lease.LeaseId));
    }

    [Fact]
    public async Task A_non_live_report_opens_a_gap_for_the_lease_and_a_later_live_report_closes_it()
    {
        // Requirement (c): a non-live recording session must be LOUD without refusing to boot or
        // refusing the subscription. Delayed data is the AppHost's documented first-run default, so
        // the gap row IS the alarm — the recording keeps running underneath it.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await using var recorder = RecorderFor(connectionString);
        var (manager, transport) = ManagerFor(recorder);

        var lease = await manager.GrantAsync(SubscriptionLeaseLifetimeTests.Request(702), CancellationToken.None);
        var scope = ObservationRecorder.LeaseScope(lease.LeaseId);
        var (_, sink) = OnlySink(transport);

        sink.ApplyMarketDataType(3);

        Assert.True(
            await WaitUntilAsync(async () =>
                await OpenGapReasonAsync(connectionString, scope) == ObservationRecorder.NonLiveMarketDataReason),
            "A delayed report left no open recorder_gaps row, so a delayed session is indistinguishable from a live one.");

        // Not a refusal: the subscription is still live and still recording under the alarm.
        Assert.Single(manager.ActiveLeases());
        sink.ApplyPrice(TickType.LAST, 4300.00d);

        Assert.True(await WaitUntilAsync(async () =>
            (await MarketDataTypesAsync(connectionString, lease.LeaseId)).Length == 1));

        sink.ApplyMarketDataType(1);

        Assert.True(
            await WaitUntilAsync(async () => await OpenGapCountAsync(connectionString, scope) == 0),
            "TWS reporting live did not retire the non-live gap, so the alarm is permanently red.");

        // 'observed': TWS measured the change of regime, it was not bounded by a later process.
        Assert.Equal(["observed"], await ClosedByAsync(connectionString, scope));
    }

    [Fact]
    public async Task A_live_report_does_not_close_a_disconnect_gap()
    {
        // The deviation from "just reuse NotifyGapClosed", and the reason for it. A lease's gap
        // scope holds one open row whatever opened it, so an unconditional close would let "TWS now
        // reports live" retire a DISCONNECT gap — stamping closed_by='observed' ("a tick resumed")
        // on an outage where no tick had resumed, and truncating it to boot. A fabricated
        // measurement, which is precisely what docs/DECISIONS.md §8 exists to forbid, and it
        // shortens an outage in the direction that reads as health (docs/LESSONS.md #3).
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await using var recorder = RecorderFor(connectionString);
        var (manager, transport) = ManagerFor(recorder);

        var lease = await manager.GrantAsync(SubscriptionLeaseLifetimeTests.Request(703), CancellationToken.None);
        var scope = ObservationRecorder.LeaseScope(lease.LeaseId);
        var (_, sink) = OnlySink(transport);

        // The socket drops: IbkrRequestRegistry.FailAll faults every in-flight sink.
        sink.Fail(new IbkrConnectionException("socket dropped"));

        Assert.True(
            await WaitUntilAsync(async () => await OpenGapReasonAsync(connectionString, scope) == "disconnect"));

        sink.ApplyMarketDataType(1);

        // Give the close every chance to happen wrongly before concluding it did not.
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        Assert.Equal("disconnect", await OpenGapReasonAsync(connectionString, scope));

        // And the outage is still bounded by the path that owns it, so nothing is left immortal.
        Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));
        Assert.Equal(0, await OpenGapCountAsync(connectionString, scope));
        Assert.Equal(["inferred"], await ClosedByAsync(connectionString, scope));
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Every recorded underlying tick for one lease, in write order, as its reported type.</summary>
    private static async Task<short?[]> MarketDataTypesAsync(string connectionString, Guid leaseId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT market_data_type FROM gateway.underlying_tick_events WHERE lease_id = $1 ORDER BY event_id",
            connection);
        command.Parameters.AddWithValue(leaseId);

        var values = new List<short?>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(await reader.IsDBNullAsync(0) ? null : reader.GetInt16(0));
        }

        return [.. values];
    }

    private static async Task<string?> OpenGapReasonAsync(string connectionString, string scope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT reason FROM gateway.recorder_gaps WHERE scope = $1 AND ended_at IS NULL", connection);
        command.Parameters.AddWithValue(scope);

        return await command.ExecuteScalarAsync() as string;
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

    private static async Task<string[]> ClosedByAsync(string connectionString, string scope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT closed_by FROM gateway.recorder_gaps WHERE scope = $1 ORDER BY gap_id", connection);
        command.Parameters.AddWithValue(scope);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return await condition();
    }
}
