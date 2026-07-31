using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Recording;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Universe;

namespace TradingStuff.Tests;

/// <summary>
/// Recorder-adjacent Postgres integration tests: node assignment idempotency, coverage
/// computation, and partition maintenance. Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a
/// connection string — see <see cref="OrderIdStorePostgresTests"/> for the same convention.
/// </summary>
[Trait("Category", "RequiresPostgres")]
public sealed class ResearchRecordingPostgresTests
{
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

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    private static CoverageMonitor MonitorFor(string connectionString) =>
        new(ConfigurationFor(connectionString), new SessionClock(), Options.Create(new CoverageOptions()),
            NullLogger<CoverageMonitor>.Instance);

    /// <summary>
    /// Materialises the sessions the coverage window needs. Without this the table is empty, which is
    /// not a benign starting state — see
    /// <see cref="Coverage_refuses_to_report_a_ratio_when_the_sessions_table_is_unsynced"/>.
    /// </summary>
    private static Task SyncSessionsAsync(string connectionString) =>
        new SessionCalendarService(
                new SessionGenerator(), ConfigurationFor(connectionString), NullLogger<SessionCalendarService>.Instance)
            .SyncAllAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31), CancellationToken.None);

    [Fact]
    public async Task Migration_003_seeds_the_54_node_registered_grid()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM research.option_nodes", connection);
        var count = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(54, count);
    }

    [Fact]
    public async Task Assigning_the_same_conid_twice_is_a_no_op()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var configuration = ConfigurationFor(connectionString);
        var selector = new NodeSelector(configuration, gateway: null!, NullLogger<NodeSelector>.Instance);

        var first = await selector.UpsertAssignmentAsync(connectionString, nodeId: 1, conId: 111, "bootstrap", CancellationToken.None);
        var second = await selector.UpsertAssignmentAsync(connectionString, nodeId: 1, conId: 111, "bootstrap", CancellationToken.None);

        Assert.True(first);
        Assert.False(second);

        var current = await selector.GetCurrentAssignmentsAsync(CancellationToken.None);
        var node1 = Assert.Single(current, a => a.NodeId == 1);
        Assert.Equal(111, node1.ConId);
    }

    [Fact]
    public async Task Reassigning_a_node_closes_the_old_row_and_opens_a_new_one()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var configuration = ConfigurationFor(connectionString);
        var selector = new NodeSelector(configuration, gateway: null!, NullLogger<NodeSelector>.Instance);

        await selector.UpsertAssignmentAsync(connectionString, nodeId: 2, conId: 222, "bootstrap", CancellationToken.None);
        var changed = await selector.UpsertAssignmentAsync(connectionString, nodeId: 2, conId: 333, "strike_drift", CancellationToken.None);

        Assert.True(changed);

        var current = await selector.GetCurrentAssignmentsAsync(CancellationToken.None);
        var node2 = Assert.Single(current, a => a.NodeId == 2);
        Assert.Equal(333, node2.ConId);
        Assert.Equal("strike_drift", node2.Reason);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var history = new NpgsqlCommand(
            "SELECT count(*) FROM research.node_assignments WHERE node_id = 2 AND assigned_to IS NOT NULL", connection);
        Assert.Equal(1L, (long)(await history.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Concurrent_assignment_of_the_same_node_never_leaves_two_current_rows()
    {
        // Reproduces the exact race a reviewer demonstrated against live Postgres: two concurrent
        // UpsertAssignmentAsync calls for the same node_id, both racing to replace the same
        // "current" row. Without the partial unique index (migration 003) plus the retry-on-conflict
        // in NodeSelector, Read Committed's blocked-FOR-UPDATE re-check semantics let BOTH callers
        // conclude "no current row exists" and both insert one — two current rows for one node.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var configuration = ConfigurationFor(connectionString);

        var selectorA = new NodeSelector(configuration, gateway: null!, NullLogger<NodeSelector>.Instance);
        var selectorB = new NodeSelector(configuration, gateway: null!, NullLogger<NodeSelector>.Instance);

        // Seed an initial "current" row so both racers have something to contend over closing.
        await selectorA.UpsertAssignmentAsync(connectionString, nodeId: 5, conId: 500, "bootstrap", CancellationToken.None);

        var barrier = new Barrier(2);

        Task<bool> RaceAsync(NodeSelector selector, int conId) => Task.Run(async () =>
        {
            // Task.Run is essential: RaceAsync(a) and RaceAsync(b) are evaluated as ordinary
            // synchronous method-call arguments to Task.WhenAll below, and an async method runs
            // synchronously up to its first `await`. SignalAndWait() is a BLOCKING call, not an
            // await, so calling RaceAsync(a) directly would block the test thread inside evaluating
            // Task.WhenAll's first argument — forever, since RaceAsync(b) (the barrier's second
            // participant) would never get a chance to be invoked. Task.Run moves each call onto
            // its own thread-pool thread so both barrier participants actually run concurrently.
            barrier.SignalAndWait();
            return await selector.UpsertAssignmentAsync(connectionString, nodeId: 5, conId, "bootstrap", CancellationToken.None);
        });

        await Task.WhenAll(
            RaceAsync(selectorA, 501),
            RaceAsync(selectorB, 502));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.node_assignments WHERE node_id = 5 AND assigned_to IS NULL", connection);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Coverage_counts_distinct_minutes_with_data_per_conid()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var from = new DateTimeOffset(2026, 7, 31, 14, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(10);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            // conId 999: ticks in 3 distinct minutes out of the 10-minute window.
            foreach (var offsetMinutes in new[] { 0, 0, 3, 7 })
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO gateway.option_quote_events (con_id, lease_id, observed_at, changed_fields, origin, normalization_version) " +
                    "VALUES ($1, $2, $3, 1, 1, 1)",
                    connection);
                insert.Parameters.AddWithValue(999);
                insert.Parameters.AddWithValue(Guid.NewGuid());
                insert.Parameters.AddWithValue(from.AddMinutes(offsetMinutes).AddSeconds(15));
                await insert.ExecuteNonQueryAsync();
            }

            // closed_by is required alongside ended_at: the schema refuses a gap that claims an end
            // without saying whether that end was observed or merely inferred at a later startup.
            await using var gap = new NpgsqlCommand(
                "INSERT INTO gateway.recorder_gaps (scope, started_at, ended_at, reason, closed_by) " +
                "VALUES ($1, $2, $3, $4, 'observed')", connection);
            gap.Parameters.AddWithValue("lease:test");
            gap.Parameters.AddWithValue(from.AddMinutes(4));
            gap.Parameters.AddWithValue(from.AddMinutes(5));
            gap.Parameters.AddWithValue("disconnect");
            await gap.ExecuteNonQueryAsync();
        }

        await SyncSessionsAsync(connectionString);

        var monitor = MonitorFor(connectionString);
        var report = await monitor.GetCoverageAsync(from, to, CancellationToken.None);

        Assert.Equal(CoverageBasisStatus.Measured, report.Basis.Status);

        var node999 = Assert.Single(report.PerConId);
        Assert.Equal(999, node999.ConId);
        Assert.Equal(3, node999.MinutesWithData);
        Assert.Equal(10, node999.TotalMinutes); // ten in-session minutes, not ten wall-clock minutes
        Assert.Equal(0.3, node999.CoverageRatio, precision: 3);

        var gapRow = Assert.Single(report.Gaps);
        Assert.Equal("disconnect", gapRow.Reason);
    }

    [Fact]
    public async Task Coverage_expects_the_session_length_not_the_window_length()
    {
        // The regression the session calendar was wired in for. A whole UTC day of Cboe index
        // recording is 1,185 expected minutes (GTH 00:15-13:15 UTC plus RTH 13:30-20:15 UTC on
        // 2026-07-31), not the 1,440 the wall-clock denominator asked for — which put a flawless
        // recording day at 82% and made the roadmap's 95% acceptance gate unreachable.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await SyncSessionsAsync(connectionString);

        var day = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var report = await MonitorFor(connectionString).GetCoverageAsync(day, day.AddDays(1), CancellationToken.None);

        Assert.Equal(CoverageBasisStatus.Measured, report.Basis.Status);
        Assert.Equal(1185, report.TotalMinutes);
        Assert.Equal(2, report.Basis.Sessions.Count);
        Assert.Equal(report.Basis.GeneratedSessions, report.Basis.PersistedSessions);
    }

    [Fact]
    public async Task Ticks_recorded_outside_every_session_cannot_push_coverage_past_one()
    {
        // Between the 08:15 CT GTH close and the 08:30 CT RTH open the Cboe index book is shut, but
        // TWS will still deliver the occasional snapshot tick. Counting those in the numerator while
        // the denominator only knows about sessions is how a ratio drifts above 100% — the numerator
        // is therefore filtered by the same clipped session intervals as the denominator.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await SyncSessionsAsync(connectionString);

        var day = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            // 13:20 and 13:25 UTC: after GTH closes at 13:15, before RTH opens at 13:30.
            foreach (var minute in new[] { 20, 25 })
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO gateway.underlying_tick_events (con_id, lease_id, observed_at, changed_fields, origin, normalization_version) " +
                    "VALUES ($1, $2, $3, 1, 1, 1)",
                    connection);
                insert.Parameters.AddWithValue(4001);
                insert.Parameters.AddWithValue(Guid.NewGuid());
                insert.Parameters.AddWithValue(day.AddHours(13).AddMinutes(minute));
                await insert.ExecuteNonQueryAsync();
            }

            // One tick that IS inside RTH, so the conId is not simply absent from the report.
            await using var inSession = new NpgsqlCommand(
                "INSERT INTO gateway.underlying_tick_events (con_id, lease_id, observed_at, changed_fields, origin, normalization_version) " +
                "VALUES ($1, $2, $3, 1, 1, 1)",
                connection);
            inSession.Parameters.AddWithValue(4001);
            inSession.Parameters.AddWithValue(Guid.NewGuid());
            inSession.Parameters.AddWithValue(day.AddHours(14));
            await inSession.ExecuteNonQueryAsync();
        }

        var report = await MonitorFor(connectionString).GetCoverageAsync(day, day.AddDays(1), CancellationToken.None);

        var underlying = Assert.Single(report.PerConId, row => row.ConId == 4001);
        Assert.Equal(1, underlying.MinutesWithData); // the two out-of-session ticks contribute nothing
    }

    [Fact]
    public async Task Coverage_refuses_to_report_a_ratio_when_the_sessions_table_is_unsynced()
    {
        // The absence hazard, and the reason CoverageMonitor consults the generator at all: a query
        // over research.sessions cannot emit a row for a session that is missing, and a missing
        // session SHRINKS the denominator. An empty table would therefore have reported a perfect
        // score for a window in which nothing was recorded, rather than reporting nothing.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var day = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        var report = await MonitorFor(connectionString).GetCoverageAsync(day, day.AddDays(1), CancellationToken.None);

        Assert.Equal(CoverageBasisStatus.SessionsOutOfSync, report.Basis.Status);
        Assert.Null(report.OverallCoverageRatio);
        Assert.Equal(0, report.Basis.PersistedSessions);
        Assert.Equal(2, report.Basis.GeneratedSessions);
    }

    [Fact]
    public async Task Coverage_over_a_closed_market_reports_no_measurement_rather_than_zero_percent()
    {
        // Saturday 2026-08-01. Nothing was expected, so nothing is measured — a weekend is not a 0%
        // recording day, and reporting it as one is how an acceptance gate becomes permanently red
        // and therefore unread.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await SyncSessionsAsync(connectionString);

        var saturday = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var report = await MonitorFor(connectionString).GetCoverageAsync(saturday, saturday.AddDays(1), CancellationToken.None);

        Assert.Equal(CoverageBasisStatus.NoSessionInWindow, report.Basis.Status);
        Assert.Null(report.OverallCoverageRatio);
        Assert.Equal(0, report.TotalMinutes);
        Assert.Empty(report.Basis.Sessions);
    }

    [Fact]
    public async Task Coverage_is_zero_with_no_data_and_no_recorded_conids()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        await SyncSessionsAsync(connectionString);

        // Inside the 2026-07-31 Cboe RTH session, so there genuinely is a denominator to fall short of.
        var from = new DateTimeOffset(2026, 7, 31, 14, 0, 0, TimeSpan.Zero);
        var report = await MonitorFor(connectionString).GetCoverageAsync(from, from.AddHours(1), CancellationToken.None);

        Assert.Equal(CoverageBasisStatus.Measured, report.Basis.Status);
        Assert.Equal(60, report.TotalMinutes);
        Assert.Empty(report.PerConId);
        Assert.Equal(0d, report.OverallCoverageRatio);
    }

    [Fact]
    public async Task A_currently_assigned_node_with_zero_ticks_reports_zero_percent_instead_of_being_invisible()
    {
        // Regression: a plain GROUP BY over the raw event tables never produces a row for a conId
        // with no ticks at all — a total recording failure was previously indistinguishable from
        // "this conId doesn't exist", the opposite of what a coverage report is for.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var configuration = ConfigurationFor(connectionString);
        var selector = new NodeSelector(configuration, gateway: null!, NullLogger<NodeSelector>.Instance);

        // A node assignment for a conId that never once ticks.
        await selector.UpsertAssignmentAsync(connectionString, nodeId: 9, conId: 999999, "bootstrap", CancellationToken.None);
        await SyncSessionsAsync(connectionString);

        var from = new DateTimeOffset(2026, 7, 31, 14, 0, 0, TimeSpan.Zero);
        var report = await MonitorFor(connectionString).GetCoverageAsync(from, from.AddHours(1), CancellationToken.None);

        var dead = Assert.Single(report.PerConId, c => c.ConId == 999999);
        Assert.Equal(0, dead.MinutesWithData);
        Assert.Equal(0, dead.CoverageRatio);
        Assert.Equal(0d, report.OverallCoverageRatio); // the only expected conId, and it's fully dead
    }

    [Fact]
    public async Task Partition_maintenance_creates_partitions_and_is_idempotent()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var maintainer = new PartitionMaintainer(ConfigurationFor(connectionString), NullLogger<PartitionMaintainer>.Instance);

        await maintainer.EnsureUpcomingPartitionsAsync(connectionString, CancellationToken.None);
        await maintainer.EnsureUpcomingPartitionsAsync(connectionString, CancellationToken.None); // rerun must not throw

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expectedPartition = $"option_quote_events_{today:yyyyMMdd}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = $1", connection);
        command.Parameters.AddWithValue(expectedPartition);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task One_undreatable_partition_date_does_not_block_every_other_date_in_the_sweep()
    {
        // Regression: EnsureUpcomingPartitionsAsync had no per-call try/catch, so one date that
        // can't get a partition created (here: a row for that date already sits in DEFAULT — the
        // exact Postgres conflict this class's remarks document) aborted the whole sweep, silently
        // starving every OTHER date in the loop, for both tables, on every retry, forever.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var maintainer = new PartitionMaintainer(ConfigurationFor(connectionString), NullLogger<PartitionMaintainer>.Instance);

        var poisonedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO gateway.option_quote_events (con_id, lease_id, observed_at, changed_fields, origin, normalization_version) " +
                "VALUES (1, $1, $2, 1, 1, 1)",
                connection);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(poisonedDate.ToDateTime(new TimeOnly(12, 0)));
            await insert.ExecuteNonQueryAsync(); // lands in the DEFAULT partition; poisons this date
        }

        // Must not throw despite one of the four dates being un-creatable.
        await maintainer.EnsureUpcomingPartitionsAsync(connectionString, CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var check = new NpgsqlConnection(connectionString);
        await check.OpenAsync();

        foreach (var offset in new[] { 0, 1, 3 }) // every date EXCEPT the poisoned one (offset 2)
        {
            var expected = $"option_quote_events_{today.AddDays(offset):yyyyMMdd}";
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = $1", check);
            command.Parameters.AddWithValue(expected);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }

        // The poisoned date's own partition genuinely could not be created — confirming the test
        // actually exercised the failure path, not a no-op.
        var poisonedPartition = $"option_quote_events_{poisonedDate:yyyyMMdd}";
        await using var poisonedCheck = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = $1", check);
        poisonedCheck.Parameters.AddWithValue(poisonedPartition);
        Assert.Equal(0L, (long)(await poisonedCheck.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task An_event_lands_in_the_default_partition_when_no_daily_partition_exists()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        // Deliberately far in the future — no partition maintenance has run for this date, but the
        // insert must still succeed via the DEFAULT partition rather than fail.
        var farFuture = DateTimeOffset.UtcNow.AddYears(1);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var insert = new NpgsqlCommand(
            "INSERT INTO gateway.option_quote_events (con_id, lease_id, observed_at, changed_fields, origin, normalization_version) " +
            "VALUES ($1, $2, $3, 1, 1, 1)",
            connection);
        insert.Parameters.AddWithValue(1);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(farFuture);

        await insert.ExecuteNonQueryAsync(); // must not throw
    }
}
