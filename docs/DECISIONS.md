# Architectural decisions

Load-bearing choices, each with the reasoning and the alternatives that were rejected. **The rejected
alternatives are the point** — a decision recorded without them gets re-litigated by the next person
who finds the chosen option inconvenient, and the second attempt rarely rediscovers why it was
rejected the first time.

Structural facts are in `docs/ARCHITECTURE.md`; current status in `docs/STATE.md`; the practices these
decisions grew out of are in `docs/LESSONS.md`.

---

## 1. One process owns the TWS socket

`TradingStuff.IbkrGateway` owns exactly one `EClientSocket`. Everything else reaches it over internal
HTTP.

A TWS connection is stateful and single-owner per `clientId`: request ids, order ids, and market-data
ticker ids are connection-scoped integers *you* allocate, and `nextValidId` seeds the order sequence
once per connection. Two services with independent id sequences against one account is how you get
orphaned orders and lost fills.

**Rejected:** a connection per service (id collisions, and only `clientId 0` or the configured master
receives order events for orders placed elsewhere). The Client Portal Web API (needs an interactive
browser login plus a `/tickle` keepalive, and the session expires — a poor fit for a service expected
to start unattended).

## 2. Postgres is the only data bus

No message broker carries research data. The snapshot builder and coverage monitor poll recent
partitions on timers.

At roughly 7M events and under 1 GB a day, with one consumer and time-range reads, Postgres *is* the
channel.

**Rejected:** Kafka and ClickHouse — three orders of magnitude below their break-even, and each adds
an operational surface with no consumer. RabbitMQ for research (it stays in the compose file for the
execution plane); SSE/long-poll event delivery. Revisit only if a sub-second live UI is ever wanted,
which research does not need.

## 3. Raw Npgsql for writes; the read side should move to Dapper

No ORM. Hand-written SQL with embedded, ordered `.sql` migrations applied under an advisory lock.

The recorder's hot path needs binary `COPY`, partition DDL is raw SQL regardless, and the claim cycle
needs `FOR UPDATE ... SKIP LOCKED` and `timestamptz[]` array parameters. An ORM adds nothing to any of
those and you would drop through it anyway.

**Open, and recommended:** ~15 multi-column read projections map by *ordinal position*
(`reader.GetInt64(0), reader.GetString(1), …`). Inserting a column into a `SELECT` list shifts every
subsequent ordinal, and a type-compatible shift yields **wrong data with no error**. Dapper's
name-based mapping removes that class outright. Keep raw Npgsql for writes and DDL.

**Rejected:** EF Core (the hot path and the DDL both bypass it); SQL Server (Postgres partitioning and
`COPY` are what this workload needs, and the platform is local-first).

## 4. The recorder lives in the gateway; ResearchService writes everything derived

The gateway writes raw observations straight to Postgres. ResearchService writes bars, sessions,
snapshots, features, and every report.

**Perishability asymmetry.** Live option ticks are unrecoverable, so the recording path gets the
fewest failure points — one hop, one dependency. ResearchService is redeployed constantly during
research iteration and must be able to restart all day without losing a tick. Historical bars *are*
re-requestable, so backfill takes the two-hop path.

This has a consequence that has already bitten twice: **the gateway outlives ResearchService by
design.** Any invariant of the form "component A starts before component B writes" is only true when
they start together — which is never the broken case. See decision 12.

## 5. `SessionClock` is the only type permitted to convert a timezone

UTC `timestamptz` is canonical everywhere. Intraday historical requests use `formatDate=2` (epoch) so
exchange-local timezone strings never enter parsing at all. Session labels come only from the
`research.sessions` table.

**Rejected:** a second conversion path anywhere. Two authorities on "what time is it at the exchange"
is the state that produces silent one-minute disagreements between a denominator and its numerator.

## 6. The session calendar is ground truth, and it refuses rather than projects

`exchange-calendars.json` plus `SessionGenerator` is the single authority for "what data was expected
when". Coverage, gap detection, and every future feature/label cutoff are validated *against* it.

Because a defect here is **invisible by construction** — the artifact that would catch it inherits the
same assumption — calendar entries are established from the venue schedule
(`reqHistoricalData(whatToShow="SCHEDULE")`, cross-checked against `contractDetails` and real bar
spans), each entry carries an honest `confidence`, and a range that cannot be established is **left
unwritten** rather than extrapolated.

Related consequences:

- **Instrument ≠ venue.** `InstrumentCalendars` maps each instrument to its own calendar. The SPX
  *index* stops at the cash close (390 min) while SPX *options* trade to 16:15 ET (405 min); using the
  option window for index bars flagged every correct session as short.
- **`EffectiveFrom`/`EffectiveTo` with a load-time tiling check.** Rows sharing a label must cover the
  calendar's lifetime with no gap and no overlap. A gap emits no session for the dates inside it —
  absence rendering as health.
- **Holiday ≠ closed.** `partialSessionSets` exists because CME trades shortened sessions on several
  US holidays. Good Friday is not a rule: CME was shut in 2024/2025 and open in 2023/2026.

**Rejected:** deriving an early close as "the regular close minus a fixed offset" (measured false for
VIX); inferring a session's open from the first traded bar (that is only an upper bound).

## 7. A node is a role, not a contract

`research.option_nodes` holds 54 registered roles (`30DTE-25D-P`). `research.node_assignments` maps
each role to a concrete conId over time, with `assigned_from`/`assigned_to`. A strike or expiry roll
is a new assignment row, not a new node.

This is what makes a longitudinal series survive rotation, and it is the identity Phase 4's studies
will key on. Coverage is therefore reported **per role**, summing each conId's own tenure — reporting
per conId turned a flawlessly-recorded, merely-rotated node into two partial rows averaging ~50 %.

**Selection must refuse rather than approximate.** A node's target strike must be *bracketed* by
listed strikes on both sides; an unbounded nearest-match silently clamped nine roles per bucket onto
four contracts while every report showed them healthy. Bracketing cannot be satisfied by an edge clamp
at any window width, which is why the guard is structural rather than a tolerance.

**`selector_version` is a data-provenance boundary.** Version 1 assignments came from the collapsing
selector and describe a grid that was not what it claimed; version 2 onward is trustworthy.

## 8. Provenance is a column, not an inference

Wherever a value could have been *measured* or *assumed*, the record says which:

| Column | Values |
|---|---|
| `recorder_gaps.closed_by` | `observed` (a tick resumed) / `inferred` (a later process bounded it) |
| `schema_migrations.checksum_source` | `verified` / `assumed` / `unknown` |
| `ibkr_order_map.perm_id_state` | `assigned` / `never_reported` / `pending` |

Backfilled rows take the *weaker* claim. A checksum computed from what the assembly embeds today is an
assumption about what ran, not a measurement of it — and rows that predate the distinction are
`unknown`, because nothing distinguishes them after the fact.

**Rejected:** inferring provenance from row shape (e.g. treating "has an end time" as "was observed").
That is exactly how an inferred bound comes to read as a measurement.

## 9. The money check fails closed

`PortfolioRiskEvaluator` refuses to price what it cannot price. A leg whose quote is missing, whose
Greeks are all zero, or which has **no price on the side it trades** is `UNPRICEABLE_LEG` — a
rejection carrying no figures at all.

The per-side rule matters: a zero bid on a *sold* leg is not conservative, it pushes the net across
zero and hands a credit spread the debit-spread formula.

Max-loss formulas are quantity-aware and **do not branch on the sign of the price** — exposure is a
property of the strikes, and a sign branch is reachable by one bad quote. The validator and the
evaluator enforce shape independently, because they are separate services and neither may trust its
caller.

**Rejected:** estimating from a partial quote (the gateway deliberately returns zero-filled snapshots
on timeout, and 0/0 is routine pre-market); a default arm that estimates an unknown strategy rather
than rejecting it.

## 10. Invariants belong in the schema

Where the database can hold an invariant, it does — because a convention is only as good as the next
writer's memory of it.

- `CHECK (state <> 'inflight' OR (claimed_by IS NOT NULL AND lease_expires_at IS NOT NULL))` — an
  inflight row is *always* reclaimable, including one inserted by hand in psql.
- `CHECK ((ended_at IS NULL) = (closed_by IS NULL))` — a closed gap always says how it was closed.
- `CREATE UNIQUE INDEX … ON node_assignments (node_id) WHERE assigned_to IS NULL` — one current
  assignment per node. `SELECT ... FOR UPDATE` does **not** give this under Read Committed: a blocked
  query re-checks its `WHERE` against the new committed row version, and a row that no longer matches
  is silently excluded, so both callers conclude "no current row" and both insert.

**Note the ordering trap:** `ADD CONSTRAINT` validates existing rows immediately, so backfill *before*
declaring. Learned by aborting a migration against a database with one closed gap in it.

## 11. Claim, don't check-then-act

`BackfillStore.ClaimAsync` is a single `UPDATE ... RETURNING` over a `FOR UPDATE ... SKIP LOCKED`
candidate subquery. `SKIP LOCKED` never blocks, so the unblock-and-re-evaluate window does not exist,
and the decision and the write are one statement in one transaction.

The consequence of getting this wrong is what makes it worth the care: a losing claimer would see zero
pending rows, and **"zero pending rows" is indistinguishable from "the job is done"**. Completion is
therefore never inferred from an empty claim anywhere in that class.

The same shape governs the subscription lease: `ActiveLease` is an encapsulated state machine whose
ticker, line lease, and terminated flag are private behind one lock, reachable only through
check-and-mutate-as-one-step methods. `TryClaimTermination` is the only way to obtain the state needed
to unwind, and one `TerminateAsync` is its only caller — so a future third termination path *cannot*
skip the gap close.

## 12. Put the invariant in the schema, not in a startup order

Migration 012 creates a 14-day forward horizon of daily partitions **at migration time**.

The alternative — gate the partition maintainer on migrations, and the recorder on the maintainer —
only holds when both processes start together, and per decision 4 they deliberately do not. As a
schema property it holds for every start order, for a ResearchService that is switched off, and for a
gateway that outlives several of them.

This matters because a row landing in a `DEFAULT` partition **permanently** blocks that date's real
partition. Verified against Postgres 17, and unrecoverable without a manual migration.

## 13. Research UI is a React + Vite SPA served by ResearchService

Static assets under `/ui`, API under `/research/*`. The `/research/*` prefix is deliberately anonymous
and read-only, matching AuditDashboard's existing posture.

Two build subtleties, both learned the hard way: `wwwroot` is a **build artifact**, so the Web SDK's
implicit content glob (which runs at evaluation, before Vite writes) must be removed and the assets
copied explicitly — otherwise a clean checkout ships a service where every `/ui/*` path 404s while the
build reports success. And static files must run **before** routing, or the SPA fallback claims every
request and `StaticFileMiddleware` skips it.

**Open:** the ClientApp types are hand-written and independent of the C# records, so a server-side
contract change compiles clean and renders an empty page. Generating them is the structural fix.

## 14. Paper is for testing; live is hard-gated

On a verified `DU` account, exercise everything — place orders, cancel them, blow through risk limits,
saturate the line ledger, kill the connection mid-request. That is what it is for, and importing
caution from the live side is what let a fatal recorder bug ship.

The live side is gated by construction: exactly **one** `placeOrder` call site, reachable by no test;
`IBKR:AllowLiveTrading` false in every committed file; a `DU`-prefix check at connect. Adding or
changing a real order-placement call site for a live account is not a routine edit.

**Rejected:** relaxing the paper-side caution (it costs real defects), and any test path that can
reach `placeOrder`.

## 15. Provider-neutral contracts — an acknowledged debt

The stated goal is canonical internal contracts with provider specifics confined to adapters.

**The codebase does not currently meet it.** `research.bars` is keyed on
`(con_id, what_to_show, bar_size, use_rth, ts_utc)` — a provider instrument identifier and two verbatim
TWS parameter names in a primary key — and `ConId` / `WhatToShow` / `UseRth` are load-bearing in
`TradingStuff.ResearchContracts`.

Deferring the *second adapter* is fine. Shaping the *canonical store* like one vendor's API is not the
same thing: the first costs a class later, the second costs a migration of every recorded bar. It gets
more expensive every day the recorder runs, and Phase 3 snapshots will key derived data off the same
shape.

Recorded here as a known debt with its reversal cost, rather than as a principle the code honours.
