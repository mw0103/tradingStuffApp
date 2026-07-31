# TradingStuff

Greenfield .NET Aspire trading microservice workspace focused on paper options execution.

## Current Slice

- C# execution service with REST order APIs.
- C# risk service with portfolio, max-loss, buying-power, duplicate-order, and Greeks-aware checks.
- C# market-data service with deterministic IBKR-shaped option quotes and Greeks for repeatable paper execution.
- C# audit dashboard with local operator links.
- Shared contracts for options, multileg orders, quotes, fills, risk decisions, and lifecycle events.
- Aspire AppHost wiring for Postgres, RabbitMQ, Keycloak, required external IBKR Gateway URL, and all services.
- xUnit coverage for strategy validation, risk rejection, paper fills, and end-to-end workflow orchestration.

## Run

Use a writable .NET CLI home in this environment:

```bash
mkdir -p /tmp/dotnet_home
export DOTNET_CLI_HOME=/tmp/dotnet_home
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
```

Build and test:

```bash
dotnet build TradingStuff.slnx
dotnet test tests/TradingStuff.Tests/TradingStuff.Tests.csproj -m:1 --no-restore
```

Start the distributed app:

```bash
aspire start --non-interactive
```

The AppHost parameter `ibkr-gateway-url` defaults to `http://localhost:5000`. Point it at the local IBKR Gateway/TWS bridge used in your environment before replacing the deterministic paper quote provider with a real IBKR client.

## Auth

Services currently use a local bearer token scheme for internal calls while Keycloak is modeled in Aspire:

```text
Authorization: Bearer dev-internal-token
```

The next production step is to replace `DevelopmentJwtAuthenticationHandler` with real OIDC/JWT bearer validation against Keycloak.

## Known Follow-ups

- Replace in-memory repositories/outbox with Postgres-backed order state and RabbitMQ publishing.
- Replace deterministic market-data provider with an IBKR Gateway adapter.
- Add the Python ML signal service after execution/risk/data behavior is stable.
- Address Aspire 13.4.2 transitive `MessagePack` vulnerability warnings when an upstream patched Aspire package is available.
