using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// <see cref="ObservationRecorder"/> against a real Postgres: batched COPY persistence, shutdown
/// flushing, and the buffer-overflow gap's open-then-close lifecycle.
/// </summary>
/// <remarks>
/// Three of these are regressions for defects that lost unrecoverable data or left a gap row
/// immortal, and each asserts the mechanism rather than the symptom: that a graceful shutdown
/// persists what was buffered, that a shutdown which gives up says so in <c>write_failures</c> and a
/// gap row, and that a buffer-overflow gap closes with no further tick on the channel — see
/// <c>ObservationRecorder.OverflowGap</c> and <c>DisposeAsync</c> for why each of those was false.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class ObservationRecorderPostgresTests
{
    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _created = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);

            lock (_created)
            {
                _created.Add(meter);
            }

            return meter;
        }

        public bool Owns(Meter meter)
        {
            lock (_created)
            {
                return _created.Contains(meter);
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Sums one recorder's counter measurements. Scoped to the meters ONE factory created rather
    /// than to the meter's name: the name is shared with every other recorder in the test run, and
    /// "how many observations did this recorder fail to persist" must not pick up someone else's.
    /// </summary>
    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _totals = [];

        public CounterProbe(TestMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (factory.Owns(instrument.Meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                lock (_totals)
                {
                    _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + measurement;
                }
            });

            _listener.Start();
        }

        public long Total(string instrument)
        {
            lock (_totals)
            {
                return _totals.GetValueOrDefault(instrument);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<string> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return connectionString;
    }

    private static ObservationRecorder CreateRecorder(
        string connectionString, IMeterFactory? meterFactory = null, double? shutdownDrainSeconds = null)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString };

        if (shutdownDrainSeconds is { } seconds)
        {
            settings["Recorder:ShutdownDrainSeconds"] = seconds.ToString(CultureInfo.InvariantCulture);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ObservationRecorder(
            configuration, meterFactory ?? new TestMeterFactory(), NullLogger<ObservationRecorder>.Instance);
    }

    private static OptionQuoteObservation SampleOptionObservation(int conId, Guid leaseId) => new(
        new ObservationEnvelope(conId, leaseId, DateTimeOffset.UtcNow, NormalizationVersion: 1, ObservationOrigin.Stream),
        QuoteFieldChanges.Bid, Bid: 1.95m, Ask: 2.05m, BidSize: 8m, AskSize: 12m, Last: 2.00m, LastSize: 1m,
        Volume: 5m, OpenInterest: 100m, GreeksVariant.Model, Iv: 0.20m, Delta: 0.51m, Gamma: 0.001m,
        Vega: 7.9m, Theta: -2.4m, UnderlyingPrice: 7436.57m, Locked: false, Crossed: false);

    private static UnderlyingTickObservation SampleUnderlyingObservation(int conId, Guid leaseId) => new(
        new ObservationEnvelope(conId, leaseId, DateTimeOffset.UtcNow, NormalizationVersion: 1, ObservationOrigin.Stream),
        QuoteFieldChanges.Last, Bid: 7436.50m, Ask: 7436.75m, BidSize: 3m, AskSize: 4m, Last: 7436.57m,
        LastSize: 1m, Volume: 12m, Locked: false, Crossed: false);

    [Fact]
    public async Task Enqueued_observations_are_persisted_via_batched_copy()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var leaseId = Guid.NewGuid();

        await using (var recorder = CreateRecorder(connectionString))
        {
            for (var i = 0; i < 10; i++)
            {
                recorder.EnqueueOption(SampleOptionObservation(555, leaseId));
            }

            // Assert on the wait, do not merely perform it: WaitUntilAsync returns whether the
            // condition ever held, and discarding that made this test pass with zero rows persisted.
            Assert.True(
                await WaitUntilAsync(async () =>
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand(
                        "SELECT count(*) FROM gateway.option_quote_events WHERE con_id = 555", connection);
                    return (long)(await command.ExecuteScalarAsync())! == 10;
                }, TimeSpan.FromSeconds(5)),
                "The 10 enqueued observations never reached gateway.option_quote_events.");
        }
    }

    [Fact]
    public async Task Opening_a_gap_twice_for_the_same_scope_does_not_duplicate_the_row()
    {
        // This is the exact call sequence CheckOverflow makes while a channel stays saturated
        // across many ticks: repeated OpenGapAsync calls for the same scope must not spam rows.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await using var recorder = CreateRecorder(connectionString);

        await recorder.OpenGapAsync("recorder:option-buffer", "buffer_overflow", CancellationToken.None);
        await recorder.OpenGapAsync("recorder:option-buffer", "buffer_overflow", CancellationToken.None);
        await recorder.OpenGapAsync("recorder:option-buffer", "buffer_overflow", CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = 'recorder:option-buffer' AND ended_at IS NULL",
            connection);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Closing_a_gap_lets_a_later_open_start_a_fresh_row()
    {
        // The regression this guards: CheckOverflow.EnqueueOption's "no longer saturated" branch
        // must actually reach CloseGapAsync — before that fix, a buffer-overflow gap, once opened,
        // never closed even after the backlog fully drained.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await using var recorder = CreateRecorder(connectionString);
        const string scope = "recorder:underlying-buffer";

        await recorder.OpenGapAsync(scope, "buffer_overflow", CancellationToken.None);
        await recorder.CloseGapAsync(scope);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT ended_at FROM gateway.recorder_gaps WHERE scope = $1 ORDER BY gap_id DESC LIMIT 1", connection);
            command.Parameters.AddWithValue(scope);
            var endedAt = await command.ExecuteScalarAsync();
            Assert.IsNotType<DBNull>(endedAt);
        }

        // Recovered, then saturated again later: this must open a NEW row, not resurrect the closed one.
        await recorder.OpenGapAsync(scope, "buffer_overflow", CancellationToken.None);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var openCount = new NpgsqlCommand(
                "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = $1 AND ended_at IS NULL", connection);
            openCount.Parameters.AddWithValue(scope);
            Assert.Equal(1L, (long)(await openCount.ExecuteScalarAsync())!);

            await using var totalCount = new NpgsqlCommand(
                "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = $1", connection);
            totalCount.Parameters.AddWithValue(scope);
            Assert.Equal(2L, (long)(await totalCount.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task A_gap_left_open_by_a_dead_process_is_bounded_at_the_next_startup()
    {
        // Found by killing the gateway three times during live paper testing. Only the process that
        // opened a gap ever closes it, so an ungraceful exit leaves ended_at NULL forever — and
        // CoverageMonitor counts an unended gap as overlapping EVERY later window, so one crash in
        // July silently fails coverage for the rest of the platform's life.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        // The dead process: opens a gap, then goes away without closing it.
        await using (var abandoned = CreateRecorder(connectionString))
        {
            await abandoned.OpenGapAsync("lease:abandoned", "disconnect", CancellationToken.None);
        }

        // A gap this process closed itself must keep its 'observed' provenance through the sweep.
        await using var survivor = CreateRecorder(connectionString);
        await survivor.OpenGapAsync("lease:recovered", "disconnect", CancellationToken.None);
        await survivor.CloseGapAsync("lease:recovered");

        await using var restarted = CreateRecorder(connectionString);
        await restarted.ReconcileOrphanedGapsAsync(CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var stillOpen = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps WHERE ended_at IS NULL", connection);
        Assert.Equal(0L, (long)(await stillOpen.ExecuteScalarAsync())!);

        await using var provenance = new NpgsqlCommand(
            "SELECT scope, closed_by FROM gateway.recorder_gaps ORDER BY gap_id", connection);
        var closedBy = new Dictionary<string, string>();
        await using (var reader = await provenance.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                closedBy[reader.GetString(0)] = reader.GetString(1);
            }
        }

        // The distinction the column exists for: one end time was measured, the other only bounded.
        Assert.Equal("inferred", closedBy["lease:abandoned"]);
        Assert.Equal("observed", closedBy["lease:recovered"]);
    }

    [Fact]
    public async Task Observations_buffered_at_shutdown_are_flushed_rather_than_discarded()
    {
        // DisposeAsync used to cancel BEFORE completing the writers, so "complete the channel and
        // let the loops drain to the end" could not happen: the fill threw on the already-cancelled
        // token, the loop faulted with its in-flight batch, and everything still queued went with
        // it — up to 55,000 unrecoverable observations on every graceful restart, with no
        // write_failures and no gap row to tell CoverageMonitor where the hole came from.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var leaseId = Guid.NewGuid();
        const int enqueued = 20_000; // several batches, well under ChannelCapacity so nothing is dropped

        var recorder = CreateRecorder(connectionString);

        for (var i = 0; i < enqueued; i++)
        {
            recorder.EnqueueOption(SampleOptionObservation(557, leaseId));
        }

        // No polling around this: when DisposeAsync returns, a graceful shutdown is over and every
        // observation it accepted must already be durable.
        await recorder.DisposeAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.option_quote_events WHERE con_id = 557", connection);

        Assert.Equal((long)enqueued, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Observations_lost_to_a_shutdown_drain_timeout_are_counted_and_leave_a_gap()
    {
        // The other half of the same contract. The drain is bounded, because a dead Postgres must
        // not be able to hang shutdown — but data that the deadline gives up on has to be recorded
        // as lost, not simply dropped. An ACCESS EXCLUSIVE lock on the target table stalls the COPY
        // exactly the way an unresponsive server would, while leaving the connection usable for
        // reading the gap row back afterwards.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var leaseId = Guid.NewGuid();
        var meterFactory = new TestMeterFactory();
        using var probe = new CounterProbe(meterFactory);
        const int enqueued = 6_000; // one full batch in flight plus a remainder still queued

        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync();
        await using var blocking = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand(
            "LOCK TABLE gateway.option_quote_events IN ACCESS EXCLUSIVE MODE", blocker, blocking))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        var recorder = CreateRecorder(connectionString, meterFactory, shutdownDrainSeconds: 1);

        for (var i = 0; i < enqueued; i++)
        {
            recorder.EnqueueOption(SampleOptionObservation(558, leaseId));
        }

        await recorder.DisposeAsync();
        await blocking.RollbackAsync();

        // Every observation is accounted for: none may vanish with the faulting drain task.
        Assert.Equal(enqueued, probe.Total("gateway.recorder.write_failures"));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var gap = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps " +
            "WHERE scope = 'recorder:write:option' AND ended_at IS NULL", connection);

        // Left open on purpose: the process is exiting, so nobody observes recording resume. The
        // next startup's ReconcileOrphanedGapsAsync bounds it as 'inferred' — which it can only do
        // because a row exists at all.
        Assert.Equal(1L, (long)(await gap.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_buffer_overflow_gap_closes_after_the_drain_frees_capacity_with_no_further_ticks()
    {
        // The immortal-gap defect, third door. The close used to be attempted only by a LATER
        // enqueue that happened to observe both headroom and an already-published gap id — so a
        // channel that saturates and then goes quiet keeps its gap open indefinitely. That is not a
        // hypothetical for recorder:underlying-buffer: neither the SPX nor the VIX index level
        // updates through Cboe GTH, so an overflow in the last seconds before the RTH close leaves
        // the row open all night and all weekend, and CoverageMonitor reads an unended gap as an
        // outage overlapping EVERY window from that moment on.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await using var recorder = CreateRecorder(connectionString);
        var leaseId = Guid.NewGuid();
        const string scope = "recorder:underlying-buffer";

        // Outrun the drain loop, which can only take BatchSize before it has to go and write them.
        for (var i = 0; i < 120_000; i++)
        {
            recorder.EnqueueUnderlying(SampleUnderlyingObservation(999, leaseId));
        }

        Assert.True(
            await WaitUntilAsync(() => CountGapsAsync(connectionString, scope, openOnly: true, expected: 1), TimeSpan.FromSeconds(15)),
            "Saturating the underlying buffer did not open a buffer_overflow gap, so the rest of this test proves nothing.");

        // Not one further enqueue past this point — the whole defect is that the close depended on one.
        Assert.True(
            await WaitUntilAsync(() => CountGapsAsync(connectionString, scope, openOnly: true, expected: 0), TimeSpan.FromSeconds(60)),
            "The buffer-overflow gap was still open after the backlog drained: nothing closes it but the next tick.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var provenance = new NpgsqlCommand(
            "SELECT closed_by FROM gateway.recorder_gaps WHERE scope = $1", connection);
        provenance.Parameters.AddWithValue(scope);

        var rows = new List<string>();
        await using (var reader = await provenance.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }
        }

        // One row for one episode — the hysteresis on the close is what stops sustained saturation
        // spraying a row per drained batch — and its end was measured, not merely bounded.
        Assert.Equal(["observed"], rows);
    }

    private static async Task<bool> CountGapsAsync(string connectionString, string scope, bool openOnly, long expected)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = $1" + (openOnly ? " AND ended_at IS NULL" : string.Empty),
            connection);
        command.Parameters.AddWithValue(scope);

        return (long)(await command.ExecuteScalarAsync())! == expected;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return await condition();
    }
}
