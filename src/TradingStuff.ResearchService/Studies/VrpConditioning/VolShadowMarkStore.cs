using System.Text.Json;
using Npgsql;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// Persists shadow marks (<c>research.vol_shadow_marks</c>, migration 021). One row per decision
/// date; recomputing a date replaces its row — these are operational records, not claims, and the
/// registry's append-only discipline deliberately does not apply.
/// </summary>
public sealed class VolShadowMarkStore(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string? ConnectionString => configuration.GetConnectionString("trading");

    public async Task SaveAsync(VrpShadowMark mark, object plannerIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("No 'trading' connection string; the shadow mark cannot be persisted.");

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO research.vol_shadow_marks (
                mark_date, generated_at, train_from, train_to, train_rows,
                vix_close, implied_variance, qcj_forecast, harx_forecast,
                qcj_spread, harx_spread, vix_spread, qcj_bucket, harx_bucket, vix_bucket,
                intended_vega, shadow_alloc_qcj, shadow_alloc_harx, shadow_alloc_vix, planner_intent)
            VALUES ($1, now(), $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, 1.0, $15, $16, $17, $18)
            ON CONFLICT (mark_date) DO UPDATE SET
                generated_at = EXCLUDED.generated_at,
                train_from = EXCLUDED.train_from, train_to = EXCLUDED.train_to, train_rows = EXCLUDED.train_rows,
                vix_close = EXCLUDED.vix_close, implied_variance = EXCLUDED.implied_variance,
                qcj_forecast = EXCLUDED.qcj_forecast, harx_forecast = EXCLUDED.harx_forecast,
                qcj_spread = EXCLUDED.qcj_spread, harx_spread = EXCLUDED.harx_spread,
                vix_spread = EXCLUDED.vix_spread,
                qcj_bucket = EXCLUDED.qcj_bucket, harx_bucket = EXCLUDED.harx_bucket,
                vix_bucket = EXCLUDED.vix_bucket,
                shadow_alloc_qcj = EXCLUDED.shadow_alloc_qcj,
                shadow_alloc_harx = EXCLUDED.shadow_alloc_harx,
                shadow_alloc_vix = EXCLUDED.shadow_alloc_vix,
                planner_intent = EXCLUDED.planner_intent
            """,
            connection);

        command.Parameters.Add(new() { Value = mark.MarkDate });
        command.Parameters.Add(new() { Value = mark.TrainFrom });
        command.Parameters.Add(new() { Value = mark.TrainTo });
        command.Parameters.Add(new() { Value = mark.TrainRows });
        command.Parameters.Add(new() { Value = mark.VixClose });
        command.Parameters.Add(new() { Value = mark.ImpliedVariance });
        command.Parameters.Add(new() { Value = mark.QcjForecast });
        command.Parameters.Add(new() { Value = mark.HarxForecast });
        command.Parameters.Add(new() { Value = mark.QcjSpread });
        command.Parameters.Add(new() { Value = mark.HarxSpread });
        command.Parameters.Add(new() { Value = mark.VixSpread });
        command.Parameters.Add(new() { Value = mark.QcjBucket });
        command.Parameters.Add(new() { Value = mark.HarxBucket });
        command.Parameters.Add(new() { Value = mark.VixBucket });
        command.Parameters.Add(new() { Value = mark.ShadowAllocQcj });
        command.Parameters.Add(new() { Value = mark.ShadowAllocHarx });
        command.Parameters.Add(new() { Value = mark.ShadowAllocVix });
        command.Parameters.Add(new()
        {
            Value = JsonSerializer.Serialize(plannerIntent, SerializerOptions),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<object>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return [];

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT mark_date, generated_at, train_rows, vix_close, implied_variance,
                   qcj_forecast, harx_forecast, qcj_spread, qcj_bucket, harx_bucket, vix_bucket,
                   shadow_alloc_qcj, shadow_alloc_harx, shadow_alloc_vix, planner_intent
            FROM research.vol_shadow_marks
            ORDER BY mark_date DESC
            LIMIT $1
            """,
            connection)
        {
            Parameters = { new() { Value = limit } },
        };

        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new
            {
                markDate = reader.GetFieldValue<DateOnly>(0),
                generatedAt = reader.GetFieldValue<DateTimeOffset>(1),
                trainRows = reader.GetInt32(2),
                vixClose = reader.GetDouble(3),
                impliedVariance = reader.GetDouble(4),
                qcjForecast = reader.GetDouble(5),
                harxForecast = reader.GetDouble(6),
                qcjSpread = reader.GetDouble(7),
                qcjBucket = reader.GetInt32(8),
                harxBucket = reader.GetInt32(9),
                vixBucket = reader.GetInt32(10),
                shadowAllocQcj = reader.GetDouble(11),
                shadowAllocHarx = reader.GetDouble(12),
                shadowAllocVix = reader.GetDouble(13),
                plannerIntent = JsonSerializer.Deserialize<JsonElement>(reader.GetString(14)),
            });
        }

        return rows;
    }
}
