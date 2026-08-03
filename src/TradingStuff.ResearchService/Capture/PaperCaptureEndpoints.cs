namespace TradingStuff.ResearchService.Capture;

/// <summary>
/// Read-only visibility over the raw capture tables. Nothing here captures, re-captures, or deletes:
/// the tables are append-only and <see cref="PaperCaptureService"/> is their only writer.
/// </summary>
/// <remarks>
/// It exists because a capture layer nobody can look at is indistinguishable from one that is not
/// running. Refusal rows are returned alongside snapshots for the same reason they are written —
/// an evening the gateway was down must be visible as an evening the gateway was down.
/// </remarks>
public static class PaperCaptureEndpoints
{
    public static void MapPaperCaptureEndpoints(this WebApplication app)
    {
        app.MapGet("/research/paper-capture", async (
            PaperCaptureStore store, int? limit, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(store.ConnectionString))
            {
                return Results.Problem(
                    title: "Research persistence is not configured.",
                    detail: "No 'trading' connection string.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(await store.ListSnapshotsAsync(
                Math.Clamp(limit ?? 30, 1, 500), cancellationToken));
        });

        app.MapGet("/research/paper-capture/fills", async (
            PaperCaptureStore store, DateOnly? tradingDate, int? limit, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(store.ConnectionString))
            {
                return Results.Problem(
                    title: "Research persistence is not configured.",
                    detail: "No 'trading' connection string.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(await store.ListFillsAsync(
                tradingDate, Math.Clamp(limit ?? 200, 1, 2000), cancellationToken));
        });
    }
}
