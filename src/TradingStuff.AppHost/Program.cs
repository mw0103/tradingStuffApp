var builder = DistributedApplication.CreateBuilder(args);

var devInternalToken = builder.AddParameter(
    "dev-internal-token",
    "dev-internal-token",
    publishValueAsDefault: true);

var ibkrGatewayUrl = builder.AddParameter(
    "ibkr-gateway-url",
    "http://localhost:5000",
    publishValueAsDefault: true);

var ibkrGateway = builder.AddExternalService("ibkr-gateway", ibkrGatewayUrl);

var postgres = builder.AddContainer("postgres", "postgres", "17")
    .WithEnvironment("POSTGRES_USER", "trading")
    .WithEnvironment("POSTGRES_PASSWORD", "trading")
    .WithEnvironment("POSTGRES_DB", "trading")
    .WithEndpoint(port: 5432, targetPort: 5432, name: "tcp");

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

var marketData = builder.AddProject(
        "marketdataservice",
        "../TradingStuff.MarketDataService/TradingStuff.MarketDataService.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(ibkrGateway)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IBKR__GatewayUrl", ibkrGateway)
    .WithEnvironment("MarketData__Source", "ibkr-deterministic-paper-feed");

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
    .WithEnvironment("RiskService__BaseUrl", risk.GetEndpoint("http"))
    .WithEnvironment("MarketDataService__BaseUrl", marketData.GetEndpoint("http"))
    .WaitFor(risk)
    .WaitFor(marketData)
    .WaitFor(rabbitmq)
    .WaitFor(postgres)
    .WaitFor(keycloak);

builder.AddProject(
        "auditdashboard",
        "../TradingStuff.AuditDashboard/TradingStuff.AuditDashboard.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(execution)
    .WithReference(risk)
    .WithReference(marketData)
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("Dashboard__ExecutionBaseUrl", execution.GetEndpoint("http"))
    .WithEnvironment("Dashboard__RiskBaseUrl", risk.GetEndpoint("http"))
    .WithEnvironment("Dashboard__MarketDataBaseUrl", marketData.GetEndpoint("http"))
    .WithEnvironment("Dashboard__RabbitManagementUrl", rabbitmq.GetEndpoint("management"))
    .WithEnvironment("Dashboard__KeycloakUrl", keycloak.GetEndpoint("http"))
    .WaitFor(execution)
    .WaitFor(risk)
    .WaitFor(marketData)
    .WaitFor(rabbitmq)
    .WaitFor(keycloak);

builder.Build().Run();
