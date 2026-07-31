using System.Reflection;
using Npgsql;

namespace TradingStuff.ResearchService.Persistence;

/// <summary>Where the schema stands, for the status endpoint and for tests.</summary>
public sealed record MigrationState(
    string Status, // disabled | pending | applied | failed
    IReadOnlyList<string> Applied,
    string? Error);

/// <summary>
/// Applies the embedded SQL migrations, in name order, exactly once each.
/// </summary>
/// <remarks>
/// ResearchService owns ALL schema — including the <c>gateway.*</c> tables the IbkrGateway writes —
/// so there is exactly one migration authority. The runner takes a Postgres advisory lock so two
/// service instances cannot race, records applied migrations in <c>research.schema_migrations</c>,
/// and retries in the background rather than failing the host: the gateway and this service both
/// tolerate a database that is still coming up.
/// </remarks>
public sealed class MigrationRunner(IConfiguration configuration, ILogger<MigrationRunner> logger)
    : BackgroundService
{
    // Arbitrary but fixed: the advisory lock key that serialises migration runs across processes.
    private const long AdvisoryLockKey = 0x54_52_41_44_49_4E_47;

    private volatile MigrationState _state = new("pending", [], null);

    public MigrationState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; research persistence is disabled.");
            _state = new MigrationState("disabled", [], "No 'trading' connection string is configured.");
            return;
        }

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var applied = await ApplyOnceAsync(connectionString, stoppingToken);
                _state = new MigrationState("applied", applied, null);

                logger.LogInformation("Schema is current: {Count} migration(s) applied.", applied.Count);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                _state = new MigrationState("failed", _state.Applied, ex.Message);

                // Early attempts race the Postgres container starting; later ones mean real trouble.
                var delay = TimeSpan.FromSeconds(Math.Min(30, 5 * attempt));

                logger.Log(
                    attempt <= 5 ? LogLevel.Information : LogLevel.Error,
                    ex,
                    "Migration attempt {Attempt} failed; retrying in {Delay}s.",
                    attempt,
                    delay.TotalSeconds);

                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    /// <summary>One full pass — ensure the database exists, then apply pending migrations.</summary>
    internal async Task<IReadOnlyList<string>> ApplyOnceAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(connectionString, cancellationToken);
        return await ApplyAsync(connectionString, cancellationToken);
    }

    /// <summary>Creates the target database when the server is up but the database is not there.</summary>
    private static async Task EnsureDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = new NpgsqlConnection(connectionString);
            await probe.OpenAsync(cancellationToken);
            return;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            // Fall through and create it via the maintenance database.
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database
                       ?? throw new InvalidOperationException("The 'trading' connection string names no database.");
        builder.Database = "postgres";

        await using var maintenance = new NpgsqlConnection(builder.ConnectionString);
        await maintenance.OpenAsync(cancellationToken);

        try
        {
            // Identifier, not a parameter — quote it. CREATE DATABASE cannot run inside a transaction.
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", maintenance);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Another instance won the creation race — exactly the outcome we wanted.
        }
    }

    private async Task<IReadOnlyList<string>> ApplyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
        {
            advisoryLock.Parameters.AddWithValue(AdvisoryLockKey);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using (var bootstrap = new NpgsqlCommand(
                "CREATE SCHEMA IF NOT EXISTS research; " +
                "CREATE TABLE IF NOT EXISTS research.schema_migrations (" +
                "  name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
                connection))
            {
                await bootstrap.ExecuteNonQueryAsync(cancellationToken);
            }

            var alreadyApplied = new HashSet<string>(StringComparer.Ordinal);

            await using (var existing = new NpgsqlCommand("SELECT name FROM research.schema_migrations", connection))
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    alreadyApplied.Add(reader.GetString(0));
                }
            }

            var applied = new List<string>(alreadyApplied);

            foreach (var (name, sql) in LoadEmbeddedMigrations())
            {
                if (alreadyApplied.Contains(name))
                {
                    continue;
                }

                logger.LogInformation("Applying migration {Name}.", name);

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                {
                    await migration.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = new NpgsqlCommand(
                    "INSERT INTO research.schema_migrations (name) VALUES ($1)", connection, transaction))
                {
                    record.Parameters.AddWithValue(name);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(name);
            }

            applied.Sort(StringComparer.Ordinal);
            return applied;
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            release.Parameters.AddWithValue(AdvisoryLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>Embedded migrations in apply order. Names must sort in the intended order.</summary>
    internal static IReadOnlyList<(string Name, string Sql)> LoadEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                                   ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
                using var reader = new StreamReader(stream);

                // Recorded under the bare file name, not the full manifest resource name — the
                // applied-migration history must survive assembly, namespace, and folder renames.
                return (Name: NormalizeName(name), Sql: reader.ReadToEnd());
            })
            .OrderBy(migration => migration.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeName(string manifestResourceName)
    {
        // "TradingStuff.ResearchService.Persistence.Migrations.001_foundations.sql"
        // → "001_foundations.sql". The file name is everything after the second-to-last dot's
        // preceding segment boundary; resource names encode folders with dots, so take the last
        // two dot-separated segments (base name + extension).
        var segments = manifestResourceName.Split('.');

        return segments.Length >= 2
            ? $"{segments[^2]}.{segments[^1]}"
            : manifestResourceName;
    }
}
