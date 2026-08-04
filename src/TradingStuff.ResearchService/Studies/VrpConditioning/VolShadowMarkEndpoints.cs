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
        //
        // The body lives in RunAsync below because there are now TWO callers — this endpoint, which
        // an operator drives by hand, and ShadowMarkTrigger, which fires it once a day so the record
        // does not depend on somebody remembering. Duplicating the body for the scheduler would put
        // two versions of "what a shadow mark IS" in the process, which is exactly the drift the
        // protocol's Phase 1 record cannot survive.
        app.MapPost("/research/shadow-marks/run", async (
            VolResidualBarLoader barLoader,
            ISessionClock sessionClock,
            VolShadowMarkStore store,
            SpyShortVolPlanner planner,
            ILogger<VolShadowMarkStore> logger,
            CancellationToken cancellationToken) =>
        {
            var outcome = await RunAsync(barLoader, sessionClock, store, planner, logger, cancellationToken);

            return outcome.Status switch
            {
                VolShadowMarkRunStatus.NotConfigured => Results.Problem(
                    "No 'trading' connection string is configured.", statusCode: StatusCodes.Status503ServiceUnavailable),
                VolShadowMarkRunStatus.Refused => Results.UnprocessableEntity(
                    new { refused = true, reason = outcome.Refusal }),
                _ => Results.Ok(new { mark = outcome.Mark, plannerIntent = outcome.PlannerIntent }),
            };
        });

        app.MapGet("/research/shadow-marks", async (
            VolShadowMarkStore store, int? limit, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(Math.Clamp(limit ?? 30, 1, 500), cancellationToken)));
    }

    /// <summary>
    /// Computes today's shadow mark, records the planner's intent beside it, and upserts the row.
    /// </summary>
    /// <remarks>
    /// Extracted verbatim from the endpoint lambda when <c>ShadowMarkTrigger</c> arrived; the only
    /// change is that the three terminal outcomes are returned as a value instead of as an
    /// <c>IResult</c>, so a background service can log them and an HTTP caller can render them. No
    /// step, order, window, or refusal condition differs from the hand-driven path — there is
    /// exactly one implementation of a shadow mark and both callers reach it here.
    /// </remarks>
    internal static async Task<VolShadowMarkRunOutcome> RunAsync(
        VolResidualBarLoader barLoader,
        ISessionClock sessionClock,
        VolShadowMarkStore store,
        SpyShortVolPlanner planner,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            return new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.NotConfigured, null, null, null);
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
            return new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Refused, null, null, refusal);
        }

        // What the planner WOULD build right now. "SHADOW" is not an account id that can
        // reach the execution plane - nothing here submits - and a gateway that is down or a
        // market that is closed records as the planner's own named refusal.
        object plannerIntent;
        try
        {
            var plan = await planner.PlanAsync("SHADOW", mark.MarkDate, null, cancellationToken);
            plannerIntent = DescribeIntent(plan);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            plannerIntent = new { planned = false, refusal = $"Gateway unreachable: {ex.Message}" };
        }

        await store.SaveAsync(mark, plannerIntent, cancellationToken);

        return new VolShadowMarkRunOutcome(VolShadowMarkRunStatus.Recorded, mark, plannerIntent, null);
    }

    /// <summary>
    /// The <c>planner_intent</c> document: what the planner would have built, or its named refusal.
    /// </summary>
    /// <remarks>
    /// <c>legQuotes</c> carries the protocol's shadow record item 7 input — the contemporaneous NBBO
    /// of each leg the planner selected — and it has to be written here because it is only knowable
    /// here. A quote reconstructed later measures a different moment, and the planner reads both
    /// legs anyway to compute the credit; this stops that read being discarded. It is null on an
    /// operator-supplied price, where no quote was consulted, which is not the same as no market.
    /// <para>
    /// A static rather than inline in the endpoint so the persisted shape is pinnable by a test —
    /// the item this closes is "the NBBO reaches the record", and a shape only an HTTP round trip
    /// can see is one nothing checks.
    /// </para>
    /// </remarks>
    internal static object DescribeIntent(OrderPlanResult plan) =>
        plan.Order is { } order
            ? new
            {
                planned = true,
                description = order.Description,
                netLimit = order.LimitPrice,
                limitSource = order.LimitPriceSource,
                legQuotes = order.LegQuotes,
            }
            : new { planned = false, refusal = plan.Failure };
}

/// <summary>What one shadow-mark run concluded. Three outcomes, none of which is an empty success.</summary>
internal enum VolShadowMarkRunStatus
{
    /// <summary>No <c>trading</c> connection string, so nothing could be read or written.</summary>
    NotConfigured,

    /// <summary>The forecaster declined to produce a mark and said why. Nothing was persisted.</summary>
    Refused,

    /// <summary>A mark, and the planner's intent or its named refusal, are on the record.</summary>
    Recorded,
}

/// <summary>The result of <see cref="VolShadowMarkEndpoints.RunAsync"/>, for both of its callers.</summary>
internal sealed record VolShadowMarkRunOutcome(
    VolShadowMarkRunStatus Status,
    VrpShadowMark? Mark,
    object? PlannerIntent,
    string? Refusal);
