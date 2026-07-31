using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MigrationRunner>());

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/research/status", (MigrationRunner migrations) =>
    {
        var state = migrations.State;

        return Results.Ok(new
        {
            migrations = new { state.Status, state.Applied, state.Error },
        });
    })
    .RequireAuthorization();

// The runtime-verified IBKR capability registry — most recent probe per key first.
app.MapGet("/research/capabilities", async (
        IConfiguration configuration,
        CancellationToken cancellationToken) =>
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Results.Problem(
                title: "Research persistence is not configured.",
                detail: "No 'trading' connection string.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(
                "SELECT DISTINCT ON (probe_key) " +
                "  probe_id, probe_key, con_id, ran_at, tws_server_version, market_data_type, " +
                "  succeeded, result::text, error_code, notes " +
                "FROM research.capability_probes " +
                "ORDER BY probe_key, ran_at DESC",
                connection);

            var probes = new List<CapabilityProbeRecord>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                probes.Add(new CapabilityProbeRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetBoolean(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return Results.Ok(probes);
        }
        catch (NpgsqlException ex)
        {
            return Results.Problem(
                title: "Could not read the capability registry.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();
