using Npgsql;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Sessions;

/// <summary>What one <see cref="SessionCalendarService.SyncAsync"/> pass did.</summary>
/// <param name="Deleted">
/// Rows removed because the generator no longer produces them — a newly-entered closure retiring a
/// session that should never have existed. Non-zero here is the operator's signal that previously
/// published sessions changed shape.
/// </param>
public sealed record SessionCalendarSyncResult(
    string Calendar,
    DateOnly From,
    DateOnly To,
    bool DatabaseConfigured,
    int Generated,
    int Inserted,
    int Updated,
    int Deleted,
    short GeneratorVersion);

/// <summary>
/// Persists generated sessions into <c>research.sessions</c> so SQL-side consumers (coverage
/// denominators, gap detection, backfill slice planning) can join against the same boundaries
/// <see cref="SessionClock"/> answers with in process.
/// </summary>
/// <remarks>
/// <para>
/// The table is a materialised view of <see cref="SessionGenerator"/>, not an independent source of
/// truth — which is why syncing DELETES rows the generator no longer produces rather than only
/// inserting. Leaving a stale row behind is the dangerous direction: once a closure is entered for a
/// date (a day of mourning announced after the calendar was last generated), a session row surviving
/// on that date keeps a phantom trading day alive in every SQL consumer, and nothing downstream can
/// tell it apart from a real one.
/// </para>
/// <para>
/// Sync is idempotent: rerunning over the same range with the same generator version touches no rows
/// and reports zeros.
/// </para>
/// </remarks>
public sealed class SessionCalendarService(
    SessionGenerator generator,
    IConfiguration configuration,
    ILogger<SessionCalendarService> logger)
{
    /// <summary>Generates <paramref name="calendar"/> over the range and reconciles the table to it.</summary>
    public async Task<SessionCalendarSyncResult> SyncAsync(
        string calendar,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var sessions = generator.Generate(calendar, from, to);
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; the session calendar cannot be persisted.");

            return new SessionCalendarSyncResult(
                calendar, from, to, DatabaseConfigured: false, sessions.Count, 0, 0, 0,
                SessionGenerator.GeneratorVersion);
        }

        if (generator.Data.KnownGoodThrough is { } knownGood && to > knownGood)
        {
            // Rule-projected sessions are structurally right but carry no closure that has not been
            // announced yet — no future day of mourning, no future storm. Say so rather than let a
            // study silently treat 2029 sessions as being as trustworthy as 2024 ones.
            logger.LogWarning(
                "Generating {Calendar} sessions through {To:yyyy-MM-dd}, past the calendar data's " +
                "verified horizon of {KnownGood:yyyy-MM-dd}. Sessions after that date are rule " +
                "projections and contain no unscheduled closures or early closes.",
                calendar, to, knownGood);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var tradingDates = sessions.Select(session => session.TradingDate).ToArray();
        var labels = sessions.Select(session => session.Label).ToArray();

        var (inserted, updated) = await UpsertAsync(connection, transaction, calendar, sessions, cancellationToken);
        var deleted = await DeleteStaleAsync(
            connection, transaction, calendar, from, to, tradingDates, labels, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (inserted > 0 || updated > 0 || deleted > 0)
        {
            logger.LogInformation(
                "Session calendar {Calendar} {From:yyyy-MM-dd}..{To:yyyy-MM-dd} synced at generator " +
                "version {Version}: {Inserted} inserted, {Updated} updated, {Deleted} deleted.",
                calendar, from, to, SessionGenerator.GeneratorVersion, inserted, updated, deleted);
        }

        return new SessionCalendarSyncResult(
            calendar, from, to, DatabaseConfigured: true, sessions.Count, inserted, updated, deleted,
            SessionGenerator.GeneratorVersion);
    }

    /// <summary>Syncs every calendar in the dataset over the same range.</summary>
    public async Task<IReadOnlyList<SessionCalendarSyncResult>> SyncAllAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var results = new List<SessionCalendarSyncResult>();

        foreach (var calendar in generator.Data.CalendarKeys)
        {
            results.Add(await SyncAsync(calendar, from, to, cancellationToken));
        }

        return results;
    }

    /// <summary>Reads persisted sessions back, with their real <c>session_id</c>s.</summary>
    public async Task<IReadOnlyList<TradingSession>> GetPersistedAsync(
        string calendar,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT session_id, calendar, trading_date, open_utc, close_utc, label, is_half_day " +
            "FROM research.sessions " +
            "WHERE calendar = $1 AND trading_date >= $2 AND trading_date <= $3 " +
            "ORDER BY trading_date, open_utc",
            connection);
        command.Parameters.AddWithValue(calendar);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var sessions = new List<TradingSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new TradingSession(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<DateOnly>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                reader.GetBoolean(6)));
        }

        return sessions;
    }

    private static async Task<(int Inserted, int Updated)> UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string calendar,
        IReadOnlyList<TradingSession> sessions,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            return (0, 0);
        }

        // One statement for the whole range via parallel arrays — a 30-year calendar is ~7,500 rows,
        // and a round trip per row would make regeneration slow enough that operators avoid doing it.
        // The DO UPDATE ... WHERE IS DISTINCT FROM clause is what makes a rerun a genuine no-op:
        // unchanged rows are not rewritten, so RETURNING yields nothing for them.
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO research.sessions
                (calendar, trading_date, open_utc, close_utc, label, is_half_day, generator_version)
            SELECT $1, t.trading_date, t.open_utc, t.close_utc, t.label, t.is_half_day, $2
            FROM unnest($3::date[], $4::timestamptz[], $5::timestamptz[], $6::text[], $7::boolean[])
                 AS t(trading_date, open_utc, close_utc, label, is_half_day)
            ON CONFLICT (calendar, trading_date, label) DO UPDATE SET
                open_utc          = EXCLUDED.open_utc,
                close_utc         = EXCLUDED.close_utc,
                is_half_day       = EXCLUDED.is_half_day,
                generator_version = EXCLUDED.generator_version
            WHERE (sessions.open_utc, sessions.close_utc, sessions.is_half_day, sessions.generator_version)
                  IS DISTINCT FROM
                  (EXCLUDED.open_utc, EXCLUDED.close_utc, EXCLUDED.is_half_day, EXCLUDED.generator_version)
            RETURNING (xmax = 0) AS was_insert
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue(calendar);
        command.Parameters.AddWithValue(SessionGenerator.GeneratorVersion);
        command.Parameters.AddWithValue(sessions.Select(session => session.TradingDate).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.OpenUtc).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.CloseUtc).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.Label).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.IsHalfDay).ToArray());

        var inserted = 0;
        var updated = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetBoolean(0))
            {
                inserted++;
            }
            else
            {
                updated++;
            }
        }

        return (inserted, updated);
    }

    private static async Task<int> DeleteStaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string calendar,
        DateOnly from,
        DateOnly to,
        DateOnly[] tradingDates,
        string[] labels,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM research.sessions s
            WHERE s.calendar = $1
              AND s.trading_date >= $2
              AND s.trading_date <= $3
              AND NOT EXISTS (
                  SELECT 1 FROM unnest($4::date[], $5::text[]) AS t(trading_date, label)
                  WHERE t.trading_date = s.trading_date AND t.label = s.label)
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue(calendar);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);
        command.Parameters.AddWithValue(tradingDates);
        command.Parameters.AddWithValue(labels);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
