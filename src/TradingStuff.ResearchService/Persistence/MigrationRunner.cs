using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace TradingStuff.ResearchService.Persistence;

/// <summary>Where the schema stands, for the status endpoint and for tests.</summary>
/// <param name="UnverifiedBaselines">
/// Migrations whose recorded checksum is an ASSUMPTION rather than a measurement — see
/// <see cref="ChecksumProvenance"/>. Non-empty means checksum drift is only being detected for edits
/// made after this database was upgraded; an edit made before it is indistinguishable from a clean
/// environment and always will be. Carried on the state so the status endpoint reports the caveat
/// alongside the reassuring <c>applied</c> list rather than leaving it in a log line nobody reads.
/// </param>
public sealed record MigrationState(
    string Status, // disabled | pending | applied | failed
    IReadOnlyList<string> Applied,
    string? Error,
    IReadOnlyList<string>? UnverifiedBaselines = null);

/// <summary>
/// Where a <c>research.schema_migrations.checksum</c> value came from, and therefore what it is
/// evidence of.
/// </summary>
/// <remarks>
/// The distinction exists because migration 010's backfill blesses rather than measures. It writes
/// the checksum of whatever the assembly embeds TODAY into every row that predates the checksum
/// column, which is not the checksum of what actually ran: an environment whose <c>003_recorder.sql</c>
/// was hand-patched before the upgrade records exactly the same value as a clean environment that
/// applied the current text. Both then report "applied", both compare clean forever, and their
/// schemas differ — the precise state 010's own header says nothing could previously distinguish.
/// <para>
/// The checksum of a migration that ran before checksums existed is not recoverable; that is
/// inherent, not a gap to close later. So the fix is not to detect the undetectable but to stop the
/// record from claiming otherwise: a baseline that was assumed says so, everywhere it is surfaced,
/// and never reads as evidence that the file on disk is what ran.
/// </para>
/// </remarks>
public static class ChecksumProvenance
{
    /// <summary>
    /// The runner computed this checksum from the SQL it was about to execute and wrote it in the
    /// same transaction as the DDL. The bytes on disk ARE the bytes that ran.
    /// </summary>
    public const string Verified = "verified";

    /// <summary>
    /// Backfilled onto a row that predates the checksum column, from whatever the assembly embedded
    /// under that name at upgrade time. An assumption about what ran, not a measurement of it.
    /// </summary>
    public const string Assumed = "assumed";

    /// <summary>
    /// The row predates provenance tracking itself: it carries a checksum recorded by migration 010,
    /// which could equally have come from a real apply or from 010's backfill, and nothing in the
    /// ledger distinguishes the two after the fact. Treated as unverified, because the weaker claim
    /// is the only honest one.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>Whether a baseline with this provenance is evidence of what actually ran.</summary>
    public static bool IsVerified(string? provenance) =>
        string.Equals(provenance, Verified, StringComparison.Ordinal);
}

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
/// Reports the schema state, so a ResearchService running without its schema stops looking healthy.
/// </summary>
/// <remarks>
/// Registered separately from <c>MapDefaultEndpoints</c>'s "self" check on purpose: "self" only ever
/// says the process is up, which is exactly what a service whose migrations failed also says. The
/// failure this exists for is not exotic — a migration conflict logs, retries, and then everything
/// downstream fails on a missing table while <c>/health</c> answers 200 and <c>/research/status</c>
/// reports <c>applied: []</c> to anyone who thinks to look.
/// <para>
/// An unverified checksum baseline is deliberately NOT degraded. It is a permanent property of an
/// upgraded database with no in-process remedy, and a gate that is permanently amber is a gate
/// nobody reads (migration 006's lesson). It rides in <see cref="HealthCheckResult.Data"/> and the
/// description instead, where it informs without crying wolf.
/// </para>
/// </remarks>
public sealed class MigrationHealthCheck(MigrationRunner runner) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var state = runner.State;

        var data = new Dictionary<string, object>
        {
            ["status"] = state.Status,
            ["applied"] = state.Applied.Count,
            ["unverifiedBaselines"] = state.UnverifiedBaselines ?? [],
        };

        var caveat = state.UnverifiedBaselines is { Count: > 0 } unverified
            ? $" {unverified.Count} migration(s) carry an assumed, unverified checksum baseline " +
              $"({string.Join(", ", unverified)})."
            : string.Empty;

        return Task.FromResult(state.Status switch
        {
            "applied" => HealthCheckResult.Healthy(
                $"Schema is current: {state.Applied.Count} migration(s) applied.{caveat}", data),

            // No connection string at all is a configured-off local run, not a fault — but it is not
            // health either, because every research endpoint will fail.
            "disabled" => HealthCheckResult.Degraded(
                state.Error ?? "Research persistence is disabled.", data: data),

            "pending" => HealthCheckResult.Degraded("Migrations have not completed yet.", data: data),

            _ => HealthCheckResult.Unhealthy(
                state.Error ?? "Migrations failed; this service has no schema.", data: data),
        });
    }
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

    // "Already exists" failures do not resolve themselves on the timescale of a retry loop — the
    // same DDL fails identically until a human reconciles the database — so the FAST retry is
    // bounded rather than sharing the backoff used for ordinary startup races. Not 1: a single retry
    // absorbs the vanishingly rare case where the conflict was itself transient (e.g. a concurrent
    // out-of-band script mid-run), without disguising the common case, which is a hand-applied
    // migration that needs a human.
    private const int MaxConflictAttempts = 3;

    /// <summary>Test seam: the fast retry between conflict attempts.</summary>
    internal TimeSpan ConflictFastRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test seam: the slow retry the runner falls back to once the fast attempts are spent.
    /// </summary>
    /// <remarks>
    /// Falling back rather than returning is the whole point. Exhausting the fast attempts used to
    /// exit <see cref="ExecuteAsync"/> for good, ~10 s after startup, leaving a service with NO
    /// SCHEMA behind a healthy-looking <c>/health</c> and a <c>/research/status</c> reporting
    /// <c>applied: []</c>. And the ordinary remediation — an operator dropping the hand-created
    /// object a minute later — is exactly the transient case <see cref="MaxConflictAttempts"/>'
    /// comment cites as its own justification, so the one recovery that actually happens in practice
    /// was the one nothing was watching for. Slow, because a conflict genuinely does need a human
    /// and hammering the database changes nothing; indefinite, because when that human arrives the
    /// service must notice without needing a restart it will not get.
    /// </remarks>
    internal TimeSpan ConflictSlowRetry { get; init; } = TimeSpan.FromMinutes(1);

    private volatile MigrationState _state = new("pending", [], null);

    /// <summary>
    /// Set by <see cref="ApplyAsync"/> on each pass. A field rather than part of the return value
    /// because the applied-migration list is the runner's public contract and several tests depend
    /// on its shape; safe because the advisory lock and the single hosted service make passes
    /// strictly sequential.
    /// </summary>
    private IReadOnlyList<string> _unverifiedBaselines = [];

    public MigrationState State => _state;

    /// <summary>
    /// Test seam: the unverified baselines observed by the most recent <see cref="ApplyAsync"/>,
    /// reachable without going through the hosted loop (which only ever runs the embedded set).
    /// </summary>
    internal IReadOnlyList<string> UnverifiedBaselines => _unverifiedBaselines;

    /// <summary>One ledger row, as far as checksum comparison is concerned.</summary>
    private sealed record LedgerEntry(string? Checksum, string? Source);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; research persistence is disabled.");
            _state = new MigrationState("disabled", [], "No 'trading' connection string is configured.", []);
            return;
        }

        var attempt = 0;
        var conflictAttempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var applied = await ApplyOnceAsync(connectionString, stoppingToken);
                _state = new MigrationState("applied", applied, null, _unverifiedBaselines);

                logger.LogInformation("Schema is current: {Count} migration(s) applied.", applied.Count);
                return;
            }
            catch (MigrationConflictException ex)
            {
                conflictAttempts++;
                _state = new MigrationState("failed", _state.Applied, ex.Message, _unverifiedBaselines);

                if (conflictAttempts == MaxConflictAttempts)
                {
                    logger.LogCritical(
                        ex,
                        "Migration {Name} still conflicts with an existing object after {Attempts} " +
                        "attempts. THIS SERVICE IS RUNNING WITHOUT ITS SCHEMA: nothing that reads or " +
                        "writes research.* or gateway.* will work, and the migration health check " +
                        "reports Unhealthy until this is reconciled. Retrying every {Delay} from here " +
                        "so that dropping the conflicting object recovers the service without a restart.",
                        ex.MigrationName,
                        conflictAttempts,
                        ConflictSlowRetry);
                }
                else if (conflictAttempts < MaxConflictAttempts)
                {
                    logger.LogError(
                        ex,
                        "Migration {Name} attempt {Attempt} conflicted with an existing object; " +
                        "retrying {Remaining} more time(s) before slowing down.",
                        ex.MigrationName,
                        conflictAttempts,
                        MaxConflictAttempts - conflictAttempts);
                }
                else
                {
                    logger.LogError(
                        ex,
                        "Migration {Name} still conflicts with an existing object (attempt {Attempt}); " +
                        "the schema is still incomplete.",
                        ex.MigrationName,
                        conflictAttempts);
                }

                await Task.Delay(
                    conflictAttempts >= MaxConflictAttempts ? ConflictSlowRetry : ConflictFastRetry,
                    stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempt++;
                _state = new MigrationState("failed", _state.Applied, ex.Message, _unverifiedBaselines);

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
                "  name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now(), " +
                "  checksum text, checksum_source text); " +
                // A brand-new table already has the columns from the CREATE TABLE above; these lines
                // are what bring an EXISTING ledger (created before checksums, or before checksum
                // provenance) up to date, every startup, before anything below assumes the columns
                // are there. Idempotent and cheap — Postgres no-ops an ADD COLUMN IF NOT EXISTS
                // against a column that already exists. checksum_source cannot wait for migration 013
                // to add it, for the same reason checksum could not wait for 010: the backfill below
                // runs BEFORE any migration in this pass, so the column it writes must already exist.
                "ALTER TABLE research.schema_migrations ADD COLUMN IF NOT EXISTS checksum text; " +
                "ALTER TABLE research.schema_migrations ADD COLUMN IF NOT EXISTS checksum_source text",
                connection))
            {
                await bootstrap.ExecuteNonQueryAsync(cancellationToken);
            }

            var embeddedChecksums = migrations.ToDictionary(m => m.Name, m => m.Checksum, StringComparer.Ordinal);
            var ledger = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);

            await using (var existing = new NpgsqlCommand(
                "SELECT name, checksum, checksum_source FROM research.schema_migrations", connection))
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    ledger[reader.GetString(0)] = new LedgerEntry(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2));
                }
            }

            // Backfill: a row recorded before the checksum column existed has nothing to compare
            // against yet. The only checksum available for a migration nobody has re-run since is
            // whatever is CURRENTLY embedded under that name, so that becomes the baseline. A name
            // with no current match (an old migration file since deleted or renamed) is left alone —
            // there is nothing to compute a baseline from, and that is a different problem than this
            // migration exists to catch.
            //
            // Written as ChecksumProvenance.Assumed, and that word is the point. This value is what
            // the assembly embeds TODAY, not what ran: if this database's copy of the file was
            // hand-patched before the upgrade, the divergence 010 exists to catch is being recorded
            // as the baseline rather than detected, and the ledgers of the diverged and the clean
            // environment come out byte-identical. Nothing can recover the real checksum after the
            // fact — so the record says "assumed" instead of quietly passing this off as verified.
            foreach (var (name, entry) in ledger.ToArray())
            {
                if (entry.Checksum is not null || !embeddedChecksums.TryGetValue(name, out var baseline))
                {
                    continue;
                }

                await using (var backfill = new NpgsqlCommand(
                    "UPDATE research.schema_migrations SET checksum = $1, checksum_source = $2 " +
                    "WHERE name = $3 AND checksum IS NULL",
                    connection))
                {
                    backfill.Parameters.AddWithValue(baseline);
                    backfill.Parameters.AddWithValue(ChecksumProvenance.Assumed);
                    backfill.Parameters.AddWithValue(name);
                    await backfill.ExecuteNonQueryAsync(cancellationToken);
                }

                ledger[name] = new LedgerEntry(baseline, ChecksumProvenance.Assumed);

                logger.LogWarning(
                    "Migration {Name} was applied before checksums existed, so its baseline has been " +
                    "ASSUMED from the file this assembly embeds today — it is not the checksum of what " +
                    "actually ran and is not evidence that the two agree. If this file was edited " +
                    "before the upgrade, that divergence is now baked in and undetectable. Only edits " +
                    "made from here on will be caught.",
                    name);
            }

            var unverifiedBaselines = ledger
                .Where(entry => entry.Value.Checksum is not null && !ChecksumProvenance.IsVerified(entry.Value.Source))
                .Select(entry => entry.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            if (unverifiedBaselines.Length > 0)
            {
                // Every startup, not just the one that did the backfilling. The caveat is a standing
                // property of this database, and the run that establishes it is precisely the run
                // nobody is watching.
                logger.LogWarning(
                    "{Count} migration(s) carry an unverified checksum baseline ({Names}). Checksum " +
                    "drift is only detected for edits made after that baseline was recorded; an edit " +
                    "made before it cannot be distinguished from a clean environment. Treat a clean " +
                    "comparison on these as an absence of evidence, not evidence of agreement.",
                    unverifiedBaselines.Length,
                    string.Join(", ", unverifiedBaselines));
            }

            // Only once every applied row has a baseline does a mismatch mean something: the file's
            // content today does not match what was recorded as having run. Continuing past that
            // would silently apply new migrations on top of a foundation nobody can vouch for, so it
            // is fatal — named, and before anything else touches the schema — rather than logged and
            // ignored. An assumed baseline still gets this treatment: it is weaker evidence about
            // what ran, but a mismatch against it is still a file that changed after the ledger last
            // saw it. The message says which kind of baseline it is so the operator reconciling by
            // hand knows whether "revert the file to what actually ran" is even a well-defined
            // instruction.
            foreach (var (name, _, checksum) in migrations)
            {
                if (ledger.TryGetValue(name, out var recorded)
                    && recorded.Checksum is not null
                    && !string.Equals(recorded.Checksum, checksum, StringComparison.Ordinal))
                {
                    var provenance = ChecksumProvenance.IsVerified(recorded.Source)
                        ? "recorded when it was applied"
                        : $"an {recorded.Source ?? ChecksumProvenance.Unknown} baseline, not a measurement of what ran";

                    throw new InvalidOperationException(
                        $"Migration '{name}' has changed since it was applied (recorded checksum " +
                        $"{recorded.Checksum} — {provenance}; current file checksum {checksum}). The " +
                        "database and the migration file have diverged. Reconcile by hand — revert " +
                        "the file to what actually ran, or confirm the divergence is intentional and " +
                        "update research.schema_migrations yourself — before this runner will proceed.");
                }
            }

            _unverifiedBaselines = unverifiedBaselines;

            var applied = new List<string>(ledger.Keys);

            foreach (var (name, sql, checksum) in migrations)
            {
                if (ledger.ContainsKey(name))
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

                // ChecksumProvenance.Verified, and it is earned rather than asserted: this checksum
                // was computed from the very SQL executed two statements up, and lands in the same
                // transaction as that DDL. Nothing else in this file may write this value.
                await using (var record = new NpgsqlCommand(
                    "INSERT INTO research.schema_migrations (name, checksum, checksum_source) VALUES ($1, $2, $3)",
                    connection,
                    transaction))
                {
                    record.Parameters.AddWithValue(name);
                    record.Parameters.AddWithValue(checksum);
                    record.Parameters.AddWithValue(ChecksumProvenance.Verified);
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
