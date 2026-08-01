using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// Postgres integration tests for <see cref="SessionCalendarService"/> — that generated sessions
/// land in <c>research.sessions</c> as the same exact UTC instants the clock reports, that a rerun
/// is a true no-op, and that a row the generator no longer produces is removed rather than left
/// behind as a phantom trading day. Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection
/// string, matching <see cref="BackfillPostgresTests"/>.
/// </summary>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class SessionCalendarPostgresTests
{
    private const string Nyse = "NYSE";
    private const string CboeGth = "CBOE_INDEX_GTH";
    private const string CmeEs = "CME_ES";

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<(string ConnectionString, SessionCalendarService Service)> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        var runner = new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);

        var service = new SessionCalendarService(
            new SessionGenerator(), configuration, NullLogger<SessionCalendarService>.Instance);

        return (connectionString, service);
    }

    private static DateOnly Date(int year, int month, int day) => new(year, month, day);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sync_persists_every_generated_session_and_a_rerun_changes_nothing()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, service) = await PrepareAsync(server);

        var first = await service.SyncAsync(Nyse, Date(2025, 1, 1), Date(2025, 12, 31), CancellationToken.None);

        Assert.True(first.DatabaseConfigured);
        Assert.Equal(250, first.Generated);  // published 2025 NYSE session count (Carter closure included)
        Assert.Equal(250, first.Inserted);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, first.Deleted);
        Assert.Equal(SessionGenerator.GeneratorVersion, first.GeneratorVersion);

        // Idempotency is the property that makes regeneration safe to run on a timer: the upsert's
        // IS DISTINCT FROM guard must leave unchanged rows untouched rather than rewriting them.
        var second = await service.SyncAsync(Nyse, Date(2025, 1, 1), Date(2025, 12, 31), CancellationToken.None);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Deleted);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var count = new NpgsqlCommand(
            "SELECT count(*), min(generator_version), max(generator_version) FROM research.sessions", connection);
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(250L, reader.GetInt64(0));
        Assert.Equal(SessionGenerator.GeneratorVersion, reader.GetInt16(1));
        Assert.Equal(SessionGenerator.GeneratorVersion, reader.GetInt16(2));
    }

    [Fact]
    public async Task Persisted_boundaries_are_the_exact_utc_instants_the_clock_reports()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (_, service) = await PrepareAsync(server);
        var clock = new SessionClock();

        foreach (var calendar in new[] { Nyse, CboeGth, CmeEs })
        {
            await service.SyncAsync(calendar, Date(2026, 1, 1), Date(2026, 1, 31), CancellationToken.None);

            var persisted = await service.GetPersistedAsync(
                calendar, Date(2026, 1, 1), Date(2026, 1, 31), CancellationToken.None);
            var generated = clock.SessionsBetween(calendar, Date(2026, 1, 1), Date(2026, 1, 31));

            Assert.Equal(generated.Count, persisted.Count);

            // session_id is assigned by the database; everything else must survive the round trip
            // through timestamptz unchanged. A timezone-aware column that silently re-rendered these
            // in the server's local zone would show up right here.
            Assert.Equal(
                generated.Select(session => session with { SessionId = 0 }).ToArray(),
                persisted.Select(session => session with { SessionId = 0 }).ToArray());

            Assert.All(persisted, session => Assert.True(session.SessionId > 0));
        }

        // The overnight case specifically: the Globex day for Monday 2026-01-05 must come back out
        // of Postgres opening on the PREVIOUS UTC date.
        var globex = (await service.GetPersistedAsync(CmeEs, Date(2026, 1, 5), Date(2026, 1, 5), CancellationToken.None))
            .Single(session => session.Label == "GTH");

        Assert.Equal(Utc(2026, 1, 4, 23, 0), globex.OpenUtc);
        Assert.Equal(Utc(2026, 1, 5, 22, 0), globex.CloseUtc);
        Assert.Equal(Date(2026, 1, 5), globex.TradingDate);
    }

    [Fact]
    public async Task Sync_removes_a_phantom_session_the_generator_no_longer_produces()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, service) = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Stand in for a calendar generated before a closure was known: a session on 2025-01-09,
        // the day of mourning for Jimmy Carter. Leaving it behind would keep a phantom trading day
        // alive in every SQL consumer, indistinguishable from a real one.
        await using (var phantom = new NpgsqlCommand(
            "INSERT INTO research.sessions " +
            "(calendar, trading_date, open_utc, close_utc, label, is_half_day, generator_version) " +
            "VALUES ($1, $2, $3, $4, 'RTH', false, 0)",
            connection))
        {
            phantom.Parameters.AddWithValue(Nyse);
            phantom.Parameters.AddWithValue(Date(2025, 1, 9));
            phantom.Parameters.AddWithValue(Utc(2025, 1, 9, 14, 30));
            phantom.Parameters.AddWithValue(Utc(2025, 1, 9, 21, 0));
            await phantom.ExecuteNonQueryAsync();
        }

        var result = await service.SyncAsync(Nyse, Date(2025, 1, 1), Date(2025, 1, 31), CancellationToken.None);

        Assert.Equal(1, result.Deleted);

        var january = await service.GetPersistedAsync(Nyse, Date(2025, 1, 1), Date(2025, 1, 31), CancellationToken.None);
        Assert.DoesNotContain(january, session => session.TradingDate == Date(2025, 1, 9));

        // The surrounding sessions are still there — a delete scoped too widely is as bad as a
        // phantom row.
        Assert.Contains(january, session => session.TradingDate == Date(2025, 1, 8));
        Assert.Contains(january, session => session.TradingDate == Date(2025, 1, 10));
    }

    [Fact]
    public async Task Sync_corrects_a_row_whose_boundaries_or_generator_version_are_stale()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, service) = await PrepareAsync(server);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // A real trading date carrying boundaries an hour off (the classic DST mistake) and an older
        // generator version.
        await using (var stale = new NpgsqlCommand(
            "INSERT INTO research.sessions " +
            "(calendar, trading_date, open_utc, close_utc, label, is_half_day, generator_version) " +
            "VALUES ($1, $2, $3, $4, 'RTH', true, 0)",
            connection))
        {
            stale.Parameters.AddWithValue(Nyse);
            stale.Parameters.AddWithValue(Date(2025, 7, 15));
            stale.Parameters.AddWithValue(Utc(2025, 7, 15, 14, 30));
            stale.Parameters.AddWithValue(Utc(2025, 7, 15, 21, 0));
            await stale.ExecuteNonQueryAsync();
        }

        var result = await service.SyncAsync(Nyse, Date(2025, 7, 1), Date(2025, 7, 31), CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Deleted);

        var corrected = (await service.GetPersistedAsync(
                Nyse, Date(2025, 7, 15), Date(2025, 7, 15), CancellationToken.None))
            .Single();

        Assert.Equal(Utc(2025, 7, 15, 13, 30), corrected.OpenUtc);
        Assert.Equal(Utc(2025, 7, 15, 20, 0), corrected.CloseUtc);
        Assert.False(corrected.IsHalfDay);
        Assert.Equal(SessionGenerator.GeneratorVersion, await GeneratorVersionOf(connection, Date(2025, 7, 15)));
    }

    [Fact]
    public async Task One_trading_date_carries_both_an_rth_and_a_gth_row_for_a_calendar_that_has_both()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (_, service) = await PrepareAsync(server);

        await service.SyncAsync(CmeEs, Date(2025, 11, 24), Date(2025, 11, 28), CancellationToken.None);

        var thanksgivingWeek = await service.GetPersistedAsync(
            CmeEs, Date(2025, 11, 24), Date(2025, 11, 28), CancellationToken.None);

        // Mon-Wed and Friday trade normally, two rows each. Thanksgiving Thursday carries ONE row:
        // Globex equity index trades a shortened session (17:00 CT Wednesday to 12:00 CT) rather
        // than closing, and there is no regular session inside it. This test asserted the Thursday
        // was absent entirely, which was the defect — nine rows, not eight.
        Assert.Equal(9, thanksgivingWeek.Count);

        var thanksgiving = thanksgivingWeek.Where(session => session.TradingDate == Date(2025, 11, 27)).ToArray();
        var shortened = Assert.Single(thanksgiving);
        Assert.Equal("GTH", shortened.Label);
        Assert.Equal(Utc(2025, 11, 26, 23, 0), shortened.OpenUtc);
        Assert.Equal(Utc(2025, 11, 27, 18, 0), shortened.CloseUtc);
        Assert.True(shortened.IsHalfDay);

        var friday = thanksgivingWeek.Where(session => session.TradingDate == Date(2025, 11, 28)).ToArray();
        Assert.Equal(2, friday.Length);
        Assert.All(friday, session => Assert.True(session.IsHalfDay));

        // The Globex day belonging to the half-day Friday still opens on Thanksgiving EVENING —
        // CME reopens at 17:00 CT on holiday evenings — and is truncated at the 12:15 CT early close.
        var globex = friday.Single(session => session.Label == "GTH");
        Assert.Equal(Utc(2025, 11, 27, 23, 0), globex.OpenUtc);
        Assert.Equal(Utc(2025, 11, 28, 18, 15), globex.CloseUtc);
    }

    [Fact]
    public async Task Sync_all_covers_every_calendar_in_the_dataset()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var (connectionString, service) = await PrepareAsync(server);

        var results = await service.SyncAllAsync(Date(2026, 2, 1), Date(2026, 2, 28), CancellationToken.None);

        Assert.Equal(new SessionClock().Calendars.Count, results.Count);
        Assert.All(results, result => Assert.True(result.Inserted > 0));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var distinct = new NpgsqlCommand("SELECT count(DISTINCT calendar) FROM research.sessions", connection);
        Assert.Equal((long)results.Count, (long)(await distinct.ExecuteScalarAsync())!);
    }

    private static async Task<short> GeneratorVersionOf(NpgsqlConnection connection, DateOnly tradingDate)
    {
        await using var command = new NpgsqlCommand(
            "SELECT generator_version FROM research.sessions WHERE trading_date = $1", connection);
        command.Parameters.AddWithValue(tradingDate);

        return (short)(await command.ExecuteScalarAsync())!;
    }
}
