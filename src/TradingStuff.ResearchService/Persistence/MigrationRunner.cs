using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TradingStuff.ResearchService.Persistence;

/// <summary>Where the schema stands, for the status endpoint and for tests.</summary>
public sealed record MigrationState(
    string Status, // disabled | pending | applied | failed
    IReadOnlyList<string> Applied,
    string? Error);

/// <summary>
/// A migration's DDL failed because the object it creates already exists — the signature of a
/// migration applied by hand outside this runner (or a ledger row inserted/edited directly). Retrying
/// cannot fix this: the identical statement fails identically every time until a human reconciles the
/// database or the ledger, so this gets a short, bounded retry and a named, actionable error instead
/// of the indefinite backoff <see cref="MigrationRunner"/> uses for ordinary startup races (Postgres
/// not accepting connections yet, the database not created yet, and so on).
/// </summary>
internal sealed class MigrationConflictException(string migrationName, Exception inner)
    : Exception(
        $"Migration '{migrationName}' failed because an object it creates already exists in the " +
        "database. This is the signature of a migration applied by hand outside this runner (or a " +
        "hand-edited research.schema_migrations row) — retrying will not resolve it. Reconcile the " +
        "database (drop or adjust the conflicting object) or the ledger (insert the migration's name " +
        $"and checksum so the runner considers it already applied) before restarting. Original error: " +
        $"{inner.Message}",
        inner)
{
    public string MigrationName { get; } = migrationName;
}

/// <summary>
/// Applies the embedded SQL migrations, in name order, exactly once each.
/// </summary>
/// <remarks>
/// ResearchService owns ALL schema — including the <c>gateway.*</c> tables the IbkrGateway writes —
/// so there is exactly one migration authority. The runner takes a Postgres advisory lock so two
/// service instances cannot race, records applied migrations (with a checksum of their content, so a
/// file edited after the fact is detected rather than silently trusted) in
/// <c>research.schema_migrations</c>, and retries transient failures in the background rather than
/// failing the host: the gateway and this service both tolerate a database that is still coming up.
/// A migration that fails because its target already exists is a different class of problem —
/// retrying it forever cannot help — and gets a short, bounded retry instead.
/// </remarks>
public sealed class MigrationRunner(IConfiguration configuration, ILogger<MigrationRunner> logger)
    : BackgroundService
{
    // Arbitrary but fixed: the advisory lock key that serialises migration runs across processes.
    private const long AdvisoryLockKey = 0x54_52_41_44_49_4E_47;

    // "Already exists" failures never resolve themselves — the same DDL fails identically forever —
    // so this bounds the retry rather than trusting the same backoff used for ordinary startup races.
    // Not 1: a single retry absorbs the vanishingly rare case where the conflict was itself transient
    // (e.g. a concurrent out-of-band script mid-run), without disguising the common case, which is a
    // hand-applied migration that needs a human.
    private const int MaxConflictAttempts = 3;

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
        var conflictAttempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var applied = await ApplyOnceAsync(connectionString, stoppingToken);
                _state = new MigrationState("applied", applied, null);

                logger.LogInformation("Schema is current: {Count} migration(s) applied.", applied.Count);
                return;
            }
            catch (MigrationConflictException ex)
            {
                conflictAttempts++;
                _state = new MigrationState("failed", _state.Applied, ex.Message);

                if (conflictAttempts >= MaxConflictAttempts)
                {
                    logger.LogCritical(
                        ex,
                        "Migration {Name} still conflicts with an existing object after {Attempts} " +
                        "attempts; giving up rather than retrying forever.",
                        ex.MigrationName,
                        conflictAttempts);
                    return;
                }

                logger.LogError(
                    ex,
                    "Migration {Name} attempt {Attempt} conflicted with an existing object; " +
                    "retrying {Remaining} more time(s) before giving up.",
                    ex.MigrationName,
                    conflictAttempts,
                    MaxConflictAttempts - conflictAttempts);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
    internal Task<IReadOnlyList<string>> ApplyOnceAsync(
        string connectionString,
        CancellationToken cancellationToken) =>
        ApplyOnceAsync(connectionString, LoadEmbeddedMigrations(), cancellationToken);

    /// <summary>
    /// As <see cref="ApplyOnceAsync(string, CancellationToken)"/>, but against an explicit migration
    /// set rather than what the assembly currently embeds. Exists so a test can simulate a migration
    /// file changing after it was applied — something that cannot happen through the embedded-resource
    /// path within a single test run — by applying one set and then a second set that reuses a name
    /// with different SQL.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ApplyOnceAsync(
        string connectionString,
        IReadOnlyList<(string Name, string Sql, string Checksum)> migrations,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(connectionString, cancellationToken);
        return await ApplyAsync(connectionString, migrations, cancellationToken);
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

    private async Task<IReadOnlyList<string>> ApplyAsync(
        string connectionString,
        IReadOnlyList<(string Name, string Sql, string Checksum)> migrations,
        CancellationToken cancellationToken)
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
                "  name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now(), checksum text); " +
                // A brand-new table already has the column from the CREATE TABLE above; this line is
                // what brings an EXISTING ledger (created before checksums existed) up to date, every
                // startup, before anything below assumes the column is there. Idempotent and cheap —
                // Postgres no-ops an ADD COLUMN IF NOT EXISTS against a column that already exists.
                "ALTER TABLE research.schema_migrations ADD COLUMN IF NOT EXISTS checksum text",
                connection))
            {
                await bootstrap.ExecuteNonQueryAsync(cancellationToken);
            }

            var embeddedChecksums = migrations.ToDictionary(m => m.Name, m => m.Checksum, StringComparer.Ordinal);
            var appliedChecksums = new Dictionary<string, string?>(StringComparer.Ordinal);

            await using (var existing = new NpgsqlCommand(
                "SELECT name, checksum FROM research.schema_migrations", connection))
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    appliedChecksums[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            // Backfill: a row recorded before the checksum column existed has nothing to compare
            // against yet. The only checksum available for a migration nobody has re-run since is
            // whatever is CURRENTLY embedded under that name, so that becomes the baseline. A name
            // with no current match (an old migration file since deleted or renamed) is left alone —
            // there is nothing to compute a baseline from, and that is a different problem than this
            // migration exists to catch.
            foreach (var (name, checksum) in appliedChecksums.ToArray())
            {
                if (checksum is not null || !embeddedChecksums.TryGetValue(name, out var baseline))
                {
                    continue;
                }

                await using (var backfill = new NpgsqlCommand(
                    "UPDATE research.schema_migrations SET checksum = $1 WHERE name = $2 AND checksum IS NULL",
                    connection))
                {
                    backfill.Parameters.AddWithValue(baseline);
                    backfill.Parameters.AddWithValue(name);
                    await backfill.ExecuteNonQueryAsync(cancellationToken);
                }

                appliedChecksums[name] = baseline;

                logger.LogInformation(
                    "Backfilled a content checksum for previously applied migration {Name}; this " +
                    "becomes the baseline future runs are checked against.",
                    name);
            }

            // Only once every applied row has a real baseline does a mismatch mean something: the
            // file's content today does not match what was recorded as having run. Continuing past
            // that would silently apply new migrations on top of a foundation nobody can vouch for, so
            // it is fatal — named, and before anything else touches the schema — rather than logged
            // and ignored.
            foreach (var (name, _, checksum) in migrations)
            {
                if (appliedChecksums.TryGetValue(name, out var recorded)
                    && recorded is not null
                    && !string.Equals(recorded, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Migration '{name}' has changed since it was applied (recorded checksum " +
                        $"{recorded}, current file checksum {checksum}). The database and the " +
                        "migration file have diverged. Reconcile by hand — revert the file to what " +
                        "actually ran, or confirm the divergence is intentional and update " +
                        "research.schema_migrations yourself — before this runner will proceed.");
                }
            }

            var applied = new List<string>(appliedChecksums.Keys);

            foreach (var (name, sql, checksum) in migrations)
            {
                if (appliedChecksums.ContainsKey(name))
                {
                    continue;
                }

                logger.LogInformation("Applying migration {Name}.", name);

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                try
                {
                    await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                    {
                        await migration.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                catch (PostgresException ex) when (IsAlreadyExistsConflict(ex))
                {
                    // The transaction is rolled back implicitly when the `await using` above disposes
                    // it without a commit — same as any other exception from this block.
                    throw new MigrationConflictException(name, ex);
                }

                await using (var record = new NpgsqlCommand(
                    "INSERT INTO research.schema_migrations (name, checksum) VALUES ($1, $2)",
                    connection,
                    transaction))
                {
                    record.Parameters.AddWithValue(name);
                    record.Parameters.AddWithValue(checksum);
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

    /// <summary>
    /// Postgres' "this object already exists" family (DDL run twice, or by hand outside this runner).
    /// Matched by SqlState first since that is exact; the message-text fallback catches variants the
    /// fixed code list misses without needing to be exhaustive — false positives here only mean an
    /// unrelated failure gets the bounded-retry path instead of the indefinite one, which is a longer
    /// wait, not a wrong answer.
    /// </summary>
    private static bool IsAlreadyExistsConflict(PostgresException ex) =>
        ex.SqlState is
            PostgresErrorCodes.DuplicateDatabase or
            PostgresErrorCodes.DuplicateSchema or
            PostgresErrorCodes.DuplicateTable or
            PostgresErrorCodes.DuplicateColumn or
            PostgresErrorCodes.DuplicateObject or
            PostgresErrorCodes.DuplicateFunction or
            PostgresErrorCodes.DuplicatePreparedStatement
        || ex.MessageText.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>Embedded migrations in apply order. Names must sort in the intended order.</summary>
    internal static IReadOnlyList<(string Name, string Sql, string Checksum)> LoadEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                                   ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
                using var reader = new StreamReader(stream);
                var sql = reader.ReadToEnd();

                // Recorded under the bare file name, not the full manifest resource name — the
                // applied-migration history must survive assembly, namespace, and folder renames.
                return (Name: NormalizeName(name), Sql: sql, Checksum: ComputeChecksum(sql));
            })
            .OrderBy(migration => migration.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// A content fingerprint for a migration's SQL text, used to detect a file that changed after it
    /// was recorded as applied. SHA-256 over the raw bytes exactly as embedded: no normalisation
    /// (line-ending, whitespace, or otherwise), because normalising would let a change that alters
    /// behaviour but happens to survive normalisation slip through undetected — the property this
    /// exists to guarantee is "the bytes that ran are the bytes on disk", not "the bytes are
    /// equivalent under some notion of insignificant difference".
    /// </summary>
    internal static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

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
