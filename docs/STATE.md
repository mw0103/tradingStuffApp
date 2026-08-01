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
  1-minute buckets with data ÷ expected minutes) over a window, plus overlapping `recorder_gaps`
  rows. `GET /research/nodes` reports current assignments. **Superseded in Phase 2:** the
  denominator was a fixed UTC window here, which Phase 2 replaced with real session minutes — see
  the Phase 2 entry for why that made the 95 % gate unreachable by construction.
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
- **`SessionCalendarService` + `SessionClock` + `SessionCalendarSynchronizer`** — the platform's
  single authority for "what data was expected when". `SessionClock` is the only type permitted to
  call `TimeZoneInfo.ConvertTime`, and it is registered under both its own type and `ISessionClock`
  so resolving the interface cannot hand out a second clock with a second cache. The synchronizer is
  a hosted service because `research.sessions` has no other writer and an unwritten session table
  does not fail loudly — it shrinks every denominator, which makes coverage read *higher*; its timer
  exists to move the `today + N` horizon under a process that runs for weeks, not to detect change.
  Verified live: one startup pass materialised 32,682 rows across 4 calendars for 1993→2026.
- **The coverage denominator was wrong, and wrong in the flattering direction.** It divided
  tick-bearing minutes by `(to - from)`, so the default trailing-24 h window asked for 1,440 minutes
  of a market open for about 1,185 and reported single-digit percentages on a perfectly healthy day —
  which made the roadmap's 95 % acceptance threshold **unreachable by construction, and therefore
  meaningless**. Expected minutes now come from the union of the RTH and GTH sessions overlapping the
  window, clipped to it, counted by an ordered sweep rather than summed (`CME_ES` nests RTH inside
  its Globex row; summing would inflate that denominator by 405 min/day). The numerator is filtered
  by the same clipped intervals, so numerator ⊆ denominator by construction and an out-of-session
  tick cannot push a conId past 100 %.
- **Coverage refuses to report a number it cannot justify.** `research.sessions` is reconciled
  boundary-for-boundary against what `ISessionClock` generates for the same window; on any
  disagreement the ratio is `null` with status `sessions-out-of-sync`. This is the class (c)
  absent-row discipline applied to a denominator: a query over the table cannot emit a row for a
  *missing* session, and the missing row shrinks the denominator, so absence renders as health in
  the one direction nobody checks. A weekend now reports `no-session-in-window` with no ratio rather
  than a fabricated 0 % — migration 006's lesson, that a permanently-red gate is a gate nobody reads,
  applied a second time.
  **CORRECTION (2026-08-01):** this entry originally called the clock "a genuinely independent
  witness". That was wrong and is retracted. `SessionGenerator` is registered as a singleton, and
  both `ISessionClock` and `SessionCalendarService` resolve the same instance *including its
  memoisation cache* — so the two sides of the comparison are one function call over one cached
  result. It detects write drift (a stale table, a partial sync, a hand-edited row) and cannot detect
  a wrong calendar. The Phase 1+2 review found the instantiation: for `CME_ES` on Thanksgiving 2025
  both sides were empty, `Matches` returned true, and coverage reported `measured` over a window that
  omitted 1,140 real minutes. The reconciliation passed *because* both sides inherited the same
  defect. The claim is now stated accurately in `CoverageMonitor`'s own remarks.
- **`GET /research/sessions`** — generated vs persisted rows side by side with an `in-sync` /
  `missing` / `mismatched` / `phantom` state per row, so the calendar everything downstream is
  validated against can itself be checked.
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

**Defect: an evicted lease's gap was never closed, on purpose.** `SweepExpiredAsync` opened a
`line_evicted` gap and deliberately left it open, reasoning that the lease is finished permanently.
That reasoning is right about the lease and wrong about the row: `CoverageMonitor` does not read an
unended gap as "this lease ended", it reads it as "recording is missing, and still missing", against
every window from then on. Because ResearchService is expected to redeploy constantly — that is the
stated reason the recorder lives in the gateway rather than beside it — each redeploy abandons its
~54 node leases, the gateway evicts them after 3 missed heartbeats, and **one redeploy would poison
coverage forever**. Observed live: 80 immortal gaps from a single afternoon of restarts. Fixed by
bounding the gap at teardown with `closed_by = 'inferred'`. The real question ("was this conId
covered?") is answered by the per-conId tick counts, which span lease changes; the row now only
explains where one lease's stream stopped.

**Defect: every `/ui/*` path returned 404 while the service reported healthy.** Two independent
causes, both invisible to the build. First, `WebApplication` inserts `UseRouting` ahead of all user
middleware, so the `/ui/{**slug}` SPA fallback was selected as an endpoint before
`StaticFileMiddleware` ran — and that middleware deliberately does nothing once an endpoint is
chosen, so no real asset was ever reachable. Routing is now declared explicitly after
`UseStaticFiles`. Second, the fallback's `StaticFileOptions` carried `RequestPath = "/ui"`, but the
fallback rewrites the path to the bare file name, which can never carry that prefix, so the lookup
missed every time. Separately, Vite writes into `wwwroot` after MSBuild evaluation, so the SDK's
implicit glob matched nothing and the assets never reached `bin/` or a publish output — a published
service would have shipped with no UI at all. All three fixed and verified by loading both pages.

**Known nit, not fixed:** `/ui/backfill` can render a negative relative age ("-95s ago") when the
Postgres container's clock leads the browser's. Cosmetic, but a negative age on an operator
dashboard reads as a bug in the data rather than in the clock; worth clamping at zero.

**Live observation worth keeping:** portfolio positions come back with `exchange: "AMEX"` while
chains and quotes use `SMART`. `PortfolioRiskEvaluator` joins quotes to order legs by whole-record
equality, so this would silently zero every Greeks and max-loss check if the two ever met — they do
not, because `QuoteRequest` echoes the contract it was ASKED for rather than TWS's normalized one
(verified live: requested SMART, returned SMART). Worth remembering before anyone "simplifies" that
echo into using the resolved contract.

### Phase 2 adversarial review — 19 confirmed defects fixed, 7 refuted

Two review passes, 89 agents: one over the committed Phase 2 diff (11 confirmed of 22 raised), one
over the gap-detection package (8 confirmed of 20). Structure was deliberately adversarial — diverse
finder lenses, then a **three-lens refutation panel per finding** (read-the-code, reproduce-it,
does-it-actually-mislead), defaulting to refuted and killing anything ≥2 reviewers rejected. That
killed 7 findings, and on one survivor a dissenter established that exhausted-counts-as-settled is
deliberate, documented and tested, which scoped the fix to four narrow faults instead of tearing out
a working design. A single reviewer both generating and judging its own claims would have done
neither.

**One theme accounts for most of it: a subsystem reporting "finished" or "clean" over work that was
never done.** Six instances, all fixed:

- **The gap detector had a hole exactly where losses accumulate.** A historical job's audit ceiling
  was its frozen `target_to` and a top-up's was a 2-day lookback, so the band between them widened by
  a day per day of operation and no job's analysis covered it. Reproduced: three deliberately-emptied
  RTH sessions reported `checked` with zero gaps, and asking for those days explicitly returned
  `window-rejected` on both jobs. Fixed by auditing to `now` and adding `GapReport.Series`, a
  cross-job reconciliation grouped on `(conId, whatToShow, barSize, useRth)` that subtracts audited
  from claimed windows, plus per-job `Unaudited` ranges — a window nothing looked at can no longer
  render as an empty gap list. **Verified live**: SPX's historical and top-up jobs now reconcile as
  one series with no unaudited remainder.
- **Nothing planned that band either.** `PlanHistorical` walked backward from the frozen `target_to`
  and `PlanTopUp` planned only the current 15-minute bucket, so any outage longer than an hour lost
  minute bars permanently. Added `PlanForward` (to a floored UTC midnight, so same-day reruns still
  add zero rows) and a bounded top-up catch-up window. **Verified live**: the forward anchor returns
  390 SPX bars for exactly the band nothing used to request.
- **The ES walker declared 100% with 25 of 29 contracts unplanned.** Completion was derived from
  request-row counts, and a contract skipped on a failed head probe contributes no rows, so it cannot
  lower any count. It now counts contracts it could not plan and forces `running` when planning was
  partial.
- **Exhausted slices flipped a job to `complete` at 100%.** Kept the exhausted-is-settled accounting;
  added terminal status `complete_with_gaps` (migration 009), made `PercentComplete` count only
  resolved slices, and made status derived each pass rather than latched. The operator path back is
  raising `MaxAttempts` — which already makes exhausted rows claimable, and now reopens the job too.
- **A transient failure burned an attempt with no backoff.** Added `GatewayOutcome.Unreachable` split
  on `HttpRequestError`: a connection/DNS/TLS failure provably never reached TWS, so it refunds the
  attempt like `Paced` does; genuinely ambiguous failures stay `Transient` and now back off instead of
  spinning. `BrokenCircuitException` was escaping the catch filter and stranding `inflight` rows.
- **Coverage capped a flawless day near 50%.** Every conId was measured against the whole window
  regardless of how long it held its node assignment, and node rotation is routine (every 2 minutes,
  and at every UTC date roll). Now reported **per node role** — the identity the 95 % gate is actually
  about — summing each conId's own tenure intersected with session minutes, with per-conId segments
  kept visible underneath so a contract that dies on assignment still shows 0 % rather than being
  smoothed into a healthy average. A latent bug fell out of this: the expected-conId query had **no
  window filter at all**, so a historical report mixed in current assignments.

**Order safety.** The fill-settle grace added earlier the same day was itself defective: it completed
on the first execution per leg, so a 5-lot vertical would persist 2 fills totalling 7 contracts
instead of 3 totalling 10 — and its comment claimed distinct-leg counting was what made it correct,
which is backwards. The sound predicate needed information the tracker did not have: `orderStatus`
counts a BAG in **spreads** while `execDetails` counts each leg in **contracts**, so leg *i* owes
`filled × ratio[i]` and the combo ratio now travels with the leg index. It sums stored fill
quantities (not TWS's cumulative field — the question is whether the list about to be persisted is
complete) and only evaluates once terminal, since a predicate satisfied mid-partial-fill would latch
a `TaskCompletionSource` that cannot be un-set. It fails safe: if `filled` were ever in leg contracts
the expected total is only too high, giving a timeout and a warning rather than a truncated record.

**Lease lifetime, rewritten rather than patched.** `ActiveLease` is now an encapsulated state machine
— ticker, line lease, heartbeat and terminated flag private behind one lock, reachable only through
check-and-mutate-as-one-step methods. Four defects fell out of that: `ReleaseAsync` never bounded its
gap (the eviction-path fix had been applied to only one of two termination paths, and release runs
every 2 minutes on rotation); replay and teardown raced through `ContainsKey` check-then-act, leaking
a market-data line and a live TWS subscription per occurrence against a budget of 80; a lease granted
*during* a replay pass was never replayed and silently recorded nothing for the session; and
`OnSinkFailed` fired its gap-open loose, so with `FailAll` faulting ~54 sinks at once the INSERT could
land after termination had already looked for something to close. The structural claim holds:
`TryClaimTermination` is the only way to obtain the state needed to unwind, and one `TerminateAsync`
is its only caller, so a future third termination path cannot skip the gap close.

**Also fixed:** `reqHeadTimeStamp` classified a genuine no-data 162 as transient and re-probed forever
at one paced request per attempt (162 is overloaded — the existing bars-path discriminator now
applies); basis priority ranked `empty`/`permanent` above `exhausted`, so an abandoned range read as
benign; the daily-bar path compared date-overlap expectations against instant-containment landings,
fabricating `succeeded_but_absent` for present data; an in-progress session flagged as the
checkpoint-lied alarm on every poll during market hours; the reported bars figure summed pre-dedup
TWS counts (migration 009 adds `bars_landed`, populated from the insert's row count); and the overall
coverage ratio returned a fabricated 0 % when *no instrument was being measured at all* — a fresh
deployment reading as total failure, the loudest possible false alarm.

**Two verification notes worth keeping.** Every agent verified its regression tests by reintroducing
the defect and confirming they fail — the negative control that distinguishes a test which would have
caught the bug from one that merely encodes it. And the lease agent caught its own false-green from a
stale incremental build by probing rather than trusting the run. Both practices are now the standard
for this project.

**Unfixed, deliberately.** `CBOE_VIX_GTH` is not added to `exchange-calendars.json` despite the live
measurement (VIX 1-min runs 02:15–15:59 CT, not `CBOE_INDEX_GTH`'s 19:15–08:15): that file is the
ground truth every downstream artifact is validated against (CLAUDE.md class (b)) and one day's
observation is not a published schedule. The evidence is recorded in `InstrumentCalendars` for
whoever writes it. The RTH expectation currently under-flags, which is the safe direction.

### Phase 0-and-earlier adversarial review — 16 confirmed + 1 critic-found, 0 refuted (2026-07-31)

A full adversarial pass over everything on main up to and including Phase 0, **run on Fable by
explicit instruction** (an in-prompt model directive, per the policy's exception 2): the milestone-1
execution stack (never before reviewed), the PR #1 gateway core, and the Phase 0 additions. Six
lenses, a three-lens refutation panel per finding, a completeness critic. 44 raw findings, 16
verified — and, uniquely across the three reviews so far, **the panel killed nothing**: 16/16
survived, 7 critical. The never-reviewed money-computing code was exactly where the worst defects
in the repository were hiding, behind a fully green test suite that exercised the happy path of
formulas wrong by construction.

**The risk engine approved what it could not price.** The gateway deliberately completes timed-out
quotes as bid=0/ask=0/Greeks=0 (observed live pre-market), and the evaluator priced those as zero
risk: a 10-lot credit vertical approved with EstimatedMaxLoss $0 against a $2,500 limit, reproduced
against built assemblies. The account client and paper engine both already treated zero-filled data
as untrustworthy; the money check was the one component that did not. Fixed fail-closed — and
stricter than specified, on the fix agent's own argument: a zero bid on a *sold* leg is not
conservative (it flips the net across zero and hands a credit spread the debit formula), so the
rule is per-side: no price on the side the leg trades at → `UNPRICEABLE_LEG`, no figures, rejection.

**Every max-loss formula was quantity-blind, and the shape checks beneath them leaked.** A 1×10
"vertical" (nine naked short calls, unbounded) was approved at ~$150; the cover check thought one
long covered ten shorts; and an out-of-range `StrategyKind` bypassed shape validation entirely,
falling to the evaluator's `_ => Math.Max(0m, netDebit)` arm — a naked strangle at $0. Replaced
with one quantity-aware defined-risk formula that **does not branch on the sign of the price**
(exposure is a property of the strikes; the sign branch was reachable by one bad quote), a
contract-allocating cover check, validator+evaluator both rejecting unknown strategies and unequal
quantities independently (separate services; neither trusts its caller). The fix agent also found a
17th defect the review missed: `Multiplier: 0` priced an entire order at $0 and TWS never sees the
field. Breach codes 12 → 18, centralised in `RiskBreachCodes`, enumerable at `GET /risk/breach-codes`,
every code now tested at exactly-equal and one-cent-past boundaries.

**Cancel was a fiction for broker-routed orders.** `IOrderRouter` had no cancel operation;
`POST /orders/{id}/cancel` flipped the local record while the order kept working at TWS — able to
fill after the operator was told it was dead. The router seam now carries cancel; the IBKR router
asks the gateway and the record shows **what the broker said** (ordinarily `PendingCancel`, which
can still fill), with "could not ask" a distinct 502 rather than a 200 with an unchanged order.
Replace is refused (409) for broker-routed orders — the gateway exposes no replace, and a TWS
replace is a new `placeOrder` call site nobody should invent without a paper round trip behind it.

**A broker order could exist with zero record.** Nothing persisted before routing. Orders now
persist `Submitted` before `RouteAsync`, with the crash windows analysed in both orderings —
record-no-broker-order (visible, reconcilable) is now the failure shape, never the reverse. The
retry story was rebuilt on a confirmed hole: ExecutionService generated a fresh Guid per submit, so
the gateway's duplicate-transmission guard had nothing stable to recognise; the internal id is now
derived from `{AccountId}:{ClientOrderId}`, and a resubmit replays the settled outcome or re-routes
under the same id. The risk client also still had default HTTP retries — a lost evaluate response
would retry into `DUPLICATE_ORDER` and permanently burn the id. Both fixed; the duplicate guard is
now an atomic claim released on rejection, burned only on approval.

**The gateway core, live-verified.** `PacedSocket` resolved the socket BEFORE awaiting budgets, so
a call parked across a reconnect issued against the dead client — for `placeOrder`, a silent no-op
behind an already-persisted order map row. All 14 methods now resolve after acquisition, with both
trading gates inside the protected region. The never-transmitted compensation missed
`InvalidOperationException`; replaced with a provable discriminator (an about-to-transmit callback)
so everything before the write compensates by construction. `QuoteSnapshot.Source` gains a
`-partial` suffix when a field was never received (nullness, not zero-ness, is the discriminator) —
verified live on after-hours quotes.

**The prescribed account-feed fix was refuted by the broker and replaced.** The review said: cancel
the previous account-summary subscription before re-issuing. Live TWS proved that wrong — TWS caps
**distinct request ids** (two per client), and cancelling does not return the slot: cancel-then-
reissue hit error 322 on the third cycle exactly like never cancelling. Re-issuing on the SAME id
works indefinitely. Stream ids are now allocated once per process; four feed rebuilds in one live
session, zero 322s. Two `RequiresTws` tests pin the cap behaviour so nobody "fixes" it back.
Broker behaviour is not knowledge until a live connection has demonstrated it — this is the
clearest instance yet.

Also live-measured: SPXW combo limits refuse penny prices (leg `minTick` 0.05 — TWS error 110 at
0.33, accepted at 0.35), so combo net limits now snap to tick, always downward. The per-leg
`MinTick` is not yet threaded from `IbkrMarketDataClient` into the builder; an SPXW multi-lot whose
net does not divide into nickels still draws an honest 110.

**Verification discipline, now with a track record.** Every fix agent ran negative controls
(reintroduce the defect, watch the test fail, restore), and across the four agents this caught
**three tests that passed against the defect they claimed to guard**: a race test green 3-runs-in-5
against check-then-act, a one-attempt HTTP test whose harness never installed the resilience
handler it opted out of, and one spurious result from siblings rebuilding shared bin/obj. A test
that has never failed against its defect is decoration; this is now the project standard.

**Operator-visible behaviour changes:** orders against illiquid/pre-market books are rejected
(`UNPRICEABLE_LEG`) where they were previously approved at $0; replace returns 409 for broker-routed
orders; cancel reports broker truth (`PendingCancel` can still fill); startup fails fast on
`Execution:Router=ibkr` with a non-ibkr portfolio source, and on any present-but-unparseable risk
limit (previously: silent fallback to the loosest defaults in the system). Migration files are now
checksummed — one edited after application fails startup naming the file.

**Deliberately not fixed:** the account feed does not react to TWS 1101 (values freeze at the blip;
the rebuild is now safe to trigger, but the wiring adds a pump-thread path the agent could not
exercise live — flagged, not shipped); per-leg `MinTick` threading (above); live verification of
the new ExecutionService cancel path end-to-end (markets closed — queued for the Sunday session
with the multi-lot BAG semantics pin); crossed quotes are still priced (locked/crossed markets are
legitimately common); Market orders carry no slippage model.

### Live verification of the Phase 0 fixes, and one new defect it exposed (2026-08-01, Saturday)

Ran the Phase 0 fixes against live paper TWS with the full service chain (gateway + MarketData +
Risk + Execution, `Execution:Router=ibkr`). A closed market is the ideal condition for this: SPY
options quote 0/0, which is exactly what previously produced $0 max-loss approvals.

**The critical fix is confirmed end to end.** The same 10-lot SPY vertical from the review finding,
submitted through ExecutionService against genuine 0/0 quotes, is now **rejected** and nothing
reaches the broker:

```
status 3 (RiskRejected), decision 1 (Rejected), open orders at TWS: []
UNPRICEABLE_LEG  Leg 0 (SPY 2026-08-03 742 Call) — no bid to sell it into
UNPRICEABLE_LEG  Leg 1 (SPY 2026-08-03 740 Call) — no offer to buy it against
```

Note the messages are per SIDE, not per quote — the short leg fails on a missing bid, the long leg
on a missing ask. That is the stricter rule the fix agent argued for over the weaker
`Bid<=0 && Ask<=0` spec, working as reasoned. Also confirmed live: `-partial` source marking
(`ibkr-live-partial` on 0/0 quotes), the 18-code `GET /risk/breach-codes` surface, and the
router/portfolio cross-check refusing to start on `ibkr` + `development`.

**NEW DEFECT, found by making the mistake rather than by reading code (now fixed).**
`MarketData:Source` was set to `"ibkr"` — plausible, and not one of the recognised values
(`ibkr-live` / `ibkr-delayed`). MarketDataService degraded to the deterministic generator, exactly
as its fail-safe default intends, while `Execution:Router=ibkr` kept transmitting. A 10-lot SPY
vertical was approved against **synthetic quotes of bid 27.34 / ask 28.46 on a Saturday**, when the
real market for that contract was 0/0, and the order rested at TWS as a live paper order
(ibkrOrderId 13). Risk had checked numbers that were invented.

This is the identical shape to the router/portfolio pair the review already fixed — each setting is
fail-safe alone, the combination is not — and the guard was written for two of the three settings
that must agree. Critically, **`UNPRICEABLE_LEG` does not catch it**: that guard refuses quotes it
cannot price, and the deterministic feed emits confident, well-formed, entirely fictional ones.
Fail-safe degradation plus fail-closed pricing still leaves the hole, because neither component can
see that the *other* one changed meaning. Fixed with `EnsureRouterAndMarketDataAgree` beside its
sibling; the exact incident configuration now refuses to start. Negative control: 5 tests fail with
the guard reverted. A further test pins the guard's duplicated strings to `MarketDataSources`'
real constants, so a rename cannot silently disarm it.

**Deferred item #43, partially closed.** The ExecutionService cancel path was exercised against a
real resting broker order (the one placed by the incident above). It works: the cancel reached TWS,
`/ibkr/orders/open` went to `[]`, and the recorded event is honest — *"Cancel requested. IBKR order
13 reports PreSubmitted"* — refusing to claim `Cancelled` on a status TWS had not yet acknowledged.

**But it is incomplete, in the safe direction.** TWS confirmed `Cancelled` seconds later and
ExecutionService's record is still `Submitted`, permanently: nothing reconciles the record after the
cancel call returns. The original defect said *dead* about a *working* order (dangerous); this says
*maybe-working* about a *dead* one (safe, but wrong). Closing it needs either open-order
reconciliation or the gateway pushing status to ExecutionService — real work, and moot until the
in-memory store is replaced, since a restart loses the record anyway. Recorded, not built.

**Still genuinely blocked on a live market:** the multi-lot BAG fill-semantics pin (needs an actual
multi-lot fill) and the cancel *happy path* on a filled-or-fillable order.

**Post-merge confirmation (2026-08-01):** `Category=RequiresTws` run clean against live paper TWS,
5/5 — `TRADING_TEST_TWS=127.0.0.1:7497`. The two tests that matter most here are the account-feed
regression pins: `Re_issuing_the_same_account_summary_id_works_indefinitely` and
`A_third_account_summary_id_is_refused_even_after_cancelling_the_previous_one` — together they hold
the fact TWS itself disproved during the review (cancelling a summary subscription does not free
its slot; reusing the same request id is what works) in place against anyone re-"fixing" it back to
the plausible-but-wrong cancel-then-reissue approach. Still open, unchanged: the ExecutionService
cancel path and the multi-lot BAG semantics pin both need a live order round trip, which no test in
this run exercises.

### Phase 1+2 adversarial review — 4 criticals, all reproduced (2026-08-01)

Five Opus/high lenses over the ~9,100 lines of Phase 1 and 2 code, deliberately **not** organised per
component — both earlier passes were, and repeating that would mostly re-find what was already fixed.
The lenses were: cross-subsystem seams, **this week's fix diffs reviewed as new code**, split-path
lifetimes (class a), negative claims (class c), and the session calendar as ground truth (class b).
Ten fix agents, all Opus/high per the class overrides.

Every critical was **reproduced**, not argued — two by executing against the real classes, two
against live TWS data. Where two lenses disagreed, the one that ran the code was right: the seams
lens read the lease lifetime carefully and judged it sound; the lifetime lens ran it and found a
line-budget leak.

**CRITICAL — the 54-node grid collapsed onto ~4 contracts per DTE bucket.** `ChainWindow = 20` is a
half-width in *strikes* (41 strikes ≈ ±100 points at SPX 7440) while node targets are *moneyness*
(±2.5 % … −15 %, i.e. ±186 to −1,116 points). Every non-ATM target fell outside the requested window
and an unbounded `OrderBy(|strike − target|).FirstOrDefault()` clamped them all to the window edge:
three call roles onto one strike, four put roles onto another. 54 roles → ~24 distinct conIds, with
~30 of the 80 research lines spent double-subscribing contracts already recorded. Coverage reported
all 54 healthy, because each role *was* pointing at a live, well-recorded contract — the wrong one.
**The volatility smile the platform exists to record was never being recorded.**

Fixing it uncovered a third layer neither lens found: `reqSecDefOptParams` returns the strike **union
across every expiration** in the trading class, so simply widening the window would have produced
*phantom* contracts. Proven live — `SPXW 2026-08-06 P 6620` is a union member and returns error 200
(no security definition) while 6625 resolves; per-expiration ladders that day were 238 / 502 / 70
strikes. The chain is now sourced from one `reqContractDetails` per expiration (the real ladder, with
conIds, which it caches — a selector pass costs **6 paced requests instead of 60**), and a node's
target must be *bracketed* by listed strikes on both sides. An edge clamp can never satisfy bracketing
at any window width or increment, so the collapse cannot silently recur even if every constant is
later mistuned. **Verified live: 45 assigned nodes, 45 distinct conIds, max strike deviation 0.0075 %.**

**CRITICAL — coverage was structurally blind to it.** The expected set was built *from*
`node_assignments`, not from the 54-row `option_nodes` registry, so a role that was never assigned
produced no row and its absence *raised* the unweighted mean. Reproduced: 53 of 54 roles recording
nothing reported **100 %**, against 1.85 % for the identical outage with the assignments present.
The two defects are one failure from both ends — selection silently broke the surface, and the one
report that would catch it could not see the gap. Now enumerated from the registry via LEFT JOIN,
with the mean's denominator being the registry itself, so removing an assignment can never raise the
number. **Verified live: 54 perNode rows, 9 unassigned ones present and visible.**

**CRITICAL — `CME_ES` emitted no session at all on holidays Globex actually trades.** A `US_MARKET`
holiday made the whole date non-trading, so Thanksgiving 2025 (1,140 real ES bars), July 4 2025
(1,140), MLK 2026 (1,140) and Good Friday 2026 (915) produced nothing — and `TradingDateOf`
attributed every one of those bars to the *next* trading date. Coverage excluded them from numerator
and denominator alike, reporting 100 % over a day it never measured. Fixed with a `partialSessionSets`
concept: 39 entries, 30 measured from `reqHistoricalData(whatToShow="SCHEDULE")` against live TWS and
9 explicitly projected as `unverified`. Two findings worth keeping: **Good Friday is not a rule**
(CME was shut in 2024/2025, open to 08:15 CT in 2023/2026 — the years it is the first Friday of a
month, i.e. employment-report day), and **2025-01-09 was wrongly a closure** — Globex traded to
08:30 CT. Christmas and New Year are genuine closures, and Dec 31 is a full 16:00 session.

**CRITICAL — a lease granted across a reconnect leaked the entire research line budget.**
`GrantAsync`'s epoch check fired a full replay pass with no ledger reset, while `TryPublishIssued`
dropped the displaced `LineLease` on the documented assumption that "a republish only happens on a
replay, and by then the ledger has already been zeroed". Reproduced at production scale: 57 leases
in, 80 research lines consumed, and 57 still held after releasing all 58. **This was introduced by a
Phase 2 fix**, and the same false premise was written a second time in `ForgetLineLease` — now
deleted. Live-verified 3/3 after the fix: every subscribe paired to an unsubscribe by exact ticker id.

**Also fixed:** SPX index measured against the SPX *option* session (405 vs the real 390 minutes, so
100 % of SPX RTH sessions reported a permanent 15-bar shortfall); Cboe index GTH closing ten minutes
early (780 → 790); `PlanForward` added to only one of two planners, leaving ES stopping at its frozen
anchor — *the exact defect its own commit claimed to fix*; `GapReport.Series` computed from the
`jobId`-filtered subset, so a single-job query declared a whole series reconciled; a `Checked` job
laundering its own unaudited window into the audited set; the recorder discarding up to 55,000
buffered observations on every graceful shutdown; a `buffer_overflow` gap that only a later enqueue
could close; an unreachable gateway at heartbeat time faulting `RecorderOrchestrator` and stopping the
whole host under the default `StopHost`; migration 010's checksum baseline *blessing* an
already-diverged database rather than detecting it; `MigrationHealthCheck` written, tested, and
registered nowhere; and `ApplyError` losing permId on every rejected or cancelled order.

**`CBOE_VIX_GTH` is now written**, reversing a standing decision recorded above. The original refusal
("one day's observed bars are not a published schedule") was right on the evidence then available;
`contractDetails` reporting `0215-0815` + `0830-1600` is the venue's schedule as the provider
distributes it, which is a different class of evidence. `CBOE_VIX_RTH` carries four dated rows
tracking three real schedule changes, cross-checked against a bar-count change landing on exactly the
right weekend. VIX no longer carries an `Unmodelled` window, so the permanently-red series flag is gone.

**`selector_version` 1 → 2 is a data-provenance boundary.** Every `node_assignments` row written
before 2026-08-01 came from the collapsing selector and describes a grid that was not what it claimed.
Phase 4's study identity must treat version-1 tenures as suspect rather than as history.

**Verification discipline — six false-green tests caught, three by agents in their own work.** A gap
suite with no positive `Reconciled == true` control (a fix auditing nothing would have passed
everything); a partition-isolation test that stopped isolating once the horizon migration pre-created
its poisoned date; a `WaitUntilAsync` whose return value was discarded, so it passed with zero rows
persisted; an `openOrder` test that called the tracker directly and passed with the callback wiring
deleted; a migration test asserting a phrase its own fixture filename supplied; and a test whose first
attempt passed at the wrong place because `HttpClient` buffers response content. None would have been
caught by reading. **Reintroduce-the-defect is now the project standard and belongs in CLAUDE.md.**

**Integrated state:** 783 unit / 143 `RequiresPostgres` / 10 `RequiresTws`, all green together.
Migrations 001–014 apply clean with zero unverified baselines. Order-safety invariants re-verified
after ten concurrent agents: one `placeOrder` call site, no test reaches it, `AllowLiveTrading` false
everywhere, DU-prefix check intact.

**Known, not fixed.** `RecorderOrchestrator` races migrations on a cold start (queries
`option_nodes` before 003 applies) — it now logs and continues rather than faulting the host, and the
next pass recovers, but it is the same class `PartitionMaintainer` fixed structurally via the schema
horizon. SPY has **570 minutes a day of real extended-hours data** (04:00–19:59 ET) outside every
modelled session, latent only because both shipped SPY jobs are `useRth=true`; an `NYSE_EXTENDED`
calendar is the complete fix. The 90DTE bucket is refused as `duplicate-con-id` until an SPX
expiration enters its window (~5 days) — correct behaviour, but it means 9 roles record nothing
meanwhile. ES `liquidHours` is 45 minutes longer than the modelled RTH row (under-states, safe
direction). No CME partial entries before 2022-11-24, because IBKR reports earlier holidays as full
sessions, which is demonstrably wrong for Thanksgiving 2021 and unresolvable from available bars.

## Left

Milestone 2 (research platform — sequenced in `docs/plans/ibkr-edge-research-roadmap.md`):

- **Node drift detection and reassignment** — Phase 1 shipped bootstrap-only node selection
  (fixed moneyness offsets). Re-evaluating a node's assigned strike against its own recorded delta
  once streaming (the roadmap's "|Δ−target| > 0.10 sustained 30 min" rule), with dual-subscribe
  overlap and a churn cap, is a deliberate, documented follow-up — not implemented yet.
- **Per-conId session calendars.** Coverage uses ONE denominator (Cboe RTH+GTH) for every conId,
  but SPY is NYSE-calendared and neither the SPX nor the VIX index level updates through Cboe GTH,
  so those three underlying lines cannot exceed roughly a third of the denominator however healthy
  the recording is — and `OverallCoverageRatio` is an unweighted mean, so they drag it down a couple
  of points. Not a regression (the old wall-clock denominator was worse for everything), but until
  this is fixed **read the 95 % gate against the option-node rows, not the overall figure**. Doing
  it properly needs a conId → instrument → calendar mapping, and the core underlyings are not in
  `node_assignments` to hang one off.
- **A dead core-underlying subscription is still invisible.** Phase 1 fixed this for option nodes by
  unioning tick counts with `node_assignments`, but the core underlyings are not in that table, so a
  fully-dead SPX/VIX/SPY line still produces no row rather than a 0 % row. Same absent-row class as
  the defects above; carried forward knowingly.
- **No repair-on-demand for a drifted `research.sessions`.** Recovery is a restart or the 12 h
  timer. A sync trigger on an endpoint was rejected deliberately: regenerating can retire a
  published session row, which must not be a side effect of a page refresh.
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
