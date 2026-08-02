using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.ResearchContracts;
using TradingStuff.Volatility;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// The paper-run protocol's Phase 1 surface: compute and persist today's shadow mark, and list
/// what has accumulated. No endpoint here places, plans-for-submission, or arms anything.
/// </summary>
public static class VolShadowMarkEndpoints
{
    public static void MapShadowMarkEndpoints(this WebApplication app)
    {
        // Computes the shadow mark for the latest complete SPX session in research.bars, asks the
        // planner what it WOULD build (recorded, never submitted), and upserts the row.
        app.MapPost("/research/shadow-marks/run", async (
            VolResidualBarLoader barLoader,
            ISessionClock sessionClock,
            VolShadowMarkStore store,
            SpyShortVolPlanner planner,
            ILogger<VolShadowMarkStore> logger,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(store.ConnectionString))
            {
                return Results.Problem(
                    "No 'trading' connection string is configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Trailing window: enough for the feature warm-up, the fold runner's training floor,
            // and a stable set of quintile breakpoints. Three years is comfortable and cheap.
            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var from = today.AddYears(-3);

            var spxBars = await barLoader.LoadSpxOneMinuteBarsAsync(from, today, cancellationToken);
            var vix = await barLoader.LoadVixDailyClosesAsync(from, today, cancellationToken);

            var spxDays = VolatilityPresets.BuildSpxStudyTarget(
                sessionClock, HistoricalBarAdapter.ToIntradayBars(spxBars).ToList());

            var (mark, refusal) = VrpShadowForecaster.Compute(spxDays, vix);

            if (mark is null)
            {
                // A refusal is a first-class answer, logged and returned - never an empty 200.
                logger.LogWarning("Shadow mark refused: {Refusal}", refusal);
                return Results.UnprocessableEntity(new { refused = true, reason = refusal });
            }

            // What the planner WOULD build right now. "SHADOW" is not an account id that can
            // reach the execution plane - nothing here submits - and a gateway that is down or a
            // market that is closed records as the planner's own named refusal.
            object plannerIntent;
            try
            {
                var plan = await planner.PlanAsync("SHADOW", mark.MarkDate, null, cancellationToken);
                plannerIntent = plan.Order is { } order
                    ? new { planned = true, description = order.Description, netLimit = order.LimitPrice, limitSource = order.LimitPriceSource }
                    : new { planned = false, refusal = plan.Failure };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                plannerIntent = new { planned = false, refusal = $"Gateway unreachable: {ex.Message}" };
            }

            await store.SaveAsync(mark, plannerIntent, cancellationToken);

            return Results.Ok(new { mark, plannerIntent });
        });

        app.MapGet("/research/shadow-marks", async (
            VolShadowMarkStore store, int? limit, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(Math.Clamp(limit ?? 30, 1, 500), cancellationToken)));
    }
}
