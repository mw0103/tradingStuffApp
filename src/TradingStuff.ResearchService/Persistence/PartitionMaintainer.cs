using Npgsql;

namespace TradingStuff.ResearchService.Persistence;

/// <summary>
/// One UTC date whose rows are sitting in a partitioned table's DEFAULT partition — i.e. one date
/// whose real partition Postgres will now permanently refuse to create.
/// </summary>
/// <param name="FirstObservedAt">
/// When THIS process first saw this date stranded, not when the rows landed. It is what separates
/// "the standing condition an operator has already been told about" from "a new date just started
/// stranding", which is the whole point of tracking it (see
/// <see cref="PartitionMaintainer.ReviewDefaultPartitionsAsync"/>).
/// </param>
public sealed record StrandedPartitionDate(
    string Schema,
    string Table,
    DateOnly Date,
    long Rows,
    DateTimeOffset FirstObservedAt,
    bool Repairable);

/// <summary>
/// Keeps the daily partitions the gateway's raw-event recorder writes into created ahead of need,
/// and reports — actionably — any date whose rows have already been stranded in DEFAULT.
/// </summary>
/// <remarks>
/// <para>
/// Both raw event tables also carry a DEFAULT partition (migration 003) as a safety net so an
/// incoming COPY never fails outright when this service falls behind. That safety net has a sharp
/// edge, verified directly against Postgres: once a row for a given date sits in DEFAULT, Postgres
/// permanently refuses to create the real partition for that date afterward
/// (<c>updated partition constraint for default partition ... would be violated by some row</c>).
/// The rows are not lost, but that date then has no partition to export or DROP, which is exactly
/// what Phase 3's "hot 60 days → Parquet → DROP partition" retention needs.
/// </para>
/// <para>
/// <b>Where the "a tick cannot be written before its partition exists" guarantee lives, and why it
/// is not here.</b> It lives in the SCHEMA — migration 012 creates a
/// <see cref="DaysAhead"/>-day forward horizon of daily partitions at migration time — because the
/// schema is the only thing the two processes involved actually share. The recorder runs in the
/// IbkrGateway process, this maintainer runs in ResearchService, and ResearchService is
/// deliberately expected to redeploy and restart underneath a gateway that keeps recording
/// (docs/STATE.md says so explicitly, and Phase 2 has the immortal-gap incident to show for it).
/// Any rule of the form "the maintainer runs before the recorder writes" is therefore only true
/// when the two happen to start together, which is precisely the case that does NOT need fixing.
/// Stated as a schema invariant instead, it holds for every start order and for a ResearchService
/// that is simply switched off: after migrations, partitions exist for the next two weeks.
/// </para>
/// <para>
/// This class's remaining jobs are (a) to EXTEND that horizon on a rolling basis so a long-lived
/// deployment never reaches its end, (b) to wait for the schema to exist before sweeping at all —
/// its first sweep used to run concurrently with migrations on a cold start, fail every statement,
/// and drop into the 1-minute failure retry, which is how a whole UTC day used to get stranded
/// before the second sweep ever ran — and (c) to report anything already stranded in a way an
/// operator can act on and, once handled, stop hearing about.
/// </para>
/// </remarks>
public sealed class PartitionMaintainer(IConfiguration configuration, ILogger<PartitionMaintainer> logger)
    : BackgroundService
{
    /// <summary>
    /// Tables whose daily partitions must be created ahead of need, with the column each is
    /// RANGE-partitioned on. Only the raw-event tables: they are written continuously and
    /// partitioned by day, so they genuinely need a rolling window created for them.
    /// </summary>
    private static readonly (string Schema, string Table, string PartitionKey)[] DailyPartitionedTables =
    [
        ("gateway", "option_quote_events", "observed_at"),
        ("gateway", "underlying_tick_events", "observed_at"),
    ];

    /// <summary>
    /// Every partitioned table whose DEFAULT partition is an alarm condition — a superset of
    /// <see cref="DailyPartitionedTables"/>.
    /// </summary>
    /// <remarks>
    /// <c>research.bars</c> needs no partition creation (migration 004 pre-creates every yearly
    /// partition for 1990-2035 on an empty table) but it very much needs the alarm, and for a
    /// subtler reason than the raw-event tables: because every in-range partition already exists,
    /// a correctly-dated bar can never land in DEFAULT. So a row appearing there does not mean
    /// "maintenance fell behind" — it means the row's timestamp is OUTSIDE 1990-2035, which in
    /// practice means it was mis-parsed. An epoch-seconds value read as something else lands in
    /// 1970; that is a data-corruption signal, and pre-creating the partitions is precisely what
    /// removed the loud insert-time rejection that would otherwise have surfaced it.
    /// </remarks>
    private static readonly (string Schema, string Table, string PartitionKey)[] DefaultPartitionWatchTables =
    [
        ("gateway", "option_quote_events", "observed_at"),
        ("gateway", "underlying_tick_events", "observed_at"),
        ("research", "bars", "ts_utc"),
    ];

    /// <summary>
    /// How far ahead daily partitions are kept. This value is duplicated in migration 012, which
    /// creates the same horizon at migration time so a database is never writable without one —
    /// keep the two in step. Two weeks, not the original three days: the horizon's real job is to
    /// survive a ResearchService that is down or redeploying while the gateway keeps recording, and
    /// three days made "nobody looked at this over a long weekend" enough to strand a day
    /// permanently.
    /// </summary>
    internal const int DaysAhead = 14;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long to wait between checks for the schema existing. Short, because this is the cold-start
    /// path and the gap between "migration 003 commits" and "partitions exist" is the window this
    /// whole class is about: the shorter it is, the less of it a mis-ordered start can use. The
    /// schema-created-by-migration-012 horizon is what actually closes it; this only keeps the
    /// belt-and-braces path tight.
    /// </summary>
    private static readonly TimeSpan WaitForSchemaInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stranded dates this process has already raised the alarm for, so a standing condition can be
    /// reported as standing rather than re-raised as news on every sweep. Reset by a restart, on
    /// purpose: an operator who has just restarted the service has not necessarily read the log from
    /// before it, so the Critical is re-raised exactly once per process.
    /// </summary>
    private readonly Dictionary<(string Schema, string Table, DateOnly Date), StrandedPartitionDate> _stranded = [];

    private IReadOnlyList<StrandedPartitionDate> _strandedSnapshot = [];
    private bool _loggedWaitingForSchema;
    private bool _checkedSessionTimeZone;

    /// <summary>
    /// The dates currently stranded in a DEFAULT partition, as of the last completed sweep. Exposed
    /// so an operator surface can render the standing condition; the log alone deliberately goes
    /// quiet about it after the first Critical, and a condition nobody can see is a condition nobody
    /// fixes.
    /// </summary>
    public IReadOnlyList<StrandedPartitionDate> StrandedDates => Volatile.Read(ref _strandedSnapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; partition maintenance is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = SweepInterval;

            try
            {
                if (!await SchemaIsReadyAsync(connectionString, stoppingToken))
                {
                    // NOT an error, and deliberately not routed through the catch below. On a cold
                    // start this service races its own MigrationRunner; every statement in a sweep
                    // would fail, the outer catch would call that a failed sweep, and the 1-minute
                    // failure retry would then be the ONLY thing standing between a freshly-migrated
                    // database and the recorder's first COPY landing in DEFAULT. Waiting quietly for
                    // the schema is both quieter and faster.
                    if (!_loggedWaitingForSchema)
                    {
                        _loggedWaitingForSchema = true;
                        logger.LogInformation(
                            "Partition maintenance is waiting for the schema (migrations 003/004) before its first sweep.");
                    }

                    nextDelay = WaitForSchemaInterval;
                }
                else
                {
                    await SweepAsync(connectionString, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A slow sweep is how a DEFAULT-partition landmine gets planted (see the class
                // remarks), so a failed sweep is retried soon — not after the full 6h interval.
                logger.LogError(ex, "Partition maintenance sweep failed; retrying in {Delay}.", RetryAfterFailure);
                nextDelay = RetryAfterFailure;
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One full sweep: extend the horizon, review what is stranded, repair it if that has been
    /// explicitly asked for. Internal so tests drive the real sequence rather than a rearrangement
    /// of its parts.
    /// </summary>
    internal async Task SweepAsync(string connectionString, CancellationToken cancellationToken)
    {
        await EnsureUpcomingPartitionsAsync(connectionString, cancellationToken);

        var stranded = await ReviewDefaultPartitionsAsync(connectionString, cancellationToken);
        await RepairStrandedDatesAsync(connectionString, stranded, cancellationToken);
    }

    /// <summary>
    /// True once every partitioned table this class touches exists. Deliberately asked of the
    /// DATABASE rather than of the in-process <see cref="MigrationRunner"/>: migrations may have been
    /// applied by a different ResearchService instance, or on a previous run of this one, and the
    /// only precondition that actually matters is that the tables are there.
    /// </summary>
    private static async Task<bool> SchemaIsReadyAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var (schema, table, _) in DefaultPartitionWatchTables)
            {
                await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection);
                command.Parameters.AddWithValue($"{schema}.{table}");

                if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                {
                    return false;
                }
            }

            return true;
        }
        catch (NpgsqlException)
        {
            // Postgres not up yet, or the database not created yet — MigrationRunner owns creating
            // it and retries on its own schedule. Indistinguishable from "schema not ready" here,
            // and the response is the same either way.
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Creating the rolling horizon
    // ---------------------------------------------------------------------------------------------

    internal async Task EnsureUpcomingPartitionsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await WarnOnceIfSessionTimeZoneIsNotUtcAsync(connection, cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var (schema, table, _) in DailyPartitionedTables)
        {
            for (var offset = 0; offset <= DaysAhead; offset++)
            {
                var forDate = today.AddDays(offset);

                try
                {
                    await EnsurePartitionAsync(connection, schema, table, forDate, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One un-creatable date (most likely the DEFAULT-partition conflict described
                    // in this class's remarks) must not block every OTHER date in the same sweep —
                    // for both tables, on every retry, forever. Log and move on; the DEFAULT-rows
                    // review below still surfaces the underlying problem, named by date.
                    logger.LogError(
                        ex, "Could not ensure the {Schema}.{Table} partition for {Date}; continuing with the rest of the sweep.",
                        schema, table, forDate);
                }
            }
        }
    }

    private async Task EnsurePartitionAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        DateOnly forDate,
        CancellationToken cancellationToken)
    {
        var partitionName = PartitionNameFor(table, forDate);

        // Checked explicitly rather than trusting ExecuteNonQueryAsync's return value for a
        // "CREATE TABLE IF NOT EXISTS" DDL statement: that value's meaning for a no-op vs. an
        // actual creation is not something to rely on across driver/server versions, and an
        // "always logs 'created'" bug is exactly the kind of thing that erodes trust in these logs
        // being an honest signal — which matters given the DEFAULT-partition review depends on
        // operators actually trusting what gets logged here.
        var existedBefore = await PartitionExistsAsync(connection, schema, partitionName, cancellationToken);

        await using var command = new NpgsqlCommand(
            $"CREATE TABLE IF NOT EXISTS {schema}.\"{partitionName}\" {PartitionOfClause(schema, table, forDate)}",
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (!existedBefore)
        {
            logger.LogInformation("Created partition {Schema}.{Partition}.", schema, partitionName);
        }
    }

    private static string PartitionNameFor(string table, DateOnly forDate) => $"{table}_{forDate:yyyyMMdd}";

    /// <summary>
    /// The bound clause for one day's partition. Identifiers cannot be parameterised; every value
    /// that reaches string interpolation here is either a fixed literal from
    /// <see cref="DailyPartitionedTables"/> or a computed date, never external input.
    /// </summary>
    /// <remarks>
    /// Bare date literals, matching migration 012 exactly. They are cast to <c>timestamptz</c> using
    /// the SESSION time zone, so on a non-UTC server these bounds are local midnights rather than UTC
    /// midnights and would not line up with the UTC dates the partitions are NAMED for. That is a
    /// real fragility (migration 004 avoids it by writing <c>AT TIME ZONE 'UTC'</c> explicitly), and
    /// it is deliberately NOT changed here: any partition created by an earlier build carries the
    /// old bounds, and an adjacent partition with different bounds fails to create with an overlap
    /// error — on this table, that failure is how a date gets stranded. So the discrepancy is
    /// detected and reported instead, once per process, by
    /// <see cref="WarnOnceIfSessionTimeZoneIsNotUtcAsync"/>.
    /// </remarks>
    private static string PartitionOfClause(string schema, string table, DateOnly forDate) =>
        $"PARTITION OF {schema}.{table} " +
        $"FOR VALUES FROM ('{forDate:yyyy-MM-dd}') TO ('{forDate.AddDays(1):yyyy-MM-dd}')";

    private static async Task<bool> PartitionExistsAsync(
        NpgsqlConnection connection, string schema, string partitionName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection);
        command.Parameters.AddWithValue($"{schema}.\"{partitionName}\"");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Every daily partition boundary written by this class is a bare date literal resolved against
    /// the session time zone, while the dates themselves come from <c>DateTime.UtcNow</c>. On a UTC
    /// server (the postgres:17 image and the Aspire deployment) those agree exactly; anywhere else
    /// they silently do not, and the disagreement lands rows near midnight in the wrong day's
    /// partition. Reported once per process rather than every sweep — see the class remarks on why a
    /// permanently-repeating Critical is worse than no Critical at all.
    /// </summary>
    private async Task WarnOnceIfSessionTimeZoneIsNotUtcAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (_checkedSessionTimeZone)
        {
            return;
        }

        _checkedSessionTimeZone = true;

        await using var command = new NpgsqlCommand("SHOW TimeZone", connection);
        var timeZone = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "(unknown)";

        if (timeZone is "UTC" or "Etc/UTC" or "UCT" or "Etc/UCT" or "Universal" or "Zulu")
        {
            return;
        }

        logger.LogCritical(
            "Postgres session time zone is '{TimeZone}', not UTC. Daily partition bounds are written as " +
            "bare date literals and are therefore LOCAL midnights, while the partitions are named for " +
            "the UTC date — so ticks near midnight land in the wrong day's partition. Set the server's " +
            "timezone to UTC. Reported once per process.",
            timeZone);
    }

    // ---------------------------------------------------------------------------------------------
    // Reviewing what is already stranded
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds every UTC date with rows sitting in a DEFAULT partition, and reports each one exactly
    /// once as news and thereafter as a standing condition.
    /// </summary>
    /// <remarks>
    /// The old version of this counted rows per TABLE and logged Critical whenever the count was
    /// non-zero. Nothing ever removes stranded rows, so that Critical fired on every sweep forever,
    /// and a genuinely new stranded date on a later day was indistinguishable from the standing one —
    /// the "permanently red gate is a gate nobody reads" failure this repo has now recorded three
    /// times (migration 006's immortal gaps, Phase 2's fabricated 0% coverage, and this). Grouping by
    /// date is what makes the two distinguishable at all, and it is also what lets the message name
    /// the exact remedy for the exact date.
    /// </remarks>
    internal async Task<IReadOnlyList<StrandedPartitionDate>> ReviewDefaultPartitionsAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var found = await FindStrandedDatesAsync(connectionString, cancellationToken);
        var current = new Dictionary<(string, string, DateOnly), StrandedPartitionDate>();

        foreach (var (schema, table, date, rows) in found)
        {
            var key = (schema, table, date);
            var repairable = DailyPartitionedTables.Any(t => t.Schema == schema && t.Table == table);

            if (_stranded.TryGetValue(key, out var known))
            {
                current[key] = known with { Rows = rows };

                // Deliberately Warning, not Critical: the operator was told, in full, when this date
                // first appeared. Repeating that Critical every six hours for the life of the
                // deployment is what teaches people to filter the alarm out, and then the NEXT date
                // — the one that is actually news — arrives into a channel nobody reads.
                logger.LogWarning(
                    "Still stranded: {Rows} row(s) for {Date} in {Schema}.{Table}_default, unchanged since " +
                    "{FirstObserved:u} (this process). The full diagnosis and remedy were logged as Critical then.",
                    rows, date.ToString("yyyy-MM-dd"), schema, table, known.FirstObservedAt);

                continue;
            }

            var entry = new StrandedPartitionDate(schema, table, date, rows, observedAt, repairable);
            current[key] = entry;
            LogNewlyStranded(entry);
        }

        foreach (var (key, previous) in _stranded)
        {
            if (!current.ContainsKey(key))
            {
                logger.LogInformation(
                    "Cleared: {Schema}.{Table}_default no longer holds rows for {Date}; its real partition " +
                    "can be created again.",
                    previous.Schema, previous.Table, previous.Date.ToString("yyyy-MM-dd"));
            }
        }

        _stranded.Clear();

        foreach (var (key, value) in current)
        {
            _stranded[key] = value;
        }

        var snapshot = current.Values.OrderBy(s => s.Schema).ThenBy(s => s.Table).ThenBy(s => s.Date).ToArray();
        Volatile.Write(ref _strandedSnapshot, snapshot);

        return snapshot;
    }

    private void LogNewlyStranded(StrandedPartitionDate entry)
    {
        if (!entry.Repairable)
        {
            // research.bars: every in-range yearly partition was pre-created on an empty table, so a
            // correctly-dated bar CANNOT land here. A row that did is out of 1990-2035, which in
            // practice means its timestamp was mis-parsed. Creating a partition for it would enshrine
            // the corrupted value, so the repair path deliberately refuses this table.
            logger.LogCritical(
                "{Rows} row(s) dated {Date} are in {Schema}.{Table}_default. Every in-range partition for " +
                "this table was pre-created (migration 004), so a correctly-dated row CANNOT land here — " +
                "these timestamps fall outside 1990-2035, which in practice means they were mis-parsed (an " +
                "epoch value read wrongly lands in 1970). Treat this as DATA CORRUPTION, not as maintenance " +
                "lag: find the ingestion path that produced them before doing anything else. The automatic " +
                "repair does not apply to this table and will not touch it — creating a partition for a " +
                "mis-parsed date would only make the wrong value permanent.",
                entry.Rows, entry.Date.ToString("yyyy-MM-dd"), entry.Schema, entry.Table);

            return;
        }

        // Identifiers are pre-composed rather than left as repeated {Schema}/{Table} placeholders:
        // the remedy below is meant to be copy-pasteable SQL, and a structured-logging template
        // renders each placeholder only as many times as it has arguments.
        var parent = $"{entry.Schema}.{entry.Table}";
        var partition = $"{entry.Schema}.\"{PartitionNameFor(entry.Table, entry.Date)}\"";
        var date = entry.Date.ToString("yyyy-MM-dd");
        var range = $"{DailyPartitionedTables.First(t => t.Table == entry.Table).PartitionKey} >= '{date}' " +
                    $"AND {DailyPartitionedTables.First(t => t.Table == entry.Table).PartitionKey} < " +
                    $"'{entry.Date.AddDays(1):yyyy-MM-dd}'";

        logger.LogCritical(
            "NEW: {Rows} row(s) for {Date} (UTC) are stranded in the DEFAULT partition of {Parent}, so Postgres " +
            "will now permanently refuse to create {Partition} (\"updated partition constraint for default " +
            "partition ... would be violated by some row\"). The rows are NOT lost and every query over the " +
            "parent table still returns them — but that date has no partition to export or DROP, so retention " +
            "cannot process it. REMEDY: set Partitions:RepairStrandedRows=true to have the next sweep move them " +
            "(one transaction, row counts verified, nothing deleted), or run the equivalent manual migration " +
            "yourself: {Remedy}",
            entry.Rows,
            date,
            parent,
            partition,
            $"BEGIN; " +
            $"LOCK TABLE {parent} IN ACCESS EXCLUSIVE MODE; " +
            $"CREATE TEMP TABLE rescue ON COMMIT DROP AS SELECT * FROM {parent}_default WHERE {range}; " +
            $"DELETE FROM {parent}_default WHERE {range}; " +
            $"CREATE TABLE {partition} {PartitionOfClause(entry.Schema, entry.Table, entry.Date)}; " +
            $"INSERT INTO {parent} OVERRIDING SYSTEM VALUE SELECT * FROM rescue; " +
            $"COMMIT;");
    }

    internal async Task<IReadOnlyList<(string Schema, string Table, DateOnly Date, long Rows)>> FindStrandedDatesAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var found = new List<(string, string, DateOnly, long)>();

        foreach (var (schema, table, partitionKey) in DefaultPartitionWatchTables)
        {
            // Grouped by UTC date rather than counted in bulk: the date is what names the un-creatable
            // partition, and it is the only thing that distinguishes a new stranding event from the
            // standing one. Table and column names are always fixed literals from the arrays above,
            // never external input. This is a sequential scan of the DEFAULT partition, which is
            // affordable at a 6h sweep and is the same cost the old bulk count(*) already paid.
            await using var command = new NpgsqlCommand(
                $"SELECT ({partitionKey} AT TIME ZONE 'UTC')::date AS d, count(*) " +
                $"FROM {schema}.{table}_default GROUP BY 1 ORDER BY 1",
                connection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add((schema, table, reader.GetFieldValue<DateOnly>(0), reader.GetInt64(1)));
            }
        }

        return found;
    }

    // ---------------------------------------------------------------------------------------------
    // Repair
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Off by default, and it stays that way: these are recorded market data rows, the roadmap treats
    /// prospective ticks as unrecoverable, and moving several million of them takes an ACCESS
    /// EXCLUSIVE lock on a table a live recorder is COPYing into. Turning it on is a deliberate
    /// operator act in response to the Critical above, which names this switch.
    /// </summary>
    private sealed record RepairSettings(bool Enabled, long MaxRows, int LockTimeoutSeconds, int StatementTimeoutSeconds);

    private RepairSettings ReadRepairSettings()
    {
        var section = configuration.GetSection("Partitions");

        return new RepairSettings(
            section.GetValue("RepairStrandedRows", false),
            // A whole UTC day of option ticks is roughly 7M rows. The default cap sits under that on
            // purpose: the common case this exists for is a short cold-start window of a few thousand
            // rows, which moves in well under a second, and an operator who genuinely wants to move a
            // full day should be making that choice explicitly with the recorder paused.
            section.GetValue("MaxRepairRows", 2_000_000L),
            section.GetValue("RepairLockTimeoutSeconds", 10),
            section.GetValue("RepairStatementTimeoutSeconds", 300));
    }

    internal async Task RepairStrandedDatesAsync(
        string connectionString,
        IReadOnlyList<StrandedPartitionDate> stranded,
        CancellationToken cancellationToken)
    {
        if (stranded.Count == 0)
        {
            return;
        }

        var settings = ReadRepairSettings();

        if (!settings.Enabled)
        {
            return;
        }

        foreach (var entry in stranded.Where(e => e.Repairable))
        {
            try
            {
                if (await RepairStrandedDateAsync(connectionString, entry, settings, cancellationToken))
                {
                    _stranded.Remove((entry.Schema, entry.Table, entry.Date));
                    Volatile.Write(ref _strandedSnapshot, [.. _stranded.Values]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing is lost by a failed repair — every step runs inside one transaction, so a
                // failure rolls the whole thing back and leaves the rows exactly where they were.
                // The most likely failure is lock_timeout against a live recorder, which is why the
                // message says so rather than leaving an operator to infer it.
                logger.LogError(
                    ex,
                    "Repair of {Schema}.{Table}_default for {Date} failed and was rolled back; the rows are " +
                    "untouched. If this is a lock timeout, the recorder is writing to this table — retry with " +
                    "recording paused, or raise Partitions:RepairLockTimeoutSeconds.",
                    entry.Schema, entry.Table, entry.Date.ToString("yyyy-MM-dd"));
            }
        }
    }

    /// <summary>
    /// Moves one date's stranded rows out of DEFAULT and into a freshly created real partition, in a
    /// single transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every row is preserved by construction, not by care: the rows are copied to a temp table,
    /// deleted, the partition is created, and the rows are re-inserted through the PARENT (so
    /// Postgres routes them) — all inside one transaction whose final act is to verify that the new
    /// partition holds exactly as many rows as were taken out. Any mismatch, any error, any timeout
    /// rolls the whole thing back and the rows stay in DEFAULT. There is no code path that deletes
    /// without a verified re-insert.
    /// </para>
    /// <para>
    /// The ACCESS EXCLUSIVE lock is taken FIRST, before anything is read. That is what makes the
    /// current date repairable at all: without it, a concurrent COPY could land more rows for this
    /// date in DEFAULT between the DELETE and the CREATE, and the CREATE would then fail its
    /// partition-constraint scan. With it, writers block for the duration and resume against a table
    /// that now routes the date correctly. <c>lock_timeout</c> bounds how long this is allowed to
    /// fight a live recorder for that lock, and <c>statement_timeout</c> bounds how long the recorder
    /// can be stalled once it is held.
    /// </para>
    /// <para>
    /// <c>OVERRIDING SYSTEM VALUE</c> is required because <c>event_id</c> is GENERATED ALWAYS: the
    /// point is to move the ORIGINAL rows, ids included, not to mint new ones. The identity sequence
    /// has already advanced past these values, so re-inserting them cannot collide with a future row.
    /// </para>
    /// </remarks>
    private async Task<bool> RepairStrandedDateAsync(
        string connectionString,
        StrandedPartitionDate entry,
        RepairSettings settings,
        CancellationToken cancellationToken)
    {
        var (schema, table, partitionKey) =
            DailyPartitionedTables.First(t => t.Schema == entry.Schema && t.Table == entry.Table);

        var partition = PartitionNameFor(table, entry.Date);
        var from = entry.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = entry.Date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var range = $"{partitionKey} >= $1 AND {partitionKey} < $2";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var timeouts = new NpgsqlCommand(
            $"SET lock_timeout = '{settings.LockTimeoutSeconds}s'; " +
            $"SET statement_timeout = '{settings.StatementTimeoutSeconds}s'",
            connection))
        {
            await timeouts.ExecuteNonQueryAsync(cancellationToken);
        }

        var columns = await ColumnListAsync(connection, schema, table, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockTable = new NpgsqlCommand(
            $"LOCK TABLE {schema}.{table} IN ACCESS EXCLUSIVE MODE", connection, transaction))
        {
            await lockTable.ExecuteNonQueryAsync(cancellationToken);
        }

        var toMove = (long)(await ScalarAsync(
            $"SELECT count(*) FROM {schema}.{table}_default WHERE {range}", from, to))!;

        if (toMove == 0)
        {
            // Someone else got there first (another instance, a hand-run migration). Nothing to do,
            // and reporting it as a repair would be a lie.
            await transaction.RollbackAsync(cancellationToken);
            return true;
        }

        if (toMove > settings.MaxRows)
        {
            await transaction.RollbackAsync(cancellationToken);

            logger.LogError(
                "Refusing to repair the DEFAULT partition of {Parent} for {Date}: {Rows} row(s) exceeds " +
                "Partitions:MaxRepairRows ({Max}). Moving them holds an ACCESS EXCLUSIVE lock on the parent " +
                "table for the duration, stalling the recorder. Raise the cap deliberately, with recording " +
                "paused.",
                $"{schema}.{table}", entry.Date.ToString("yyyy-MM-dd"), toMove, settings.MaxRows);

            return false;
        }

        await NonQueryAsync(
            $"CREATE TEMP TABLE partition_rescue ON COMMIT DROP AS " +
            $"SELECT * FROM {schema}.{table}_default WHERE {range}", from, to);

        var deleted = await NonQueryAsync(
            $"DELETE FROM {schema}.{table}_default WHERE {range}", from, to);

        if (deleted != toMove)
        {
            throw new InvalidOperationException(
                $"Repair aborted: expected to move {toMove} row(s) out of {schema}.{table}_default for " +
                $"{entry.Date:yyyy-MM-dd} but the DELETE removed {deleted}. Rolled back; nothing changed.");
        }

        await NonQueryAsync($"CREATE TABLE {schema}.\"{partition}\" {PartitionOfClause(schema, table, entry.Date)}");

        var inserted = await NonQueryAsync(
            $"INSERT INTO {schema}.{table} ({columns}) OVERRIDING SYSTEM VALUE SELECT {columns} FROM partition_rescue");

        var landed = (long)(await ScalarAsync($"SELECT count(*) FROM {schema}.\"{partition}\""))!;

        if (inserted != toMove || landed != toMove)
        {
            throw new InvalidOperationException(
                $"Repair aborted: {toMove} row(s) were taken out of {schema}.{table}_default for " +
                $"{entry.Date:yyyy-MM-dd} but {inserted} were re-inserted and {landed} are in " +
                $"{schema}.\"{partition}\". Rolled back; nothing changed.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Repaired the DEFAULT partition of {Parent} for {Date}: {Rows} row(s) moved into {Partition}, " +
            "verified row-for-row inside the transaction. Nothing was deleted.",
            $"{schema}.{table}", entry.Date.ToString("yyyy-MM-dd"), toMove, $"{schema}.\"{partition}\"");

        return true;

        async Task<object?> ScalarAsync(string sql, params object[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);

            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            return await command.ExecuteScalarAsync(cancellationToken);
        }

        async Task<int> NonQueryAsync(string sql, params object[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);

            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The parent table's columns, in attribute order, read from the catalog rather than hardcoded so
    /// a column added by a later migration is carried through a repair instead of being dropped from
    /// the rescued rows.
    /// </summary>
    private static async Task<string> ColumnListAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT string_agg(quote_ident(attname), ', ' ORDER BY attnum) " +
            "FROM pg_attribute WHERE attrelid = $1::regclass AND attnum > 0 AND NOT attisdropped",
            connection);
        command.Parameters.AddWithValue($"{schema}.{table}");

        return (string?)await command.ExecuteScalarAsync(cancellationToken)
               ?? throw new InvalidOperationException($"{schema}.{table} reports no columns.");
    }
}
