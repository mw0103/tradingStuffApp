# Trading Execution Service Plan

## Summary

Build a local-first Aspire monorepo for a trading platform. The first milestone is an end-to-end paper options trade: an internal service submits a multileg options order, auth succeeds, market data supplies quotes and Greeks, portfolio risk approves or rejects the order, execution simulates fills, state is persisted, lifecycle events are published, and audit surfaces show the workflow.

C# owns execution, risk, data processing, APIs, orchestration, and service contracts. Python ML is deferred until the execution foundation is stable.

## Target Services

- `TradingStuff.ExecutionService`: REST order API, request validation, order lifecycle, paper execution, event publishing boundary.
- `TradingStuff.RiskService`: portfolio risk approval, max-loss checks, buying-power checks, duplicate-order protection, Greeks exposure limits.
- `TradingStuff.MarketDataService`: option contract/quote/Greeks boundary, shaped for IBKR Gateway-backed data.
- `TradingStuff.AuditDashboard`: local operator surface for service links and audit visibility.
- `TradingStuff.AppHost`: Aspire orchestration for services plus Postgres, RabbitMQ, Keycloak, and local IBKR Gateway URL.
- Future `ml-service`: Python signal/model service, not part of v1.

## Execution Scope

- Asset class: options.
- Trading mode: paper trading first; no live order placement in milestone one.
- Broker target: Interactive Brokers, with live order placement isolated behind future adapters.
- Supported strategy families: verticals, calendars, diagonals, straddles, and strangles.
- Supported order behavior: market, limit, stop, stop-limit, cancel, and replace.
- First milestone acceptance: submit an internal API request, pass risk, consume option quote/Greek data, simulate fills, persist state, and publish lifecycle events.

## Interfaces

- `POST /orders`: submit an order.
- `GET /orders`: list orders.
- `GET /orders/{id}`: fetch order state.
- `GET /orders/{id}/events`: fetch lifecycle events.
- `POST /orders/{id}/cancel`: request cancellation.
- `POST /orders/{id}/replace`: request replace.
- `POST /risk/evaluate-order`: evaluate pre-trade risk.
- `GET /risk/limits`: inspect configured risk limits.
- `POST /market-data/options/quotes`: fetch option quote snapshots.
- `GET /market-data/options/chains/{underlying}`: fetch a deterministic option chain.
- `GET /market-data/ibkr/status`: inspect IBKR market-data dependency status.

Event contracts use stable names, order IDs, event IDs, correlation IDs, timestamps, and lifecycle statuses.

## Data And Infrastructure

- Postgres is the intended durable store for orders, fills, quote snapshots, risk decisions, and audit records.
- RabbitMQ is the intended event transport for execution lifecycle events.
- Keycloak is the intended local OIDC issuer.
- Aspire models Postgres, RabbitMQ, Keycloak, all C# services, and the required external IBKR Gateway URL.

## Risk Model

V1 risk checks include:

- Buying power.
- Max loss per order.
- Max position size/contracts per order.
- Max daily loss.
- Duplicate client order detection.
- Delta, gamma, theta, and vega exposure limits.
- Rejection of uncovered short volatility spreads in v1.

Every risk decision should retain inputs, quote snapshot references, computed exposure, result, and reason codes once persistence is added.

## Test Plan

- Strategy validation tests for supported options shapes.
- Risk approval/rejection tests for Greek and uncovered-short failures.
- Paper fill tests for market and limit behavior.
- End-to-end workflow tests with fake risk and market-data clients.
- Later integration tests for Postgres, RabbitMQ, Keycloak JWT validation, IBKR market data, and Aspire runtime startup.

## Assumptions

- The workspace is greenfield.
- Local target stack is .NET 10, Aspire 13.4, Docker, and Python 3.14.
- IBKR Gateway/TWS will be available locally when replacing deterministic quote generation.
- No live broker orders are placed in v1.
- Python ML remains out of scope until execution/risk/data behavior is stable.
