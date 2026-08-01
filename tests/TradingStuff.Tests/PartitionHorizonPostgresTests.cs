using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// The cold-start partition window, and the DEFAULT-partition alarm that reports what falls through
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The failure these pin is permanent and cannot be undone by code: once a row for date D sits in a
/// table's DEFAULT partition, Postgres refuses forever to create the real partition for D. Migration
/// 003 creates the raw-event tables and their DEFAULT partitions together, so from the instant it
/// commits the recorder's COPY succeeds — into DEFAULT — and PartitionMaintainer, in a different
/// process, used to be racing migrations at that exact moment.
/// </para>
/// <para>
/// <see cref="A_row_in_default_permanently_blocks_that_dates_partition"/> is the reproduction: it
/// applies the migration set WITHOUT 012, which is what the schema looked like before this fix, and
/// shows a first tick stranding today permanently.
/// <see cref="Migrations_alone_leave_no_window_for_a_first_tick_to_strand"/> is the same sequence
/// against the current schema.
/// </para>
/// <para>
/// Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection string, per the convention in
/// <see cref="ResearchRecordingPostgresTests"/>.
/// </para>
/// </remarks>
[Trait("Category", "RequiresPostgres")]
public sealed class PartitionHorizonPostgresTests
{
    private const string PartitionHorizonMigration = "012_partition_horizon.sql";

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static IConfiguration ConfigurationFor(string connectionString, params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString };

        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>Applies every embedded migration to a fresh database — the current schema.</summary>
    private static Task<string> PrepareAsync(string server) => PrepareAsync(server, skipMigration: null);

    /// <summary>
    /// As above, but optionally omitting one migration by name, so a test can stand the schema up
    /// exactly as it was before that migration existed. This is how the pre-fix behaviour is
    /// reproduced rather than merely described.
    /// </summary>
    private static async Task<string> PrepareAsync(string server, string? skipMigration)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        var runner = new MigrationRunner(
            ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);

        var migrations = MigrationRunner.LoadEmbeddedMigrations()
            .Where(m => skipMigration is null || !string.Equals(m.Name, skipMigration, StringComparison.Ordinal))
            .ToArray();

        await runner.ApplyOnceAsync(connectionString, migrations, CancellationToken.None);

        return connectionString;
    }

    private static PartitionMaintainer MaintainerFor(
        string connectionString, ILogger<PartitionMaintainer>? logger = null, params (string, string)[] settings) =>
        new(ConfigurationFor(connectionString, settings), logger ?? NullLogger<PartitionMaintainer>.Instance);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task<bool> PartitionExistsAsync(string connectionString, string table, DateOnly date) =>
        ScalarAsync<bool>(connectionString, "SELECT to_regclass($1) IS NOT NULL", $"gateway.\"{table}_{date:yyyyMMdd}\"");

    /// <summary>
    /// A tick the way the gateway's recorder produces one: a plain INSERT into the PARENT table, so
    /// Postgres does the routing. Which partition it lands in is the entire subject of these tests,
    /// so nothing here may name a partition.
    /// </summary>
    private static async Task InsertTickAsync(string connectionString, DateTime observedAtUtc, int conId = 1)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO gateway.option_quote_events " +
            "(con_id, lease_id, observed_at, changed_fields, bid, ask, origin, normalization_version) " +
            "VALUES ($1, $2, $3, 1, 1.25, 1.35, 1, 1)",
            connection);
        command.Parameters.AddWithValue(conId);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(DateTime.SpecifyKind(observedAtUtc, DateTimeKind.Utc));

        await command.ExecuteNonQueryAsync();
    }

    // =============================================================================================
    // The window itself
    // =============================================================================================

    [Fact]
    public async Task A_row_in_default_permanently_blocks_that_dates_partition()
    {
        // REPRODUCTION of the shipped defect, against the schema as it stood before migration 012.
        // The sequence is exactly the cold-start one: migrations land (003 makes the table writable
        // and gives it a DEFAULT partition), the recorder writes a tick before PartitionMaintainer
        // has swept, and the sweep that follows can never create that date's partition again.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        // Nothing has created a daily partition yet — the pre-012 state of a freshly migrated database.
        Assert.False(await PartitionExistsAsync(connectionString, "option_quote_events", today));

        await InsertTickAsync(connectionString, now);

        Assert.Equal(
            1L,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM gateway.option_quote_events_default"));

        // The sweep that "looks like it worked": it must not throw, and it creates every other date.
        await MaintainerFor(connectionString).EnsureUpcomingPartitionsAsync(connectionString, CancellationToken.None);

        // ...but today's partition is now permanently un-creatable. This is the permanent consequence.
        Assert.False(await PartitionExistsAsync(connectionString, "option_quote_events", today));
        Assert.True(await PartitionExistsAsync(connectionString, "option_quote_events", today.AddDays(1)));

        // And it stays that way however many times the sweep runs — there is no retry out of this.
        await MaintainerFor(connectionString).EnsureUpcomingPartitionsAsync(connectionString, CancellationToken.None);
        Assert.False(await PartitionExistsAsync(connectionString, "option_quote_events", today));
    }

    [Fact]
    public async Task Migrations_alone_leave_no_window_for_a_first_tick_to_strand()
    {
        // The same sequence against the current schema, with PartitionMaintainer never run at all —
        // which is the point: the guarantee cannot depend on ResearchService's background loop,
        // because the process that writes ticks (IbkrGateway) outlives ResearchService by design.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        Assert.True(await PartitionExistsAsync(connectionString, "option_quote_events", today));

        await InsertTickAsync(connectionString, now);

        Assert.Equal(
            0L,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM gateway.option_quote_events_default"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connectionString, $"SELECT count(*) FROM gateway.\"option_quote_events_{today:yyyyMMdd}\""));
    }

    [Fact]
    public async Task The_migration_horizon_covers_a_researchservice_that_never_starts()
    {
        // A ResearchService that is down, redeploying, or simply switched off must not be able to
        // strand a day. The horizon migrations create is what buys that, and it must match
        // PartitionMaintainer.DaysAhead or the maintainer's own sweep would leave a hole at the seam.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var table in new[] { "option_quote_events", "underlying_tick_events" })
        {
            for (var offset = 0; offset <= PartitionMaintainer.DaysAhead; offset++)
            {
                Assert.True(
                    await PartitionExistsAsync(connectionString, table, today.AddDays(offset)),
                    $"gateway.{table}_{today.AddDays(offset):yyyyMMdd} should have been created by migration 012.");
            }
        }
    }

    [Fact]
    public async Task Migration_012_does_not_abort_when_a_date_is_already_stranded()
    {
        // On an EXISTING database, today may already be poisoned before 012 ever runs. One
        // un-creatable date must not fail the migration and leave the service unable to start — the
        // same per-date isolation the Phase 1 review added to the C# sweep, expressed in PL/pgSQL.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        await InsertTickAsync(connectionString, now);

        // Now apply 012 on top, as an upgrade of a running deployment would.
        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        var applied = await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        Assert.Contains(PartitionHorizonMigration, applied);

        // Today could not be created — that damage is already done and is not what this asserts.
        Assert.False(await PartitionExistsAsync(connectionString, "option_quote_events", today));

        // Every other date in the horizon still got its partition, for both tables.
        for (var offset = 1; offset <= PartitionMaintainer.DaysAhead; offset++)
        {
            Assert.True(await PartitionExistsAsync(connectionString, "option_quote_events", today.AddDays(offset)));
            Assert.True(await PartitionExistsAsync(connectionString, "underlying_tick_events", today.AddDays(offset)));
        }
    }

    [Fact]
    public async Task The_maintainer_waits_quietly_for_the_schema_instead_of_failing_its_first_sweep()
    {
        // The other half of the cold-start defect. On an unmigrated database every statement in a
        // sweep failed, the outer catch called that a failed sweep, and the maintainer dropped into
        // its 1-minute failure-retry — which was the ONLY thing standing between a database that had
        // just finished migrating and the recorder's first COPY. Waiting is both quieter and faster,
        // and an Error here is a false alarm about a database that is merely still starting up.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = $"{server.TrimEnd(';')};Database={database}";

        await using (var maintenance = new NpgsqlConnection($"{server.TrimEnd(';')};Database=postgres"))
        {
            await maintenance.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", maintenance);
            await create.ExecuteNonQueryAsync();
        }

        var log = new CollectingLogger();
        IHostedService maintainer = MaintainerFor(connectionString, log);

        await maintainer.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        await maintainer.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("waiting for the schema"));
    }

    // =============================================================================================
    // The alarm
    // =============================================================================================

    [Fact]
    public async Task A_stranded_date_is_critical_once_and_a_standing_warning_thereafter()
    {
        // Nothing removes stranded rows, so the old per-table "count > 0 -> LogCritical" fired on
        // every sweep for the life of the deployment. A permanently-red gate is a gate nobody reads —
        // recorded twice already in this project — and it made a genuinely NEW stranded date
        // indistinguishable from the standing one.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        await InsertTickAsync(connectionString, now);

        var log = new CollectingLogger();
        var maintainer = MaintainerFor(connectionString, log);

        await maintainer.SweepAsync(connectionString, CancellationToken.None);

        var first = Assert.Single(log.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("NEW:", first.Message);
        Assert.Contains(today.ToString("yyyy-MM-dd"), first.Message);
        Assert.Contains("option_quote_events_default", first.Message);
        // The remedy has to be IN the alarm; an alarm that only says "something is wrong" is the
        // thing being fixed here, not the thing being kept.
        Assert.Contains("Partitions:RepairStrandedRows", first.Message);
        Assert.Contains("PARTITION OF", first.Message);

        Assert.Single(maintainer.StrandedDates);
        Assert.Equal(today, maintainer.StrandedDates[0].Date);

        log.Entries.Clear();
        await maintainer.SweepAsync(connectionString, CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Critical);
        var standing = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Still stranded", standing.Message);
        Assert.Contains(today.ToString("yyyy-MM-dd"), standing.Message);
    }

    [Fact]
    public async Task A_second_stranded_date_is_reported_as_new_alongside_the_standing_one()
    {
        // The distinction the whole rewrite exists for: a NEW date starting to strand must be
        // audible over a standing condition that has already been reported in full.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var tomorrow = today.AddDays(1);

        await InsertTickAsync(connectionString, now);

        var log = new CollectingLogger();
        var maintainer = MaintainerFor(connectionString, log);

        await maintainer.SweepAsync(connectionString, CancellationToken.None);
        log.Entries.Clear();

        // A second date starts stranding. (Constructed directly rather than by waiting a day: drop
        // tomorrow's partition, which the sweep above created, then write to that date.)
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP TABLE gateway.\"option_quote_events_{tomorrow:yyyyMMdd}\"", connection);
            await drop.ExecuteNonQueryAsync();
        }

        await InsertTickAsync(connectionString, tomorrow.ToDateTime(new TimeOnly(12, 0)));

        await maintainer.SweepAsync(connectionString, CancellationToken.None);

        var critical = Assert.Single(log.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains(tomorrow.ToString("yyyy-MM-dd"), critical.Message);
        Assert.DoesNotContain(today.ToString("yyyy-MM-dd"), critical.Message);

        var standing = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(today.ToString("yyyy-MM-dd"), standing.Message);

        Assert.Equal(2, maintainer.StrandedDates.Count);
    }

    [Fact]
    public async Task A_stranded_bars_row_is_diagnosed_as_corruption_and_never_repaired()
    {
        // research.bars pre-creates every yearly partition for 1990-2035, so a correctly-dated bar
        // CANNOT reach DEFAULT. A row that did carries an out-of-range timestamp — a mis-parse — and
        // creating a partition for it would only make the wrong value permanent.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            await using var seedInstrument = new NpgsqlCommand(
                "SELECT instrument_id FROM research.instruments ORDER BY instrument_id LIMIT 1", connection);
            var instrumentId = (short)(await seedInstrument.ExecuteScalarAsync())!;

            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.bars " +
                "(con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, open, high, low, close, source) " +
                "VALUES (1, $1, '1 min', 'TRADES', true, $2, 1, 1, 1, 1, 'backfill')",
                connection);
            insert.Parameters.AddWithValue(instrumentId);
            // An epoch-seconds value read as something else: year 1970, outside the pre-created range.
            insert.Parameters.AddWithValue(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            await insert.ExecuteNonQueryAsync();
        }

        var log = new CollectingLogger();
        var maintainer = MaintainerFor(
            connectionString, log, ("Partitions:RepairStrandedRows", "true"));

        await maintainer.SweepAsync(connectionString, CancellationToken.None);

        var critical = Assert.Single(log.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("DATA CORRUPTION", critical.Message);
        Assert.Contains("1970-01-01", critical.Message);

        // Repair was ENABLED and still refused: the row is untouched and no 1970 partition appeared.
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM research.bars_default"));
        Assert.False(await ScalarAsync<bool>(
            connectionString, "SELECT to_regclass($1) IS NOT NULL", "research.\"bars_1970\""));
        Assert.False(maintainer.StrandedDates[0].Repairable);
    }

    // =============================================================================================
    // Repair
    // =============================================================================================

    [Fact]
    public async Task Repair_is_off_unless_it_is_explicitly_asked_for()
    {
        // These are recorded market data rows and the roadmap treats prospective ticks as
        // unrecoverable. Nothing moves them as a side effect of a routine sweep.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        await InsertTickAsync(connectionString, now);
        await MaintainerFor(connectionString).SweepAsync(connectionString, CancellationToken.None);

        Assert.Equal(
            1L,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM gateway.option_quote_events_default"));
        Assert.False(await PartitionExistsAsync(connectionString, "option_quote_events", today));
    }

    [Fact]
    public async Task Repair_moves_every_stranded_row_into_a_real_partition_and_loses_none()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        // A spread of times across the stranded day, plus a row on a DIFFERENT date that must not be
        // touched by a repair scoped to today.
        var strandedAt = new[]
        {
            today.ToDateTime(new TimeOnly(0, 0)),
            today.ToDateTime(new TimeOnly(9, 30)),
            today.ToDateTime(new TimeOnly(23, 59, 59)),
        };

        foreach (var at in strandedAt)
        {
            await InsertTickAsync(connectionString, at, conId: 42);
        }

        // A SECOND stranded date, behind the horizon so the sweep never creates its partition either.
        // Repair is not scoped to today, and it must handle each date on its own rather than lumping
        // the whole DEFAULT partition into one range.
        var yesterday = today.AddDays(-1);
        await InsertTickAsync(connectionString, yesterday.ToDateTime(new TimeOnly(12, 0)), conId: 7);

        var idsBefore = await EventIdsAsync(connectionString, 42);

        var maintainer = MaintainerFor(
            connectionString, logger: null, ("Partitions:RepairStrandedRows", "true"));

        await maintainer.SweepAsync(connectionString, CancellationToken.None);

        // Today's partition exists and holds exactly the rescued rows, ids and all — the ids matter:
        // event_id is GENERATED ALWAYS, so a repair that re-inserted without OVERRIDING SYSTEM VALUE
        // would silently renumber recorded market data.
        Assert.True(await PartitionExistsAsync(connectionString, "option_quote_events", today));
        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                connectionString, $"SELECT count(*) FROM gateway.\"option_quote_events_{today:yyyyMMdd}\""));
        Assert.Equal(idsBefore, await EventIdsAsync(connectionString, 42));

        // The second date was repaired independently, into its own partition.
        Assert.True(await PartitionExistsAsync(connectionString, "option_quote_events", yesterday));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connectionString, $"SELECT count(*) FROM gateway.\"option_quote_events_{yesterday:yyyyMMdd}\""));

        // Nothing was lost anywhere: the parent still returns every row that was ever written, and
        // DEFAULT is empty, so both dates can be exported and dropped by retention again.
        Assert.Equal(
            4L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM gateway.option_quote_events"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM gateway.option_quote_events_default"));

        // The payload survived the move, not just the row count.
        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM gateway.option_quote_events WHERE con_id = 42 AND bid = 1.25 AND ask = 1.35"));

        Assert.Empty(maintainer.StrandedDates);
    }

    [Fact]
    public async Task Repair_refuses_rather_than_stalling_the_recorder_when_the_move_is_too_large()
    {
        // Moving rows holds an ACCESS EXCLUSIVE lock on a table a live recorder is COPYing into. The
        // cap is what keeps an automatic repair from becoming an unannounced outage.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server, skipMigration: PartitionHorizonMigration);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        await InsertTickAsync(connectionString, now);
        await InsertTickAsync(connectionString, now);

        var log = new CollectingLogger();
        var maintainer = MaintainerFor(
            connectionString,
            log,
            ("Partitions:RepairStrandedRows", "true"),
            ("Partitions:MaxRepairRows", "1"));

        await maintainer.SweepAsync(connectionString, CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("MaxRepairRows"));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM gateway.option_quote_events_default"));
        Assert.False(await PartitionExistsAsync(connectionString, "option_quote_events", today));
    }

    private static async Task<List<long>> EventIdsAsync(string connectionString, int conId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM gateway.option_quote_events WHERE con_id = $1 ORDER BY event_id", connection);
        command.Parameters.AddWithValue(conId);

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>
    /// Captures level and formatted message. The alarm's whole purpose is what an operator reads, so
    /// the tests assert on the rendered text rather than on the fact that something was logged.
    /// </summary>
    private sealed class CollectingLogger : ILogger<PartitionMaintainer>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
