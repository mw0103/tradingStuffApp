using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// <see cref="ObservationRecorder"/> against a real Postgres: batched COPY persistence, and the
/// buffer-overflow gap's open-then-close lifecycle (a regression test — the close half was missing
/// until this pass; see <c>ObservationRecorder.CheckOverflow</c>'s remarks).
/// </summary>
[Trait("Category", "RequiresPostgres")]
public sealed class ObservationRecorderPostgresTests
{
    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<string> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        return connectionString;
    }

    private static ObservationRecorder CreateRecorder(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        return new ObservationRecorder(configuration, new TestMeterFactory(), NullLogger<ObservationRecorder>.Instance);
    }

    private static OptionQuoteObservation SampleOptionObservation(int conId, Guid leaseId) => new(
        new ObservationEnvelope(conId, leaseId, DateTimeOffset.UtcNow, NormalizationVersion: 1, ObservationOrigin.Stream),
        QuoteFieldChanges.Bid, Bid: 1.95m, Ask: 2.05m, BidSize: 8m, AskSize: 12m, Last: 2.00m, LastSize: 1m,
        Volume: 5m, OpenInterest: 100m, GreeksVariant.Model, Iv: 0.20m, Delta: 0.51m, Gamma: 0.001m,
        Vega: 7.9m, Theta: -2.4m, UnderlyingPrice: 7436.57m, Locked: false, Crossed: false);

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

            await WaitUntilAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT count(*) FROM gateway.option_quote_events WHERE con_id = 555", connection);
                return (long)(await command.ExecuteScalarAsync())! == 10;
            }, TimeSpan.FromSeconds(5));
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
