-- 019: ThetaData historical SPX/SPXW/VIX option-chain ingestion (Phase 9).
--
-- Canonical storage is keyed on (underlying, trading_class, expiration, strike, option_right,
-- observed_at) -- NEVER on a ThetaData identifier. docs/DECISIONS.md §15 records the existing debt
-- (research.bars carries con_id and two verbatim TWS parameter names in its primary key); this
-- table is deliberately NOT built the same way a second time. trading_class stays a first-class
-- column rather than vendor metadata for the same reason migration 003's node registry keeps it:
-- SPX (AM-settled monthlies) and SPXW (PM-settled weeklies/dailies) are genuinely different
-- instruments at the same strike and expiration, not a broker/vendor spelling of the same one.
--
-- Vendor identity -- WHICH vendor, which of its own symbol strings, which endpoint, which sampling
-- interval, when it was fetched -- is carried as descriptive provenance columns on
-- option_chain_quotes, the same role research.bars' `source` and `request_id` columns play. None of
-- them are part of the primary key, and ThetaData's own "symbol" string (which conflates underlying
-- and trading class -- see TradingStuff.ResearchService.OptionChains.ThetaSymbolMap) never appears
-- as an identifying column anywhere in this schema.

-- ============ ingestion jobs ============
-- One row per (underlying, trading_class, date range) an operator has asked the platform to fill
-- in. Mirrors research.backfill_jobs' role: a declaration of intent, not the checkpoint itself.

CREATE TABLE research.option_chain_jobs (
    job_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name            text NOT NULL UNIQUE,
    underlying      text NOT NULL,              -- canonical: 'SPX', 'VIX'
    trading_class   text NOT NULL,              -- 'SPX' (AM monthlies), 'SPXW' (PM weeklies/dailies), 'VIX'
    target_from     date NOT NULL,
    target_to       date NOT NULL,

    -- THE SIZING DECISION (docs/FOLLOWUP.md §4.5, docs/plans/ibkr-edge-research-roadmap.md Phase 9):
    -- ingestion DEFAULTS to '1m' -- one minute-bucketed daily snapshot per contract per trading day,
    -- via ThetaDataClient.GetDailyChainQuotesAsync, which is what the vendor's own interval=1m
    -- parameter produces here, at whatever clock time ThetaDataOptions.SnapshotTimeOfDay names
    -- (process-wide config, defaulting to 15:45 exchange-local -- there is deliberately no per-job
    -- override column here: ThetaDataClient's snapshot time is a single instance field, and a column
    -- this schema could not actually make the client honour per job would be exactly the kind of
    -- surface docs/LESSONS.md warns against — present, plausible, and silently ignored).
    -- 'tick' is a recognised value but OptionChainCoordinator.PlanJobAsync
    -- never plans request rows for a job carrying it -- see OptionChainEndpoints for where choosing
    -- tick is made to require an explicit, separate confirmation, and OptionChainStore.EnsureJobAsync
    -- for why such a job is created already 'paused' and stays that way. Bulk tick ingestion was
    -- explicitly ruled out of scope for the automatic coordinator; this column exists so a job can
    -- SAY it wants tick without the coordinator ever acting on that by itself.
    interval        text NOT NULL DEFAULT '1m' CHECK (interval IN ('1m', 'tick')),

    priority        integer NOT NULL DEFAULT 0,
    status          text NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'running', 'paused', 'complete', 'complete_with_gaps', 'failed')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (target_to >= target_from)
);

-- ============ ingestion requests — the checkpoint ============
-- One row per (job, expiration). THIS is what makes ingestion resumable and idempotent, the same
-- role research.backfill_requests plays for bar backfill: a restart re-derives "what's left to do"
-- from this table, and a rerun that plans the same job re-derives the identical expiration list and
-- inserts zero new rows via ON CONFLICT DO NOTHING.
--
-- Granularity is deliberately per-EXPIRATION, not per-day: ThetaDataClient.GetDailyChainQuotesAsync
-- already pulls a whole [target_from, target_to] date range for one expiration in a single call (the
-- vendor bills this as one request regardless of how many days it spans), so slicing further would
-- only multiply request rows without buying any real resumability -- a partially-landed expiration on
-- a crashed claim is retried whole, which costs one extra paced call, not a lost afternoon of
-- backfill the way an interrupted bar slice would.

CREATE TABLE research.option_chain_requests (
    request_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id            bigint NOT NULL REFERENCES research.option_chain_jobs,
    expiration        date NOT NULL,
    state             text NOT NULL DEFAULT 'pending'
                          CHECK (state IN ('pending', 'inflight', 'succeeded', 'empty', 'failed', 'permanent')),
    attempts          integer NOT NULL DEFAULT 0,
    claimed_by        text,
    lease_expires_at  timestamptz,
    error_message     text,
    quotes_returned   integer,
    quotes_landed     integer,
    requested_at      timestamptz,
    completed_at      timestamptz,
    UNIQUE (job_id, expiration)
);

CREATE INDEX option_chain_requests_job_state_idx ON research.option_chain_requests (job_id, state);

-- ============ landed quotes (canonical) ============
-- ts_utc-equivalent here is observed_at. ThetaData's quote timestamp arrives as a naive local
-- instant (no offset in the wire format -- verified against the live Terminal 2026-08-02); it is
-- interpreted as America/New_York (the OPRA/Cboe listing timezone for every root this ingests) and
-- converted to UTC once, at the adapter boundary (OptionChainQuoteCsvParser), the same "convert once,
-- at the boundary" discipline HistoricalBarAdapter follows for realized-vol inputs. That
-- interpretation is an ASSUMPTION about the feed's timestamp convention, not something this migration
-- or its adapter has verified against ThetaData's own documentation -- see the ingestion report.
--
-- PARTITIONING: yearly, matching migration 004's reasoning for research.bars almost exactly -- a
-- DEFAULT partition catches anything outside the pre-created range without a hard insert failure, and
-- creating 2010-2035 up front, before this table has ever received a row, is what avoids the
-- "a row already landed in DEFAULT permanently blocks the real partition for that range" failure
-- migration 004 documents and this repo has verified directly against live Postgres 17.
CREATE TABLE research.option_chain_quotes (
    underlying       text NOT NULL,
    trading_class    text NOT NULL,
    expiration       date NOT NULL,
    strike           numeric NOT NULL,
    option_right     char(1) NOT NULL CHECK (option_right IN ('C', 'P')),
    observed_at      timestamptz NOT NULL,
    trading_date     date NOT NULL,
    bid              numeric,
    ask              numeric,
    bid_size         numeric,
    ask_size         numeric,
    bid_exchange     smallint,
    ask_exchange     smallint,

    -- ---- provenance: descriptive only, never part of the primary key (see header) ----
    vendor           text NOT NULL DEFAULT 'thetadata',
    vendor_symbol    text NOT NULL,   -- the vendor's own root/symbol string, e.g. 'SPXW' — NOT an identity column
    vendor_endpoint  text NOT NULL,   -- e.g. '/v3/option/history/quote'
    interval         text NOT NULL,  -- what was actually requested for THIS row: '1m' or 'tick'
    request_id       bigint REFERENCES research.option_chain_requests,
    fetched_at       timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (underlying, trading_class, expiration, strike, option_right, observed_at)
) PARTITION BY RANGE (observed_at);

CREATE TABLE research.option_chain_quotes_default PARTITION OF research.option_chain_quotes DEFAULT;

DO $$
DECLARE
    yr integer;
BEGIN
    FOR yr IN 2010..2035 LOOP
        EXECUTE format(
            'CREATE TABLE research.option_chain_quotes_%1$s PARTITION OF research.option_chain_quotes FOR VALUES FROM (%2$L) TO (%3$L)',
            yr,
            (make_date(yr, 1, 1)::timestamp AT TIME ZONE 'UTC'),
            (make_date(yr + 1, 1, 1)::timestamp AT TIME ZONE 'UTC')
        );
    END LOOP;
END $$;

-- The read pattern every consumer of this table has: "give me the whole chain for this underlying
-- and trading class on this trading date", to build one OptionChainSlice per expiration observed
-- that day.
CREATE INDEX option_chain_quotes_lookup_idx ON research.option_chain_quotes
    (underlying, trading_class, trading_date, expiration);
