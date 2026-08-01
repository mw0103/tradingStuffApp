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
dotnet test tests/TradingStuff.Tests/TradingStuff.Tests.csproj -m:1    # unit only; all should pass
aspire start --non-interactive                                        # full distributed app
```

`-m:1` on the test run is deliberate — keep it. Two suites are trait-gated out of that run and are
NOT optional before claiming something works:

- `--filter "Category=RequiresPostgres"` — needs `TRADING_TEST_POSTGRES`. Start the container with a
  raised `-c max_connections` (the harness leaks pools and never drops its test databases).
- `--filter "Category=RequiresTws"` — needs `TRADING_TEST_TWS=127.0.0.1:7497` and a running paper
  TWS. Some of these drive a raw `EClientSocket`, bypassing the pacing governor, so sustained probing
  can trip TWS's limits: it is not a reliable single-run gate. Re-run before concluding a failure is
  real.

`aspire start` brings up Postgres, RabbitMQ, and Keycloak containers, so it needs Docker running.
For logic changes, `dotnet test` alone is the fast loop.

## Layout

| Project | Role |
|---|---|
| `TradingStuff.Contracts` | All shared records/enums, single file `TradingContracts.cs`. Changes here ripple everywhere. |
| `TradingStuff.ExecutionService` | Order REST API, validation, lifecycle, paper fills, event publishing |
| `TradingStuff.RiskService` | Pre-trade risk: buying power, max loss, contract count, daily loss, Greeks limits. Inputs come from the stubbed provider or the real IBKR account, per `Portfolio:Source`. |
| `TradingStuff.MarketDataService` | Option quotes, Greeks, chains. Deterministic generator or IBKR, per `MarketData:Source`. |
| `TradingStuff.IbkrGateway` | Owns the **single** TWS socket. Contract resolution, chains, quotes, account/positions, order placement. Also the recorder — raw ticks go straight to Postgres from here, not via ResearchService (`docs/DECISIONS.md` §4). |
| `TradingStuff.ResearchService` | Research plane: migrations, session calendar, node selection, coverage, backfill, gap detection, `/research/*` + the `/ui` SPA. |
| `TradingStuff.ResearchContracts` | Research-side records. Churns faster than `Contracts`, deliberately separate so it does not ripple through the execution services. |
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
- **Exception: `TradingStuff.Volatility` is `double` throughout.** Realized variance takes
  logarithms, square roots and sums of squares, none of which `decimal` supports; it would have to
  be converted at every step, far slower, and no more accurate once a log has been taken. The
  conversion happens once, at `HistoricalBarAdapter`, which is the boundary in exactly the sense
  `IBApi` is. Everything upstream of it stays `decimal`. This is not licence for `double`
  elsewhere: it covers the estimator library and nothing else.
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

Two documents are worth reading before you write code here, not just when you get stuck:

- **`docs/LESSONS.md`** — the practices that have actually caught defects in this repository, each
  with the incident that produced it. Read it before reviewing, before fixing, and before believing a
  green test run. The two that matter most: **reproduce, don't inspect** (careful reading has twice
  produced confident wrong verdicts on code a harness disproved in minutes), and **reintroduce the
  defect to prove your test fails** — six false-green tests were caught that way in one session, three
  of them by the agent that had just written them.
- **`docs/DECISIONS.md`** — load-bearing architectural choices *with the alternatives that were
  rejected and why*. Consult it before changing a boundary, adding a dependency, or "simplifying"
  something that looks redundant. Several entries exist because the obvious simplification was tried
  and caused an outage.

**`docs/FOLLOWUP.md`** is the deferred-work register: issues found and deliberately not fixed while
driving to the MVP. Read its first section before running anything — it records live database state
that exists nowhere in version control and silently reverts on a fresh environment.

Outstanding (from `docs/STATE.md`):

- In-memory order/event stores → Postgres
- In-memory event publisher → RabbitMQ
- `DevelopmentJwtAuthenticationHandler` → Keycloak OIDC/JWT validation
- Provider identifiers are in the canonical schema's primary key (`research.bars` is keyed on
  `con_id` plus two verbatim TWS parameter names) — a recorded debt whose reversal cost grows daily,
  see `docs/DECISIONS.md` §15
- SPX/SPXW combos park in `PreSubmitted` at TWS while SPY combos fill — unexplained, see `docs/STATE.md`
- Python ML signal service
- Aspire transitive `MessagePack` advisory, pending an upstream patch

The IBKR integration is complete end to end and a full round trip has filled on the paper account.
Milestone 1 is **not** complete: persistence and event transport are unmet, and Postgres/RabbitMQ/
Keycloak start but nothing connects to them. Prerequisites and gotchas are in `docs/STATE.md`; API
detail is in the `ibkr` skill.

## Model and effort policy — MANDATORY DEFAULT

Governs behaviour; not a suggestion to surface and move past. Check the active phase in
`docs/STATE.md`, then match the work:

| Work | Model | Effort |
|---|---|---|
| Phase 1–3 and Phase 7 implementation (recorder, backfill, snapshots, execution simulator) | Sonnet | medium |
| Phase 4 implementation (features, labels, baselines, study runner) | Sonnet | high |
| Phase 5, 6, 8 (residual models + gates, implied-vs-forecast study, shadow/live ops) | Opus | high |
| ALL leakage reviews and order-safety reviews, any phase | Opus | high |
| UI (`ClientApp/`) and documentation work | Haiku | low |

**Class-based overrides beat the phase row, whatever phase the work falls in.** These come from
counted defect outcomes, not guesses — see `docs/DECISIONS.md` §16 for the evidence:

- **(a) Split-path lifetime state machines → Opus/high.** Any object or row whose
  acquire/complete/release spans two or more interleaving code paths: leases, registries,
  replay/reconnect reconciliation, claim-then-update coordinators, crash/inflight reapers.
- **(b) Ground-truth manufacturers → Opus/high.** Anything producing reference data other components
  are validated *against* — session calendars, clocks, as-of/cutoff machinery.
- **(c) Negative-claim acceptance criteria → minimum Sonnet/high.** Packages whose correctness
  statement is "nothing is silently missing / a rerun adds nothing / no duplicates exist". The review
  must name the absent-row check AND which table the claim is measured on.
- **(d) Read-only UI stays Haiku/low** even beside backend work. Defend this row against escalation.
  A server-side aggregation endpoint is NOT UI — it belongs to the package owning its query semantics.

**Two exceptions, and only these — both require words in the prompt itself:** the prompt says not to
apply the policy, or the prompt names a specific model/effort for this task.

**A `/model` switch is NOT an exception**, however immediately it precedes the prompt. Session model
is ambient state, not an instruction. Reasoning in `docs/DECISIONS.md` §16.

Absent an in-prompt exception, apply it without asking:

- **Main-loop work** — if the session model does not match the row, say so in one line and
  **delegate to the matching pinned agent**. Delegation is the default remedy. Only when genuinely
  impossible (a one-line fix mid-task; work inseparable from interactive back-and-forth) may it
  proceed on the session's model, and then only by saying so plainly first.
- **Delegated work** — always pass the `model` (and `effort` in Workflow `agent()` calls) matching
  the table. This is fully within direct control.

Pinned agents: `implementer` (Sonnet), `ui-builder` (Haiku), `leakage-reviewer` (Opus/high) — see
`.claude/agents/`. If one is not yet visible to the session's Agent tool (a known lag after the files
are added), fall back to `general-purpose` with an explicit `model` override rather than dropping the
policy.

**Before starting a phase**, run the three-agent arbitration (Opus attacker vs Opus justifier, Fable
arbiter) defined in `docs/DECISIONS.md` §16, per work package, and record the verdict, winning
argument and conceded counterpoint in `docs/STATE.md`.

## Trading safety

The paper/live boundary is the safety mechanism. Everything on the paper side of it is meant to be
used hard; everything on the live side is gated. Do not blur the two, and do not import caution
from the live side into the paper side — that caution is what let a fatal recorder bug ship
(see `docs/STATE.md`, Phase 1).

### The paper account is FOR testing. Use it. Do not ask permission.

**On a verified `DU` account, exercise anything you build — without checking first.** Place orders,
cancel them, fill them, place bad ones, blow through risk limits, exhaust market-data lines, run
the balance to zero. It is simulated money in an account that can be reset. That is the entire
reason it exists.

**This is a requirement, not merely a permission.** If you build or change something that talks to
TWS, run it against the paper account before claiming it works. Unit tests stub the socket, so they
cannot tell you what TWS *accepts* — contract shapes, tick types, entitlements, error semantics are
not knowledge until a live connection has demonstrated them. A green unit suite is not evidence
about broker behaviour, and "all tests pass" must never be reported as though it were.

Deliberately in scope on paper, and expected:
- Real order placement and cancellation via `Execution:Router=ibkr` + `Portfolio:Source=ibkr`.
- Failure injection: kill the TWS connection mid-request, force reconnect and 1101 replay, saturate
  the line ledger, trip pacing limits, stop Postgres under a live recorder.
- Long-running loads: the full backfill drain, a whole RTH+GTH recording session.

Add a `Category=RequiresTws` test whenever behaviour can be pinned by one, so verification stops
being a manual ritual that gets skipped.

### The live side stays hard-gated

A `U`-prefixed account is real money and is **out of scope for v1** (`docs/PLAN.md`). Adding or
changing a real order-placement call site *for a live account* is not a routine edit — confirm
first. `IBKR:AllowLiveTrading` stays false in every committed file, the `DU`-prefix check stays,
and no test may reach `placeOrder`.

TWS paper ports are 7497 (TWS) and 4002 (Gateway); 7496/4001 are live. Never commit account
numbers, API session tokens, or position dumps.

## IBKR work

There is a project skill at `.claude/skills/ibkr/` covering the TWS socket API, the single-socket
adapter design, contract/Greeks mapping to `TradingContracts.cs`, and the staged migration off the
deterministic provider. It loads automatically when IBKR work comes up — consult it rather than
improvising the API surface.
