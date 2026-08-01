# Follow-up

Deferred work, recorded deliberately. The project is being driven to an **MVP** — run and
visualise the study, and trade it on the paper account with automation. Hardening is worth doing
when there is something worth risking real money on; until then, issues get written down here and
we move on.

Nothing in this file blocks the MVP unless it says so. Ordered by what will hurt first, not by
severity in the abstract.

---

## 1. Live state that exists ONLY in the database — will silently revert

**This is the most dangerous section in the file**, because none of it is in version control. A
fresh database, a `docker volume rm`, or a colleague's machine reverts every line of it, and the
symptom is not an error — it is the historical drain quietly never running again.

| What was changed | Where | Why | Restore/redo |
|---|---|---|---|
| The three `kind='topup'` backfill jobs set to `status='paused'` | Aspire Postgres, container `postgres-934a0e61`, db `trading` | They starve the historical drain — see §2.1 | `update research.backfill_jobs set status='running' where kind='topup';` |
| `vix-daily-trades` priority raised 80 → 200 | same | VIX daily is only 22 slices but sat behind SPX (6,056) and SPY (7,882), so it would not have run for days — and both HAR-X and the calibrated-VIX baseline need it | it will re-seed at 80 from `BackfillJobCatalog` |

**Both changes must move into `BackfillJobCatalog` (or the coordinator) before anyone else runs
this.** Until then, a new environment silently reproduces the original "8 healthy jobs, thousands
of planned slices, zero bars" state.

---

## 2. Blocks a real study result

### 2.1 Top-up jobs starve the historical drain — *task #58*

`BackfillStore` claims slices with `ORDER BY bj.priority DESC, br.end_time_utc DESC`. Top-ups sit
at priority 1000 and regenerate slices continuously; historical jobs are 60–100. Measured
2026-08-01: after ~25 minutes of a running coordinator, **every historical slice across all five
jobs had `attempts = 0`** while top-ups had succeeded 49 times.

Pausing the top-ups unblocked it immediately — SPX drained 49 slices in ~4 minutes and
`research.bars` went 180 → 13,380 rows. That isolates priority starvation rather than a gateway,
TWS, or pacing fault.

**Proper fix:** the two job classes need separate concurrency budgets rather than one
priority-ordered queue. A top-up that must run every 15 minutes and a multi-day historical drain
are not comparable on a single scale. Options: reserve a share of each claim batch for
`kind='historical'`; give top-ups their own loop with a small dedicated budget; or make priority a
tiebreak *within* kind rather than across kinds.

**Why it reads as healthy:** `/research/backfill` reports 8 jobs and 22,443 planned slices, which
looks like progress. Same class as `docs/LESSONS.md` §3.

### 2.2 The drain fills the reserved holdout first

Slices are claimed newest-first (`br.end_time_utc DESC`), so the backfill works backwards from
today. Every SPX bar landed so far is Jan–Jul 2026 — **entirely inside the reserved
2024-01-01..2026-07-31 holdout**, and therefore unusable by any development run.

The study correctly answers `insufficient-data` rather than scoring forbidden data. But it means
**no live `ok` run exists yet**: the `ok` path, the elastic-net CV, and the corrected candidate are
exercised by synthetic-bar unit tests only, never against real market data. The drain must reach
pre-2024 first (~1.5–2 h at the pacing ceiling from the time of writing).

Not a defect — newest-first is the right default for a recorder. Worth a `from`/`to` bound on the
historical planner so a dev run can pull the window it actually needs first.

---

## 3. Correctness and safety, recorded and unfixed

### 3.1 A TWS market-data downgrade has never been observed on the wire — *task #56*

Migration 016 + the `non_live_market_data` gap exist to catch TWS *serving* a different regime than
was *requested*. Live verification on 2026-08-01 confirmed the callback fires and its value is
stamped (`reported=1`, 4 rows, all non-null) — but this account is entitled to live Cboe index
data, so requested and served **agree**. The divergence the whole feature exists for is covered by
unit tests only. Frozen-outside-hours is probably the cheapest way to force a real one.

### 3.2 Replay ordering race in market-data-type provenance

On replay the first tick may arrive before TWS re-reports the type. Ticks in that window are
stamped `NULL` — which reads as *unknown*, never as live — and `ApplyMarketDataType` raises the
alarm when the callback lands. Honest rather than empty, which is the acceptable direction. Pin it
with a `RequiresTws` test on a live session.

### 3.3 Automation fill paths unverified — *corrected 2026-08-01, it is worse than this said*

The original entry said submission, resting, status and cancellation **are** exercisable on a
Saturday and only fills are not. **That was wrong, and measured wrong on the wire.**

Probed against live paper TWS on 2026-08-01 (SPY 2026-08-07 740C and 742C, through the running
gateway at `marketDataType=3`): **both legs return `bid 0 / ask 0` with all four Greeks zero.**
SPY options have no book at all outside the regular session. Two consequences, and neither is a
defect:

- The automation's own pricing step refuses — there is no offer to buy the long leg against, so no
  marketable limit exists. It records a `refused` decision row naming that.
- Even with a limit supplied by hand, `PortfolioRiskEvaluator` rejects the order `UNPRICEABLE_LEG`
  before it is routed. `UnpriceableReason` refuses a buy leg with `Ask <= 0` and refuses any leg
  whose Greeks are all exactly zero (a live option always carries non-zero gamma and vega).

So on a Saturday, with a **coherent** configuration, **no order reaches TWS at all** — not even to
rest. The earlier claim that a resting order was obtainable came from the 2026-08-01 incident, where
the order rested only because `MarketData:Source` had silently degraded to the deterministic
generator and risk was pricing invented quotes. Under a correct configuration that path is closed,
which is the guard working.

**What that leaves unverified on the wire, in full:** `placeOrder` from automation, an order resting
at TWS, cancellation of an automated order, fills, partial fills, and commission reporting. The
submission path is verified as far as ExecutionService → risk and no further. Needs a weekday RTH
session — see also task #43.

**Cheapest way to get the rest without waiting for Monday:** `IBKR:MarketDataType=2` (frozen) serves
the last close snapshot outside hours, which would price the spread. It is a gateway-wide setting
that also affects the live recorder, so it was not changed under a running session.

### 3.6 The paper account now trades on DELAYED quotes by default

`AppHost` moved to `Execution__Router=ibkr`, `Portfolio__Source=ibkr` and
`MarketData__Source=ibkr-delayed` (2026-08-01). The three are one decision — ExecutionService refuses
to boot unless they agree — but the value chosen is `ibkr-delayed`, not `ibkr-live`, because the
regime TWS actually serves is set by `ibkr-market-data-type`, which is still `3` so that first-run
setup works without an OPRA subscription. The label matches the feed, which is the point.

**It is still delayed data pricing a pre-trade risk check**, and delayed quotes are up to 15 minutes
stale. The account is verified entitled to live Cboe/OPRA data (docs/STATE.md, 2026-08-01: requested
1, served 1). **Before any automated order is trusted on price, move both together:**
`ibkr-market-data-type=1` **and** `MarketData__Source=ibkr-live`. Moving one without the other either
lies about the feed or wastes the entitlement.

### 3.7 The automation kill switch does not survive a restart

`POST /research/automation/kill` is in-memory. A process restart clears it and automation re-arms
from configuration. The status endpoint says so verbatim in `killSwitch.durability`, and the durable
stop is `PaperAutomation__Enabled=false`, but an operator who kills the switch and then sees the
service restart has no warning beyond that string. Persisting it (a row, or a file beside the
connection string) is small work that was deliberately skipped for MVP speed.

### 3.8 The automation signal cannot currently return "trade", by construction

`VolResidualSignal` refuses twice over: the latest run is `insufficient-data` (§2.2), and even an
`ok` run would be a **development** run, which the study's own pre-registration says nothing may be
traded on. So the scheduled path is a no-trade path today and the `enter` branch is exercised only
by unit tests with a fake signal. That is deliberate — inventing an entry rule to make the loop fire
would put a fabricated signal in the decision table — but it means **the automated submission path
has never run end to end from a real signal.** The manual endpoint
(`POST /research/automation/manual-order`, operator-supplied limit, recorded as `trigger='manual'`)
exists to exercise the rest of the path without faking one. A real entry rule is Phase 5/6 work with
a gate and a leakage review in front of it.

### 3.9 The per-session cap is per trading date, and a weekend order counts against Monday

`ISessionClock.TradingDateOf` assigns an instant outside any session to the **next** session's
trading date — the leak-safe direction, and correct for the research plane. It means a manual order
placed on Saturday consumes one of Monday's two. Correct-ish and cheap to live with; noted because
the arithmetic is not obvious from the status endpoint.

### 3.9a A risk-rejected order consumes a slot of the per-session cap

Measured live 2026-08-01: the manual order came back `RiskRejected` (`UNPRICEABLE_LEG` ×2), nothing
reached TWS, and `ordersThisSession` still went to 1. Deliberate — the cap counts orders *submitted
to ExecutionService*, not orders that reached the venue, so a strategy that is rejected every cycle
stops after two attempts instead of hot-looping against risk. Recorded because it is not what
"orders this session" reads like at a glance, and because the opposite choice is defensible if the
cap is ever meant to bound broker exposure rather than attempts.

### 3.10 A crash between transmitting and recording can refund one order of the cap

The cap is derived from `research.paper_automation_decisions` plus an in-process claim taken before
the order leaves the service. A failed row write leaves the claim held (the safe direction), but a
**process crash** in that window loses the claim, and the restarted process re-derives a count that
does not include the order. Bounded at one order, and only on a crash inside a window of
milliseconds. Closing it properly means deriving the order id before submitting rather than after —
ExecutionService already derives its own id deterministically from `(accountId, clientOrderId)`, so
the fix is to duplicate that derivation and claim the row first.

### 3.4 SPX/SPXW combos park in `PreSubmitted` — *task #37*

Unexplained; SPY combos fill. Automation deliberately uses **SPY** for this reason. Revisit before
any SPX automation.

### 3.5 Other recorded items

- **#51** `RecorderOrchestrator` races migrations on cold start.
- **#46** gateway-side partition pre-ensure as defence in depth.
- **#48** TWS refuses a new API client for ~30 s after a same-process disconnect.
- **#53** gate `mapping.Unmodelled` on `job.UseRth`.
- **#50** wire `MigrationHealthCheck` + two `Program.cs` items.

---

## 4. Research-design debt

### 4.1 The study reads IBKR VIX, not Cboe — deviation from the preregistration

`docs/research/volatility-forecast-residual-study.md` specifies Cboe's official daily VIX history:
an index has no trades, and a reconstructed `TRADES` bar carries ambiguous timestamp and
construction semantics a registered baseline should not inherit. The dev run uses IBKR bars because
no Cboe feed exists yet. **Acceptable for a development run; must not survive into a registered
one.**

### 4.2 Other registration judgment calls, recorded

- HAR features are **mean-of-log-RV**, not log-of-mean-RV — the registration's literal definition,
  differing from `HarDatasetBuilder` by a Jensen gap, hence that builder is not reused.
- Opex distance is **calendar-day**, not trading-day; the registration does not specify.
- 12 of the ≤15 registered features; Tier-2 overnight ES deliberately out of scope.

### 4.3 The ranked backlog is stale — see the roadmap's own warning

Four of its twelve scoring criteria were scored against "no option history exists". With ThetaData
Options Pro that constraint is gone, so studies 6–10 are understated by an unknown margin and rank
order below #5 should not be acted on until rescored. Deliberately **not** rescored inline: it
changes what gets built.

### 4.4 Phase 9 has no row in the CLAUDE.md model-policy table

ThetaData chain ingestion is new work with no model/effort assignment. Recommended **Sonnet/high**
on class (c): "survivorship-free" is a negative claim, and those must name the check that would
detect a violation.

### 4.5 Phase 9 storage sizing must be redone before ingestion is designed

The roadmap's "Postgres is enough" arithmetic assumed option data came only from the live recorder.
Options Pro serves **true tick** on request (`interval=tick`: 783 rows in a one-minute window
against 62 at the 1-second default). Fourteen years of tick across a 54-node grid is far past that
estimate. **Policy to adopt: ingestion defaults to 1-minute; tick is explicit and study-scoped.**
Bulk tick belongs in object/file storage with manifests, hashes, lineage and coverage in Postgres —
not an automatic fourteen-year backfill merely because it is available.

### 4.6 ThetaData capability findings are not persisted — *task #57*

Measured against the live Terminal on 2026-08-01 but living only in a chat transcript and in task
#57. The roadmap requires them in `research.capability_probes`. Summary: Options **paid and deep**
(SPXW 2,227 expirations from 2012-06-01; SPX 205 from 2012-06-16; VIX options 318 from 2012-06-20;
full NBBO both sides; true tick available). Index and stock endpoints **403 — not subscribed**,
which is expected (ThetaData sells the three product lines separately) and harmless, since IBKR
covers both and reaches deeper. **Unresolved:** no Greeks/IV endpoint found at any plausible v3
route despite the tier advertising them.

Also: the class comment in `tests/.../LiveThetaTerminalTests.cs` still says measurements were taken
"on a FREE subscription". Options is paid; that comment is now wrong.

---

## 5. Known, accepted, lower priority

- **#41** the Postgres test harness leaks pools and never drops its test databases, producing
  `53300: too many clients` around ~96 tests and container segfaults at ~1,000 accumulated
  databases. Worked around with `-c max_connections=400`. It lies convincingly — see
  `docs/LESSONS.md` §11 before diagnosing a failure it caused.
- **#38** ClientApp types are hand-written duplicates of the C# contracts, with no compile-time
  link. Drift is silent by construction.
- **#39** read projections use ordinal mapping; Dapper was recommended.
- **#36** per-instrument calendars are not wired into `CoverageMonitor`, so the 95 % gate must be
  read against option-node rows, not the overall figure.
- **#49** record that `reqSecDefOptParams` returns a strike **union**, not per-expiration ladders.
- Hand-rolled routing in `App.tsx` (pathname sniffing + `pushState`) is fine for four pages and
  will not scale much past that.
- No frontend test runner at all.
