using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// Maps the option-chain ingestion package's <c>/research/options/*</c> surface. Anonymous, like
/// every other <c>/research/*</c> diagnostic/control surface (see Program.cs) — nothing here reaches
/// TWS, places an order, or touches the paper/live boundary; it only talks to a local ThetaData
/// Terminal and Postgres.
/// </summary>
public static class OptionChainEndpoints
{
    public static void MapOptionChainEndpoints(this WebApplication app)
    {
        // Progress, derived from research.option_chain_requests rather than any in-memory tracker —
        // the same "a restart re-derives state from the checkpoint table" principle
        // GET /research/backfill is built on, including for the SAME reason: every job row is
        // reported, even one with zero request rows, so a job whose planning has not run yet renders
        // at 0% instead of being silently absent.
        app.MapGet("/research/options/status", async (
                OptionChainStore store,
                OptionChainCoordinator coordinator,
                IOptions<OptionChainOptions> optionChainOptions,
                CancellationToken cancellationToken) =>
            {
                var settings = optionChainOptions.Value;

                if (string.IsNullOrWhiteSpace(store.ConnectionString))
                {
                    return Results.Problem(
                        title: "Research persistence is not configured.",
                        detail: "No 'trading' connection string.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    var jobs = await store.GetStatusAsync(settings.MaxAttempts, cancellationToken);
                    return Results.Ok(new OptionChainStatusReport(
                        settings.Enabled, coordinator.OwnerId, settings.MaxAttempts, jobs));
                }
                catch (NpgsqlException ex)
                {
                    return Results.Problem(
                        title: "Could not read option-chain ingestion progress.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        // Declares an ingestion job. interval defaults to '1m' (docs/FOLLOWUP.md §4.5's sizing
        // decision) — a caller has to type nothing to get the safe default. Selecting 'tick'
        // requires BOTH interval=tick AND confirmTick=true; without confirmTick the request is
        // rejected with 400 rather than silently defaulting away the choice OR silently honouring an
        // expensive one. Even a confirmed tick job is created already 'paused' by
        // OptionChainStore.EnsureJobAsync and the automatic coordinator never plans or claims it —
        // see that method's remarks for why bulk tick ingestion is out of scope entirely, not merely
        // gated.
        app.MapPost("/research/options/jobs", async (
                string name,
                string underlying,
                string tradingClass,
                DateOnly from,
                DateOnly to,
                string? interval,
                bool? confirmTick,
                int? priority,
                OptionChainStore store,
                CancellationToken cancellationToken) =>
            {
                if (to < from)
                {
                    return Results.Problem(
                        title: "Invalid date range.",
                        detail: $"'to' ({to:yyyy-MM-dd}) is before 'from' ({from:yyyy-MM-dd}).",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var validation = OptionChainIntervalValidation.Validate(interval, confirmTick);

                if (!validation.IsValid)
                {
                    return Results.Problem(
                        title: validation.ErrorTitle, detail: validation.ErrorDetail,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var resolvedInterval = validation.ResolvedInterval!;

                if (string.IsNullOrWhiteSpace(store.ConnectionString))
                {
                    return Results.Problem(
                        title: "Research persistence is not configured.",
                        detail: "No 'trading' connection string.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    var job = await store.EnsureJobAsync(
                        name, underlying.ToUpperInvariant(), tradingClass.ToUpperInvariant(), from, to,
                        resolvedInterval, priority ?? 0, cancellationToken);

                    return job is null
                        ? Results.Problem(title: "Could not create the job.", statusCode: StatusCodes.Status500InternalServerError)
                        : Results.Ok(job);
                }
                catch (NpgsqlException ex)
                {
                    return Results.Problem(
                        title: "Could not create the option-chain ingestion job.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        // Re-runs the ThetaData capability probes on demand (see OptionChainCapabilityProbes) and
        // persists every finding into research.capability_probes. Read-only against the vendor —
        // list/strike/quote lookups only — never a bulk backfill trigger.
        app.MapPost("/research/options/probes/run", async (
                OptionChainCapabilityProbes probes, CancellationToken cancellationToken) =>
            {
                try
                {
                    var results = await probes.RunAllAsync(cancellationToken);
                    return Results.Ok(results.ToDictionary(r => r.ProbeKey, r => r.Report));
                }
                catch (NpgsqlException ex)
                {
                    return Results.Problem(
                        title: "Could not persist capability probe results.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
