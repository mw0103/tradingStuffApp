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

// A real Aspire Postgres resource, not a bare container: it produces the connection string that the
// gateway (order-id map, raw recording) and ResearchService (everything derived) actually consume.
var postgresUser = builder.AddParameter("postgres-user", "trading", publishValueAsDefault: true);
var postgresPassword = builder.AddParameter("postgres-password", "trading", publishValueAsDefault: true);

var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithImageTag("17")
    .WithHostPort(5432);

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
var ibkrGateway = builder.AddProject(
        "ibkrgateway",
        "../TradingStuff.IbkrGateway/TradingStuff.IbkrGateway.csproj")
    .WithExternalHttpEndpoints()
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
    // Deterministic quotes by default. Switch to "ibkr-delayed" (or "ibkr-live") to pull real data.
    .WithEnvironment("MarketData__Source", "ibkr-deterministic-paper-feed");
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
    // "paper" simulates fills locally. Set to "ibkr" to send approved orders to the paper account
    // through the gateway. Anything unrecognised stays on paper.
    .WithEnvironment("Execution__Router", "paper")
    // Fixed development buying power with no positions. Set to "ibkr" to feed risk the real account's
    // buying power, daily P&L, and position Greeks. Set this whenever Execution__Router is "ibkr":
    // otherwise real orders are approved against fabricated portfolio inputs.
    .WithEnvironment("Portfolio__Source", "development")
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
