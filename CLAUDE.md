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

## Model and effort policy (milestone 2 research phases) — MANDATORY DEFAULT

This is a default that governs behavior, not a suggestion to surface and move past. **Use the
recommended model and reasoning effort for the work at hand unless one of the two exceptions below
applies.** At the start of any research-platform task, check which phase is active in
`docs/STATE.md` (the "Left" list names the next phase; the last "Done" entry names the completed
one), then match the work against this table:

| Work | Model | Reasoning effort |
|---|---|---|
| Phase 1–3 and Phase 7 implementation (recorder, backfill, snapshots, execution simulator) | Sonnet | medium |
| Phase 4 implementation (features, labels, baselines, study runner) | Sonnet | high |
| Phase 5, 6, 8 (residual models + gates, implied-vs-forecast study, shadow/live ops) | Opus | high |
| ALL leakage reviews and order-safety reviews, any phase | Opus | high |
| UI (`ClientApp/`) and documentation work | Haiku | low |

**Class-based overrides — these beat the phase row above, whatever phase the work falls in.**
Derived from measured review outcomes (see `docs/STATE.md`), not from guesses about difficulty:

- **(a) Split-path lifetime state machines → Opus/high.** Any object or row whose
  acquire/complete/release is managed across two or more interleaving code paths: leases,
  registries, replay/reconnect reconciliation, claim-then-update coordinators, crash/inflight
  reapers. *This class produced 4 of Phase 1's 8 confirmed defects, all top-severity.*
- **(b) Ground-truth manufacturers → Opus/high.** Anything producing the reference data other
  components are validated *against* — session calendars, clocks, the as-of/cutoff machinery. A
  defect here is invisible by construction: the validating artifact inherits the same bug.
- **(c) Negative-claim acceptance criteria → minimum Sonnet/high.** Packages whose correctness
  statement is "nothing is silently missing / a rerun adds nothing / no duplicates exist" — gap
  reports, coverage, idempotency assertions. The phase review must name the absent-row check AND
  which table the negative claim is measured on. *Three Phase 1 defects shared one root: a query
  cannot emit a row for the absent case, so absence renders as health.*
- **(d) Read-only UI stays Haiku/low** even when listed beside backend work — empirically zero
  defects across two shipped phases. Defend this row against escalation. A server-side aggregation
  endpoint is NOT UI: it belongs to the package owning its query semantics.

**The two exceptions, and only these — both require words in the prompt itself:**
1. The prompt says not to apply this policy (for this task, or generally).
2. The prompt names a specific model or effort level to use for this task.

**A `/model` switch is NOT an exception**, no matter how immediately it precedes the prompt.
Changing the session model is ambient state, not an instruction, and is at least as likely to be a
mistake, a leftover from earlier work, or unrelated to the task being asked for. "The user selected
Opus, so they must want Opus for this" is exactly the inference this policy exists to prevent —
only an explicit in-prompt instruction overrides the table.

Absent an in-prompt exception, apply the policy without asking and without waiting for confirmation:

- **Main-loop work**: if the active session model/effort does not match the row for the work about
  to start, say so in one line and **delegate that work to the matching pinned agent below** — do
  not do it inline on the wrong model. Delegation is the default remedy, not a preference. Only
  when delegation is genuinely impossible (a one-line fix mid-task; the work is inseparable from an
  interactive back-and-forth) may the work proceed on the session's model, and then only by saying
  so plainly first.
- **Delegated work (Agent/Workflow tool calls)**: always pass the `model` (and `effort` inside
  Workflow's `agent()` calls) matching the table — this is fully within direct control and has no
  excuse for drifting from policy.

Pinned-model agents exist for exactly this: `implementer` (Sonnet), `ui-builder` (Haiku),
`leakage-reviewer` (Opus, high effort) — see `.claude/agents/`. Route leakage/order-safety review
work through `leakage-reviewer` rather than reviewing inline on a smaller model. If a project-level
agent isn't yet visible to the current session's Agent tool (a known lag right after the agent
files are added), fall back to `general-purpose` with an explicit `model` override matching the
table rather than dropping the policy.

### Phase-start validation: adversarial, with a separate arbiter

Before starting a phase, decide the per-work-package model assignment with a three-agent
adversarial structure, not a single validator:

1. **Attacker (Opus, high)** — argues the standing table is already correct for every package, and
   attacks each candidate escalation on its merits.
2. **Justifier (Opus, high)** — argues for escalation wherever warranted, making the strongest
   available case.
3. **Arbiter (Fable)** — decides per package, on the arguments presented.

The table is a prior written in advance from a guess at phase difficulty; it is not a measurement,
and this step exists to correct it. But a single validator both *generates* the escalation case and
*judges* it, which is precisely where bias hides — telling one model to "watch its own bias" is a
weak corrective. Assigning the two sides removes the stake: an agent instructed to argue against
escalation has no incentive to escalate, and the arbiter adjudicates a narrow question (which brief
is better supported) rather than an open-ended one.

Constraints that make this work rather than just cost three times as much:

- **Per work package, not per phase.** A phase mixes genuinely subtle work with plumbing; one
  verdict for the whole phase is too coarse to act on.
- **The table wins ties.** If the briefs are evenly matched, the default stands. Deviation requires
  positive justification, or "balanced" quietly becomes a ratchet upward.
- **Escalation triggers** — a deviation must name at least one; "seems complex/important" is not
  one: novel concurrency or process-lifecycle invariants; correctness that is hard to cover with
  tests (timezone/session semantics — **especially any single authority whose output downstream
  artifacts are validated against**, because the operative hazard is not that conversion is hard
  but that the validator and the validated share an assumption, which is exactly what defeats the
  "but it is a testable oracle" counter-argument; decimal/precision boundaries; idempotency under
  concurrency; partitioning/storage-engine semantics); safety invariants (order placement,
  live-capital gates, the leakage firewall); irreversible or unrecoverable data paths (anything
  writing the prospective recording, which cannot be re-collected); cross-cutting refactors
  touching many call sites.
- **Both advocates must steelman the other side** and concede its strongest point explicitly.
  Otherwise the arbiter is choosing between two weak briefs on style.
- **De-escalation is in scope.** A package that is plainly CRUD, plumbing, or docs should be named
  as such even where the table says otherwise.
- **Calibrate from outcomes, not forecasts.** All three agents get the PREVIOUS phase's
  adversarial-review results — how many defects were confirmed and of what *class* — and are told
  to weight that over any impression of difficulty. Confirmed-defect classes are the only real
  signal about where the table is mis-calibrated, and the durable output is a correction **by class
  of work**, not by phase number.
- **Record the verdict, the winning argument, and the conceded counterpoint in `docs/STATE.md`**
  with the phase entry — so the next phase can check what was predicted against what its review
  actually found, and so a wrong call is visible rather than folklore.

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
