# State

Updated: 2026-07-14

## Done

- Aspire/.NET solution scaffolded.
- Execution, risk, market-data, audit dashboard, contracts, service defaults, and AppHost projects created.
- Paper options workflow implemented with strategy validation, Greeks-aware risk, deterministic quotes, fills, lifecycle events, and local auth.
- AppHost models Postgres, RabbitMQ, Keycloak, services, and external IBKR Gateway URL.
- Tests pass: 6/6.
- Full solution builds.

## Left

- Replace in-memory order/event stores with Postgres.
- Replace in-memory event publisher with RabbitMQ.
- Replace dev bearer-token auth with Keycloak/OIDC JWT validation.
- Replace deterministic market data with real IBKR Gateway adapter.
- Add Python ML signal service.
- Add richer audit dashboard.
- Address Aspire transitive `MessagePack` advisories when upstream package is patched.
