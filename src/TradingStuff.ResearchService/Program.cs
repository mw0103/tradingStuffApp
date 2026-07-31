using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Backfill;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Recording;
using TradingStuff.ResearchService.Universe;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MigrationRunner>());
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

builder.Services.AddSingleton<NodeSelector>();
builder.Services.AddSingleton<CoverageMonitor>();
builder.Services.AddSingleton<RecorderOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecorderOrchestrator>());

builder.Services.Configure<BackfillOptions>(builder.Configuration.GetSection("Backfill"));
builder.Services.AddSingleton<BackfillStore>();
builder.Services.AddSingleton<BackfillCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackfillCoordinator>());

// Seeds the ES job's per-contract request rows (BackfillJobCatalog deliberately excludes ES — a
// CONTFUT cannot page a past endDateTime, so it must be walked contract-by-contract); the
// coordinator above drains whatever this seeds with no change of its own, since a request row's
// contract is rebuilt from research.instruments plus the row's own con_id.
builder.Services.AddSingleton<EsContractWalker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EsContractWalker>());

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
// Nothing under this prefix mutates state; if that ever changes, that endpoint must add
// .RequireAuthorization() individually rather than this comment being quietly wrong.

app.MapGet("/research/status", (MigrationRunner migrations) =>
    {
        var state = migrations.State;

        return Results.Ok(new
        {
            migrations = new { state.Status, state.Applied, state.Error },
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

app.MapGet("/research/nodes", async (NodeSelector nodeSelector, CancellationToken cancellationToken) =>
    Results.Ok(await nodeSelector.GetCurrentAssignmentsAsync(cancellationToken)));

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

app.MapDefaultEndpoints();

app.Run();
