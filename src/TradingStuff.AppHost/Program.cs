var builder = DistributedApplication.CreateBuilder(args);

var devInternalToken = builder.AddParameter(
    "dev-internal-token",
    "dev-internal-token",
    publishValueAsDefault: true);

// TWS speaks a socket protocol, not HTTP, so it cannot be modelled as an AddExternalService URL.
// Ports: 7497 TWS paper, 7496 TWS live, 4002 Gateway paper, 4001 Gateway live.
// TWS must have "Enable ActiveX and Socket Clients" on, with this host under Trusted IPs.
var ibkrHost = builder.AddParameter("ibkr-host", "127.0.0.1", publishValueAsDefault: true);
var ibkrPort = builder.AddParameter("ibkr-port", "7497", publishValueAsDefault: true);
var ibkrClientId = builder.AddParameter("ibkr-client-id", "11", publishValueAsDefault: true);

// 3 = delayed data: no OPRA subscription required, which makes first-run setup work out of the box.
// Set to 1 for live data once the account has market data subscriptions.
var ibkrMarketDataType = builder.AddParameter("ibkr-market-data-type", "3", publishValueAsDefault: true);

// Unattended order submission from the research plane. **Committed default: false, and it stays
// false in every committed file**, exactly like ibkr-allow-live-trading is never set at all.
//
// A parameter rather than a hardcoded literal so that switching it on for a session is a runtime
// decision (`Parameters__paper-automation-enabled=true`) instead of an edit somebody forgets to
// revert — an edited literal is how a default becomes permanent. It is deliberately the ONLY thing
// in this file that gates unattended trading now that the router below is real: everything else
// (a coherent execution plane, a connected DU account, a session, a signal, the per-session cap)
// narrows what automation may do, but this is what decides whether it runs at all.
// Read from configuration with a hardcoded "false" fallback, rather than a literal. A literal-valued
// AddParameter is FIXED at that literal — it never consults configuration — so the only way to switch
// automation on for a session would be to edit this line, and an edit somebody forgets to revert is
// how a default quietly becomes permanent. This way the committed default is off, and enabling it is
// `Parameters__paper-automation-enabled=true` in the environment, which reverts by itself.
var paperAutomationEnabled = builder.AddParameter(
    "paper-automation-enabled",
    builder.Configuration["Parameters:paper-automation-enabled"] ?? "false",
    publishValueAsDefault: true);

// Which quote provider the mesh uses, as ONE value set on BOTH services that care.
//
// It is a parameter rather than two string literals because the two literals diverging is not a
// hypothetical: MarketData__Source was first set only on marketdataservice, and executionservice —
// whose EnsureRouterAndMarketDataAgree reads its OWN configuration, since a guard cannot see another
// process's environment — found it unset and refused to boot with
// "MarketData:Source is '(unset)'". That is the guard working exactly as intended (fail-closed, and
// it cost nothing because it fired at startup), but it is also a standing invitation to "fix" it by
// pasting a second literal that can then drift from the first. One parameter, two consumers.
//
// Note that paper automation checks this differently again, and deliberately: it asks
// MarketDataService which provider it RESOLVED, because two services holding different values for
// this key is precisely the divergence a configuration-string check cannot see.
var marketDataSource = builder.AddParameter(
    "market-data-source", "ibkr-delayed", publishValueAsDefault: true);

// A real Aspire Postgres resource, not a bare container: it produces the connection string that the
// gateway (order-id map, raw recording) and ResearchService (everything derived) actually consume.
var postgresUser = builder.AddParameter("postgres-user", "trading", publishValueAsDefault: true);
var postgresPassword = builder.AddParameter("postgres-password", "trading", publishValueAsDefault: true);

// A named data volume, not the Aspire default (ephemeral, destroyed with the container): the
// `trading` database holds the SPX/SPY backfill (~15h/~23h respectively) and every recorded tick,
// and Track B tick recording is unrecoverable by construction. Losing the volume on an ordinary
// `aspire start`/stop cycle would silently discard both.
//
// WithLifetime(Persistent) as well, not just the volume: without it, Aspire stops and REMOVES the
// container on every app-host shutdown and recreates a fresh one on the next `aspire start`. The
// named volume survives that regardless (a volume outlives the container that mounted it), so data
// is not lost either way — but a fresh container means initdb runs again, Postgres logs the startup
// sequence again, and this AppHost model has other containers (rabbitmq, keycloak) with their own
// state that benefits from the same treatment for local-first iteration speed: a persistent
// container reattaches to what was already running instead of paying container startup cost on
// every `aspire start`. The cost is that `aspire start` no longer guarantees a clean container
// per run — acceptable here because this is a local dev/research box, not a shared or CI
// environment, and the whole point of this host is long-lived state across restarts.
var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithImageTag("17")
    .WithHostPort(5432)
    .WithDataVolume("tradingstuff-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

// POSTGRES_USER=trading makes initdb create a database named "trading"; MigrationRunner also
// creates it defensively if a different server is pointed at.
var tradingDb = postgres.AddDatabase("trading");

var rabbitmq = builder.AddContainer("rabbitmq", "rabbitmq", "4-management")
    .WithEnvironment("RABBITMQ_DEFAULT_USER", "trading")
    .WithEnvironment("RABBITMQ_DEFAULT_PASS", "trading")
    .WithEndpoint(port: 5672, targetPort: 5672, name: "amqp")
    .WithHttpEndpoint(port: 15672, targetPort: 15672, name: "management");

var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.3")
    .WithArgs("start-dev")
    .WithEnvironment("KEYCLOAK_ADMIN", "admin")
    .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", "admin")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http");

// Sole owner of the TWS socket. A TWS connection is single-owner per client id, so no other
// service may connect directly — they all go through this one over internal HTTP.
//
// Deliberately NOT WithExternalHttpEndpoints(): every caller of this endpoint is another project in
// this same AppHost graph (.WithReference(ibkrGateway) below resolves it whether or not it is marked
// external — internal service discovery does not need the flag). Marking it external buys nothing
// for that internal traffic and costs two things: it puts the order-placement surface one click away
// in the Aspire dashboard's resource list, and it is the exact flag that becomes real public ingress
// the moment this AppHost is ever published to a target that honours it (Azure Container Apps, App
// Service). On this host `aspire start` binds every project's Kestrel endpoint to loopback regardless
// of this flag (verified: `ss -tlnp` shows the gateway's port bound to 127.0.0.1/[::1] only, external
// or not) — but that is a property of today's local orchestrator, not a guarantee this AppHost can
// rely on, and it does nothing about the publish-time exposure. Least privilege: the one component
// that owns the TWS socket and places real orders gets no more reach than it needs.
var ibkrGateway = builder.AddProject(
        "ibkrgateway",
        "../TradingStuff.IbkrGateway/TradingStuff.IbkrGateway.csproj")
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IBKR__Host", ibkrHost)
    .WithEnvironment("IBKR__Port", ibkrPort)
    .WithEnvironment("IBKR__ClientId", ibkrClientId)
    .WithEnvironment("IBKR__MarketDataType", ibkrMarketDataType)
    // The order-id map. No WaitFor(postgres): the gateway starts and serves market data without a
    // database; only order-mapping persistence degrades (loudly) when Postgres is down.
    .WithReference(tradingDb);
    // IBKR__AllowLiveTrading is deliberately not set here. It defaults to false, so a non-DU
    // account cannot trade; enabling live trading must be a conscious, per-environment decision.

var marketData = builder.AddProject(
        "marketdataservice",
        "../TradingStuff.MarketDataService/TradingStuff.MarketDataService.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(ibkrGateway)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IbkrGateway__BaseUrl", ibkrGateway.GetEndpoint("http"))
    // Real quotes from TWS through the gateway. This MUST stay one of the recognised IBKR values for
    // as long as Execution__Router is "ibkr" below: ExecutionService's EnsureRouterAndMarketDataAgree
    // refuses to BOOT on the combination of a real router and an unrecognised quote source, which is
    // the fail-closed answer to the 2026-08-01 incident where a 10-lot SPY vertical was approved
    // against invented bid 27.34 / ask 28.46 on a Saturday.
    //
    // The parameter defaults to "ibkr-delayed" rather than "ibkr-live" deliberately, and it is a
    // label that must match reality rather than ambition: the actual regime TWS serves is driven by
    // ibkr-market-data-type above, which is 3 (delayed) so that first-run setup works without an OPRA
    // subscription. Claiming "ibkr-live" while the gateway requests delayed data would put a name on
    // the feed that the feed does not have. The two move TOGETHER — see docs/FOLLOWUP.md §3.6 before
    // trusting a price this mesh computes.
    .WithEnvironment("MarketData__Source", marketDataSource);
    // No WaitFor(ibkrGateway) on purpose: the gateway reports unhealthy whenever TWS is down, and
    // waiting on health would stop the whole mesh from starting just because TWS is closed.

var risk = builder.AddProject(
        "riskservice",
        "../TradingStuff.RiskService/TradingStuff.RiskService.csproj")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WaitFor(postgres);

var execution = builder.AddProject(
        "executionservice",
        "../TradingStuff.ExecutionService/TradingStuff.ExecutionService.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(risk)
    .WithReference(marketData)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithReference(ibkrGateway)
    .WithEnvironment("RiskService__BaseUrl", risk.GetEndpoint("http"))
    .WithEnvironment("MarketDataService__BaseUrl", marketData.GetEndpoint("http"))
    .WithEnvironment("IbkrGateway__BaseUrl", ibkrGateway.GetEndpoint("http"))
    // ---- the real paper-account path, on by default -------------------------------------------
    // Approved orders are transmitted to the DU paper account through the gateway, and risk evaluates
    // them against that account's real buying power, daily P&L and position Greeks. This is what the
    // paper account is for (CLAUDE.md: "On a verified DU account, exercise anything you build").
    //
    // These three settings — router, portfolio source, and MarketData__Source above — are ONE
    // decision, not three. Each degrades to its safe value independently on an unrecognised string,
    // which is correct alone and wrong together: docs/LESSONS.md §9. ExecutionService refuses to boot
    // unless all three agree (EnsureRouterAndPortfolioAgree + EnsureRouterAndMarketDataAgree), and
    // paper automation independently refuses to arm unless it can MEASURE that they agree — it asks
    // ExecutionService which router and portfolio provider it resolved and MarketDataService which
    // quote provider it resolved, rather than reading its own copy of these variables.
    //
    // What still stands between this and an unattended order:
    //   - IBKR__AllowLiveTrading is unset (defaults false), so a non-DU account cannot trade at all;
    //   - the gateway's DU-prefix check, and paper automation's own re-check of it;
    //   - PaperAutomation__Enabled is set from the paper-automation-enabled parameter, whose
    //     committed default is "false" — with the path now real, THAT is the gate that decides
    //     whether anything fires unattended.
    .WithEnvironment("Execution__Router", "ibkr")
    .WithEnvironment("Portfolio__Source", "ibkr")
    // The third of the three, and it must be set HERE as well as on marketdataservice:
    // EnsureRouterAndMarketDataAgree reads this process's own configuration, because no guard can see
    // another process's environment. Omitting it is not a silent hole — this service refuses to boot
    // — but it is a refusal to boot, so it belongs beside the two settings it has to agree with.
    .WithEnvironment("MarketData__Source", marketDataSource)
    .WaitFor(risk)
    .WaitFor(marketData)
    .WaitFor(rabbitmq)
    .WaitFor(postgres)
    .WaitFor(keycloak);

// Research plane: schema migrations, capability registry, and (from Phase 1) recorder
// orchestration, features, labels, and studies. Owns ALL schema, including gateway.* tables.
builder.AddProject(
        "researchservice",
        "../TradingStuff.ResearchService/TradingStuff.ResearchService.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(ibkrGateway)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IbkrGateway__BaseUrl", ibkrGateway.GetEndpoint("http"))
    // The historical drain. Defaults to false in BackfillOptions and was never set here, so the
    // coordinator has never once run and research.bars has stayed empty — which is why no study
    // can be executed. It is safe to leave on: every slice is idempotent (the request row IS the
    // checkpoint), the pacing governor owns the TWS limits, and a rerun adds zero rows.
    .WithEnvironment("Backfill__Enabled", "true")
    // Paper automation talks to these two over HTTP and to nothing else that can place an order. The
    // research plane owns the signal; ExecutionService owns the order and runs the whole spine on it.
    //
    // Defaults to "false" (see the parameter's declaration). Switching it on is a per-environment
    // decision, and it still buys nothing on its own: automation independently refuses to arm unless
    // it can MEASURE that ExecutionService resolved the IBKR router and portfolio provider and that
    // MarketDataService resolved a real quote provider.
    .WithEnvironment("PaperAutomation__Enabled", paperAutomationEnabled)
    .WithReference(execution)
    .WithReference(marketData)
    .WithEnvironment("ExecutionService__BaseUrl", execution.GetEndpoint("http"))
    .WithEnvironment("MarketDataService__BaseUrl", marketData.GetEndpoint("http"))
    .WithReference(tradingDb)
    .WaitFor(postgres);
    // No WaitFor(ibkrGateway), for the same reason marketdataservice skips it: the gateway reports
    // unhealthy whenever TWS is down, and the recorder must still start, sit idle, and retry.

builder.AddProject(
        "auditdashboard",
        "../TradingStuff.AuditDashboard/TradingStuff.AuditDashboard.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(execution)
    .WithReference(risk)
    .WithReference(marketData)
    .WithReference(ibkrGateway)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("Dashboard__ExecutionBaseUrl", execution.GetEndpoint("http"))
    .WithEnvironment("Dashboard__RiskBaseUrl", risk.GetEndpoint("http"))
    .WithEnvironment("Dashboard__MarketDataBaseUrl", marketData.GetEndpoint("http"))
    .WithEnvironment("Dashboard__IbkrGatewayBaseUrl", ibkrGateway.GetEndpoint("http"))
    .WithEnvironment("Dashboard__RabbitManagementUrl", rabbitmq.GetEndpoint("management"))
    .WithEnvironment("Dashboard__KeycloakUrl", keycloak.GetEndpoint("http"))
    .WaitFor(execution)
    .WaitFor(risk)
    .WaitFor(marketData)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);

builder.Build().Run();
