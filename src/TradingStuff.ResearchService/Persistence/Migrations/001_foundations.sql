-- 001: schemas, canonical instruments, capability-probe registry, broker order map.
-- gateway.* tables are WRITTEN by TradingStuff.IbkrGateway but OWNED (migrated) here, so there is
-- exactly one schema authority in the system.

CREATE SCHEMA IF NOT EXISTS gateway;
CREATE SCHEMA IF NOT EXISTS research;

-- Canonical research instruments. Deliberately seeded, not discovered: the initial universe is a
-- deliberate design decision (docs/plans/ibkr-edge-research-roadmap.md), not whatever resolves.
CREATE TABLE research.instruments (
    instrument_id        smallint PRIMARY KEY,
    symbol               text NOT NULL,
    kind                 text NOT NULL, -- index | stock | future_family | option_class
    option_trading_class text,
    exchange             text NOT NULL,
    currency             text NOT NULL DEFAULT 'USD',
    timezone             text NOT NULL, -- IANA id of the listing exchange
    UNIQUE NULLS NOT DISTINCT (symbol, kind, option_trading_class)
);

INSERT INTO research.instruments
    (instrument_id, symbol, kind, option_trading_class, exchange, timezone) VALUES
    (1, 'SPX', 'index',         NULL,   'CBOE',  'America/Chicago'),
    (2, 'SPX', 'option_class',  'SPX',  'SMART', 'America/Chicago'),
    (3, 'SPX', 'option_class',  'SPXW', 'SMART', 'America/Chicago'),
    (4, 'VIX', 'index',         NULL,   'CBOE',  'America/Chicago'),
    (5, 'SPY', 'stock',         NULL,   'ARCA',  'America/New_York'),
    (6, 'ES',  'future_family', NULL,   'CME',   'America/Chicago');

-- Runtime-verified provider capabilities. What TWS actually serves changes with upgrades and
-- entitlements; every design decision leaning on a capability should point at the probe row that
-- verified it. Append-only: re-probes add rows, never rewrite history.
CREATE TABLE research.capability_probes (
    probe_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    probe_key          text NOT NULL,
    con_id             integer,
    ran_at             timestamptz NOT NULL,
    tws_server_version integer,
    market_data_type   integer,
    succeeded          boolean NOT NULL,
    result             jsonb NOT NULL,
    error_code         integer,
    notes              text
);

CREATE INDEX capability_probes_key_ran_idx
    ON research.capability_probes (probe_key, ran_at DESC);

-- Durable internal-order -> broker-order mapping, written by the gateway BEFORE placeOrder. Without
-- this a gateway restart forgets which internal orders already reached the broker, and a caller
-- retry places a second live order for the same internal id.
CREATE TABLE gateway.ibkr_order_map (
    internal_order_id uuid PRIMARY KEY,
    ibkr_order_id     integer NOT NULL UNIQUE,
    perm_id           bigint,
    account           text,
    last_status       text NOT NULL,
    placed_at         timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);
