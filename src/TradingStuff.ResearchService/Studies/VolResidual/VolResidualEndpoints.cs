using Npgsql;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Maps the volatility-forecast-residual study's two development-run endpoints. Anonymous, like
/// every other <c>/research/*</c> diagnostic surface (see Program.cs) — this triggers a compute-only
/// pass over already-recorded market data, it does not place orders or touch TWS.
/// </summary>
public static class VolResidualEndpoints
{
    public static void MapVolResidualStudyEndpoints(this WebApplication app)
    {
        app.MapPost("/research/studies/vol-residual/run", async (
                DateOnly? from,
                DateOnly? to,
                VolResidualStudyRunner runner,
                VolResidualStudyStore store,
                CancellationToken cancellationToken) =>
            {
                if (from is { } f && to is { } t && f > t)
                {
                    return Results.Problem(
                        title: "Invalid date range.",
                        detail: $"'to' ({t:yyyy-MM-dd}) is before 'from' ({f:yyyy-MM-dd}).",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                if (string.IsNullOrWhiteSpace(runner.ConnectionStringForDiagnostics))
                {
                    return Results.Problem(
                        title: "Research persistence is not configured.",
                        detail: "No 'trading' connection string.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    var response = await runner.RunAsync(from, to, cancellationToken);
                    await store.SaveAsync(response, cancellationToken);
                    return Results.Ok(response);
                }
                catch (NpgsqlException ex)
                {
                    return Results.Problem(
                        title: "Could not run the vol-residual development study.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        app.MapGet("/research/studies/vol-residual/latest", async (
                VolResidualStudyStore store, CancellationToken cancellationToken) =>
            {
                try
                {
                    var latest = await store.GetLatestAsync(cancellationToken);
                    if (latest is null)
                    {
                        return Results.Problem(
                            title: "No development run yet.",
                            detail: "POST /research/studies/vol-residual/run first.",
                            statusCode: StatusCodes.Status404NotFound);
                    }

                    return Results.Ok(latest);
                }
                catch (NpgsqlException ex)
                {
                    return Results.Problem(
                        title: "Could not read the latest vol-residual development run.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
