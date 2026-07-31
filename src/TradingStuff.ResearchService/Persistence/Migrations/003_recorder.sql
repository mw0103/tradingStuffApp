-- 003: option-node registry, node assignments, raw event tables, recorder gaps.
-- gateway.option_quote_events / gateway.underlying_tick_events are WRITTEN by the gateway's
-- ObservationRecorder via Npgsql binary COPY; both are daily-partitioned and append-only.
--
-- Partitions are created ahead of time by ResearchService's PartitionMaintainer. Each table also
-- gets a DEFAULT partition as a safety net: a COPY landing before its daily partition exists (or
-- after PartitionMaintainer has fallen behind) still succeeds instead of failing loudly at the
-- worst possible moment — losing live option data because of a housekeeping gap is unacceptable.
-- Rows that land in DEFAULT are a signal PartitionMaintainer needs attention, not data loss.

-- ============ option node registry ============
-- Role-based longitudinal identity: a node's ROLE ("30DTE-25DP") is permanent; which conId plays
-- that role changes over time via node_assignments. Seeded once; the grid is registry-versioned
-- (selector_version on node_assignments) so later studies cannot quietly cherry-pick nodes.

CREATE TABLE research.option_nodes (
    node_id           smallint PRIMARY KEY,
    surface           text NOT NULL DEFAULT 'SPX',
    role              text NOT NULL UNIQUE,
    min_dte           integer NOT NULL,
    max_dte           integer NOT NULL,
    trading_class     text NOT NULL,        -- SPX (AM monthlies) or SPXW (PM weeklies/dailies)
    option_right      char(1) NOT NULL CHECK (option_right IN ('C', 'P')),
    strike_kind       text NOT NULL CHECK (strike_kind IN ('moneyness', 'delta')),
    strike_target     numeric NOT NULL      -- moneyness offset (bootstrap) or |delta| target
);

CREATE TABLE research.node_assignments (
    assignment_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    node_id           smallint NOT NULL REFERENCES research.option_nodes,
    con_id            integer NOT NULL,
    assigned_from     timestamptz NOT NULL,
    assigned_to       timestamptz,          -- NULL = current
    reason            text NOT NULL,        -- session_open | strike_drift | expiry_roll | reconnect | bootstrap
    selector_version  smallint NOT NULL
);

-- Enforces "at most one current row per node" at the database level. SELECT ... FOR UPDATE alone
-- does NOT provide this guarantee: under Read Committed, a FOR UPDATE query that blocks on a row
-- another transaction is about to UPDATE re-checks the WHERE clause against the row's new
-- committed version once the lock releases, and a row that no longer satisfies
-- "assigned_to IS NULL" (because the blocking transaction just set it) is silently excluded from
-- the result — the blocked transaction then sees NO current row and inserts its own, producing two
-- "current" rows for one node. Verified by reproducing the race directly against Postgres 17.
CREATE UNIQUE INDEX node_assignments_one_current_idx
    ON research.node_assignments (node_id) WHERE assigned_to IS NULL;

CREATE INDEX node_assignments_node_from_idx ON research.node_assignments (node_id, assigned_from DESC);
CREATE INDEX node_assignments_con_id_from_idx ON research.node_assignments (con_id, assigned_from DESC);

-- Seed the registered grid: 6 DTE buckets x 9 delta nodes = 54 roles, all currently unassigned
-- (node_assignments starts empty; NodeSelector fills it in as conIds resolve).
INSERT INTO research.option_nodes
    (node_id, surface, role, min_dte, max_dte, trading_class, option_right, strike_kind, strike_target)
SELECT
    (bucket.ordinal - 1) * 9 + node.ordinal AS node_id,
    'SPX',
    bucket.label || '-' || node.label,
    bucket.min_dte,
    bucket.max_dte,
    bucket.trading_class,
    node.option_right,
    'moneyness',   -- bootstrap kind; NodeSelector switches live assignments to 'delta' once refined
    node.moneyness_seed
FROM
    (VALUES
        (1, '7DTE',  0,  10, 'SPXW'),
        (2, '14DTE', 11, 20, 'SPXW'),
        (3, '30DTE', 21, 37, 'SPXW'),
        (4, '45DTE', 38, 52, 'SPXW'),
        (5, '60DTE', 53, 75, 'SPX'),
        (6, '90DTE', 76, 105, 'SPX')
    ) AS bucket(ordinal, label, min_dte, max_dte, trading_class)
CROSS JOIN
    (VALUES
        (1, 'ATM-C',   'C', 0.000),
        (2, 'ATM-P',   'P', 0.000),
        (3, '40D-C',   'C', 0.025),
        (4, '40D-P',   'P', -0.025),
        (5, '25D-C',   'C', 0.060),
        (6, '25D-P',   'P', -0.060),
        (7, '10D-C',   'C', 0.110),
        (8, '10D-P',   'P', -0.110),
        (9, '5D-P',    'P', -0.150)
    ) AS node(ordinal, label, option_right, moneyness_seed);

-- ============ raw events (gateway-written, append-only) ============

CREATE TABLE gateway.option_quote_events (
    event_id                bigint GENERATED ALWAYS AS IDENTITY,
    con_id                  integer NOT NULL,
    lease_id                uuid NOT NULL,
    observed_at             timestamptz NOT NULL,        -- EReader pump receipt
    persisted_at             timestamptz NOT NULL DEFAULT now(),  -- COPY batch commit
    tws_last_trade_at       timestamptz,                  -- reserved; not populated until tickString(45) is wired
    changed_fields          integer NOT NULL,             -- QuoteFieldChanges bitmask
    bid numeric, ask numeric, bid_size numeric, ask_size numeric,
    last numeric, last_size numeric, volume numeric, open_interest numeric,
    greeks_variant          smallint NOT NULL DEFAULT 0,  -- 0 none, 4 model (only variant recorded in v1)
    iv numeric, delta numeric, gamma numeric, vega numeric, theta numeric, und_price numeric,
    stale                   boolean NOT NULL DEFAULT false, -- staleness is a query-time concept (Phase 3); always false here
    locked                  boolean NOT NULL DEFAULT false,
    crossed                 boolean NOT NULL DEFAULT false,
    origin                  smallint NOT NULL,            -- 1 stream, 2 snapshot, 3 replay-resubscribe
    normalization_version   smallint NOT NULL,
    PRIMARY KEY (observed_at, event_id)
) PARTITION BY RANGE (observed_at);

CREATE TABLE gateway.option_quote_events_default PARTITION OF gateway.option_quote_events DEFAULT;
CREATE INDEX option_quote_events_default_con_id_idx ON gateway.option_quote_events_default (con_id, observed_at);

CREATE TABLE gateway.underlying_tick_events (
    event_id                bigint GENERATED ALWAYS AS IDENTITY,
    con_id                  integer NOT NULL,
    lease_id                uuid NOT NULL,
    observed_at             timestamptz NOT NULL,
    persisted_at             timestamptz NOT NULL DEFAULT now(),
    changed_fields          integer NOT NULL,
    bid numeric, ask numeric, bid_size numeric, ask_size numeric,
    last numeric, last_size numeric, volume numeric,
    locked                  boolean NOT NULL DEFAULT false,
    crossed                 boolean NOT NULL DEFAULT false,
    origin                  smallint NOT NULL,
    normalization_version   smallint NOT NULL,
    PRIMARY KEY (observed_at, event_id)
) PARTITION BY RANGE (observed_at);

CREATE TABLE gateway.underlying_tick_events_default PARTITION OF gateway.underlying_tick_events DEFAULT;
CREATE INDEX underlying_tick_events_default_con_id_idx ON gateway.underlying_tick_events_default (con_id, observed_at);

-- ============ recorder gaps ============
-- Gap truth must survive raw-partition retention drops (Phase 1+ hot window, later Parquet export),
-- so it is its own permanent table, never inferred after the fact from missing rows.

CREATE TABLE gateway.recorder_gaps (
    gap_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope        text NOT NULL,             -- 'connection' | 'lease:<uuid>'
    started_at   timestamptz NOT NULL,
    ended_at     timestamptz,
    reason       text NOT NULL              -- disconnect | tws_restart_window | line_evicted | buffer_overflow | write_failure
);

CREATE INDEX recorder_gaps_scope_started_idx ON gateway.recorder_gaps (scope, started_at DESC);
-- Open gaps (ended_at IS NULL) are looked up by scope on every disconnect/reconnect edge; a
-- partial index keeps that lookup cheap regardless of how large the closed-gap history grows.
CREATE INDEX recorder_gaps_open_idx ON gateway.recorder_gaps (scope) WHERE ended_at IS NULL;
