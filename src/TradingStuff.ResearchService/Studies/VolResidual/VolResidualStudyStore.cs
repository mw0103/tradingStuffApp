using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Minimal persistence for the study's development runs: one jsonb row per run in
/// <c>research.dev_vol_residual_runs</c> (migration 017), never the trial registry — see that
/// migration's header for why a dev run must not consume a registered-variant slot.
/// </summary>
/// <remarks>
/// This intentionally does not try to be a results schema. There is exactly one write path (persist
/// what <see cref="VolResidualStudyRunner.RunAsync"/> just computed) and exactly one read path (the
/// most recent row), so normalizing the payload into columns would be speculative design for queries
/// nothing here makes. If persistence is unavailable, both endpoints degrade to "compute but don't
/// remember" rather than fail — <c>GET .../latest</c> is the one exception, since that endpoint has
/// nothing to compute and genuinely has no answer without persistence.
/// </remarks>
public sealed class VolResidualStudyStore(IConfiguration configuration, ILogger<VolResidualStudyStore> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string? ConnectionString => configuration.GetConnectionString("trading");

    public async Task SaveAsync(VolResidualRunResponse response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(
                "INSERT INTO research.dev_vol_residual_runs (run_id, generated_at, status, payload) " +
                "VALUES ($1, $2, $3, $4::jsonb)",
                connection)
            {
                Parameters =
                {
                    new() { Value = response.RunId },
                    new() { Value = response.GeneratedAt },
                    new() { Value = response.Status },
                    new() { Value = JsonSerializer.Serialize(response, SerializerOptions), NpgsqlDbType = NpgsqlDbType.Jsonb },
                },
            };

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            // A development run is still useful in the HTTP response body even if it cannot be
            // remembered — this is a convenience cache, not the source of truth for anything, so a
            // write failure here is logged and swallowed rather than turned into a 5xx for a caller
            // who already has their answer.
            logger.LogWarning(ex, "Could not persist the vol-residual development run artifact.");
        }
    }

    public async Task<VolResidualRunResponse?> GetLatestAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return null;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM research.dev_vol_residual_runs ORDER BY generated_at DESC LIMIT 1",
            connection);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string json) return null;

        return JsonSerializer.Deserialize<VolResidualRunResponse>(json, SerializerOptions);
    }
}
