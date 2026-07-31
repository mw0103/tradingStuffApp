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
dotnet test tests/TradingStuff.Tests/TradingStuff.Tests.csproj -m:1    # 149 tests, all should pass
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
| `TradingStuff.RiskService` | Pre-trade risk: buying power, max loss, contract count, daily loss, Greeks limits. Inputs come from the stubbed provider or the real IBKR account, per `Portfolio:Source`. |
| `TradingStuff.MarketDataService` | Option quotes, Greeks, chains. Deterministic generator or IBKR, per `MarketData:Source`. |
| `TradingStuff.IbkrGateway` | Owns the **single** TWS socket. Contract resolution, chains, quotes, account/positions, order placement. |
| `third_party/IBApi` | Vendored IBKR TWS API 10.45.01. Do not edit. |
| `TradingStuff.AuditDashboard` | Local operator surface |
| `TradingStuff.ServiceDefaults` | OpenTelemetry, health checks, resilience, dev auth handler |
| `TradingStuff.AppHost` | Aspire orchestration |

`ExecutionWorkflow.SubmitAsync` is the spine: validate → quote → portfolio → risk → route →
persist → publish lifecycle events. Routing goes to the simulated engine or IBKR per `Execution:Router`. Read it before changing anything order-related.

## Conventions

- **.NET 10, Aspire 13.4, C# 13.** Primary constructors, file-scoped namespaces, `sealed record` for
  contracts, collection expressions (`[]`), minimal APIs. Match the surrounding style.
- **All money and prices are `decimal`.** Never `double` outside a broker-adapter boundary
  (`IBApi` is all `double`; convert only there).
- **Never key a collection on a whole `OptionContract`.** It is a `record`, so equality covers every
  property and lookups break as soon as one side carries a broker-enriched field. Use
  `contract.Key()` → `OptionContractKey`. Do not add broker metadata like `ConId` to the record —
  conIds live in an adapter-side cache. (`TradingClass` is in the record because SPX and SPXW are
  genuinely different instruments, not broker metadata.)
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
- Risk engine has 12 breach codes and 1 is tested (**highest priority** now that the risk inputs are
  real)
- SPX/SPXW combos park in `PreSubmitted` at TWS while SPY combos fill — unexplained, see `docs/STATE.md`
- Python ML signal service
- Aspire transitive `MessagePack` advisory, pending an upstream patch

The IBKR integration is complete end to end and a full round trip has filled on the paper account.
Milestone 1 is **not** complete: persistence and event transport are unmet, and Postgres/RabbitMQ/
Keycloak start but nothing connects to them. Prerequisites and gotchas are in `docs/STATE.md`; API
detail is in the `ibkr` skill.

## Model and effort policy (milestone 2 research phases)

At session start, check which research phase is active in `docs/STATE.md` (the "Left" list names
the next phase; the last "Done" entry names the completed one) and pick the model/effort for the
work at hand from this table. **If the session's active model does not match the policy for the
phase being worked, say so explicitly before starting** — do not silently proceed on a mismatch.

| Work | Model | Reasoning effort |
|---|---|---|
| Phase 1–3 and Phase 7 implementation (recorder, backfill, snapshots, execution simulator) | Sonnet | medium |
| Phase 4 implementation (features, labels, baselines, study runner) | Sonnet | high |
| Phase 5, 6, 8 (residual models + gates, implied-vs-forecast study, shadow/live ops) | Opus | high |
| ALL leakage reviews and order-safety reviews, any phase | Opus | high |
| UI (`ClientApp/`) and documentation work | Haiku | low |

Pinned-model agents exist for delegation regardless of the session model: `implementer` (Sonnet),
`ui-builder` (Haiku), `leakage-reviewer` (Opus, high effort) — see `.claude/agents/`. Route
leakage/order-safety review work through `leakage-reviewer` rather than reviewing inline on a
smaller model.

## Trading safety

**No orders against a funded account in v1** (`docs/PLAN.md`). Two non-live modes are in scope and
mean different things: *simulated* (`Execution:Router=paper`, fills invented locally, the default)
and *paper brokerage* (`Execution:Router=ibkr`, real orders to a `DU` account settled in simulated
money). A `U`-prefixed account is real money and is out of scope.

TWS paper ports are 7497 (TWS) and 4002 (Gateway).

Adding or changing any real order-placement call site is not a routine edit — confirm before doing it.
Never commit account numbers, API session tokens, or position dumps.

## IBKR work

There is a project skill at `.claude/skills/ibkr/` covering the TWS socket API, the single-socket
adapter design, contract/Greeks mapping to `TradingContracts.cs`, and the staged migration off the
deterministic provider. It loads automatically when IBKR work comes up — consult it rather than
improvising the API surface.
