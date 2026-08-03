namespace TradingStuff.ResearchService.Studies.TermStructure;

/// <summary>
/// The A4 term-structure series' <c>/research/term-structure/*</c> surface. Anonymous like the
/// rest of <c>/research/*</c> (see Program.cs): reads Postgres, never touches TWS or an order
/// path. Rebuilding a range is idempotent by design — migration 022's remarks.
/// </summary>
public static class TermStructureEndpoints
{
    public static void MapTermStructureEndpoints(this WebApplication app)
    {
        app.MapPost("/research/term-structure/build", async (
                DateOnly from,
                DateOnly to,
                TermStructureSeriesBuilder builder,
                CancellationToken cancellationToken) =>
            {
                if (to < from)
                {
                    return Results.Problem(
                        title: "Invalid date range.",
                        detail: $"'to' ({to:yyyy-MM-dd}) is before 'from' ({from:yyyy-MM-dd}).",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    return Results.Ok(await builder.BuildAsync(from, to, cancellationToken));
                }
                catch (InvalidOperationException ex)
                {
                    // Missing connection string or an empty rate table: configuration facts the
                    // caller must fix, not transient faults.
                    return Results.Problem(
                        title: "The term-structure build cannot run.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        app.MapGet("/research/term-structure", async (
                DateOnly? from,
                DateOnly? to,
                TermStructureStore store,
                CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(
                from ?? DateOnly.MinValue, to ?? DateOnly.MaxValue, cancellationToken)));
    }
}
