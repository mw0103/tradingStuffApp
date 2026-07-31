using Npgsql;

namespace TradingStuff.ResearchService.Persistence;

/// <summary>
/// Creates the daily partitions the gateway's raw-event recorder writes into, a few days ahead of
/// need.
/// </summary>
/// <remarks>
/// Both raw event tables also carry a DEFAULT partition (migration 003) as a safety net so an
/// incoming COPY never fails outright when this service falls behind. That safety net has a sharp
/// edge, verified directly against Postgres: once a row for a given date sits in DEFAULT, Postgres
/// permanently refuses to create the real partition for that date afterward
/// (<c>updated partition constraint for default partition ... would be violated by some row</c>) —
/// recovering it requires a manual one-time migration (copy the stray rows into a hand-created
/// partition, delete them from DEFAULT). Nothing here automates that; instead this class (a) keeps
/// several days of headroom so the failure window is small, (b) retries quickly rather than waiting
/// a full sweep interval after any failure, and (c) checks every sweep whether anything has landed
/// in DEFAULT and logs it as Critical — the loud, humans-must-look-at-this-now signal the original
/// "slightly misfiled beats dropped" reasoning promised but did not, on its own, actually raise.
/// </remarks>
public sealed class PartitionMaintainer(IConfiguration configuration, ILogger<PartitionMaintainer> logger)
    : BackgroundService
{
    /// <summary>
    /// Tables whose daily partitions must be created ahead of need. Only the raw-event tables:
    /// they are written continuously and partitioned by day, so they genuinely need a rolling
    /// window created for them.
    /// </summary>
    private static readonly (string Schema, string Table)[] DailyPartitionedTables =
    [
        ("gateway", "option_quote_events"),
        ("gateway", "underlying_tick_events"),
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
    private static readonly (string Schema, string Table)[] DefaultPartitionWatchTables =
    [
        ("gateway", "option_quote_events"),
        ("gateway", "underlying_tick_events"),
        ("research", "bars"),
    ];

    private const int DaysAhead = 3;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

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
                await EnsureUpcomingPartitionsAsync(connectionString, stoppingToken);
                await WarnIfDefaultPartitionsHoldRowsAsync(connectionString, stoppingToken);
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

    private async Task WarnIfDefaultPartitionsHoldRowsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var (schema, table) in DefaultPartitionWatchTables)
        {
            // Table names are always fixed literals from the arrays above, never external input.
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.{table}_default", connection);
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (count == 0)
            {
                continue;
            }

            // Same alarm, two genuinely different diagnoses — say which, or an operator chases the
            // wrong one.
            var isPreCreated = !DailyPartitionedTables.Contains((schema, table));

            logger.LogCritical(
                "{Count} row(s) are stranded in {Schema}.{Table}_default. {Diagnosis} Recovering them " +
                "needs a manual one-time migration (copy the rows into a hand-created partition, " +
                "delete them from DEFAULT) — Postgres will not let the covering partition be created " +
                "while they sit there. See PartitionMaintainer's remarks.",
                count,
                schema,
                table,
                isPreCreated
                    ? "Every in-range partition for this table was pre-created, so a correctly-dated row " +
                      "CANNOT land here — these timestamps fall outside the pre-created range, which in " +
                      "practice means they were mis-parsed (an epoch value read wrongly lands in 1970). " +
                      "Treat this as data corruption, not as maintenance lag."
                    : "The dedicated daily partition for whatever date(s) they carry can no longer be " +
                      "created automatically.");
        }
    }

    internal async Task EnsureUpcomingPartitionsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var (schema, table) in DailyPartitionedTables)
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
                    // check below still surfaces the underlying problem loudly.
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
        var partitionName = $"{table}_{forDate:yyyyMMdd}";
        var from = forDate.ToDateTime(TimeOnly.MinValue);
        var to = forDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // Checked explicitly rather than trusting ExecuteNonQueryAsync's return value for a
        // "CREATE TABLE IF NOT EXISTS" DDL statement: that value's meaning for a no-op vs. an
        // actual creation is not something to rely on across driver/server versions, and an
        // "always logs 'created'" bug is exactly the kind of thing that erodes trust in these logs
        // being an honest signal — which matters given the DEFAULT-partition check right below
        // this one depends on operators actually trusting what gets logged here.
        var existedBefore = await PartitionExistsAsync(connection, schema, partitionName, cancellationToken);

        // Identifiers cannot be parameterised; every value that reaches string interpolation here
        // is either a fixed literal from PartitionedTables or a computed date, never external input.
        var sql =
            $"CREATE TABLE IF NOT EXISTS {schema}.\"{partitionName}\" " +
            $"PARTITION OF {schema}.{table} " +
            $"FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}')";

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (!existedBefore)
        {
            logger.LogInformation("Created partition {Schema}.{Partition}.", schema, partitionName);
        }
    }

    private static async Task<bool> PartitionExistsAsync(
        NpgsqlConnection connection, string schema, string partitionName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection);
        command.Parameters.AddWithValue($"{schema}.\"{partitionName}\"");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
