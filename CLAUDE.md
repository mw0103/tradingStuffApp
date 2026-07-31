# TradingStuff

Local-first .NET Aspire monorepo for paper options trading against Interactive Brokers.
C# owns execution, risk, market data, and orchestration. A Python ML signal service is deferred
until the execution foundation is stable.

## Build and test

This environment needs a writable .NET CLI home. Export these first — without them the CLI fails on
a read-only home directory:

```bash
export DOTNET_CLI_HOME=/tmp/dotnet_home
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
mkdir -p /tmp/dotnet_home
```

```bash
dotnet build TradingStuff.slnx
dotnet test tests/TradingStuff.Tests/TradingStuff.Tests.csproj -m:1    # 6 tests, all should pass
aspire start --non-interactive                                        # full distributed app
```

`-m:1` on the test run is deliberate — keep it.

`aspire start` brings up Postgres, RabbitMQ, and Keycloak containers, so it needs Docker running.
For logic changes, `dotnet test` alone is the fast loop.

## Layout

| Project | Role |
|---|---|
| `TradingStuff.Contracts` | All shared records/enums, single file `TradingContracts.cs`. Changes here ripple everywhere. |
| `TradingStuff.ExecutionService` | Order REST API, validation, lifecycle, paper fills, event publishing |
| `TradingStuff.RiskService` | Pre-trade risk: buying power, max loss, contract count, daily loss, Greeks limits |
| `TradingStuff.MarketDataService` | Option quotes, Greeks, chains. Currently a deterministic generator. |
| `TradingStuff.AuditDashboard` | Local operator surface |
| `TradingStuff.ServiceDefaults` | OpenTelemetry, health checks, resilience, dev auth handler |
| `TradingStuff.AppHost` | Aspire orchestration |

`ExecutionWorkflow.SubmitAsync` is the spine: validate → quote → portfolio → risk → paper execute →
persist → publish lifecycle events. Read it before changing anything order-related.

## Conventions

- **.NET 10, Aspire 13.4, C# 13.** Primary constructors, file-scoped namespaces, `sealed record` for
  contracts, collection expressions (`[]`), minimal APIs. Match the surrounding style.
- **All money and prices are `decimal`.** Never `double` outside a broker-adapter boundary.
- Services talk to each other over HTTP with a bearer token via
  `ServiceClientConfiguration.ConfigureInternalClient`. Endpoints use `.RequireAuthorization()`.
- Every order carries `OrderId` + `CorrelationId`; lifecycle events chain via `CausationId`.
- Config keys use Aspire's double-underscore env convention (`IBKR__GatewayUrl` → `IBKR:GatewayUrl`).

## State of the work

`docs/PLAN.md` is the milestone definition, `docs/STATE.md` is the current done/left list, and
`docs/ARCHITECTURE.md` the structural view. **Update `docs/STATE.md` when completing a milestone item.**

Outstanding (from `docs/STATE.md`):

- In-memory order/event stores → Postgres
- In-memory event publisher → RabbitMQ
- `DevelopmentJwtAuthenticationHandler` → Keycloak OIDC/JWT validation
- Deterministic market data → real IBKR adapter
- Python ML signal service
- Aspire transitive `MessagePack` advisory, pending an upstream patch

## Trading safety

**No live broker orders in v1** (`docs/PLAN.md`). Paper only: TWS paper port 7497 / Gateway 4002, and
a `DU`-prefixed account. `U`-prefixed is live money.

Adding or changing any real order-placement call site is not a routine edit — confirm before doing it.
Never commit account numbers, API session tokens, or position dumps.

## IBKR work

There is a project skill at `.claude/skills/ibkr/` covering the TWS socket API, the single-socket
adapter design, contract/Greeks mapping to `TradingContracts.cs`, and the staged migration off the
deterministic provider. It loads automatically when IBKR work comes up — consult it rather than
improvising the API surface.
