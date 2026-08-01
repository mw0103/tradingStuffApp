# IBKR edge-research roadmap

Milestone two: evolve TradingStuff into a market-research platform that can discover, validate,
and — just as importantly — credibly reject trading edge in SPX volatility. Companion documents:
`docs/research/ibkr-data-capability-matrix.md` (what the data allows; runtime-verified),
`docs/research/volatility-forecast-residual-study.md` (the pre-registered first study), and
`docs/research/literature-evidence-matrix.md` (evidence base and the methodological controls it
imposes). This roadmap is the architecture and sequencing half.

> **Amended 2026-08-01: no longer IBKR-only.** This document originally said "IBKR-only", and the
> constraint was load-bearing — it is what makes the capability matrix a complete account of what
> the platform can know. `TradingStuff.Volatility` now ships a ThetaData client
> (`ThetaData/ThetaDataClient.cs`) used by the model-free implied-variance half, so the platform
> depends on a second vendor. Recorded here rather than left as a contradiction between the
> roadmap and the code.
>
> What this costs: the capability matrix no longer bounds what the platform can observe, and any
> study using implied variance inherits a second availability and licensing dependency that the
> IBKR-only framing was chosen to avoid. Track A (the volatility-forecast-residual study) is
> **unaffected** — its features and labels are IBKR-sourced throughout, so its pre-registration
> still holds without amendment. The exposure is confined to the implied-variance work.
>
> The alternative, re-sourcing model-free implied variance from `IbkrGateway` option chains, stays
> open: the integral, strike selection and constant-maturity interpolation are all vendor-neutral,
> and only the chain loader would be rewritten.

## Executive summary

1. **Recorder-first.** The decisive, runtime-verified fact: IBKR option history is weeks deep and
   expired options return nothing. Every SPX surface day not recorded is lost forever, while
   underlying bars (SPX 1-min ≥2010, SPY ≥2005, VIX, ES) wait patiently. Phase 0 hardens the
   socket; Phase 1 ships the surface recorder; backfill runs concurrently after.
2. **Primary research slice (Track A):** the volatility-forecast-residual study — HAR-RV baseline
   on SPX session RV (~4,000 non-overlapping daily labels), pre-registered ≤15-feature residual
   correction, QLIKE + Diebold-Mariano + walk-forward, one-shot 2024→ holdout. Overnight-ES
   information folds in as a feature tier; a daily VRP-conditioning companion (VIX² − forward RV)
   runs alongside. Track A beats *a model*, not the market — its economic role is the fair-value
   leg of the eventual implied-vs-forecast study.
3. **Secondary track (Track B):** prospective SPX surface recording — 6 DTE buckets × 9 delta
   nodes ≈ 54 of the verified ~100 market-data lines, role-based longitudinal node identity,
   append-only raw events, as-of snapshots, passive execution observation (the only legitimate
   cost-model source).
4. **Smallest credible architecture:** extend `TradingStuff.IbkrGateway` (pacing governor,
   historical client, subscription leases, raw-event recording, order-id persistence); one new
   `TradingStuff.ResearchService` + small `TradingStuff.ResearchContracts`; Postgres via Aspire
   `AddPostgres` as the ONLY data bus; daily Parquet artifacts; a **React+Vite research UI built
   into and served by ResearchService** (the one place a real frontend earns its keep — coverage,
   surface, and study diagnostics are interactive-visualization surfaces). No Kafka, no
   ClickHouse, no RabbitMQ client for research, no Python service.
5. **False-discovery discipline is a platform feature:** immutable trial registry, pre-registered
   variants (cap 10 before the holdout opens), QLIKE-only gating, SPA tests on sweeps, PBO and
   Deflated Sharpe from the registry, placebo pipeline gates, a first-class no-trade region,
   100%-of-spread cost gating, paper fills schema-flagged as non-calibration.
6. **"No edge" is a success outcome.** Each study carries explicit falsification conditions; the
   variance-gap study's null result ("the implied-realized gap is risk compensation, not
   exploitable mispricing") is a legitimate headline conclusion.

Nothing in this roadmap places live orders. Phase 8's small-live observation requires separate
explicit authorization on top of the existing triple opt-in.

## Ranked research backlog

Scored 1–5 on twelve criteria (mechanism, data-now, backfill depth, prospective need, accumulation
burden, complexity, data-quality/look-ahead/execution/false-discovery risk, 30–60 DTE relevance,
falsifiability); totals are a screen, not a verdict.

| Rank | Study | Score | Verdict |
|---|---|---|---|
| 1 | VrpConditioningStudy (new) | 51 | Daily-grain: is VIX² − RV(t,t+21d) predictably wide/narrow given state? Runnable now; ~120 effective windows ⇒ conditioning knowledge, not P&L claims |
| 2 | IntradayVolatilityForecastResidualStudy | 50 | **Primary vertical slice** — see the pre-registration doc |
| 3 | OvernightToRegularHoursVolatilityStudy | 47 | **Folded into #2** as feature Tier-2 + ablation on the 2023-08+ subsample |
| 4 | ExecutionObservationStudy | 43 | Passive from day one inside Track B; sole legitimate cost-model source |
| 5 | SpxVixEsRegimeStudy | 41 | **Demoted**: open-ended "divergence" mining is a false-discovery machine. Becomes 2–3 pre-registered features + regime-slicing labels |
| 6 | ImpliedVsForecastVarianceStudy | 34 | **Economic destination (Track C)**; gated on ≥6 months of recorded surface |
| 7 | TermStructureKinkStudy | 31 | Prospective; reuses the recorded surface |
| 8 | GlobalHoursToRegularHoursSurfaceStudy | 30 | Prospective; GTH data verified available |
| 9 | EntryTimingStudy | 30 | Prospective; shares surface + cost model |
| 10 | SurfaceStateTransitionStudy | 29 | Prospective; needs ~6 months of surface days |
| 11 | CrossStrikeRelativeValueStudy | 22 | **Rejected**: with L1 top-of-book, sparse nodes, and pacing limits, cross-strike "dislocations" are indistinguishable from async-quote artifacts, and harvesting them is execution-dominated. Not registrable as a tradable-edge claim |

## Target architecture

Two processes touch research data; Postgres is the only data bus.

```
                              TWS (paper, 7497) — single EClientSocket
┌─────────────────────────────────────┴───────────────────────────────────────┐
│ TradingStuff.IbkrGateway (extended; stays the sole socket owner)            │
│  IbkrConnection ─ IbkrClientWrapper ─ IbkrRequestRegistry     (existing)    │
│  IbkrPacingGovernor ◄─ chokepoint for EVERY outbound call         (NEW)     │
│    ├ IbkrMarketDataClient (existing, retrofitted through governor)          │
│    ├ IbkrHistoricalClient: reqHeadTimeStamp / reqHistoricalData   (NEW)     │
│    ├ SubscriptionManager + line ledger + lease API                (NEW)     │
│    └ IbkrOrderClient (existing) + OrderIdStore (persisted)        (NEW)     │
│  ObservationRecorder: standing subs → Channel → batched COPY      (NEW)     │
│     writes ONLY: gateway.option_quote_events / underlying_tick_events /     │
│                  recorder_gaps / ibkr_order_map                             │
└──────────────┬──────────────────────────────────────────┬───────────────────┘
    HTTP /ibkr/* (dev bearer)                     Npgsql binary COPY (raw only)
┌──────────────┴───────────────────────┐      ┌───────────▼───────────┐
│ TradingStuff.ResearchService (NEW)   │◄────►│ Postgres "trading"    │
│  MigrationRunner (owns ALL schema)   │Npgsql│ (Aspire AddPostgres;  │
│  CapabilityProbeRunner + registry    │      │  day partitions)      │
│  ContractUniverseService/NodeSelector│      └───────────┬───────────┘
│  BackfillCoordinator (plan/checkpt)  │            daily verified export
│  RecorderOrchestrator + Coverage     │      ┌───────────▼───────────┐
│  SessionCalendarService/SessionClock │      │ Parquet artifacts     │
│  SurfaceSnapshotBuilder (as-of)      │      └───────────────────────┘
│  Feature/Label pipelines             │
│  ForecastRegistry (baselines)        │
│  StudyRunner + TrialRegistry         │
│  ConservativeExecutionSimulator      │
│  Research UI (React+Vite SPA, served │
│    from ResearchService wwwroot)     │
└──────────────────────────────────────┘
```

AuditDashboard gains links + a status strip only. Execution/Risk/MarketData services are untouched
except the `IbkrOptionMarketDataProvider` window/tradingClass param fix.

**Load-bearing decisions:**

- **The gateway writes raw observations to Postgres directly; ResearchService writes everything
  derived.** Live option ticks are unrecoverable, so the recording path gets the fewest failure
  points (one hop); ResearchService — redeployed constantly during research — can restart freely
  without losing a tick. Historical bars are re-requestable, so backfill takes the two-hop path
  (plan in ResearchService, execution via gateway HTTP under the governor, bars + request record
  persisted atomically together — which makes resume and gap detection trivial).
- **Pacing lives in the gateway, unconditionally.** It is a property of the single socket and
  must sit below every caller, including the existing execution-path quote fan-out (currently
  unbounded — the most likely next production failure). ResearchService sees only 429 +
  Retry-After backpressure.
- **No event transport.** One consumer, time-range reads, ~80 events/s average: Postgres is the
  channel; consumers poll recent partitions on 1-minute timers. AMQP and SSE explicitly cut.
- **Timezone doctrine:** UTC `timestamptz` canonical; intraday historical uses `formatDate=2`
  (epoch) so exchange-local strings never get parsed; session labels come only from the sessions
  table; `SessionClock` is the only type allowed to convert timezones; ES continuity is an
  explicit `node_assignments` join, never an implicit splice.
- **Contracts:** new `TradingStuff.ResearchContracts` project (references Contracts one-way).
  Research contracts churn weekly early on and must not ripple through five services and 125
  tests. **Persistence:** plain Npgsql + hand-written SQL + embedded ordered migrations (no EF —
  the hot path needs binary COPY; partition DDL is raw SQL regardless).
- **Research UI: a React+Vite SPA owned by ResearchService** (`src/TradingStuff.ResearchService/
  ClientApp/`, Vite build emitted to `wwwroot/` and served as static files by the same service —
  one deployable, no separate frontend host; `npm run dev` proxies to the service for local
  iteration; the csproj builds the app on publish). The SPA consumes ResearchService's JSON
  endpoints (`/research/*`). Auth split for this local-first operator surface: static assets and
  read-only research GETs are anonymous (matching the AuditDashboard's `/`), while anything
  mutating (study runs, recorder controls) keeps `RequireAuthorization`. The execution-path
  services remain fully authorized and get no frontend. This supersedes the earlier
  server-rendered-pages decision.
- **Schema:** `research.*` (ResearchService-written: instruments, contract_definitions,
  capability_probes, backfill_jobs/requests, bars, option_nodes, node_assignments, sessions,
  surface_snapshots/snapshot_nodes, feature/label/forecast tables, studies/runs/trials/metrics/
  artifacts, execution_scenarios) and `gateway.*` (gateway-written: option_quote_events,
  underlying_tick_events, recorder_gaps, ibkr_order_map). Raw event partitions: hot ~60 days →
  verified daily Parquet export → drop. Everything else kept forever (small).

## Market-data budget and option universe

| Consumer | Lines |
|---|---|
| Core underlyings: SPX, VIX, ES front, ES next, SPY | 5 |
| SPX option nodes: 6 DTE buckets {7,14,30,45,60,90} × 9 delta nodes (ATM C+P, 40Δ C+P, 25Δ C+P, 10Δ C+P, 5Δ P) | 54 |
| Rotation overlap (dual-subscribed during swaps) | ≤10 |
| Execution-path reserve + probes | ~15 |
| **Worst case** | **~84 / 100** (lease ceiling 90; alert at 80) |

Node identity is role-based (`30DTE-25DP`) with `node_assignments` mapping roles to concrete
conIds over time. Bootstrap: moneyness-based strike selection from spot + a VIX-scaled √T vol
guess, refined to delta targets once model Greeks arrive. Drift rule: re-evaluate at session open
and when |model Δ − target| > 0.10 sustained 30 min; ≥60-min dual-subscribe overlap; ≤6
swaps/hour. The grid is registry-versioned. Optional stretch: nearest 0/1-DTE ATM straddle
(+2 lines, high churn).

Volume estimate: ~50 contracts × ~2 events/s average over a ~23 h session ≈ 7M raw rows/day
(<1 GB; ~30–60 MB/day as Parquet); full underlying backfill ≈ 10M rows / 1–2 GB once. Postgres +
Parquet is three orders of magnitude below any new-infrastructure break-even.

## Phases

No calendar promises; sequencing + relative complexity. Full work-package detail, tests, and
acceptance criteria live in the approved plan; the contours:

- **Phase 0 — foundations (M):** Aspire `AddPostgres` + Npgsql wiring + MigrationRunner +
  migration 001 (instruments, `ibkr_order_map`, `capability_probes`); pacing governor as the
  outbound chokepoint (45 msg/s bucket, ≤54 hist req/10-min with BID_ASK ×2, 15-s identical
  cooldown, line ledger with execution reserve, orders bypass queuing but count tokens);
  order-id persistence; provider param fix; probe facts persisted. Acceptance: existing 125 tests
  green; fan-out serialized; gateway restart preserves the order map.
- **Phase 1 — recorder-first slice (L, calendar-critical):** subscription leases + heartbeats +
  reconnect replay (hooked into the existing 1101/1102 handling); ObservationRecorder
  (bounded Channel → batched COPY; drop-oldest + gap row on overflow — never block the EReader
  pump); node selection seeding the registered grid; RecorderOrchestrator; CoverageMonitor; the
  research-UI scaffold (React+Vite `ClientApp/` in ResearchService, Vite build wired into publish)
  with its first route, `/ui/coverage`. Acceptance: a full RTH+GTH session at ≥95% coverage, all
  gaps explained, visible in the UI.
- **Phase 2 — sessions + backfill (L, concurrent with Phase 1 operations):** session calendar +
  holiday data + `SessionClock`; historical client (`formatDate=2`; `keepUpToDate` deferred;
  `reqRealTimeBars` cut); resumable/idempotent BackfillCoordinator (the request table IS the
  checkpoint; identical rerun ⇒ zero new rows); ES expired-contract walker; gap detection;
  15-min intraday top-up; `/ui/backfill`; automated capability probe suite.
- **Phase 3 — surface snapshots (M):** 1-min as-of builder with quote-age/locked/crossed/missing
  discipline, raw→derived lineage, deterministic versioned rebuild; `/ui/surface`.
- **Phase 4 — features, labels, baselines, study runner (L):** cutoff-enforcing `IAsOfDataReader`
  (the leakage firewall); feature/label pipelines per the pre-registration doc; EWMA + HAR-RV
  forecasters; StudyRunner + immutable TrialRegistry; Parquet export + retention; `/ui/studies`.
  Acceptance: HAR-vs-EWMA walk-forward run twice with identical metric hashes.
- **Phase 5 — residual model + UI depth (M):** the model ladder to its gates, one-shot holdout
  last; calibration/diagnostics pages.
- **Phase 6 — implied-vs-forecast variance study (M, gated on ~3–6 recorded months).**
- **Phase 7 — structures + entry timing + conservative execution simulator (M).**
- **Phase 8 — shadow ops → gated small-live observation (M; separate authorization).**

## Build now / build later / do not build

**Now (Phases 0–2):** pacing governor; Postgres + migrations; order-id persistence; provider
param fix; leases + line ledger; surface recorder + node selection + coverage; session calendar;
historical client + backfill; capability-probe persistence; coverage/backfill UI pages.

**Later (Phases 3–7):** snapshot builder; feature/label pipelines; baselines + study runner +
trial registry + retention; residual models + diagnostics; implied-vs-forecast study; execution
simulator; structure/entry-timing comparison; shadow ops.

**Not (yet or ever):** deep historical option backfill (runtime-verified impossible); full-chain
tick recorder; Kafka/ClickHouse/new DB; RabbitMQ client for research; SSE/long-poll delivery;
`reqRealTimeBars`; Python ML service (deferred per `docs/PLAN.md`); generic next-bar
direction predictor; LSTM before linear rungs pass gates; dealer-gamma inference from daily OI;
HFT/queue-position research; NLP/news; automated live trading; optimization against paper fills;
CrossStrikeRelativeValueStudy; VX futures until entitlement is probe-verified.

## Sequence

1. ~~Rebase onto `main` (`f1e835d`)~~ — done.
2. This documentation commit.
3. Phases 0 → 1 (the Track B clock starts when Phase 1 lands) → 2 → 3 → 4 → 5 (studies to their
   gates) → 6 → 7 → 8.
4. Standing rule at every phase boundary: update `docs/STATE.md`; re-run the capability probes if
   TWS was upgraded; register every research variant before looking at results.
