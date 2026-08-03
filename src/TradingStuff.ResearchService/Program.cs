using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Backfill;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.OptionChains;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Recording;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.ResearchService.Universe;
using TradingStuff.ServiceDefaults;
using TradingStuff.Volatility.ThetaData;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// These two hosted services start back to back and then run CONCURRENTLY — a BackgroundService whose
// ExecuteAsync awaits does not block the ones registered after it — so registration order buys
// nothing here and must not be mistaken for sequencing. The maintainer used to race migrations on a
// cold start, fail its whole first sweep against tables that did not exist yet, and drop into its
// 1-minute failure retry; by the time the second sweep ran, the recorder had already put ticks for
// TODAY into the DEFAULT partition, which Postgres then permanently refuses to give a real partition.
//
// Do NOT "fix" that by making the maintainer wait on this MigrationRunner instance. The recorder that
// writes those ticks lives in the IbkrGateway process, which is deliberately designed to outlive this
// one, so any in-process ordering rule is only true when the two happen to start together — the case
// that was never the problem. The guarantee lives in the schema instead (migration 012 creates a
// 14-day partition horizon at migration time), and the maintainer now gates itself on the tables
// existing, which is a fact about the database rather than about this process's startup.
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MigrationRunner>());

// A service with no schema must not answer /health with 200. MigrationHealthCheck existed, was
// tested, and was registered nowhere — so it reported nothing at all, which is the same defect it
// was written to fix, one level up.
builder.Services.AddHealthChecks().AddCheck<MigrationHealthCheck>("migrations");

// Defence in depth, and it only makes sense WITH the health check above. The default
// BackgroundServiceExceptionBehavior is StopHost, so any single background service faulting takes
// the whole host down with it — PartitionMaintainer included, whose death is not benign (a row
// landing in a DEFAULT partition permanently blocks that date's real partition). Ignore keeps the
// rest of the host alive; the health check is what stops "alive" being mistaken for "working".
builder.Services.Configure<HostOptions>(
    options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddSingleton<PartitionMaintainer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PartitionMaintainer>());

// Talks to the gateway over HTTP, never a project reference — the gateway is a separate process
// and the sole TWS socket owner. Retries disabled: a retried lease grant would double-lease a
// conId's market-data line rather than merely repeating a safe read.
// The attempt timeout must exceed the gateway's own IBKR:HistoricalRequestTimeoutSeconds (60s by
// default): a multi-month bar request legitimately takes tens of seconds, and abandoning it from
// this side would burn the paced request slot, misreport a live request as a client timeout, and
// leave TWS still delivering into a socket nobody is waiting on. Kept under HttpClient's own 100s
// default so the resilience pipeline, not the raw client, is what expires first.
builder.Services.AddHttpClient<IbkrGatewayClient>((sp, http) =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        ServiceClientConfiguration.ConfigureInternalClient(
            http, configuration, "IbkrGateway:BaseUrl", "http://localhost:5100");
    })
    .DisableAutomaticRetries(TimeSpan.FromSeconds(80));

// A second typed client to the same gateway, for the one caller that must see HOW a chain window was
// cut rather than only what came back. Kept apart from IbkrGatewayClient because a flattened contract
// list cannot say "this window is not centred on spot" — IbkrGatewayClient used to have exactly that
// flattening method and it let a chain the gateway could not centre on spot read as a healthy one all
// the way into node_assignments. That method is gone now; see OptionChainClient.
builder.Services.AddHttpClient<OptionChainClient>((sp, http) =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        ServiceClientConfiguration.ConfigureInternalClient(
            http, configuration, "IbkrGateway:BaseUrl", "http://localhost:5100");
    })
    .DisableAutomaticRetries(TimeSpan.FromSeconds(80));

// ---- session calendar: the platform's single authority for "what data was expected when" --------
// Everything is a singleton because the generator memoises a year of sessions per calendar on first
// touch and is pure thereafter; a scoped instance would rebuild that cache per request for no reason.
// The calendar dataset is registered explicitly rather than left to SessionGenerator's parameterless
// convenience constructor, so the one place the embedded JSON enters the process is visible here.
// SessionClock is registered under both its own type and ISessionClock: the interface is the doctrine
// (the only type permitted to convert timezones), and resolving it must not hand out a second clock
// with a second cache that could answer differently.
builder.Services.AddSingleton(ExchangeCalendarSet.Embedded);
builder.Services.AddSingleton<SessionGenerator>();
builder.Services.AddSingleton<SessionClock>();
builder.Services.AddSingleton<ISessionClock>(sp => sp.GetRequiredService<SessionClock>());
builder.Services.AddSingleton<SessionCalendarService>();

// A hosted service, and the justification is on SessionCalendarSynchronizer itself: research.sessions
// has no other writer, CoverageMonitor takes its denominator from it, and an unwritten session table
// does not fail loudly — it shrinks denominators, which makes coverage read HIGHER. The timer (twice
// a day) exists because the synced range ends at today + N days and "today" moves under a process
// that runs for weeks; a pass with nothing to do writes no rows.
builder.Services.Configure<SessionCalendarOptions>(builder.Configuration.GetSection("Sessions"));
builder.Services.AddSingleton<SessionCalendarSynchronizer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionCalendarSynchronizer>());

builder.Services.AddSingleton<NodeSelector>();
builder.Services.Configure<CoverageOptions>(builder.Configuration.GetSection("Coverage"));
builder.Services.AddSingleton<CoverageMonitor>();
builder.Services.AddSingleton<RecorderOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecorderOrchestrator>());

builder.Services.Configure<BackfillOptions>(builder.Configuration.GetSection("Backfill"));
builder.Services.AddSingleton<BackfillStore>();
builder.Services.AddSingleton<BackfillCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackfillCoordinator>());

// Gap detection reads research.backfill_requests/research.bars through BackfillStore and sessions
// through ISessionClock; it has no state of its own and no hosted loop — it is a report computed
// on demand, the same shape as CoverageMonitor.
builder.Services.Configure<GapOptions>(builder.Configuration.GetSection("Gaps"));
builder.Services.AddSingleton<GapDetector>();

// Seeds the ES job's per-contract request rows (BackfillJobCatalog deliberately excludes ES — a
// CONTFUT cannot page a past endDateTime, so it must be walked contract-by-contract); the
// coordinator above drains whatever this seeds with no change of its own, since a request row's
// contract is rebuilt from research.instruments plus the row's own con_id.
builder.Services.AddSingleton<EsContractWalker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EsContractWalker>());

// ---- Phase 9: ThetaData historical option-chain ingestion ---------------------------------------
// A local Theta Terminal, not TWS — an entirely separate process on 127.0.0.1:25503 that proxies to
// ThetaData and holds its own credentials, so requests from here carry none of their own (see
// ThetaDataClient's remarks). Bound from "ThetaData" so BaseAddress/Timeout/StrikeDivisor/
// SnapshotTimeOfDay are all operator-configurable without a code change; defaults match the client's
// own (25503, 10-minute timeout, divisor 1 for v3, 15:45 snapshot).
builder.Services.Configure<ThetaDataOptions>(builder.Configuration.GetSection("ThetaData"));
builder.Services.AddSingleton(sp => new ThetaDataClient(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ThetaDataOptions>>().Value));

builder.Services.Configure<OptionChainOptions>(builder.Configuration.GetSection("OptionChains"));
builder.Services.AddSingleton<OptionChainStore>();
builder.Services.AddSingleton<OptionChainCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OptionChainCoordinator>());
builder.Services.AddSingleton<OptionChainCapabilityProbes>();

// The volatility-forecast-residual study's DEVELOPMENT run (docs/research/volatility-forecast-residual-study.md
// is the pre-registration; this is explicitly not the registered scripted run). Read-only over
// research.bars, like CoverageMonitor/GapDetector above — no hosted loop, computed on request.
builder.Services.AddSingleton<VolResidualBarLoader>();
builder.Services.AddSingleton<VolResidualStudyRunner>();
builder.Services.AddSingleton<VolResidualStudyStore>();

// The companion VRP-conditioning study: the 21-trading-day version of the same pipeline, answering
// the question the parent study's one-session horizon cannot. Shares VolResidualBarLoader above.
builder.Services.AddSingleton<VrpConditioningStudyRunner>();
builder.Services.AddSingleton<VrpConditioningStudyStore>();

// ---- paper automation ---------------------------------------------------------------------------
// Off unless PaperAutomation:Enabled is the exact string "true", and even then it arms only if
// ExecutionService resolved the IBKR router AND the IBKR portfolio provider AND MarketDataService
// resolved a real quote provider AND the gateway is connected on a DU account — see
// PaperAutomationArming. Nothing here is set in AppHost.
//
// The research plane owns the SIGNAL and nothing else. Orders go out over HTTP to ExecutionService's
// existing POST /orders, which runs validate -> quote -> portfolio -> risk -> route -> persist ->
// publish. There is no path from this service to the IBKR gateway's order surface and none to
// placeOrder.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PaperAutomationOptions>(builder.Configuration.GetSection("PaperAutomation"));

// Retries stripped for the reason on DisableAutomaticRetries: POST /orders is not idempotent, and a
// retried order reaches the broker twice under two broker order ids while the caller sees only the
// last attempt's outcome. Observed live on 2026-07-31. 60s comfortably exceeds ExecutionService's own
// wait on the gateway's 20s order settle timeout.
builder.Services.AddHttpClient<ExecutionServiceClient>((sp, http) =>
    {
        ServiceClientConfiguration.ConfigureInternalClient(
            http, sp.GetRequiredService<IConfiguration>(), "ExecutionService:BaseUrl", "http://localhost:5000");
    })
    .DisableAutomaticRetries(TimeSpan.FromSeconds(60));

// Quote reads ARE safe to retry, but they are left on the default resilience pipeline rather than
// given a bespoke one: this client prices the order that the client above submits, and the two must
// not drift apart in configuration for no reason.
builder.Services.AddHttpClient<MarketDataServiceClient>((sp, http) =>
{
    ServiceClientConfiguration.ConfigureInternalClient(
        http, sp.GetRequiredService<IConfiguration>(), "MarketDataService:BaseUrl", "http://localhost:5001");
});

builder.Services.AddSingleton<IPaperAutomationStore, PaperAutomationStore>();
builder.Services.AddSingleton<IPaperRunDecisionStore, PaperRunDecisionStore>();
builder.Services.AddSingleton<SpyVerticalPlanner>();
builder.Services.AddSingleton<SpyShortVolPlanner>();

// The exit side of the same loop. It selects nothing — it reverses whatever the account reports open
// and prices it to cross — so it needs the quote client and the options, and no chain access at all.
builder.Services.AddSingleton<SpyExitPlanner>();
builder.Services.AddSingleton<TradingStuff.ResearchService.Studies.VrpConditioning.VolShadowMarkStore>();
builder.Services.AddSingleton<TradingStuff.ResearchService.Studies.TermStructure.TermStructureStore>();
builder.Services.AddSingleton<TradingStuff.ResearchService.Studies.TermStructure.TermStructureSeriesBuilder>();
// The signal source, selected by PaperAutomation:Signal. 'vol-residual' stays the default and keeps
// refusing every path; 'constant-exposure' asks for the protocol's mandated constant one-vega
// position and only while an operator-signed research.paper_run_decisions row stands.
//
// Resolved once, here, because the loop asks the signal a question rather than choosing one — so an
// unrecognised value cannot refuse at evaluation time the way an unknown Structure does. It lands on
// the refusing signal instead, and says so at Critical: a typo must not read as a deliberate choice,
// and it must not silently arm anything either.
builder.Services.AddSingleton<IAutomationSignal>(sp =>
{
    var configured = sp.GetRequiredService<IOptions<PaperAutomationOptions>>().Value.Signal;
    var selected = PaperAutomationOptions.Signals.Select(configured, out var recognised);
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TradingStuff.ResearchService.Automation.Signal");

    if (!recognised)
    {
        logger.LogCritical(
            "PaperAutomation:Signal is '{Configured}', which this build does not recognise. Falling back to " +
            "'{Fallback}', which refuses every path. Known values: '{VolResidual}', '{ConstantExposure}'.",
            configured, selected,
            PaperAutomationOptions.Signals.VolResidual,
            PaperAutomationOptions.Signals.ConstantExposure);
    }

    if (selected != PaperAutomationOptions.Signals.ConstantExposure)
    {
        return ActivatorUtilities.CreateInstance<VolResidualSignal>(sp);
    }

    logger.LogWarning(
        "PaperAutomation:Signal is 'constant-exposure': the loop will ask for a position whenever an unrevoked " +
        "research.paper_run_decisions row authorizes the paper run. No forecast is consulted — the protocol's " +
        "exposure is constant by construction. Arming, the DU-only check, the session and the cap all still apply.");

    return ActivatorUtilities.CreateInstance<ConstantExposureSignal>(sp);
});

builder.Services.AddSingleton<PaperAutomationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PaperAutomationService>());

var app = builder.Build();

var spaFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
    Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

// Static files MUST run before routing, and routing is therefore declared explicitly here.
// WebApplication otherwise inserts UseRouting at the very top of the pipeline, ahead of all user
// middleware — and StaticFileMiddleware deliberately does nothing when an endpoint has already been
// selected. The /ui/{**slug} SPA fallback below matches every path under /ui, so with the implicit
// ordering the real files were never reachable and every asset 404'd while the service looked
// perfectly healthy. Found by curling the running app; nothing about the build reports it.
app.UseStaticFiles(new StaticFileOptions { RequestPath = "/ui", FileProvider = spaFiles });

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Fallback to index.html for client-side routing, scoped to /ui so the API routes work. No
// RequestPath here, unlike the middleware above: the fallback rewrites the path to the file name
// itself, which can never carry the /ui prefix, so setting one makes the lookup miss every time.
app.MapFallbackToFile("/ui/{**slug}", "index.html", new StaticFileOptions { FileProvider = spaFiles });

// Redirect /ui or /ui/ to /ui/coverage
app.MapGet("/ui", () => Results.Redirect("/ui/coverage", permanent: false));

// Everything under /research/* is a read-only diagnostic surface for this local-first operator UI
// (coverage, capability registry, migration/node status) and is deliberately anonymous, matching
// AuditDashboard's existing `/` — the same posture the roadmap specifies for the React research UI.
//
// Two exceptions now exist, both under /research/automation and both carrying .RequireAuthorization()
// individually exactly as this comment has always required: POST .../resume re-arms automation, and
// POST .../manual-order submits a real order to the paper account. The kill switch beside them is
// deliberately left anonymous — it only ever STOPS trading, and a kill switch behind a credential is
// one that does not get pressed. See PaperAutomationEndpoints for the full reasoning.

app.MapGet("/research/status", (MigrationRunner migrations) =>
    {
        var state = migrations.State;

        return Results.Ok(new
        {
            // UnverifiedBaselines is projected deliberately: a checksum backfilled from whatever the
            // assembly embeds today is an ASSUMPTION about what actually ran, not a measurement of
            // it, and a count that appears nowhere an operator looks is the same as no count. See
            // MigrationRunner's provenance remarks and migration 013.
            migrations = new { state.Status, state.Applied, state.Error, state.UnverifiedBaselines },
        });
    });

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
    });

// The default window stays a trailing 24 hours, but it means something different now that the
// denominator comes from research.sessions rather than from (to - from): a trailing day ending
// mid-RTH spans one whole Cboe GTH session plus the two RTH halves either side of it, which is
// exactly one session-day of expected minutes. Under the old wall-clock arithmetic the same window
// asked for 1,440 minutes of a market open for roughly 1,185 of them, so a flawless recording day
// could not clear the 95% acceptance threshold at all.
app.MapGet("/research/coverage", async (
        DateTimeOffset? from,
        DateTimeOffset? to,
        CoverageMonitor coverage,
        CancellationToken cancellationToken) =>
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-24);

        return Results.Ok(await coverage.GetCoverageAsync(start, end, cancellationToken));
    });

// The registered grid, not just the assigned rows: what each node was selected FOR, what it is bound
// to, and the deviation between them. This used to report (node_id, con_id) and nothing else, which
// is why nine roles per DTE bucket sharing four contracts was invisible from outside the database —
// every one of those conIds was a live contract with ~100% coverage. Reading `assigned` against
// `distinctConIds` now answers "is the grid actually 54 contracts?" in one glance.
app.MapGet("/research/nodes", async (NodeSelector nodeSelector, CancellationToken cancellationToken) =>
    Results.Ok(await nodeSelector.GetGridReportAsync(cancellationToken)));

// The session calendar, side by side: what the generator produces for a date range, and what
// research.sessions actually holds for it. Both halves are reported because this is the reference
// data every downstream artifact is validated AGAINST — coverage denominators today, snapshot and
// feature cutoffs later — so a defect in it is invisible by construction, and the only thing that
// makes it visible is a human being able to read the boundaries against a published exchange
// calendar. Hence the per-row generated/persisted pair, the duration in minutes (a half day is
// obvious at a glance), and the dataset's own revision and knownGoodThrough horizon.
//
// Read-only in the strict sense: it never syncs, even when it reports rows as missing. Regenerating
// can RETIRE a published session row, which is not something a page refresh should be able to do —
// SessionCalendarSynchronizer owns that on a schedule the logs record.
app.MapGet("/research/sessions", async (
        string? calendar,
        DateOnly? from,
        DateOnly? to,
        SessionCalendarService sessions,
        SessionCalendarSynchronizer synchronizer,
        SessionClock clock,
        CancellationToken cancellationToken) =>
    {
        // A week either side of today: enough to see the session that is running, the one that just
        // ran, and the one coming up, without an operator having to type dates to get a useful answer.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? today.AddDays(-7);
        var end = to ?? today.AddDays(7);

        if (end < start)
        {
            return Results.Problem(
                title: "Invalid date range.",
                detail: $"'to' ({end:yyyy-MM-dd}) is before 'from' ({start:yyyy-MM-dd}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Bounded because the response is one row per session per calendar and the full dataset runs
        // to tens of thousands; a year at a time is plenty to audit a calendar by hand.
        if (end.DayNumber - start.DayNumber > 366)
        {
            return Results.Problem(
                title: "Date range too wide.",
                detail: "At most 366 days can be described in one request.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var requested = calendar is { Length: > 0 }
            ? calendar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [.. clock.Calendars];

        if (requested.Except(clock.Calendars, StringComparer.Ordinal).ToArray() is { Length: > 0 } unknown)
        {
            return Results.Problem(
                title: "Unknown calendar.",
                detail: $"'{string.Join("', '", unknown)}' is not in the calendar dataset. " +
                        $"Known calendars: {string.Join(", ", clock.Calendars)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return Results.Ok(new
            {
                calendar = await sessions.DescribeAsync(requested, start, end, cancellationToken),
                sync = synchronizer.State,
            });
        }
        catch (NpgsqlException ex)
        {
            return Results.Problem(
                title: "Could not read the session calendar.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

// Backfill progress, derived from research.backfill_requests rather than from any in-memory
// tracker — the same "a restart re-derives state from the checkpoint table" principle the table
// exists for, which also means this endpoint answers correctly while the coordinator is disabled,
// stopped, or running in another process.
//
// EVERY job row is reported, including one with no request rows at all: that job renders as 0
// slices and 0% rather than being omitted. A query that cannot emit a row for the absent case makes
// absence read as health, which was the shared root cause of three of the Phase 1 review's eight
// confirmed defects. `enabled` is reported for the same reason — a coordinator that is switched off
// must not look like a coordinator with nothing left to do.
app.MapGet("/research/backfill", async (
        BackfillStore store,
        BackfillCoordinator coordinator,
        Microsoft.Extensions.Options.IOptions<BackfillOptions> backfillOptions,
        CancellationToken cancellationToken) =>
    {
        var settings = backfillOptions.Value;

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

            return Results.Ok(new BackfillStatusReport(
                settings.Enabled, coordinator.OwnerId, settings.MaxAttempts, jobs));
        }
        catch (NpgsqlException ex)
        {
            return Results.Problem(
                title: "Could not read backfill progress.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

// Compares each backfill job's OWN declared range — expected sessions × expected bars, from the
// session calendar — against what actually landed, and reports the mismatch as labelled GapRange
// entries. "Empty or explained" is the roadmap's own acceptance criterion for this (Phase 2 item g).
//
// Every job in research.backfill_jobs is reported, including one this detector cannot check (no
// resolved conId, an unmapped instrument, an unsupported bar size) — it appears with a CheckStatus
// other than "checked" and an explanation, never silently omitted. See GapDetector's remarks for
// exactly where the negative claim ("nothing is silently missing") is measured.
app.MapGet("/research/backfill/gaps", async (
        long? jobId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        GapDetector detector,
        BackfillStore store,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            return Results.Problem(
                title: "Research persistence is not configured.",
                detail: "No 'trading' connection string.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var report = await detector.GetReportAsync(jobId, from, to, cancellationToken);

            if (jobId is { } id && report.Jobs.Count == 0)
            {
                return Results.Problem(
                    title: "Unknown job.",
                    detail: $"No backfill job with id {id}.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(report);
        }
        catch (NpgsqlException ex)
        {
            return Results.Problem(
                title: "Could not compute the gap report.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

app.MapOptionChainEndpoints();

app.MapVolResidualStudyEndpoints();

app.MapVrpConditioningStudyEndpoints();
app.MapShadowMarkEndpoints();
TradingStuff.ResearchService.Studies.TermStructure.TermStructureEndpoints.MapTermStructureEndpoints(app);

app.MapPaperAutomationEndpoints();
app.MapPaperRunDecisionEndpoints();

app.MapDefaultEndpoints();

app.Run();
