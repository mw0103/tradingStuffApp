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
    private static readonly (string Schema, string Table)[] PartitionedTables =
    [
        ("gateway", "option_quote_events"),
        ("gateway", "underlying_tick_events"),
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

        foreach (var (schema, table) in PartitionedTables)
        {
            // Table names are always one of the two fixed literals above, never external input.
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.{table}_default", connection);
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            if (count > 0)
            {
                logger.LogCritical(
                    "{Count} row(s) are stranded in {Schema}.{Table}_default. The dedicated daily " +
                    "partition for whatever date(s) they carry can no longer be created automatically " +
                    "— this needs a manual one-time migration (copy the rows into a hand-created " +
                    "partition, delete them from DEFAULT). See PartitionMaintainer's remarks.",
                    count, schema, table);
            }
        }
    }

    internal async Task EnsureUpcomingPartitionsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var (schema, table) in PartitionedTables)
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
