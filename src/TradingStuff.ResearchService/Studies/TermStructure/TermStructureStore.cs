using Npgsql;
using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.ResearchService.Studies.TermStructure;

/// <summary>One persisted session-date row of the A4 term-structure series.</summary>
public sealed record TermStructureRow(
    DateOnly SessionDate,
    string Status,
    DateTimeOffset SnapshotUtc,
    double? Variance9d,
    double? Variance30d,
    double? Slope,
    double? Near9dDays,
    double? Far9dDays,
    int? Strikes9d,
    double? Near30dDays,
    double? Far30dDays,
    int? Strikes30d,
    double? Underlying1530,
    string? Note);

/// <summary>
/// Persistence for <c>research.implied_term_structure</c> and the DTB4WK rates it discounts
/// with. Upsert-by-date everywhere: rebuilding after more chain ingestion lands is the intended
/// operating mode (migration 022's remarks), so writes must be idempotent re-runs, not appends.
/// </summary>
public sealed class TermStructureStore(IConfiguration configuration)
{
    public string? ConnectionString => configuration.GetConnectionString("trading");

    public async Task SaveAsync(TermStructureRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO research.implied_term_structure
              (session_date, status, snapshot_utc, variance_9d, variance_30d, slope,
               near_9d_days, far_9d_days, strikes_9d, near_30d_days, far_30d_days, strikes_30d,
               underlying_15_30, note, built_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, now())
            ON CONFLICT (session_date) DO UPDATE SET
              status = EXCLUDED.status, snapshot_utc = EXCLUDED.snapshot_utc,
              variance_9d = EXCLUDED.variance_9d, variance_30d = EXCLUDED.variance_30d,
              slope = EXCLUDED.slope,
              near_9d_days = EXCLUDED.near_9d_days, far_9d_days = EXCLUDED.far_9d_days,
              strikes_9d = EXCLUDED.strikes_9d,
              near_30d_days = EXCLUDED.near_30d_days, far_30d_days = EXCLUDED.far_30d_days,
              strikes_30d = EXCLUDED.strikes_30d,
              underlying_15_30 = EXCLUDED.underlying_15_30, note = EXCLUDED.note,
              built_at = now()
            """,
            connection);

        command.Parameters.Add(new() { Value = row.SessionDate });
        command.Parameters.Add(new() { Value = row.Status });
        command.Parameters.Add(new() { Value = row.SnapshotUtc });
        command.Parameters.Add(new() { Value = (object?)row.Variance9d ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Variance30d ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Slope ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Near9dDays ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Far9dDays ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Strikes9d ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Near30dDays ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Far30dDays ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Strikes30d ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Underlying1530 ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)row.Note ?? DBNull.Value });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TermStructureRow>> ListAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return [];

        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT session_date, status, snapshot_utc, variance_9d, variance_30d, slope,
                   near_9d_days, far_9d_days, strikes_9d, near_30d_days, far_30d_days, strikes_30d,
                   underlying_15_30, note
            FROM research.implied_term_structure
            WHERE session_date >= $1 AND session_date <= $2
            ORDER BY session_date
            """,
            connection);

        command.Parameters.Add(new() { Value = from });
        command.Parameters.Add(new() { Value = to });

        var rows = new List<TermStructureRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TermStructureRow(
                reader.GetFieldValue<DateOnly>(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                NullableDouble(reader, 3), NullableDouble(reader, 4), NullableDouble(reader, 5),
                NullableDouble(reader, 6), NullableDouble(reader, 7), NullableInt(reader, 8),
                NullableDouble(reader, 9), NullableDouble(reader, 10), NullableInt(reader, 11),
                NullableDouble(reader, 12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return rows;
    }

    /// <summary>
    /// Loads the persisted DTB4WK rows into the carry-forward rate source the builder
    /// discounts with, converting from published discount basis to continuous compounding
    /// (<see cref="TreasuryBillRate.ContinuousFromDiscount"/>) at read time — the table stays
    /// a faithful copy of the source.
    /// </summary>
    public async Task<HistoricalRiskFreeRate?> LoadRatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT rate_date, discount_rate_pct FROM research.risk_free_rates ORDER BY rate_date",
            connection);

        var rates = new List<KeyValuePair<DateTime, double>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rates.Add(new KeyValuePair<DateTime, double>(
                reader.GetFieldValue<DateOnly>(0).ToDateTime(TimeOnly.MinValue),
                TreasuryBillRate.ContinuousFromDiscount(reader.GetDouble(1))));
        }

        return rates.Count == 0 ? null : new HistoricalRiskFreeRate(rates);
    }

    private static double? NullableDouble(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("No 'trading' connection string is configured.");

        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
