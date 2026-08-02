using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// One jsonb row per development run in <c>research.dev_vrp_conditioning_runs</c> (migration 020).
/// Never the trial registry — see that migration's header, and
/// <see cref="VrpConditioningRunResponse.Registrable"/>, which is false by construction.
/// </summary>
public sealed class VrpConditioningStudyStore(IConfiguration configuration, ILogger<VrpConditioningStudyStore> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string? ConnectionString => configuration.GetConnectionString("trading");

    public async Task SaveAsync(VrpConditioningRunResponse response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(
                "INSERT INTO research.dev_vrp_conditioning_runs (run_id, generated_at, status, payload) " +
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
            // Same posture as the parent study's store: the caller already has its answer in the
            // response body, so a failure to remember the run is logged, not turned into a 5xx.
            logger.LogWarning(ex, "Could not persist the vrp-conditioning development run artifact.");
        }
    }

    public async Task<VrpConditioningRunResponse?> GetLatestAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return null;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM research.dev_vrp_conditioning_runs ORDER BY generated_at DESC LIMIT 1",
            connection);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string json) return null;

        return JsonSerializer.Deserialize<VrpConditioningRunResponse>(json, SerializerOptions);
    }
}
