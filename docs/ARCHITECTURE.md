# Architecture

## Overview

TradingStuff is a local-first Aspire monorepo for options execution. The current implementation favors clean service boundaries and testable domain behavior over vendor-specific infrastructure code. Postgres, RabbitMQ, Keycloak, and IBKR Gateway are modeled in Aspire now, while the application services use replaceable interfaces so durable storage, messaging, OIDC, and real market data can be added without reshaping the domain.

## Runtime Topology

```text
Internal client
    |
    v
ExecutionService
    |        \
    |         \ publishes lifecycle events
    v          v
RiskService   RabbitMQ
    ^
    |
MarketDataService ---> IBKR Gateway/TWS

ExecutionService/RiskService/MarketDataService ---> Postgres

Keycloak ---> service JWT validation

AuditDashboard ---> operator visibility
```

In the current slice, RabbitMQ, Postgres, Keycloak, and IBKR are AppHost-modeled dependencies. Execution state and events are still in memory, auth uses a development bearer-token handler, and market data is deterministic for repeatable paper execution.

## Projects

- `src/TradingStuff.Contracts`: shared DTOs/enums for orders, option contracts, quotes, Greeks, risk decisions, fills, lifecycle events, and published events.
- `src/TradingStuff.ServiceDefaults`: shared health checks, service discovery, OpenTelemetry wiring, HTTP resilience, and current development auth.
- `src/TradingStuff.ExecutionService`: order REST API, option strategy validation, execution workflow, paper fill engine, order repository boundary, event publisher boundary.
- `src/TradingStuff.RiskService`: portfolio risk evaluator and risk API.
- `src/TradingStuff.MarketDataService`: deterministic quote/Greek provider and market-data API shaped for IBKR replacement.
- `src/TradingStuff.AuditDashboard`: lightweight local status page.
- `src/TradingStuff.AppHost`: Aspire graph for services and infrastructure.
- `tests/TradingStuff.Tests`: focused unit/workflow coverage.

## Execution Flow

1. A client submits `SubmitOrderRequest` to `ExecutionService`.
2. `OrderRequestValidator` validates v1 option strategy shape and order-type requirements.
3. `ExecutionService` requests option quote snapshots from `MarketDataService`.
4. `ExecutionService` loads a portfolio snapshot.
5. `ExecutionService` calls `RiskService` with order, portfolio, and quote inputs.
6. `RiskService` returns approved/rejected risk decision with breaches and Greeks exposure delta.
7. Approved orders are passed to `PaperExecutionEngine`.
8. The paper engine creates simulated fills from quote bid/ask data.
9. `ExecutionService` stores the order and emits lifecycle events through `IExecutionEventPublisher`.

## Boundaries And Replacement Points

- `IOrderRepository`: currently in-memory; replace with Postgres-backed persistence.
- `IExecutionEventPublisher`: currently in-memory/logging; replace with RabbitMQ publisher and outbox.
- `IMarketDataClient`: HTTP boundary to market data; keep while replacing deterministic provider with IBKR adapter.
- `IRiskClient`: HTTP boundary to risk; keep for separate risk-service ownership.
- `IPortfolioProvider`: currently development snapshot; replace with account/position store and broker reconciliation.
- `DevelopmentJwtAuthenticationHandler`: replace with real JWT bearer validation against Keycloak.

## Data Model Direction

The shared contract model already separates:

- Order request and execution order state.
- Option contract identity.
- Order legs and strategy kind.
- Quote snapshots and Greeks.
- Portfolio snapshots and risk limits.
- Risk decision result and breach reason codes.
- Fill reports and lifecycle events.

The Postgres schema should preserve those boundaries rather than collapsing everything into one order table.

## Observability And Operations

All web services call `AddServiceDefaults`, which wires:

- Health endpoints: `/health`, `/alive`.
- Service discovery.
- Standard HTTP resilience.
- OpenTelemetry traces and metrics.
- Development auth.

Aspire provides the local dashboard, resource orchestration, and dependency model. The app currently builds under Aspire 13.4.2, with known transitive `MessagePack` vulnerability warnings from Aspire packages.

## Security Direction

Current local auth is:

```text
Authorization: Bearer dev-internal-token
```

The intended production-like path is:

- Keycloak realm/client setup.
- JWT bearer validation in service defaults.
- Service-to-service scopes for execution, risk, market data, and audit.
- Removal of the development token handler outside local-only workflows.
