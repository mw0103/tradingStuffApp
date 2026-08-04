# Paper-run runbook

Written 2026-08-03 as Plan D's deliverable (`docs/plans/paper-test/plan-d-operations.md`). It
describes how to bring the paper-run stack up, how to check it is actually working, how to arm
Phase 2, and how to retire the ad-hoc processes that carried the work until now.

**Nothing in this file was executed against the live machine.** The AppHost wiring was written and
built; the cutover was not performed, because live chain-ingestion drainers were mid-drain. Every
step marked **OPERATOR** is a hand action a human takes; nothing else in this repository will take
them.

The protocol this serves is `docs/plans/paper-run-protocol.md`, and it is frozen. Where this file
and the protocol disagree, the protocol is right and this file is a bug.

## Contents

1. [What comes up, and what each piece is for](#1-what-comes-up-and-what-each-piece-is-for)
2. [The configuration matrix](#2-the-configuration-matrix)
3. [Preconditions before a cutover](#3-preconditions-before-a-cutover-operator)
4. [Cold start](#4-cold-start)
5. [Health checks](#5-health-checks)
6. [The daily cadence](#6-the-daily-cadence)
7. [Arming Phase 2: the registered decision](#7-arming-phase-2-the-registered-decision-operator-human-only)
8. [The roll-day cap](#8-the-roll-day-cap)
9. [Pausing and resuming ingestion](#9-pausing-and-resuming-ingestion-operator)
10. [What a TWS daily restart looks like](#10-what-a-tws-daily-restart-looks-like)
11. [The cutover: retiring the ad-hoc processes](#11-the-cutover-retiring-the-ad-hoc-processes-operator)
12. [Who to believe when data is missing](#12-who-to-believe-when-data-is-missing)
13. [Findings: where Aspire wiring met the application](#13-findings-where-aspire-wiring-met-the-application)

---

## 1. What comes up, and what each piece is for

`aspire start` from `src/TradingStuff.AppHost` brings up eight projects and three containers (plus
the `trading` database resource). Three of those eight projects are the *same* ResearchService
binary with different loops switched on, and the chain drainer runs as three replicas — fourteen
processes and containers in all.

| Resource | What it is | Notes |
|---|---|---|
| `postgres` | Postgres 17, named volume `tradingstuff-postgres-data`, `ContainerLifetime.Persistent` | Host port 5432 is a DCP proxy in front of the container. Losing the volume loses every recorded tick and ~38h of backfill. |
| `trading` | The database inside it | The one connection string every service consumes. |
| `rabbitmq` / `keycloak` | Containers | Started because ExecutionService waits on them. Nothing on the research track uses either yet. |
| `ibkrgateway` | Sole owner of the TWS socket | Client id 11 (`ibkr-client-id`), paper port 7497. Also the raw tick recorder. |
| `marketdataservice` | Option quotes and Greeks | `MarketData__Source=ibkr-delayed`. |
| `riskservice` | Pre-trade risk | |
| `executionservice` | The order spine | `Execution__Router=ibkr`, `Portfolio__Source=ibkr`. Refuses to boot if router, portfolio and quote source disagree. |
| `researchservice` | **The paper-run instance** | Automation loop, post-close capture, the daily shadow mark, the recorder orchestrator, backfill, migrations, `/research/*`, `/ui`. |
| `research-chain-drainer` ×3 | ThetaData option-chain ingestion | Same binary; capture, shadow marks and calendar sync off. Replica count is `Parameters__chain-drainer-replicas` (default 3). |
| `research-backfill` ×1 | The second backfill coordinator | Same binary; the ad-hoc `:5714` instance's role. |
| `auditdashboard` | Local operator surface | |

**Why three drainer replicas and not one process doing three things.** `OptionChainCoordinator`
claims exactly one request row per pass, and `BackfillCoordinator` one slice — both deliberately, so
a crash can never strand a partial batch. Their throughput is therefore the *process count* and
nothing inside a process changes it. That is why the ad-hoc setup ran four extra copies of the
service, and the AppHost reproduces the arrangement rather than inventing a new one.

**Ports are Aspire-assigned, not fixed.** Every project's `launchSettings.json` binds
`http://localhost:0`, so the research plane's port changes on each start. Read it from the Aspire
dashboard's resource table, or from `ss -tlnp | grep TradingStuff.Re`, and pin it into a shell
variable once:

```bash
export RESEARCH=http://127.0.0.1:<researchservice port>
export GATEWAY=http://127.0.0.1:<ibkrgateway port>
export TOKEN=dev-internal-token
```

Every URL below is written against `$RESEARCH` / `$GATEWAY` for that reason. See
[Findings](#13-findings-where-aspire-wiring-met-the-application) — this is a real operational rough
edge and it is recorded rather than papered over.

---

## 2. The configuration matrix

Everything the AppHost sets, per instance. A blank cell means the key is not set on that instance
and the code's own default applies.

### The arming surface (paper orders)

| Key | AppHost value | Code default | Why |
|---|---|---|---|
| `PaperAutomation__Enabled` | `Parameters:paper-automation-enabled` → **`false`** | off | The one thing that decides whether the loop acts at all. |
| `PaperAutomation__Signal` | `Parameters:paper-automation-signal` → **`vol-residual`** | `vol-residual` | `vol-residual` refuses every path by construction. `constant-exposure` is the paper-run opt-in and is **not** set in committed code. |
| `PaperAutomation__Structure` | `Parameters:paper-automation-structure` → **`debit-vertical`** | `debit-vertical` | The protocol's instrument is `short-vol-credit-put`. Arming the signal without also setting this points the constant-exposure signal at the original MVP debit vertical. |
| `PaperAutomation__ExitDteThreshold` | `7` | 7 | Plan B's declared time-based exit. A protocol parameter, so it is stated rather than implied. |
| `PaperAutomation__MaxOrdersPerSession` | `2` | 2 | See [§8](#8-the-roll-day-cap). Do not set this to 1. |

All three of `Enabled`, `Signal` and `Structure` are Aspire **parameters**, so arming is a runtime
decision that reverts by itself rather than an edit somebody forgets:

```bash
Parameters__paper-automation-signal=constant-exposure \
Parameters__paper-automation-structure=short-vol-credit-put \
Parameters__paper-automation-enabled=true \
aspire start
```

None of that is sufficient on its own. Automation additionally requires: a standing, unrevoked
`research.paper_run_decisions` row ([§7](#7-arming-phase-2-the-registered-decision-operator-human-only));
ExecutionService having *resolved* the IBKR router and portfolio provider and MarketDataService a
real quote provider (measured, not read from its own config); a connected `DU` account; a named
session; and the per-session cap.

### Raw post-close capture (`researchservice` only)

| Key | AppHost value | Code default |
|---|---|---|
| `PaperCapture__Enabled` | `Parameters:paper-capture-enabled` → **`true`** | on (opt-**out**: only the exact string `false` disables it) |
| `PaperCapture__Calendar` | `NYSE` | `NYSE` |
| `PaperCapture__SessionLabel` | `RTH` | `RTH` |
| `PaperCapture__IntervalSeconds` | `300` | 300 |
| `PaperCapture__CloseDelayMinutes` | `15` | 15 |
| `PaperCapture__LookbackSessions` | `3` | 3 |
| `PaperCapture__TimelyWindowMinutes` | `120` | 120 |
| `PaperCapture__AccountId` | `Parameters:paper-capture-account-id` → **empty** | null |

Empty `AccountId` means "the account the gateway is configured to trade", which is the intended
setting. Capture defaults ON where automation defaults OFF, and the asymmetry is deliberate: a
session nobody captured cannot be captured afterwards, while a capture nobody wanted costs two HTTP
reads a day.

### The daily shadow mark (`researchservice` only)

| Key | AppHost value | Code default |
|---|---|---|
| `ShadowMarks__RunAtUtc` | `00:10:00` | `00:10:00` |
| `ShadowMarks__Calendar` | `NYSE` | `NYSE` |
| `ShadowMarks__SessionLabel` | `RTH` | `RTH` |
| `ShadowMarks__Enabled` | — (on) | on (opt-out, same shape as capture) |
| `ShadowMarks__AfterCloseMinutes` | — (null) | null |
| `ShadowMarks__CatchUpWindowMinutes` | — | 720 |

See [§6](#6-the-daily-cadence) for why 00:10 UTC and when to move it.

### Ingestion (`research-chain-drainer` ×3)

| Key | Value | Why |
|---|---|---|
| `OptionChains__Enabled` | `true` | |
| `OptionChains__LeaseSeconds` | `3600` | Default is 180. A single expiration's month-chunked walk against a cold Theta Terminal runs well past three minutes; a lease that expires mid-walk is reclaimed by a sibling and the expiration is fetched twice. |
| `ThetaData__Timeout` | `00:30:00` | Default is 10 minutes. |
| `ThetaData__SnapshotTimeOfDay` | `15:30:00` | **FROZEN.** The A4 term-structure construction on record was built from 15:30 ET snapshots. Changing this does not "improve" anything — it makes every row ingested afterwards incomparable with every row already in `research.option_chain_quotes`, silently, because nothing in the schema records the cut time. |
| `PaperCapture__Enabled` | `false` | One capture writer. |
| `ShadowMarks__Enabled` | `false` | A second copy would be correct and would repeat a three-year bar load nightly for nothing. |
| `Sessions__Enabled` | `false` | `SessionCalendarSynchronizer` is documented as `research.sessions`' only writer, and coverage denominators are read from what it writes. |

### Backfill (`research-backfill` ×1, plus `researchservice`)

| Key | Value |
|---|---|
| `Backfill__Enabled` | `true` on both `researchservice` (as it always has been) and `research-backfill` |
| `PaperCapture__Enabled` / `ShadowMarks__Enabled` / `Sessions__Enabled` | `false` on `research-backfill` |

Two coordinators is exactly what runs today. Concurrency is safe by construction: claims are leased
under a per-process `OwnerId` and the request row is the only checkpoint. **Which jobs actually
drain is not decided by any of this** — see [§9](#9-pausing-and-resuming-ingestion-operator).

### Broker and mesh

| Key | Value | Set on |
|---|---|---|
| `IBKR__Host` / `IBKR__Port` / `IBKR__ClientId` / `IBKR__MarketDataType` | `127.0.0.1` / `7497` / `11` / `3` | `ibkrgateway` |
| `IBKR__AllowLiveTrading` | **never set** (defaults false) | — |
| `MarketData__Source` | `ibkr-delayed` | `marketdataservice`, `executionservice` |
| `Execution__Router` / `Portfolio__Source` | `ibkr` / `ibkr` | `executionservice` |
| `IbkrGateway__BaseUrl` | injected endpoint | every ResearchService instance, `marketdataservice`, `executionservice` |
| `MarketDataService__BaseUrl` / `ExecutionService__BaseUrl` | injected endpoints | `researchservice` |

---

## 3. Preconditions before a cutover (OPERATOR)

### 3.1 TWS Master API Client ID — `docs/FOLLOWUP.md` §5

`IbkrExecutionsClient` issues `reqExecutions` with `ExecutionFilter.ClientId = 0`. IBKR documents
executions as visible only to the API client that placed them **unless** the connecting client id is
TWS's configured *Master API Client ID*, in which case every client's executions come back — and a
filter `ClientId` of 0 then means "do not filter by client" rather than guaranteeing cross-client
visibility. Which of those applies here is **not established**; it cannot be without a socket.

If it is own-client-only, a fill placed by hand in TWS — or by an earlier run under a different
client id — never reaches `research.paper_fills`, and the capture is silently incomplete for the
protocol's shadow-record items 6 and 9.

**Set it, then verify it. Do not assume either half.**

1. In TWS: *File → Global Configuration → API → Settings → Master API client ID*. Set it to the
   gateway's configured `IBKR:ClientId`. **Under this AppHost that is `11`** (parameter
   `ibkr-client-id`), not the `12` the ad-hoc gateway on `:5100` uses. Confirm the running value
   rather than trusting this sentence:
   ```bash
   curl -s -H "Authorization: Bearer $TOKEN" $GATEWAY/ibkr/status | grep -o '"clientId":[0-9]*'
   ```
2. Restart TWS so the setting takes (it is not applied live).
3. Place **one** manual paper order in TWS itself — not through this stack — and let it fill.
4. Wait for the next capture pass (close + 15 minutes, or up to 5 minutes later on the poll), then:
   ```bash
   curl -s "$RESEARCH/research/paper-capture/fills?limit=50"
   ```
5. If the manual fill is there, cross-client visibility is established and
   `research.paper_fills` may be read as "everything the account traded". **If it is not there,
   record that** — the fill record then means "orders this gateway placed", and the protocol's items
   6 and 9 carry that caveat for the whole run.

This check has not been performed. Until it has, read the fill record the narrow way.

### 3.2 The rest

- Docker running (`aspire start` needs it for the three containers).
- Theta Terminal on `127.0.0.1:25503`, or the chain drainers will refuse every request. The
  drainers hold their own credentials; requests from this stack carry none.
- The `tradingstuff-postgres-data` volume present. If it is gone, so is every recorded tick and
  ~38h of SPX/SPY backfill, and nothing will say so — the schema simply rebuilds empty.
- `docs/FOLLOWUP.md` §1 read. Live state exists in the database that exists in no file: the three
  `kind='topup'` backfill jobs are paused **in the database only**. No restart restores that.

---

## 4. Cold start

```bash
export DOTNET_CLI_HOME=/tmp/dotnet_home DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
mkdir -p /tmp/dotnet_home
cd src/TradingStuff.AppHost
aspire start --non-interactive
```

Expected within a few minutes: every resource running; `ibkrgateway` connected to paper TWS on a
`DU` account; three chain drainers claiming request rows; two backfill coordinators claiming slices;
`/research/*` answering; the shadow-mark trigger armed and logging its schedule at startup.

The gateway reports **unhealthy whenever TWS is down**, and that is why nothing waits on it —
`marketdataservice` and every ResearchService instance start regardless, sit idle, and retry. A
gateway showing unhealthy overnight is TWS being closed, not a fault.

---

## 5. Health checks

| URL | Healthy answer | Unhealthy answer means |
|---|---|---|
| `$RESEARCH/health` | 200 | `migrations` check failing — a service with no schema must not answer 200. |
| `$RESEARCH/research/status` | `migrations.status = "applied"`, the applied list ending at the highest migration in `Persistence/Migrations/` | A shorter list means this process applied an older set; check the binary, not the database. |
| `$RESEARCH/research/options/status` | Jobs listed, `enabled: true` on a drainer's view, request counts advancing between polls | `enabled: false` means the coordinator is switched off, which must never be read as "nothing left to do". |
| `$RESEARCH/research/backfill` | `enabled: true`, an `ownerId`, and **every** job listed — including one with zero request rows, which renders as 0% rather than being omitted | A job absent from the list is a query bug, not an idle job. |
| `$RESEARCH/research/shadow-marks` | One row per trading date, newest first | A missing date is a real gap. It is not filled in later; that is the house rule. |
| `$RESEARCH/research/term-structure` | The A4 series over the requested range | |
| `$RESEARCH/research/paper-run/decision` | `active: null` until an operator signs one; `history` lists revoked decisions too | Revoked decisions stay listed on purpose — what authorized last Tuesday's orders is not answerable from a view showing only what is authorized now. |
| `$RESEARCH/research/paper-capture` | One snapshot per closed trading date, `captureSource` = `gateway-account` | `gateway-account@late` means the snapshot was taken outside the 120-minute timely window and describes a *later* account state written against an older trading date. Real data, different measurement. |
| `$RESEARCH/research/paper-capture/fills` | The session's executions | See [§3.1](#31-tws-master-api-client-id--docsfollowupmd-5) before reading this as complete. |
| `$RESEARCH/research/automation` | The loop's own view: armed or the named refusal, orders used against the cap | |
| `$GATEWAY/ibkr/account/summary` (needs `Authorization: Bearer $TOKEN`) | `NetLiquidation`, margin and buying-power tags on a `DU…` account | Anything not `DU`-prefixed: stop. |

```bash
curl -s $RESEARCH/research/status
curl -s $RESEARCH/research/options/status
curl -s $RESEARCH/research/backfill
curl -s "$RESEARCH/research/shadow-marks?limit=10"
curl -s "$RESEARCH/research/term-structure"
curl -s $RESEARCH/research/paper-run/decision
curl -s "$RESEARCH/research/paper-capture?limit=10"
curl -s -H "Authorization: Bearer $TOKEN" $GATEWAY/ibkr/account/summary
```

`/research/*` is deliberately anonymous — it is a read-only diagnostic surface for a local-first
operator UI. The two exceptions carry `.RequireAuthorization()` individually: `POST
/research/automation/resume` and `POST /research/automation/manual-order`. The kill switch beside
them (`POST /research/automation/kill`) is deliberately anonymous, because a kill switch behind a
credential is one that does not get pressed.

---

## 6. The daily cadence

Two in-process schedulers, both on session-clock time, both idempotent, both recording refusals
rather than falling silent. No OS cron.

### Shadow mark — 00:10 UTC, for the session that closed the day before

`ShadowMarkTrigger` fires the same run `POST /research/shadow-marks/run` does; there is one
implementation and both callers reach it.

**Why 00:10 UTC and not the US close.** Backfill slices containing "now" are never claimed, by
design. So the same-evening VIX daily close does not come from the live recorder — it comes from
the 1-day-cadence job `vix-daily-trades-2026h2`, whose slice for a session becomes claimable at
00:00 UTC the following day. Ten past is the earliest instant the input can exist. A mark fired at
the bell could only ever return the forecaster's "no VIX close for the mark date" refusal, every
single day.

The due instant is anchored on the **session**, not on "today": Friday's mark is due Saturday
00:10 UTC and its window has closed by Sunday, so a weekend does not re-run it three times. The
catch-up window is 12 hours — 00:10 + 12h is 08:10 ET, before the open on either side of a DST
boundary, so a process that started late still lands the mark against the same closed market a
00:10 run would have seen. Past the open the planner would quote a *live* market and upsert that
over the prior date's intent: a different measurement wearing the same date. Missing the window
leaves a visible gap instead.

**When to move it to 16:20 ET.** The trigger is `ShadowMarks__AfterCloseMinutes=20`, which is
expressed relative to the calendar's close rather than as a UTC clock time so it does not need
re-entering twice a year (16:00 ET is 20:00 UTC in EDT and 21:00 UTC in EST). Flip it only once the
live recorder **demonstrably** lands same-day daily closes. The evidence to look for, in order:

1. `GET /research/coverage` over the session that just closed shows the VIX daily node at
   acceptance, not a gap.
2. The VIX daily close for *today* is present in `research.bars` before 21:00 UTC, on three
   consecutive sessions — checked by hand, not inferred from a job's status.
3. Then set it, and watch the next three marks for the forecaster's "no VIX close" refusal. One
   refusal means the recorder is not there yet; revert.

Until all three hold, moving it converts every mark into a refusal, which is worse than the current
one-day lag because a refusal is not retried within the window.

### Paper capture — close + 15 minutes

`PaperCaptureService` (Plan C) snapshots the account 15 minutes after each NYSE RTH close and pulls
that session's executions. Not zero minutes: TWS settles executions and recomputes margin for some
minutes after the bell. The pass polls every 5 minutes and captures any uncaptured session inside a
3-**session** lookback — sessions rather than calendar days, so a long weekend cannot silently
shrink the recovery window. An evening with the gateway down is therefore not a permanent hole; the
next pass that finds the gateway up captures it, marked `@late`.

---

## 7. Arming Phase 2: the registered decision (OPERATOR, HUMAN ONLY)

The `constant-exposure` signal refuses entry unless an unrevoked row stands in
`research.paper_run_decisions`. That row is a human sign-off — Madison's — recording that the paper
run may proceed on dev-provenance infrastructure. **No agent may register one. There is no
circumstance under which registering a decision is an automated step**, and nothing in this
repository does so.

```bash
curl -s -X POST "$RESEARCH/research/paper-run/decision" \
  -H "Authorization: Bearer dev-internal-token" \
  -H "Content-Type: application/json" \
  -d '{
        "statement": "The paper run may proceed on dev-provenance infrastructure. This amends the constant-exposure signal'"'"'s provenance refusal for the PAPER account only, never live.",
        "signedBy": "Madison"
      }'
```

`protocolRef` is optional and defaults to `docs/plans/paper-run-protocol.md`. The registration logs
at **Critical**, deliberately: it is the moment a paper account stops being a read-only research
plane, and it should be as visible in the log as the refusals it lifts.

To withdraw it:

```bash
curl -s -X POST "$RESEARCH/research/paper-run/decision/revoke" \
  -H "Content-Type: application/json" -d '{"reason": "..."}'
```

Revoked rows stay listed in `GET /research/paper-run/decision`'s `history`.

The decision is *necessary and nowhere near sufficient*. The full arming chain, in the order it
fails: `PaperAutomation__Enabled=true` → the signal is `constant-exposure` → the decision stands →
ExecutionService resolved the IBKR router **and** the IBKR portfolio provider → MarketDataService
resolved a real quote provider → the gateway is connected on a `DU` account → a named session is
open → the per-session cap has room → the hard per-spread risk cap passes.

---

## 8. The roll-day cap

**`PaperAutomation__MaxOrdersPerSession` must be 2 for the paper run. Do not set it to 1.**

An **exit consumes a cap slot exactly as an entry does**. Plan B closes a managed spread at
`ExitDteThreshold` (7) calendar days to expiration, and automation runs one spread at a time. So on
a roll day the exit spends the first slot; with a cap of 1 the replacement entry has nothing left,
and the account sits **flat overnight after every single roll**. That is a coverage hole in exactly
the state transition the run exists to observe — protocol §Success item 3, "positions survive rolls,
expirations, data outages, and order failures correctly".

Two is the smallest cap that lets one roll complete inside one session, and it is the code's own
default. It is a rail, not a tuning knob: when it is spent the loop refuses and says so — it does
not reset, wrap, or degrade to "one more".

---

## 9. Pausing and resuming ingestion (OPERATOR)

**There is no endpoint for this, and that is deliberate.** Pausing a job is an operator decision
recorded in the `status` column, applied by SQL against the `trading` database.

```sql
-- backfill
update research.backfill_jobs set status = 'paused' where kind = 'topup';
update research.backfill_jobs set status = 'pending' where kind = 'topup';   -- resume

-- option chains
update research.option_chain_jobs set status = 'paused' where name = '...';
update research.option_chain_jobs set status = 'pending' where name = '...'; -- resume
```

Claimable statuses:

| Table | Claimable | Notes |
|---|---|---|
| `research.backfill_jobs` | `pending`, `running`, `complete_with_gaps` | `complete_with_gaps` is the way back into a job stalled on exhausted slices: raise `Backfill:MaxAttempts` and its rows become claimable again. `complete` stays out. |
| `research.option_chain_jobs` | `pending`, `running`, `complete_with_gaps` | `paused` is the status every `tick` job is *created* with; the automatic coordinator must never plan or claim those rows. |

Status is **derived from the checkpoint counts, not latched** — a job returns to `running` by itself
on the next refresh. So a manual `status='running'` is not a durable pause override.

**The standing top-up pause is database-only state** (`docs/FOLLOWUP.md` §1, task #58). Top-ups sit
at priority 1000 and regenerate slices continuously while historical jobs are 60–100, so an
unpaused top-up starves the historical drain — measured 2026-08-01: every historical slice across
five jobs at `attempts = 0` while top-ups had succeeded 49 times. Nothing in version control
restores the pause. Bringing the AppHost up does **not** un-pause them and taking it down does not
pause anything; but a fresh database silently reproduces the starvation, and `/research/backfill`
reads as healthy while it does.

---

## 10. What a TWS daily restart looks like

TWS restarts itself daily, around 01:00 local. The gateway reconnects without help; this was
verified against the running instance rather than assumed — a gateway process started
2026-08-02 15:15 UTC reported `connectedAt: 2026-08-03T06:03:07Z`, i.e. it re-established the
socket by itself across the restart.

In the logs, in order:

1. The socket drops. `SubscriptionManager` sees `connectionClosed` / error **1100**
   (`ConnectivityLost`) and drops its ticker registrations — after a reconnect TWS no longer knows
   those tickers and answers error 300 ("Can't find EId").
2. `RunConnectionLoopAsync` retries: `Connecting to TWS at {Host}:{Port} as client {ClientId}.`
   While TWS is absent, `Failed to connect to TWS at {Host}:{Port}.` at Warning, with the delay
   doubling from `IBKR:ReconnectDelaySeconds` (5s) to `IBKR:MaxReconnectDelaySeconds` (60s).
3. On success: `Connected to TWS (server version {ServerVersion}), market data type
   {MarketDataType}.` then `Connected to {Count} managed account(s).`
4. If TWS is up but not ready, you get a *short-lived* session instead:
   `The previous TWS session lasted under 30s. Backing off to {Delay}s. Check TWS for a modal dialog
   awaiting input, a duplicate client id, or the API connection limit.` **That message is the
   diagnosis** — it is almost always a modal dialog TWS is waiting on, or a client id collision.
5. `TWS connectivity restored with data lost; streaming subscriptions must be re-established.` is
   error **1101** — the TWS-to-exchange link blipped and recovered without our socket dropping. The
   pacing governor zeroes the line ledger on reconnect, because a fresh socket holds no lines.

**A gateway showing unhealthy overnight is expected**, and it is why nothing in the AppHost waits on
it. The overnight consequences to expect: `RecorderOrchestrator` re-leases and logs a recording gap;
the shadow mark at 00:10 UTC may record the planner's `Gateway unreachable` refusal as its intent,
which is a first-class recorded answer, not a failure; `PaperCaptureService` records a named refusal
and retries on the next pass inside its 3-session lookback.

---

## 11. The cutover: retiring the ad-hoc processes (OPERATOR)

**Do not kill anything before its replacement is verified.** The order below is the point of this
section.

### 11.1 The inventory, as measured 2026-08-03

| Port | Process | What it is | Disposition |
|---|---|---|---|
| `:5100` | `TradingStuff.IbkrGateway`, **client id 12**, TWS 7497, account `DUQ283778` | Ad-hoc gateway bootstrapped for the drainers | Retire — replaced by `ibkrgateway` (client id 11) |
| `:5710` | `TradingStuff.ResearchService` | Ad-hoc shadow-mark instance; ran the first mark by hand | Retire — replaced by `researchservice` + `ShadowMarkTrigger` |
| `:5711`, `:5712`, `:5713` | `TradingStuff.ResearchService` with `OptionChains__Enabled=true OptionChains__LeaseSeconds=3600 ThetaData__Timeout=00:30:00 ThetaData__SnapshotTimeOfDay=15:30:00` | Chain-ingestion drainers | Retire — replaced by `research-chain-drainer` ×3 |
| `:5714` | `TradingStuff.ResearchService` with `Backfill__Enabled=true` | Backfill top-up drainer | Retire — replaced by `research-backfill` |
| `:34723`, `:34733` | `TradingStuff.IbkrGateway` (**client id 11**) and `TradingStuff.ResearchService` | **NOT stale leftovers.** These are the *currently running* `aspire run` instance's own `ibkrgateway` and `researchservice`. | Replaced by restarting the AppHost — see below |

**Correction to the plan document.** `docs/plans/paper-test/plan-d-operations.md` records
`:34723`/`:34733` as "stale leftovers from 2026-08-02 on random ports (wrong DB), safe to kill on
sight". That is wrong on both counts and killing them on sight would take down the running Aspire
mesh:

- They are children of the live `dcp` orchestrator, started in the same burst as `aspire run`
  (pid 738508), and `:34723` reports `clientId: 11` — the AppHost's `ibkr-client-id` default, not a
  hand-typed value.
- The DB is not wrong. Aspire's `postgres` resource publishes host port **5432 via a DCP proxy in
  front of container `postgres-934a0e61`, whose own published port is 36279**. The ad-hoc processes
  reach the container directly on 36279 and the Aspire mesh reaches the same container through
  5432. One database, two doors. `:34733/research/backfill` returns the same jobs and the same
  `ownerId` scheme as the ad-hoc instances.

What *is* stale about them is the **binary**: `:34733` reports its applied migrations ending at
`020_vrp_conditioning_dev_runs.sql`, so that process predates migration 021 (shadow marks) and the
whole A/B/C series. It is a stale build, not a stale process, and the remedy is a restart onto the
merged code — which is step 3 below, not a kill.

Also worth knowing before the cutover: **two gateways are currently attached to the same paper TWS**
(client ids 11 and 12), both with `tradingPermitted: true`. Retiring `:5100` frees client id 12 and
leaves exactly one socket owner, which is the design.

### 11.2 The order

1. **Verify the drains are at a safe point.** Chain drainers claim one expiration at a time under a
   3600-second lease; a kill mid-walk costs a re-fetch, not corruption, but it burns an hour of
   lease before a sibling reclaims. Check `$RESEARCH/research/options/status` for in-flight claims
   and prefer a moment with none.
2. **Do [§3.1](#31-tws-master-api-client-id--docsfollowupmd-5) first if it has not been done** —
   it needs a TWS restart, which is disruptive, and it changes which client id matters.
3. **Stop the running `aspire run` and start the merged AppHost.** This is what replaces
   `:34723`/`:34733`. Everything else on the list is still up at this point, so the research track
   is not interrupted by it.
4. **Verify the new mesh** against [§5](#5-health-checks): every resource running, gateway connected on a
   `DU` account, `/research/status` applied through the highest migration in the tree,
   `/research/options/status` showing drainers claiming, `/research/backfill` showing an `ownerId`.
5. **Only then** stop the ad-hoc processes, in this order: `:5711`, `:5712`, `:5713` (drainers),
   `:5714` (backfill), `:5710` (shadow marks), `:5100` (gateway — last, because the others use it).
6. **Re-check** `/research/options/status` and `/research/backfill` after five minutes. Claim counts
   must still be advancing. If they are not, the replacement is not doing the work and the ad-hoc
   processes were retired too early.
7. **Confirm the shadow-mark trigger.** The AppHost's `researchservice` logs its schedule at
   startup: `The daily shadow-mark trigger is armed: 00:10 UTC on the day after each session close,
   calendar NYSE/RTH, catch-up window 720 minutes.` The next mark is due at the next 00:10 UTC;
   check `$RESEARCH/research/shadow-marks` after it. A redundant manual `POST
   /research/shadow-marks/run` in the meantime is harmless — the run is idempotent per date.

### 11.3 Do not skip

- `docs/FOLLOWUP.md` §1's supervisor scaffold (`/tmp/supervise2.sh`) re-pauses the top-ups every
  three minutes. It lives in `/tmp` and dies with the machine. If it is still running, decide
  explicitly whether it stays; if it goes, [§9](#9-pausing-and-resuming-ingestion-operator)'s SQL is
  what holds the line instead, and nothing will warn you when it stops holding.
- Nothing about the cutover changes `research.backfill_jobs.status`. The top-ups stay paused or stay
  unpaused exactly as the database has them.

---

## 12. Who to believe when data is missing

| Source | Believe it for | Do not believe it for |
|---|---|---|
| **Live recorder** (in `ibkrgateway`) | Intraday ticks and bars for a session it was up for. It is the only source for tick data — Track B is unrecoverable by construction. | Same-day *daily* closes. It has not been proven to land them; that is why the shadow mark waits for backfill. Its silence during an outage is a real hole, not a vendor gap. |
| **Backfill** | Historical bars, and the daily close from 00:00 UTC the following day. The request row IS the checkpoint, so a rerun adds zero rows. | Anything containing "now" — slices spanning the present are never claimed, by design. A missing recent bar is not evidence of a vendor gap. |
| **ThetaData chains** | Historical option chains at the frozen 15:30 ET snapshot. | Anything cut at a different `SnapshotTimeOfDay`, which is incomparable with what is already stored and is not labelled as such in the schema. |
| **`/research/backfill`** | Which jobs exist and how far each has got. Every job appears, including one with zero rows. | "Progress means health." 8 jobs and 22,443 planned slices read as progress while zero bars were landing (`docs/FOLLOWUP.md` §2.1). Read `attempts` on *historical* slices, not the headline. |
| **`/research/coverage`** | Recording coverage against session-calendar denominators. | The overall figure as an acceptance gate — per-instrument calendars are not wired into `CoverageMonitor` (`docs/FOLLOWUP.md` §5, #36), so the 95% gate must be read against option-node rows. |
| **`/research/shadow-marks`** | What was marked, and what the planner would have built. A gap is a real missing day. | A `planner_intent` refusal as a data problem — "gateway unreachable" or "market closed" is the record being honest, and it is what the protocol asks for. |
| **`/research/paper-capture/fills`** | Fills this gateway placed. | "Everything the account traded" — not until [§3.1](#31-tws-master-api-client-id--docsfollowupmd-5) has been done and passed. |

The house rule underneath all of it: **absence renders as absence**. A gap is recorded with a reason
and left visible. Nothing here back-fills a number to make a report look complete.

---

## 13. Findings: where Aspire wiring met the application

Plan D is not allowed to change application logic, so these are recorded rather than worked around.

1. **`RecorderOrchestrator` has no `Enabled` switch.** Every ResearchService instance runs one, so
   the three chain drainers and the backfill drainer each stand up a full recorder that leases TWS
   market-data lines from the shared gateway budget (~90 lines, ~57 of them the recording grid).
   This is *already* today's behaviour with the ad-hoc processes, so the AppHost is not a
   regression — but it is now multiplied by a replica count that is one config value away from
   being raised. **Suggested fix, for the owner of that component:** a `Recorder:Enabled` flag in
   the same opt-out shape as `PaperCapture:Enabled`, so an auxiliary instance can decline to
   record. Until then, do not raise `Parameters__chain-drainer-replicas` without checking the
   gateway's line ledger.

2. **The research plane has no stable port under Aspire.** Every project binds `http://localhost:0`
   via `launchSettings.json`, so `$RESEARCH` changes on every start and no health-check URL in this
   runbook can be written literally. Pinning it was deliberately *not* done here: overriding the
   endpoint would conflict with the ad-hoc `:5710` still running, and with the replica set (three
   drainers cannot share a port). **Suggested fix:** pin only `researchservice` and `ibkrgateway` to
   fixed ports once the ad-hoc processes are retired.

3. **The shadow-mark run had no callable seam.** The whole computation lived inside the
   `POST /research/shadow-marks/run` endpoint lambda, so an in-process scheduler could only have
   duplicated it or called the service over loopback HTTP. Neither is acceptable — two versions of
   "what a shadow mark is" would drift, and a process calling itself over HTTP is untestable and
   deadlock-prone. The lambda body was therefore **moved verbatim** into
   `VolShadowMarkEndpoints.RunAsync`, with the three terminal outcomes returned as a value instead
   of as an `IResult`. No step, window, order, or refusal condition changed, and the endpoint
   remains the only HTTP surface. Flagged here because it is the one place Plan D touched a file
   Plan C owns.

4. **`Structure` is not covered by the arming parameter that gates orders.** `PaperAutomation:Signal`
   and `PaperAutomation:Enabled` are the documented opt-ins, but the protocol's instrument
   (`short-vol-credit-put`) is selected by a third key whose default is the original MVP debit
   vertical. Arming the signal without it points the constant-exposure signal at the wrong
   structure, and nothing refuses — the loop would trade a defined-risk debit vertical while the
   record says the run is short vol. The AppHost now surfaces `Structure` as a parameter alongside
   the other two so the three move together and are readable off the running configuration; a
   stronger fix (refusing to arm on the pair `constant-exposure` + `debit-vertical`) belongs to the
   signal's owner.

5. **`PaperCapture` and `ShadowMarks` default ON, which multiplies with replicas.** Both are
   opt-*out*, correctly — a session nobody captured cannot be captured afterwards. But that means
   every added ResearchService instance runs both unless told not to, and the AppHost has to
   remember to say `false` on each auxiliary resource. It is config discipline standing in for a
   "this instance is a worker" concept the application does not have. Recorded, not fixed.

6. **Two gateways were attached to the same paper TWS during this work** (client ids 11 and 12),
   both reporting `tradingPermitted: true`. Nothing in the stack detects or objects to that. It is
   an artefact of the ad-hoc bootstrap and [§11](#11-the-cutover-retiring-the-ad-hoc-processes-operator)
   resolves it, but the general case — a second socket owner nobody knows about — has no guard.
