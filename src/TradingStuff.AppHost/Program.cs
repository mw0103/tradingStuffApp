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

// The other two thirds of the Phase 2 arming decision, and parameters for exactly the reason
// paper-automation-enabled above is one: the COMMITTED default of each is the safe value the code
// already defaults to, and switching either on is a runtime decision that reverts by itself
// (`Parameters__paper-automation-signal=constant-exposure`) rather than an edit somebody forgets.
//
// They are separate from paper-automation-enabled because they are separate facts, and the runbook
// treats arming as a THREE-value step: 'constant-exposure' says which signal may ask for a position,
// 'short-vol-credit-put' says what structure that position is, and 'true' says the loop may act at
// all. Setting the signal without the structure arms the constant-exposure signal onto the ORIGINAL
// MVP debit vertical, which is not the instrument docs/plans/paper-run-protocol.md describes.
//
// Neither of these can cause an order on its own: paper-automation-enabled stays false, the DU-only
// check stands, and constant-exposure additionally refuses unless an operator-signed
// research.paper_run_decisions row is standing and unrevoked (POST /research/paper-run/decision —
// human only, never an agent; see the runbook).
var paperAutomationSignal = builder.AddParameter(
    "paper-automation-signal",
    builder.Configuration["Parameters:paper-automation-signal"] ?? "vol-residual",
    publishValueAsDefault: true);

var paperAutomationStructure = builder.AddParameter(
    "paper-automation-structure",
    builder.Configuration["Parameters:paper-automation-structure"] ?? "debit-vertical",
    publishValueAsDefault: true);

// The raw post-close capture pass. Defaults ON, unlike everything above, because it only READS —
// PaperCaptureOptions carries the argument: a session nobody captured cannot be captured afterwards,
// while a capture nobody wanted costs two HTTP reads a day. A parameter rather than a literal so it
// can be switched off for a session without an edit, which is the only reason it would ever move.
var paperCaptureEnabled = builder.AddParameter(
    "paper-capture-enabled",
    builder.Configuration["Parameters:paper-capture-enabled"] ?? "true",
    publishValueAsDefault: true);

// The daily shadow mark. Same opt-out shape and same parameter treatment as paper-capture-enabled
// above, and for the same reason: a day nobody marked is a permanent hole in the protocol's Phase 1
// record. Setting it explicitly also keeps this file's own rule — an unset key is indistinguishable
// from a key set to its default, and the arming surface has to be READABLE off the running config.
var shadowMarksEnabled = builder.AddParameter(
    "shadow-marks-enabled",
    builder.Configuration["Parameters:shadow-marks-enabled"] ?? "true",
    publishValueAsDefault: true);

// Empty means "the account the gateway is configured to trade", which is the intended setting on a
// TWS session managing one account. Naming one matters only when TWS manages several.
var paperCaptureAccountId = builder.AddParameter(
    "paper-capture-account-id",
    builder.Configuration["Parameters:paper-capture-account-id"] ?? string.Empty,
    publishValueAsDefault: true);

// How many chain-ingestion drainers to run. OptionChainCoordinator claims ONE request row at a time
// per process (a deliberate design choice — see its remarks), so drain throughput is the process
// count and nothing else. Three is what the ad-hoc processes this AppHost retires were running.
// Read from configuration rather than fixed so the number is a runtime decision; WithReplicas takes
// an int, so this cannot be an AddParameter.
var chainDrainerReplicas =
    int.TryParse(builder.Configuration["Parameters:chain-drainer-replicas"], out var configuredDrainers)
    && configuredDrainers > 0
        ? configuredDrainers
        : 3;

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
//
// THIS instance is the paper-run instance: it is the one that evaluates automation, captures the
// account after the close, and fires the daily shadow mark. The two auxiliary instances below run
// the same binary with those three switched off, because the work they do — draining option chains
// and draining backfill — is bounded by process count rather than by anything inside one process.
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
    // The other two thirds of the arming decision — see the parameters' declarations. Set here even
    // though both hold the code's own default: an unset key is indistinguishable from a key set to
    // the default, and this is the surface an operator has to be able to READ before signing the
    // decision that authorizes the run.
    .WithEnvironment("PaperAutomation__Signal", paperAutomationSignal)
    .WithEnvironment("PaperAutomation__Structure", paperAutomationStructure)
    // Plan B's declared exit: a managed spread is closed at this many calendar days to expiration,
    // with no discretion and no rule about profit. Seven, matching the option's own default, and it
    // is stated here because it is a PROTOCOL parameter — the run's success criterion 3 ("positions
    // survive rolls, expirations, data outages and order failures correctly") is measured against it.
    .WithEnvironment("PaperAutomation__ExitDteThreshold", "7")
    // TWO orders per trading date, not one, and the reason is arithmetic rather than appetite: an
    // EXIT consumes a cap slot exactly as an entry does. On a roll day the exit spends the first
    // slot, so a cap of 1 leaves the entry with nothing and the account sits flat overnight after
    // EVERY roll — a coverage hole in precisely the state transition the run exists to observe.
    // Two is the smallest cap that lets one roll complete inside one session. It is a rail, not a
    // knob: when it is spent the loop refuses and says so.
    .WithEnvironment("PaperAutomation__MaxOrdersPerSession", "2")
    // ---- raw post-close capture (Plan C) --------------------------------------------------------
    // Every value here is the code's own default. They are set explicitly because this is the
    // component whose output IS the protocol's shadow record items 6-9, and a capture window that
    // nobody can read off the running configuration is one nobody can audit afterwards.
    .WithEnvironment("PaperCapture__Enabled", paperCaptureEnabled)
    .WithEnvironment("PaperCapture__Calendar", "NYSE")
    .WithEnvironment("PaperCapture__SessionLabel", "RTH")
    .WithEnvironment("PaperCapture__IntervalSeconds", "300")
    .WithEnvironment("PaperCapture__CloseDelayMinutes", "15")
    .WithEnvironment("PaperCapture__LookbackSessions", "3")
    .WithEnvironment("PaperCapture__TimelyWindowMinutes", "120")
    .WithEnvironment("PaperCapture__AccountId", paperCaptureAccountId)
    // The frozen A4 snapshot time, set here as well as on the drainers. It is not a drainer-local
    // knob: calling a value FROZEN and then letting one instance carry the library's 15:45 default
    // is how "frozen" quietly becomes "whichever process happened to write the row". Any instance
    // that can plan or serve a chain request has to agree on it. See the drainers for what changing
    // it costs.
    .WithEnvironment("ThetaData__SnapshotTimeOfDay", "15:30:00")
    // ---- the daily shadow mark ------------------------------------------------------------------
    // 00:10 UTC on the day after each session's close, and the 10 minutes are load-bearing: backfill
    // slices containing "now" are never claimed, so the same-evening VIX daily close arrives from the
    // 1-day-cadence job whose slice becomes claimable at 00:00 UTC. Marking at the US close instead
    // would produce the forecaster's "no VIX close" refusal every day. Move to
    // ShadowMarks__AfterCloseMinutes=20 (16:20 ET, DST-correct by construction) once the live
    // recorder demonstrably lands same-day closes — the runbook records the evidence to look for.
    .WithEnvironment("ShadowMarks__Enabled", shadowMarksEnabled)
    .WithEnvironment("ShadowMarks__RunAtUtc", "00:10:00")
    .WithEnvironment("ShadowMarks__Calendar", "NYSE")
    .WithEnvironment("ShadowMarks__SessionLabel", "RTH")
    .WithReference(execution)
    .WithReference(marketData)
    .WithEnvironment("ExecutionService__BaseUrl", execution.GetEndpoint("http"))
    .WithEnvironment("MarketDataService__BaseUrl", marketData.GetEndpoint("http"))
    .WithReference(tradingDb)
    .WaitFor(postgres)
    // The one WaitFor this instance gained, and it closes a real observed hole rather than a
    // hypothetical one: both shadow-mark planner-intent refusals seen in production were an absent
    // dependency — "gateway :5100 down" and "MarketDataService :5001 down" — because the ad-hoc
    // process fell back to IbkrGateway:BaseUrl / MarketDataService:BaseUrl's hardcoded localhost
    // defaults with no process listening there. Under this AppHost both URLs are injected from real
    // endpoints, which makes "nothing is listening" impossible; waiting on marketdataservice makes
    // "listening but not yet up" impossible too, on the cold start where it is most likely.
    //
    // Read WaitFor precisely: no resource in this file declares a WithHttpHealthCheck, so for the
    // PROJECTS it waits for the resource to reach Running — the process started — and not for
    // /health to answer 200. (postgres is the exception: an Aspire Postgres resource brings its own
    // health check, so WaitFor(postgres) genuinely waits for a reachable database.) That is enough
    // for what this line is for — a listening socket where there was none — and it is deliberately
    // not claimed to be more. A service that is Running but still warming up will still refuse, and
    // the refusal is recorded.
    //
    // marketdataservice is safe to wait on precisely because it waits for nothing itself (no
    // WaitFor(ibkrGateway) — see its declaration), so it starts in seconds regardless of whether TWS
    // is running. There is deliberately NO WaitFor(execution) here: executionservice
    // waits on keycloak and rabbitmq, and blocking the whole research plane — migrations, recorder,
    // backfill, chain drain — behind two containers that the research track does not use would be a
    // regression for the A4 path this AppHost also has to keep working.
    .WaitFor(marketData);
    // No WaitFor(ibkrGateway), for the same reason marketdataservice skips it: the gateway reports
    // unhealthy whenever TWS is down, and the recorder must still start, sit idle, and retry.

// ---- auxiliary research instances: the ad-hoc drainers, given a durable home --------------------
// Same binary, same database, same gateway; the ONLY differences are which loops are switched on.
// They exist because two of this service's coordinators are one-at-a-time by design — the option
// chain coordinator claims a single request row per pass, and the backfill coordinator a single
// slice — so their throughput is the number of processes and nothing inside a process changes it.
// That is why the ad-hoc setup ran four extra copies; this replaces them without inventing anything.
//
// Every one of these turns OFF the three loops that must have exactly one owner:
//   PaperAutomation  — off by default already, and an unattended order path must not be replicated;
//   PaperCapture     — the schema makes a second writer safe, but not free (two gateway reads a day
//                      per instance, against a broker connection that is already the scarce resource);
//   ShadowMarks      — a second copy would be correct and would repeat a three-year bar load nightly;
//   Sessions         — SessionCalendarSynchronizer is documented as research.sessions' only writer,
//                      and coverage denominators are read from what it writes.
//
// KNOWN GAP, deliberately not worked around here: RecorderOrchestrator has no Enabled switch, so
// every instance below also runs a recorder that leases TWS market-data lines from the shared
// gateway budget (~90 lines, ~57 of them the recording grid). That is today's behaviour with the
// ad-hoc processes too, so this is not a regression — but it is now multiplied by a replica count
// that is easy to raise. Filed in the runbook's findings; the fix is a Recorder:Enabled flag, which
// belongs to the owner of that component, not to this file.
builder.AddProject(
        "research-chain-drainer",
        "../TradingStuff.ResearchService/TradingStuff.ResearchService.csproj")
    .WithReplicas(chainDrainerReplicas)
    .WithReference(ibkrGateway)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IbkrGateway__BaseUrl", ibkrGateway.GetEndpoint("http"))
    .WithEnvironment("OptionChains__Enabled", "true")
    // An hour, against a default of 180 seconds. A claim is only believable for as long as the work
    // can take, and a single expiration's month-chunked walk against a cold Theta Terminal has been
    // measured well past three minutes; a lease that expires mid-walk is reclaimed by a sibling and
    // the same expiration is fetched twice.
    .WithEnvironment("OptionChains__LeaseSeconds", "3600")
    .WithEnvironment("ThetaData__Timeout", "00:30:00")
    // 15:30:00 is FROZEN, not tuned. The A4 term-structure construction on record was built from
    // 15:30 ET snapshots, and changing this value does not "improve" the snapshot — it makes every
    // row ingested afterwards incomparable with every row already in research.option_chain_quotes,
    // silently, because nothing in the schema records which time-of-day a row was cut at.
    .WithEnvironment("ThetaData__SnapshotTimeOfDay", "15:30:00")
    .WithEnvironment("PaperCapture__Enabled", "false")
    .WithEnvironment("ShadowMarks__Enabled", "false")
    .WithEnvironment("Sessions__Enabled", "false")
    // Injected here too, though these instances run no automation loop. Every ResearchService
    // instance registers SpyShortVolPlanner and the /research/automation surface, so every instance
    // carries ExecutionService:BaseUrl and MarketDataService:BaseUrl — and unset, those fall back to
    // hardcoded localhost:5000/5001, which is EXACTLY the dangling default that produced the
    // "MarketDataService down" planner refusals in production. Leaving them dangling on four of five
    // instances would make the comment on researchservice above true only of the instance somebody
    // happened to test.
    .WithReference(execution)
    .WithReference(marketData)
    .WithEnvironment("ExecutionService__BaseUrl", execution.GetEndpoint("http"))
    .WithEnvironment("MarketDataService__BaseUrl", marketData.GetEndpoint("http"))
    .WithReference(tradingDb)
    .WaitFor(postgres);

// The backfill top-up drainer: the ad-hoc :5714 instance's role. The primary researchservice above
// keeps Backfill__Enabled=true as it always has, so this is a SECOND coordinator, which is exactly
// the arrangement running today. Concurrency is safe by construction — BackfillCoordinator claims
// under a lease with a per-process OwnerId, and the request row is the only checkpoint.
//
// Note for whoever reads this next: which JOBS actually drain is not decided here. The three
// kind='topup' jobs are paused in the DATABASE ONLY (docs/FOLLOWUP.md §1), a state no restart
// restores and no file in this repository records. Starting this instance does not un-pause them and
// stopping it does not pause anything.
builder.AddProject(
        "research-backfill",
        "../TradingStuff.ResearchService/TradingStuff.ResearchService.csproj")
    .WithReference(ibkrGateway)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IbkrGateway__BaseUrl", ibkrGateway.GetEndpoint("http"))
    .WithEnvironment("Backfill__Enabled", "true")
    // One value for the frozen snapshot time on every instance — see researchservice above.
    .WithEnvironment("ThetaData__SnapshotTimeOfDay", "15:30:00")
    .WithEnvironment("PaperCapture__Enabled", "false")
    .WithEnvironment("ShadowMarks__Enabled", "false")
    .WithEnvironment("Sessions__Enabled", "false")
    // Injected here too, though these instances run no automation loop. Every ResearchService
    // instance registers SpyShortVolPlanner and the /research/automation surface, so every instance
    // carries ExecutionService:BaseUrl and MarketDataService:BaseUrl — and unset, those fall back to
    // hardcoded localhost:5000/5001, which is EXACTLY the dangling default that produced the
    // "MarketDataService down" planner refusals in production. Leaving them dangling on four of five
    // instances would make the comment on researchservice above true only of the instance somebody
    // happened to test.
    .WithReference(execution)
    .WithReference(marketData)
    .WithEnvironment("ExecutionService__BaseUrl", execution.GetEndpoint("http"))
    .WithEnvironment("MarketDataService__BaseUrl", marketData.GetEndpoint("http"))
    .WithReference(tradingDb)
    .WaitFor(postgres);

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
