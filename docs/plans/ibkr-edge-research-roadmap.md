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

> **Second amendment, 2026-08-01: recorder-first is retired.** The first amendment above, written
> hours earlier, said the ThetaData exposure was "confined to the implied-variance work". That was
> wrong, and the error was one of scope rather than fact: it treated a second vendor as a dependency
> to be contained instead of as a change in what the platform can know.
>
> The account holds a ThetaData **Options Pro** subscription: tick-level OPRA NBBO on SPX/SPXW back
> to 2012-06-01, with Greeks and implied volatility. The single fact this roadmap's sequencing was
> built on — *IBKR option history is weeks deep, so every unrecorded surface day is lost forever* —
> no longer binds. Roughly fourteen years of option history is available on demand.
>
> Everything ordered by that fact is therefore re-ordered below. The recorder is not cancelled; it
> is demoted from "the clock is running" to its real remaining job, the prospective execution
> record. See **Sequencing** for the new order and **Cross-instrument validation** for the second
> holdout axis this opens up.
>
> Verification status, in the capability matrix's own vocabulary: tier boundaries and the
> 2012-06-01 floor are **DOC** (vendor documentation, and two official pages disagree on
> concurrency limits). The repo's `LiveThetaTerminalTests` measurements — SPXW listing 2,226
> expirations from 2012-06-01, index and stock history returning 403 — were taken on a **FREE**
> subscription and are not evidence about this account. Nothing here is **RUNTIME** until the
> `RequiresThetaTerminal` probe runs against the Pro account and its findings land in
> `research.capability_probes`. Sequencing decisions that depend on unprobed facts are marked
> ⚠ below.

## Executive summary

1. **Firewall-first** (was: recorder-first; see the second amendment). With ~14 years of SPX option
   history purchasable on demand, no wall-clock constraint orders the work any more. The binding
   constraint is now the opposite one: unlimited cheap re-runs over a deep history is the most
   effective false-discovery machine in quantitative finance, and prospective data's real
   epistemic value was never the data — it was that you *could not peek*. So the controls that
   substitute for not being able to peek move to the front: the cutoff-enforcing `IAsOfDataReader`
   (leakage impossible at compile time, not by discipline) and the immutable `TrialRegistry`
   (N counted from rows written before their own results existed) are built **before** any bulk
   study capability exists, not alongside it.

   **Source split.** IBKR for everything that is not an option — it is the *deeper* source for the
   SPX index level (1-min verified to 2010, head 2004) where ThetaData's Index tier starts 2017,
   and the label's span decides whether walk-forward fold F1 exists at all. ThetaData for option
   chains, where IBKR has nothing. ES has no ThetaData substitute (no futures), so the Tier-2
   overnight block stays IBKR-only and stays capped at ~2023-08, exactly as already registered.
2. **Primary research slice (Track A):** the volatility-forecast-residual study — HAR-RV baseline
   on SPX session RV (~4,000 non-overlapping daily labels), pre-registered ≤15-feature residual
   correction, QLIKE + Diebold-Mariano + walk-forward, one-shot 2024→ holdout. Overnight-ES
   information folds in as a feature tier; a daily VRP-conditioning companion (VIX² − forward RV)
   runs alongside. Track A beats *a model*, not the market — its economic role is the fair-value
   leg of the eventual implied-vs-forecast study.
3. **Track B is now the prospective execution record, not the data source.** The same recorder,
   the same 54 nodes, the same lease and coverage machinery — but its justification changed and
   the change is not cosmetic. It no longer races a clock, and it is no longer the only way to
   obtain a surface. What it still uniquely provides is the one thing history cannot: **the gap
   between the quoted spread and what this account actually gets filled at**, at its size, through
   its routing. ThetaData's NBBO history supplies quoted spreads across fourteen years and every
   regime — strictly better than six months of self-recording for *measurement*. It cannot supply
   the effective-versus-quoted calibration factor, which needs one's own orders.

   Consequence: Track B may now run **in parallel with** research rather than ahead of it, and its
   acceptance criterion is a reconciliation, not an accumulation.
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

> **⚠ This ranking is stale as of 2026-08-01 and is retained only so the change is visible.** Four
> of its twelve criteria — data-now, backfill depth, prospective need, accumulation burden — were
> scored against "no option history exists". Studies 6–10 were pushed down the list by a constraint
> that has since been removed, so their scores understate them by an unknown margin, and rank order
> below #5 should not be acted on until they are rescored. Rescoring is deliberately **not** done
> inline here: it changes what gets built, so it belongs in a reviewed change with the new scores
> shown, not in a silent edit to a table.

| Rank | Study | Score | Verdict |
|---|---|---|---|
| 1 | VrpConditioningStudy (new) | 51 | Daily-grain: is VIX² − RV(t,t+21d) predictably wide/narrow given state? Runnable now; ~120 effective windows ⇒ conditioning knowledge, not P&L claims |
| 2 | IntradayVolatilityForecastResidualStudy | 50 | **Primary vertical slice** — see the pre-registration doc |
| 3 | OvernightToRegularHoursVolatilityStudy | 47 | **Folded into #2** as feature Tier-2 + ablation on the 2023-08+ subsample |
| 4 | ExecutionObservationStudy | 43 | Passive from day one inside Track B; sole legitimate cost-model source |
| 5 | SpxVixEsRegimeStudy | 41 | **Demoted**: open-ended "divergence" mining is a false-discovery machine. Becomes 2–3 pre-registered features + regime-slicing labels |
| 6 | ImpliedVsForecastVarianceStudy | 34 | **Economic destination (Track C). UN-GATED 2026-08-01** — was "gated on ≥6 months of recorded surface"; ⚠ with Options Pro history it is runnable as soon as the pipeline exists, and its score of 34 was depressed almost entirely by a prospective-data burden that no longer applies. **Rescore before acting on this ranking.** |
| 7 | TermStructureKinkStudy | 31 | ⚠ Now historically testable; was prospective-only |
| 8 | GlobalHoursToRegularHoursSurfaceStudy | 30 | ⚠ Now historically testable; GTH data verified available |
| 9 | EntryTimingStudy | 30 | ⚠ Partly historical — the surface half is; the fill half still needs the execution record |
| 10 | SurfaceStateTransitionStudy | 29 | ⚠ Now historically testable; the ~6-month accumulation requirement is void |
| 11 | CrossStrikeRelativeValueStudy | 22 | **Rejected**: with L1 top-of-book, sparse nodes, and pacing limits, cross-strike "dislocations" are indistinguishable from async-quote artifacts, and harvesting them is execution-dominated. Not registrable as a tradable-edge claim |

## Cross-instrument validation: train on SPY, test on SPX

A second holdout axis, opened up by having option history on both instruments. It is worth stating
precisely what it does and does not buy, because the temptation is to over-claim it.

**Why it is worth having.** Time-based holdouts are exhaustible: there is exactly one 2024→ window,
it opens once, and after that the hypothesis family is spent. A cross-instrument transfer test is
*orthogonal* to that axis and is not consumed by using it. A real economic mechanism — a variance
risk premium — should survive the change of instrument. An artifact of SPX's specific
microstructure, or of the fitting procedure, should not. That is a discriminating test no amount of
additional SPX data can perform.

**Direction: train SPY, test SPX.** Correct as stated, and for a reason worth recording: SPY has
the deeper history (IBKR 1-min from 2005, head 1993) against SPX's 2010. Training on the deep series
and testing on the shallow one maximises training data *and* conserves the scarcer series for the
test. The reverse would waste both.

**What it is NOT: an independent sample.** SPY and SPX track the same exposure at ~0.99 daily
correlation — the same days, the same regimes, the same crises. A model that overfits March 2020
overfits it on both. The transfer test therefore yields **no independent-sample p-value** and cannot
substitute for the time-based holdout. It answers "is this an instrument-specific artifact?", not
"is this real?". Both axes are required; neither is redundant with the other. Reporting a transfer
result as though it were an out-of-sample significance test would be a category error, and is
forbidden by the same rule that forbids reusing the one-shot holdout.

**Six things that must be handled or the test is invalid.** The first three are already documented
on `VolatilityPresets`, which states outright that the transfer "should be measured rather than
assumed" and points at `VolatilityComparison` for fitting the calibration. Running that comparison
is a **precondition** of any transfer claim, and its fitted calibration is a registered artifact.

1. **Dividends.** SPY distributes four quarterly lumps of ~0.3–0.4 %, each a mechanical gap. SPX is
   a price index absorbing the same cash continuously and never shows it. Unadjusted, SPY carries
   four variance spikes a year that SPX does not.
   `RealizedVolatilityOptions.ExDividends` is deliberately empty and **must be populated** first.
2. **Stale SPX open.** The printed SPX open is assembled from staggered constituent opening prints
   and is not a tradeable simultaneous price. The pre-registration's 09:35-start sensitivity variant
   exists for this; if the transfer conclusion differs between variants, the conservative one governs.
3. **Microstructure bounce.** SPY is one security with bid-ask bounce; SPX is an average over 500
   names and is correspondingly smoother at high frequency. SPY's 1-min RV is biased *upward*
   relative to SPX's. Subsampled 5-minute sampling mitigates this and does not eliminate it.
4. **American versus European exercise — the one that actually threatens the implied leg.** SPX and
   SPXW are European; SPY options are American-style on an ETF. The model-free implied-variance
   integral assumes European exercise, so an early-exercise premium contaminates any SPY variance
   strip built with the same code path. `ModelFreeVariance` must either handle this explicitly or
   the SPY implied leg is biased by an unknown amount — and "unknown" is not a number a study may
   quietly carry. Resolve before any SPY implied-variance figure is registered.
5. **Settlement style.** SPX monthlies are AM-settled against SET opening prints; SPXW are
   PM-settled; SPY options are PM-settled and American. These are different objects and the
   `tradingClass` discipline that keeps them apart at contract resolution applies here too.
6. **The premium level does not transfer — only its conditioning structure does.** SPX and SPY have
   different investor bases (institutional index-hedging demand versus a mixed retail/institutional
   flow), so the level of the variance risk premium differs even where its state-dependence agrees.
   **Design rule: transfer the structure, refit the level.** No intercept, and no no-trade-band
   threshold, may be carried from SPY to SPX without being refitted on SPX *training* data. A band
   fitted on SPY and applied to SPX is a silent miscalibration that will read as edge.

**Cost models do not transfer either.** SPY options are penny-quoted and far tighter; SPX ATM
spreads run ~3–5 % of premium. Execution economics are fitted per instrument, always.

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

> **Phase numbers are stable identifiers, not an execution order.** Re-numbering to match the
> 2026-08-01 resequencing was considered and rejected: `CLAUDE.md`'s model-and-effort policy routes
> work *by phase number* ("Phase 1–3 and Phase 7 → Sonnet", "Phase 5, 6, 8 → Opus/high"), and
> `docs/STATE.md` references the numbers throughout. Renumbering would have silently re-routed every
> future task to the wrong model — a defect with no symptom, which is the worst kind. The numbers
> below therefore keep their original meaning; **the order in which they run is in `Sequencing`.**
> New work gets the next free number rather than a letter suffix, for the same reason.

- **Phase 0 — foundations (M):** Aspire `AddPostgres` + Npgsql wiring + MigrationRunner +
  migration 001 (instruments, `ibkr_order_map`, `capability_probes`); pacing governor as the
  outbound chokepoint (45 msg/s bucket, ≤54 hist req/10-min with BID_ASK ×2, 15-s identical
  cooldown, line ledger with execution reserve, orders bypass queuing but count tokens);
  order-id persistence; provider param fix; probe facts persisted. Acceptance: existing 125 tests
  green; fan-out serialized; gateway restart preserves the order map.
- **Phase 1 — recorder slice (L; *was* calendar-critical, no longer):** subscription leases + heartbeats +
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
- **Phase 6 — implied-vs-forecast variance study (M; ⚠ NO LONGER CALENDAR-GATED).** Was "gated on
  ~3–6 recorded months". With option history on hand it is gated only on Phases 4, 9 and 7. This
  is the program's economic destination and it moved from roughly a year out to next-in-line.
- **Phase 7 — structures + entry timing + conservative execution simulator (M; PULLED FORWARD).**
  The `ConservativeExecutionSimulator` and `CostModel` are now on the critical path rather than
  downstream of a year of recording: a backtest over fourteen years is only as honest as the costs
  charged against it. Spread *measurement* now comes from ThetaData NBBO history across all
  regimes; the effective-versus-quoted calibration still comes from Phase 8's own fills, and until
  it exists the simulator runs at its 100 %-of-half-spread scenario and says so.
- **Phase 8 — execution reconciliation → gated small-live observation (M; separate authorization).**
  Re-scoped. Was a six-month accumulation gate ahead of any live decision; is now a
  realized-versus-modelled cost reconciliation that runs **in parallel** with Phases 5–7 rather
  than after them. What it must still establish is unchanged and is not negotiable: signals stored
  daily *before* outcomes, and realized fills reconciled weekly against the model, with realized
  > 1.5× modelled halting trading and reopening the execution ledger.
- **Phase 9 — ThetaData chain ingestion (M; NEW).** Historical SPX/SPXW chains into the canonical
  schema behind the same provenance discipline as everything else: vendor identifiers confined to
  an adapter, `research.capability_probes` rows for what the account actually serves, and the
  as-of-date correctness of expiration lists **verified rather than assumed** — a survivorship-free
  claim is exactly the negative claim (`DECISIONS.md` §16 class (c)) that has to name its check.
  Reuses `ThetaDataChainLoader`; the integral, strike selection and constant-maturity interpolation
  are already vendor-neutral. ⚠ Scope depends on the capability probe.
  **`CLAUDE.md`'s model-policy table has no row for Phase 9 — add one before starting it.**
  Recommended: Sonnet/high, on class (c).

## Build now / build later / do not build

**Now (Phases 0–2):** pacing governor; Postgres + migrations; order-id persistence; provider
param fix; leases + line ledger; surface recorder + node selection + coverage; session calendar;
historical client + backfill; capability-probe persistence; coverage/backfill UI pages.

**Later (Phases 3–7):** snapshot builder; feature/label pipelines; baselines + study runner +
trial registry + retention; residual models + diagnostics; implied-vs-forecast study; execution
simulator; structure/entry-timing comparison; shadow ops.

**Struck from this list 2026-08-01 — ~~deep historical option backfill~~.** It was listed as
"runtime-verified impossible", and it was: *from IBKR*. Options Pro serves expired contracts, so a
chain reconstructed as-of a past date is genuinely survivorship-free rather than the survivorship
trap the ban existed to prevent. The premise is gone and the ban goes with it. The replacement is
narrower and still binding: **no chain may be reconstructed from *currently listed* strikes**, and
any as-of reconstruction must verify that the vendor's own expiration list is as-of correct rather
than assuming it.

**Not (yet or ever), unchanged:** full-chain tick recorder; Kafka/ClickHouse/new DB; RabbitMQ
client for research; SSE/long-poll delivery; `reqRealTimeBars`; Python ML service (deferred per
`docs/PLAN.md`); generic next-bar direction predictor; LSTM before linear rungs pass gates;
dealer-gamma inference from daily OI; HFT/queue-position research; NLP/news; automated live
trading; optimization against paper fills; CrossStrikeRelativeValueStudy; VX futures until
entitlement is probe-verified.

## Sequencing

**Superseded 2026-08-01.** The original order was `0 → 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8`, with the
note "the Track B clock starts when Phase 1 lands". That clock does not exist any more. The order
below is what replaces it; phase numbers keep their meanings (see the note under **Phases**).

0. **Pre-flight, blocking, before any data is collected.** (a) Postgres has no data volume — a
   multi-hour drain would be destroyed on app-host stop. (b) Recorded ticks carry no
   delayed-versus-live provenance and the default is *delayed*, so a whole session recorded on
   stale quotes is afterwards indistinguishable from a live one.
1. **Probe the ThetaData account** and persist the findings into `research.capability_probes`.
   Every ⚠ in this document resolves here. Documentation is not evidence, and the repo's own
   `LiveThetaTerminalTests` numbers were measured on a free subscription.
2. ~~Phases 0, 1, 2~~ — shipped. Phase 1 and 2 **acceptance criteria remain unmet**: no session has
   been recorded and no backfill drained. Both are data criteria, and both are now cheaper to meet
   than they were, but neither is met by having written the code.
3. **Phase 4 — the firewall and the registry, before anything that can be over-searched.**
   `IAsOfDataReader`, feature/label pipelines, baselines, `StudyRunner`, `TrialRegistry` (migration
   015, already shipped). This is the deliberate inversion: Phase 4 was fifth in line under the old
   plan and is first under this one, because a deep history plus cheap re-runs makes leakage and
   over-search the binding risks rather than data scarcity.
4. **Phase 9 — ThetaData chain ingestion.** Gated on step 1.
5. **Phase 5 — Track A to its gates**, one-shot holdout last, plus the SPY→SPX transfer test as
   the orthogonal axis (see **Cross-instrument validation** — it is a robustness check, not a
   second p-value, and may not be reported as one).
6. **Phases 6 and 7 together** — implied-vs-forecast against real history, with the conservative
   execution simulator that makes its costs honest. These are now the near-term work rather than
   the far horizon.
7. **Phase 3 — surface snapshots** — demoted to here. Snapshots serve the *live* surface; Track A
   never needed them and Track C can be built from historical chains. Nothing upstream waits on it.
8. **Phase 8 — execution reconciliation → gated small-live**, running in parallel from step 5
   onward rather than as a terminal gate. Live capital still requires separate explicit
   authorization on top of the existing triple opt-in.

**Standing rules, unchanged and now load-bearing rather than merely prudent:** update
`docs/STATE.md` at every phase boundary; re-run the capability probes after any TWS or Terminal
upgrade; and register every research variant **before** looking at its results. That last one was
cheap insurance when the data was scarce. With fourteen years of history and a re-runnable
backtest it is the principal defence the program has, and the hard cap of ten registered variants
before the holdout opens is what gives it teeth.
