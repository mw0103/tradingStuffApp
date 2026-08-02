using Npgsql;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// The companion study's two development-run endpoints, mirroring
/// <see cref="TradingStuff.ResearchService.Studies.VolResidual.VolResidualEndpoints"/>. Anonymous,
/// like every other <c>/research/*</c> diagnostic surface — this is a compute-only pass over
/// already-recorded market data.
/// </summary>
public static class VrpConditioningEndpoints
{
    public static void MapVrpConditioningStudyEndpoints(this WebApplication app)
    {
        app.MapPost("/research/studies/vrp-conditioning/run", async (
                DateOnly? from,
                DateOnly? to,
                VrpConditioningStudyRunner runner,
                VrpConditioningStudyStore store,
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
                        title: "Could not run the vrp-conditioning development study.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        app.MapGet("/research/studies/vrp-conditioning/latest", async (
                VrpConditioningStudyStore store, CancellationToken cancellationToken) =>
            {
                try
                {
                    var latest = await store.GetLatestAsync(cancellationToken);
                    if (latest is null)
                    {
                        return Results.Problem(
                            title: "No development run yet.",
                            detail: "POST /research/studies/vrp-conditioning/run first.",
                            statusCode: StatusCodes.Status404NotFound);
                    }

                    return Results.Ok(latest);
                }
                catch (NpgsqlException ex)
                {
                    return Results.Problem(
                        title: "Could not read the latest vrp-conditioning development run.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
