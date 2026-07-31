# State

Updated: 2026-07-31

## Done

- Aspire/.NET solution scaffolded.
- Execution, risk, market-data, audit dashboard, contracts, service defaults, and AppHost projects created.
- Paper options workflow implemented with strategy validation, Greeks-aware risk, deterministic quotes, fills, lifecycle events, and local auth.
- Tests pass: 149/149 (plus 5 Postgres-gated integration tests). Full solution builds.

### IBKR integration (stages 1-4 and 6 of `.claude/skills/ibkr/references/migration-plan.md`)

- **IBApi vendored** at `third_party/IBApi` — TWS API **10.45.01**, extracted from the official
  Windows installer (IBKR publishes no NuGet package). See `third_party/IBApi/README.md`.
- **`TradingStuff.IbkrGateway`** service owns the single TWS socket: connection lifecycle, EReader
  pump, reconnect with backoff, request/error correlation, and a health check that reports the real
  socket state.
- **Contract resolution** with a conId cache; **option chains** via `reqSecDefOptParams`;
  **streaming quotes with Greeks** via `reqMktData` + `tickOptionComputation`.
- **`MarketDataService` provider switch** on `MarketData:Source` — `ibkr-live` / `ibkr-delayed` route
  to the gateway, anything else (including a typo) falls back to the deterministic feed.
- **AppHost rewired**: the broker is no longer a bogus HTTP `AddExternalService`; it is
  `ibkr-host` / `ibkr-port` / `ibkr-client-id` / `ibkr-market-data-type` parameters plus the gateway project.
- **Verified live** against a `DU` paper account, TWS server version 223: real SPY chain
  (489 strikes, correct trading class), real conIds, real Greeks.

### IBKR order routing (stage 6)

- **`IbkrOrderBuilder`** builds combo (BAG) orders: GCD-reduced leg ratios + spread count, signed
  per-spread net price, order-type/TIF mapping, `orderStatus` → lifecycle mapping.
- **`IbkrOrderTracker`** assembles `orderStatus` / `execDetails` / `commissionAndFeesReport` into
  order state, dedupes executions on `ExecId`, and holds terminal outcomes against late chatter.
- **`IbkrOrderClient`** is the only caller of `placeOrder`, gated on `EnsureTradingPermitted()`.
- **`IOrderRouter`** in ExecutionService: `paper` (default) or `ibkr`, per `Execution:Router`.
- **Reconciliation**: `GET /ibkr/orders/open` reports what TWS considers open, including orders this
  process never placed.
- Request ids, ticker ids, and order ids now share **one sequence seeded from `nextValidId`** —
  previously both started at 1, so an `error(id, ...)` callback could not be routed unambiguously.
- **Index option support**: `IND` underlying resolution, `tradingClass` selection (SPX vs SPXW),
  `Contract.TradingClass` on resolution, and `Order.OutsideRth` for global trading hours.
- **A complete round trip filled on the paper account** (2026-07-31, pre-market):
  a 1-lot SPXW 7435/7440 call vertical opened at **3.80 debit** (order 9, permId 1749246243) and
  closed at **3.40 credit**, both at the natural, with correct per-leg fills and commissions.
  Account left flat with no resting orders.
- Reconnect backoff now treats a short-lived session as a failure. TWS can accept a socket and
  immediately reset it, and the previous logic reconnected at the base interval indefinitely.

### IBKR account and position sync (stage 5)

Written and **verified live against the `DU` paper account on 2026-07-31**, including a filled
round trip that put a real position through the portfolio read (details at the end of this section).

- **`IbkrAccountClient`** serves `GET /ibkr/account/portfolio` from `reqAccountSummary` (buying
  power, falling back through `BuyingPower` → `AvailableFunds` → `ExcessLiquidity`),
  `reqPositionsMulti` (positions), and `reqPnL` (daily P&L).
- **The reqId-scoped request variants**, not `reqPositions` / `reqAccountUpdates`. The account-wide
  forms carry no request id, so `IbkrRequestRegistry` cannot correlate them or fault them on error.
- **All three are opened once per connection and read many times**, not subscribed and cancelled per
  read — see the account-summary cap below. They stay registered in `IbkrRequestRegistry` for the
  life of the connection, and the feed is keyed on `IbkrConnectionStatus.ConnectedAt` so a reconnect
  rebuilds rather than serving values frozen at the disconnect. A portfolio read is therefore zero
  round trips. `reqPnL` has no `...End` callback and settles on its first non-sentinel daily P&L.
- **`ExistingGreeks` is built by quoting the open positions** — IBKR has no portfolio-Greeks API —
  scaled by quantity × multiplier so it sums with the order exposure `PortfolioRiskEvaluator`
  computes. Capped at `IBKR:MaxPositionsQuoted` (50) against the 100-line market data limit, and
  cached for `IBKR:PortfolioCacheSeconds` (5) because every order submission reads the portfolio.
- **`IbkrPortfolioProvider`** in ExecutionService, selected by `Portfolio:Source=ibkr`. Anything
  unrecognised stays on `DevelopmentPortfolioProvider`, matching how `Execution:Router` and
  `MarketData:Source` degrade.
- **No fallback on failure.** An unreadable portfolio raises `PortfolioUnavailableException`, which
  `POST /orders` turns into a 503 with no order placed. Substituting development figures when the
  broker is unreachable would approve a real order against numbers nobody checked.
- **Gaps are reported rather than defaulted**: `DailyPnLAvailable`, `GreeksComplete`, and
  `NonOptionPositionCount` ride on the gateway response and are logged as warnings by the provider.
  A daily P&L defaulted to zero silently disables `MAX_DAILY_LOSS`.
- **Known limit:** `PositionSnapshot` carries an `OptionContract`, so equity and futures positions in
  the account have no representation. They are counted and warned about, not counted against the
  Greek limits.

**Live verification (2026-07-31, ~11:30 ET).** A 1-lot SPY 740/741 call vertical opened at a 0.56
debit and closed at a 0.54 credit through the full `Execution:Router=ibkr` +
`Portfolio:Source=ibkr` path, leaving the account flat. With the position open the portfolio read
returned:

| Check | Observed |
|---|---|
| Contract mapping | `SPY 2026-07-31 740 C`, `tradingClass=SPY`, `multiplier=100` |
| `avgCost` ÷ multiplier | filled @ 1.90 → `AveragePrice` 1.910333 |
| Greeks × quantity × multiplier | long 740C delta 52.03 (≈ 0.52 × 1 × 100) |
| Short leg sign flip | short 741C delta −42.46, theta **+**127.28 against the long's −159.87 |
| Aggregate `ExistingGreeks` | delta 9.56 = 52.03 − 42.46 |
| Flags | `DailyPnLAvailable` and `GreeksComplete` true, `NonOptionPositionCount` 0 |

Buying power and daily P&L both moved with the trade. Six consecutive portfolio reads succeeded,
confirming the subscription rewrite: the previous design failed on the third. The close reported an
average fill price of **−0.54** — signed, so the credit survived rather than being discarded.

Two earlier fixes re-confirmed in the same run: the BAG summary execution was excluded (two leg
fills, not three), and legs were attributed by conId rather than arrival order — leg 1's execution
arrived before leg 0's.

### Open question: SPX/SPXW combos park in PreSubmitted

Not resolved, and **not a defect in this codebase** — the same code fills SPY combos immediately.

Every SPXW combo sent on 2026-07-31 during regular hours was accepted by TWS and sat at
`PreSubmitted` indefinitely with **no error and no `whyHeld`**. Ruled out by direct test: price (held
at a 3.50 limit against a 3.30 natural, at 4.50, and as a plain `MKT` order), session (11:20 ET on a
Friday, inside 09:30–16:15), `IBKR:OutsideRegularTradingHours` (held with it both false and true),
TWS precautionary settings (disabled, and no error 163 was raised), and combo construction (leg
actions, ratios, and the signed net verified correct in `IbkrOrderBuilder`, and identical in shape to
the SPY order that filled).

The remaining candidates are account-level: index-option trading permission on the paper account, or
SPX combo routing needing a destination other than `SMART`. TWS's own order row shows a hold reason
that the API does not expose — check there first. Note the SPXW round trip recorded further up this
file did fill, so this is a change in account or session state rather than something that never
worked.

### Bugs the stage 5 live run exposed (2026-07-31)

Both found by running against the paper account, both fixed, neither reachable by the unit suite
before the regression tests added with them.

- **An HTTP retry placed the same order at the broker twice.** `AddServiceDefaults` applies
  `AddStandardResilienceHandler` to every internal client, which retries on its 10s per-attempt
  timeout. The gateway waits `IBKR:OrderSettleTimeoutSeconds` (20s) for an order to settle, so a
  combo that rested longer than 10s was re-sent: one SPXW vertical went out as IBKR order 16, was
  retried as order 17, and ExecutionService recorded only 17's rejection while **order 16 stayed
  working at TWS, unknown to the service that placed it**. Two fixes, deliberately independent:
  `ServiceClientConfiguration.DisableAutomaticRetries` strips retries from the order client
  (the cause), and `IbkrOrderTracker.TryTrack` now claims the internal order id with an atomic
  `TryAdd` so one internal order can only ever become one broker order (defence in depth, and it
  holds against any duplicate caller, not just this retry policy).
- **TWS caps concurrent `reqAccountSummary` subscriptions at two, and `cancelAccountSummary` does
  not release them.** Reads one and two succeed; the third fails with
  `error 322: Maximum number of account summary requests exceeded; desubscribe to previous request
  first`. The cancel is issued with the correct request id after every read, is well-formed
  (`createCancelAccountSummaryRequestProto` sets `ReqId`, `Util.IsValidValue` passes), does not
  throw, and reports no error — TWS keeps counting the subscription regardless. **This cap is
  undocumented**: IBKR's account-summary and message-code pages do not mention it, and 322 is
  documented only as "duplicate ticker id". Fixed by opening the account streams once per connection
  instead, which is the shape these APIs are designed for anyway.

### Bugs the live round trip exposed

- **Combo fills counted the BAG summary as a leg.** IBKR reports three executions for a two-leg
  spread — the BAG carrying the net price, plus one per leg. The BAG row is now skipped, and leg rows
  are attributed by conId rather than arrival order (legs do not fill in request order, and one leg
  can fill in pieces while the other has not started).
- **Net-credit fills reported an average price of 0.** A combo's `avgFillPrice` is signed and arrives
  negative for a credit; it was being run through the price converter, which correctly rejects
  negatives for an option quote but discards every credit here.

### Bugs fixed

- `PaperExecutionEngine` correlated quotes to legs on the whole `OptionContract` record. Record
  equality covers every property, so any broker-enriched quote threw `KeyNotFoundException`
  mid-order. Now keyed on `OptionContractKey`; an unquoted leg fails the order instead of throwing.
- `ServiceClientConfiguration` moved from `ExecutionService` to `ServiceDefaults` so other services
  can configure internal clients without depending on ExecutionService.
- `IbkrConnection.Dispose` threw `ObjectDisposedException` during host shutdown, surfacing as an
  unhandled crash on every stop.

### Research platform planning (milestone 2, 2026-07-31)

Planned end-to-end and documented; no research code written yet. Deliverables:
`docs/plans/ibkr-edge-research-roadmap.md` (architecture, ranked studies, phases),
`docs/research/ibkr-data-capability-matrix.md` (runtime-verified data feasibility),
`docs/research/volatility-forecast-residual-study.md` (pre-registered first study),
`docs/research/literature-evidence-matrix.md` (~30 verified sources + platform controls).

Key runtime-verified facts (read-only wire probes against paper TWS, 2026-07-31):

- **Live streaming entitlements are shared to the paper account** — `marketDataType=1` came back
  for SPX, SPY, ES, and an SPXW option (Cboe indexes + OPRA + CME all active). The
  `ibkr-market-data-type=3` default is safe but no longer necessary for research recording.
- **SPX option history is weeks deep even for long-listed contracts** (Aug-2026 monthly 7500C:
  BID_ASK head 2026-06-10; an April request returns no data), and expired options return nothing.
  Option data must be recorded live-forward; every unrecorded day is unrecoverable.
- Deep underlying history exists: SPX 1-min TRADES pulled at 2010 (head 2004-03-04), SPY 1-min at
  2005 (head 1993-01-29), VIX 10y daily (head 2005-10), ES via per-expired-contract walk (~2–3y
  each; CONTFUT rejects past `endDateTime`, error 10339).
- SPX/SPXW GTH (overnight) option bars exist (`useRTH=0` bars from 19:15 CT).
- Historical bar timestamps arrive exchange-local with `formatDate=1`; research ingestion will use
  `formatDate=2` (epoch) exclusively.

### Research platform Phase 0 (milestone 2, 2026-07-31) — DONE

- **Pacing governor** (`IbkrGateway/Pacing/`): every outbound socket call now flows through
  `PacedSocket` → `IbkrPacingGovernor`. Budgets ~10% inside TWS's documented limits: 45 msg/s
  token bucket (order placement/cancel jumps the queue but still consumes tokens), historical
  window 54/10 min with BID_ASK counting double + 15 s identical-request cooldown + 5-per-2s
  same-contract limit (throws `IbkrPacingRejectedException` with `RetryAfter` when the wait would
  exceed the acquire timeout — the future backfill coordinator's 429), and a market-data **line
  ledger** capped at 90 of the account's 100 with 10 lines reserved for execution-class transient
  quotes. The previously unbounded quote fan-out now queues at the ledger instead of blowing the
  account cap. Metrics on Meter `TradingStuff.IbkrGateway`; `GET /ibkr/pacing` reports the ledger.
- **Broker order-id persistence** (`gateway.ibkr_order_map` + `Persistence/OrderIdStore.cs`): the
  internal-order → broker-order mapping is written BEFORE `placeOrder` and consulted on every
  placement, so a caller retry after a gateway restart is refused instead of transmitting a second
  live order (verified by an integration test that restarts the store). Postgres-down degrades
  loudly (LogCritical) unless `IBKR:RequireOrderPersistence=true`, which refuses orders instead.
  Cancel no longer requires the trading gate — cancelling reduces risk and must work exactly when
  the gate slams shut.
- **Postgres wired for real**: AppHost `AddPostgres`/`AddDatabase("trading")` (was a decorative
  `AddContainer`), connection string referenced by the gateway and the new
  **TradingStuff.ResearchService**, whose advisory-locked `MigrationRunner` owns ALL schema
  (including `gateway.*`) via embedded ordered migrations. Migration 001: schemas, seeded
  `research.instruments`, `research.capability_probes`, `gateway.ibkr_order_map`. Migration 002:
  the 2026-07-31 probe session persisted as ~26 capability rows (`GET /research/capabilities`).
  New `TradingStuff.ResearchContracts` project for research-domain records.
- **`IbkrOptionMarketDataProvider` param fix**: `window` and `tradingClass` now survive the
  MarketDataService hop, so SPXW is reachable without calling the gateway directly.
- Tests: 125 → **149** (pacing budgets under `FakeTimeProvider`, provider forwarding, migration
  set), plus 5 `Category=RequiresPostgres` integration tests (migration idempotency + probe seed +
  order-map restart survival + never-transmitted compensation + broker-id integrity) run via
  `TRADING_TEST_POSTGRES="Host=...;Username=postgres;Password=..." dotnet test --filter Category=RequiresPostgres`.

### Research platform Phase 1 (milestone 2, 2026-07-31) — CODE-COMPLETE, ACCEPTANCE NOT MET

**Status corrected 2026-07-31.** This section previously said "DONE". That was an overclaim and is
retracted. Phase 1's own acceptance criterion, written in
`docs/plans/ibkr-edge-research-roadmap.md`, is *"a full RTH+GTH session at ≥95% coverage, all gaps
explained, visible in the UI"*. No such session was ever recorded. The phase was marked done on
unit and Postgres tests alone — **not one line of the recorder had ever run against TWS.**

That was not a bookkeeping slip. It let a fatal defect ship undetected: the recorder subscribed
with a bare conId and a hardcoded `Exchange = "SMART"`, which TWS **rejects for index conIds with
error 200**, so SPX and VIX — two of the three core underlyings, including the price series the
entire research programme depends on — recorded **nothing at all**, silently. Every unit test
passed the whole time, because they all stub the socket. Found only when a live probe was finally
run (see the Phase 2 entry). **The Track B recording clock had not actually started.**

The code below is written and unit/Postgres-tested. Treat it as unverified against the broker
until a live session has been recorded end to end.

- **Migration 003**: `research.option_nodes` (54-row registered grid, 6 DTE buckets × 9 delta
  nodes, seeded in SQL), `research.node_assignments` (role → conId over time, `assigned_from`/
  `assigned_to`), `gateway.option_quote_events` + `gateway.underlying_tick_events` (daily
  RANGE-partitioned, append-only, each with a `DEFAULT` partition safety net), `gateway.recorder_gaps`.
- **`PartitionMaintainer`** (ResearchService) creates 3 days of partitions ahead on a 6 h sweep.
  **Verified directly against Postgres 17** that once a row lands in a table's `DEFAULT` partition,
  Postgres permanently refuses to create the real partition for that date afterward (`updated
  partition constraint for default partition ... would be violated by some row`) — recovering a
  stray row needs a manual one-time migration. Mitigated, not automated: fast retry (1 min, not
  6 h) after a failed sweep, and a `LogCritical` + row-count check every sweep if anything is ever
  sitting in `..._default` so it cannot go unnoticed.
- **Gateway `SubscriptionManager`** (`IbkrGateway/Subscriptions/`): standing leases on top of the
  pacing governor's line ledger — grant/heartbeat/release, `LeasePriority` (CoreRecording/
  Rotation/AdHoc; `ExecutionReserved` is refused through this API, reserved for the gateway's own
  transient quotes), eviction after 3 missed heartbeats. `IbkrConnection` gained
  `SubscriptionsMustReplay`, raised on a fresh `startApi` handshake and on TWS's 1101
  ("connectivity restored, data lost") — both reset the pacing governor's line ledger first (a
  fresh `EClientSocket` has zero real TWS lines regardless of what the ledger still thinks) then
  replay every lease in priority order with a fresh ticker id.
- **Gateway `ObservationRecorder`** (`IbkrGateway/Recording/`): `RecordingTickSink` (one per
  standing subscription) accumulates `tickPrice`/`tickSize`/`tickOptionComputation` into full-state
  rows with a changed-fields bitmask, computes locked/crossed, tags the first tick after a replay
  so its lease's gap closes automatically. `ITickSink` widened (`tickSize` routed for the first
  time; `ApplyOptionComputation` now carries IV and underlying price). Two bounded
  `Channel<T>` (50k, `DropOldest`) feed background batched Npgsql binary `COPY` loops
  (5000 rows/500 ms). Gap bookkeeping (`OpenGapAsync`/`CloseGapAsync`, deduplicated per scope)
  records `disconnect`, `line_evicted`, `buffer_overflow`, `write_failure`. A dropped-batch write
  failure loses the batch (documented — the `write_failures` counter records how much, the gap
  records that it happened); a saturated channel's `buffer_overflow` gap now **closes** once the
  backlog drains and a later enqueue observes headroom again — the first version of this only
  opened it, verified as a real defect and fixed with two deterministic Postgres tests
  (`ObservationRecorderPostgresTests`) rather than a timing-based one (an earlier attempt at an
  end-to-end "blast 50k ticks and watch it overflow" test was flaky against a live drain loop and
  was replaced with direct `OpenGapAsync`/`CloseGapAsync` tests).
- **`ContractUniverseService`/`NodeSelector`** (ResearchService): bootstrap-only in v1 — strikes
  picked by a fixed moneyness offset per node role (spot proxy = median strike of a wide chain-window
  response), never by delta, since there is no delta to target before anything has streamed.
  **Delta-based drift detection/reassignment is deliberately deferred** — see Left, below.
  `UpsertAssignmentAsync` closes the prior `node_assignments` row and opens a new one only when the
  conId actually changes (idempotent reruns are no-ops); `FOR UPDATE` added as cheap insurance
  against a future concurrent caller even though today's only caller is sequential.
- **`RecorderOrchestrator`** (ResearchService): every 2 min, bootstraps nodes, ensures leases for
  **SPX, VIX, SPY** (core underlyings) and every current node assignment, heartbeats everything
  every 20 s. **ES is deliberately deferred to Phase 2** — front-month resolution needs expiry/roll
  logic that Phase 2's `EsContractWalker` already owns; duplicating it here for Phase 1 would be
  exactly the premature abstraction the plan argues against.
- **`CoverageMonitor`** + `GET /research/coverage`: per-conId and overall coverage ratio (distinct
  1-minute buckets with data ÷ total minutes) over a window (default trailing 24 h — this measures
  a fixed UTC window, not yet an actual RTH/GTH session; `SessionCalendarService` in Phase 2 will
  let this align to real session boundaries), plus overlapping `recorder_gaps` rows.
  `GET /research/nodes` reports current assignments.
- **`/research/*` is anonymous** (matching AuditDashboard's existing unauthenticated `/`) — a
  deliberate posture change from Phase 0's `RequireAuthorization()` on `/research/status` and
  `/research/capabilities`, applied consistently to the new endpoints too.
- **React + Vite research UI** (`src/TradingStuff.ResearchService/ClientApp/`), build wired into
  the csproj (`npm ci && npm run build` before the C# build), output to `wwwroot/` served under
  `/ui`. First and only page: **`/ui/coverage`** — overall coverage % (color-coded against the 95%
  threshold), per-conId table sorted worst-first, gaps table (most recent first, open gaps
  flagged), manual + 30 s auto-refresh, clear loading/error states.
- Tests: 149 → **182** unit (`RecordingTickSinkTests` — pure, no Postgres — plus pacing-ledger
  reset coverage), plus 5 → **18** `Category=RequiresPostgres` integration tests.

### Phase 1 adversarial review — 8 confirmed defects fixed, 2 refuted, 12 investigated

A repeat of Phase 0's practice: a multi-dimension review (concurrency/lifecycle, SQL/persistence,
data-integrity) against the Phase 1 diff, findings independently re-verified by direct code
reading and, where possible, live-Postgres reproduction rather than trusted at face value — the
review tool's own finding-to-verdict pairing across concurrently-completing reviewer agents proved
unreliable for a couple of entries, so every "confirmed" claim below was re-traced by hand before
being accepted. Fixed, with a regression test for each unless noted:

- **`node_assignments` could silently hold two "current" rows for one node.** `SELECT ... FOR
  UPDATE` does NOT prevent this under Read Committed: a blocked FOR UPDATE query re-checks its
  WHERE clause against the row's new committed version once the blocking transaction's lock
  releases, and a row that no longer matches (`assigned_to` just got set) is silently excluded —
  the blocked caller then sees "no current row" and inserts its own. **Reproduced directly against
  live Postgres 17** (two concurrent transactions, exact race). Fixed with a real guarantee:
  `CREATE UNIQUE INDEX node_assignments_one_current_idx ON research.node_assignments (node_id)
  WHERE assigned_to IS NULL` (migration 003) plus a catch-and-retry-once in
  `NodeSelector.UpsertAssignmentAsync`. Test reproduces the race with two concurrent callers and
  asserts exactly one current row survives.
- **A second `SubscriptionsMustReplay` trigger arriving mid-pass was silently dropped**, with no
  follow-up scheduled — any lease that failed to reissue on the in-flight pass had no other retry
  path for the rest of the session. `SubscriptionManager.ReplayAsync` now coalesces instead of
  discarding: a losing trigger sets a pending flag the in-flight pass checks before releasing its
  gate, looping for one more full pass if set.
- **The OLD `RecordingTickSink`'s registry entry leaked on every 1101-triggered replay** (TWS
  "connectivity restored, data lost" — the socket never drops, so `registry.FailAll` never runs,
  unlike a real disconnect). Fixed: the replay pass now explicitly removes the previous ticker's
  registry entry once the new one is issued.
- **A lease's `LineLease` could go stale on a failed replay attempt** and later get disposed by
  `TeardownAsync` — decrementing the pacing ledger for a line that was never actually re-acquired
  post-reset, letting the governor silently over-admit relative to the true TWS-side count. Fixed:
  a failed replay attempt now clears the lease's `LineLease` reference.
- **Reassigning a node to a new conId leaked its old lease forever.**
  `RecorderOrchestrator._nodeLeasesByConId` was keyed by conId and only ever grew; an old conId
  simply stopped appearing in `NodeSelector.GetCurrentAssignmentsAsync`'s result and was never
  revisited, never released, and kept being heartbeated — permanently consuming a research line
  per reassignment. Fixed: tracked by nodeId instead, with the old lease explicitly released on
  detected reassignment.
- **Coverage silently excluded conIds with zero ticks** — a plain `GROUP BY con_id` over the raw
  event tables cannot produce a row for a conId that never ticked, so a fully-dead subscription
  (the worst case this report exists to catch) was invisible rather than showing 0%. Fixed:
  `CoverageMonitor` now unions tick counts with every current `node_assignments` conId, defaulting
  missing ones to 0 minutes. (Core underlyings aren't in `node_assignments`, so a fully-dead
  underlying subscription is a smaller residual gap — noted in the class remarks, not yet closed.)
- **One un-creatable partition date blocked every other date in the same sweep, for both tables,
  on every retry, forever** — `EnsureUpcomingPartitionsAsync` had no per-call try/catch. Fixed:
  each date is now isolated; a failure logs and the sweep continues. Regression test poisons one
  date (a row already in `DEFAULT` for it, per the earlier-verified Postgres behavior) and confirms
  the other three still get their partitions.
- **`WriteOptionBatchAsync`/`WriteUnderlyingBatchAsync` shared one gap scope** (`"recorder:write"`)
  despite running as two independent concurrent loops — one pipeline's success could close a gap
  that should still reflect the other still failing. Split into `recorder:write:option` /
  `recorder:write:underlying`. Same change also added **one bounded retry** before dropping a
  failed batch (up to 5,000 already-dequeued, otherwise-unrecoverable observations) — a single
  transient blip now recovers instead of being discarded outright.
- **`PartitionMaintainer`'s "Created partition" log could fire regardless of whether anything was
  actually created**, undermining the exact operator trust the `DEFAULT`-row Critical alert (added
  earlier this phase) depends on. Fixed with an explicit existence check before the
  `CREATE TABLE IF NOT EXISTS`, rather than trusting the DDL statement's return value.

**Refuted after independent verification** (not fixed — investigated and found not to be real):
open-interest recording trusting whichever of tick 27/28 fires (correct: IBKR sends only one per
contract, matching that contract's own right — verified against the documented tick semantics);
and one theorized stale-`LineLease` disposal path that, on closer trace, does not actually
occur the way first described (the *related*, real stale-reference issue above was found and
fixed independently while re-verifying this claim by hand).

### Research platform Phase 2 (milestone 2, 2026-07-31) — sessions, history, backfill

Roadmap Phase 2 (`docs/plans/ibkr-edge-research-roadmap.md`): the historical plane. Shipped:

- **Migrations 004–006.** 004: `sessions`, `backfill_jobs`, `backfill_requests`, `bars`. 005: claim
  ownership (`claimed_by`, `lease_expires_at`) with a CHECK making every inflight row reclaimable,
  plus `kind` (historical vs top-up) and per-job `slice_duration`. 006: gap-close provenance (below).
- **`IbkrHistoricalClient`** — `reqHeadTimeStamp` and `reqHistoricalData` with `formatDate=2`
  (epoch) so exchange-local timezone strings never enter parsing. `reqRealTimeBars` stays cut.
- **`SessionCalendarService` + `SessionClock`** — the only type permitted to call
  `TimeZoneInfo.ConvertTime`; half-days and DST transitions covered by tests.
- **`BackfillPlanner` / `BackfillCoordinator` / `BackfillStore`** — resumable and idempotent by
  construction; the request row IS the checkpoint. Claiming is one `UPDATE ... RETURNING` over a
  `FOR UPDATE ... SKIP LOCKED` candidate subquery, because `SKIP LOCKED` has no
  "unblock-and-silently-re-evaluate" window (the same Read Committed hazard that produced the Phase 1
  `node_assignments` defect).
- **`EsContractWalker`** — enumerates expired ES quarterlies and walks each within-contract, because
  CONTFUT cannot page into the past (runtime-verified error 10339).
- **`/research/backfill` + `/ui/backfill`** — per-job slice states, water marks, bars landed.

**Model arbitration for this phase** (per the CLAUDE.md phase-start protocol): Opus attacker vs
Opus justifier, Fable arbiter. The table's Sonnet/medium row was overridden to **Sonnet/high** for
the coordinator package on the attacker's argument that a claim/lease/reclaim state machine is a
split-path lifetime — the class that produced 4 of Phase 1's 8 confirmed defects. Conceded
counterpoint, recorded because it was not dismissed: the planner's slice arithmetic on its own is
ordinary date math and does not justify the tier; it rode along on package cohesion, not merit.

### Phase 2 live verification against the paper account (2026-07-31)

Per CLAUDE.md's standing rule that anything touching TWS is exercised against the paper account
before it is claimed to work. Everything below is an observation from a real socket, not a fixture.
Two defects were found this way that no amount of mocked testing could have surfaced.

**Defect: settlement returned before the fills existed.** A 1-lot SPY vertical returned
`filled=1, avgFillPrice=1.28, fills=[], commission=0`; four seconds later the same order read two
fills (1.67 and 0.39, differencing to exactly 1.28) and commission 1.598693. `WaitForSettlementAsync`
awaited only terminal `orderStatus`, but TWS sends that BEFORE `execDetails` and those before
`commissionAndFeesReport` — so ExecutionService would have persisted a filled order with no fills as
its permanent record. Fixed with a post-settlement grace (`IbkrOptions.FillSettleGraceSeconds`,
default 10 s) that waits for every leg to report an execution AND every execution to report its
commission; expiry is logged loudly, never thrown, because a filled order reported with partial
fills still beats failing a completed order. Re-verified by placing another live paper vertical:
fills and commission both present at return.

**Defect: a dead recorder's gap stayed open forever.** Only the process that opens a
`recorder_gaps` row ever closes it, so an ungraceful exit leaves `ended_at` NULL permanently — and
`CoverageMonitor` counts an unended gap as overlapping EVERY later window. One crash would have
made coverage permanently red, and coverage is the gate that admits a recorded day into a study.
Found by killing the gateway three times during this session and noticing two immortal gaps. Fixed:
migration 006 adds `closed_by` (`observed` | `inferred`) with a CHECK tying it to `ended_at`, and the
gateway bounds orphaned gaps at startup — marked `inferred`, because the interval really was
unrecorded but nobody watched it end, so `ended_at` is an upper bound rather than a measurement.

**Verified working live** (paper account `DU…`, TWS 127.0.0.1:7497, serverVersion 223):

- Head timestamps match the plan's recorded floors exactly: SPX 2004-03-04, SPY 1993-01-29,
  VIX 2005-10-03, SPY BID_ASK 2004-01-23.
- Expired-futures history is deeper than assumed: ESZ4 (conId 495512557) reports a head of
  **2021-06-06**, ~3.5 years before its expiry, against the roadmap's 2-year guaranteed floor.
  60 one-minute bars pulled from it. 29 ES contracts enumerated.
- Daily bars carry `tradingDate` with a null `timestamp`; intraday bars the reverse — the intended
  split, confirmed on the wire rather than assumed.
- **Pacing governor under real load**: 20 concurrent historical requests all returned 200 with
  latencies staggered 0.1 s → 24 s. No 162, no pacing violation, and the socket never reconnected.
- **Reconnect and lease replay**, the path that had never once executed: forced a genuine socket
  drop via a local TCP proxy (no root needed to kill the connection). A `disconnect` gap opened at
  19:34:49.48 and closed at 19:34:51.18; recorded ticks stop at :49 and resume at :51, exactly the
  one missing second. The lease survived and replayed.
- **Backfill backpressure**: the coordinator claimed a slice, the governor returned 429 with
  `Retry-After`, and it backed off 253 s and released the slice with its attempt refunded — no
  stranded inflight row, no attempt burned on a rejection that never reached TWS.
- Order cancel: a resting limit order (0.05 on a spread worth ~1.7) reached `Cancelled`.
- Portfolio with position Greeks and daily P&L, against positions the test orders had just opened.
- `gateway.ibkr_order_map` persists internal id ↔ broker id ↔ permId ↔ terminal status across
  restarts. With a deliberately wrong connection string the gateway logged Critical and still
  traded, which is `RequireOrderPersistence=false` behaving as documented.

**Live observation worth keeping:** portfolio positions come back with `exchange: "AMEX"` while
chains and quotes use `SMART`. `PortfolioRiskEvaluator` joins quotes to order legs by whole-record
equality, so this would silently zero every Greeks and max-loss check if the two ever met — they do
not, because `QuoteRequest` echoes the contract it was ASKED for rather than TWS's normalized one
(verified live: requested SMART, returned SMART). Worth remembering before anyone "simplifies" that
echo into using the resolved contract.

## Left

Milestone 2 (research platform — sequenced in `docs/plans/ibkr-edge-research-roadmap.md`):

- **Node drift detection and reassignment** — Phase 1 shipped bootstrap-only node selection
  (fixed moneyness offsets). Re-evaluating a node's assigned strike against its own recorded delta
  once streaming (the roadmap's "|Δ−target| > 0.10 sustained 30 min" rule), with dual-subscribe
  overlap and a churn cap, is a deliberate, documented follow-up — not implemented yet.
- Phase 2: session calendar, historical client, resumable backfill (SPX/SPY/VIX/ES incl.
  `EsContractWalker`, which will also unblock live ES tick recording), gap detection.
- Phases 3–8: snapshots → features/labels/baselines/study runner → residual models →
  implied-vs-forecast study → execution simulator → shadow ops.

Milestone 1 remainder:

- Replace in-memory order/event stores with Postgres.
- Replace in-memory event publisher with RabbitMQ.
- Replace dev bearer-token auth with Keycloak/OIDC JWT validation.
- Work out why SPX/SPXW combos park in `PreSubmitted` (see the open question above); SPY combos fill.
- Cover the risk engine's remaining breach codes (12 codes, 1 tested).
- Represent equity positions in `PortfolioSnapshot`, or accept that their delta is uncounted.
- Add Python ML signal service.
- Add richer audit dashboard.
- Address Aspire transitive `MessagePack` advisories when upstream package is patched.

## Trading prerequisites

**TWS Precautionary Settings block combo orders until cleared.** Error 163 — *"price exceeds the
Percentage constraint of 3%"* — comes from TWS's client-side presets, not an exchange, and it rejects
even a marketable spread priced at the natural against a live book: for a combo TWS compares the
*net* against a leg/underlying reference, so every spread net looks wildly off.

**Global Configuration → Presets → Options → Precautionary Settings → clear or widen *Percentage*.**
Check *Size Limit*, *Total Value Limit*, and *Number of Ticks* too. Once cleared, orders fill
normally — that is what unblocked the round trip above.

**TWS may reset API connections** (accepts the socket, then `RST`) when a modal dialog is awaiting
input. The gateway now backs off on short-lived sessions instead of reconnecting every few seconds,
but a flapping connection means TWS needs attention.

**Diagnose connection problems below the adapter before suspecting it.** A socket that connects, gets
a version-handshake reply, then stalls or resets on `START_API` is TWS refusing the API session — no
C# involved. `~/tws-api-probe.py` does exactly that handshake in twenty lines and prints whether
`managedAccounts` came back; use it to tell "TWS is not accepting API sessions" from "the gateway is
broken". A TWS that is logged in (check for established connections to IBKR on ports 4000/4001) and
listening on 7497 can still refuse `START_API` — check **Trusted IPs** contains `127.0.0.1`,
*Allow connections from localhost only*, *Master API client ID* blank or 0, and restart TWS after
changing any of them.

**Shut the gateway down gracefully or TWS keeps the session.** `SIGINT`/`SIGTERM` must reach the
built binary, not the `dotnet run` wrapper, or `IbkrConnection.StopAsync` never runs `eDisconnect`
and TWS holds a dead API session. A clean stop logs `Application is shutting down...`.

To route orders through IBKR, with the gateway running:

```bash
export Execution__Router=ibkr                # route orders to IBKR, not the simulated engine
export Portfolio__Source=ibkr                # set this too, or risk checks run on fabricated inputs
export IBKR__OutsideRegularTradingHours=true # required outside 09:30-16:15 ET (SPX/SPXW)

curl -H "Authorization: Bearer dev-internal-token" localhost:<port>/ibkr/orders/open       # reconcile
curl -H "Authorization: Bearer dev-internal-token" localhost:<port>/ibkr/account/portfolio # risk inputs
```

Order routing stays opt-in: `Execution:Router` must be exactly `ibkr`, and the gateway's
`IBKR:AllowLiveTrading` must be true for any non-`DU` account. `Portfolio:Source` is a separate
opt-in and defaults to the development figures — routing real orders without setting it evaluates
them against a fixed buying power and a flat day.

### Trade SPX, not SPY, outside regular hours

SPY options are regular-hours only — pre-market they have no book at all (bid/ask 0). **SPXW** trades
nearly 24×5 and quotes properly: 7435C 32.30/32.60, delta 0.662, observed live at ~08:15 ET.

Index options need four things equities do not, all now handled:

- The underlying is `IND` on CBOE, not `STK` on SMART (`ResolveUnderlyingAsync` probes both).
- Pass `?tradingClass=SPXW` — plain `SPX` is the AM-settled monthly series that does not trade
  extended hours.
- `Contract.TradingClass` must be set when resolving conIds, or SPX and SPXW are ambiguous.
- `IBKR:OutsideRegularTradingHours` must be true, or the order is held until the regular session.

## Notes

- Market data defaults to **delayed** (`ibkr-market-data-type=3`) so the stack works without
  subscriptions. The account's live entitlements (Cboe indexes, OPRA, CME) are verified shared to
  the paper user (2026-07-31 probes) — set `1` for live. Caveat from the official FAQ: if live and
  paper sessions run simultaneously they must be on the same device, or the paper session
  receives no data.
- `SubmitOrderRequest.LimitPrice` is a **whole-order** net (matching `PaperExecutionEngine`); TWS
  wants a **per-combo** net. `IbkrOrderBuilder.PerSpreadPrice` converts. They are identical at one
  spread, so the difference only appears on multi-lot orders.
