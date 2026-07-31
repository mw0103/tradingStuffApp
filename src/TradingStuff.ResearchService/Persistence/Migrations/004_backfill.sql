-- 004: exchange session calendar, backfill campaign/request/checkpoint tables, landed historical bars.
-- Everything downstream of Phase 1 recording (coverage windows, feature alignment, gap detection)
-- needs to reason in terms of real session boundaries and a resumable historical-data pipeline
-- rather than arbitrary UTC clock windows or in-memory backfill progress.

-- ============ session calendar ============
-- Lets later coverage/feature code align to real RTH/GTH boundaries per exchange instead of
-- arbitrary UTC windows. "calendar" is a free-text key (e.g. 'CBOE_INDEX_RTH', 'CBOE_INDEX_GTH',
-- 'CME_ES', 'NYSE') rather than an enum: the set of calendars this platform tracks grows as more
-- instruments are added, and a CHECK constraint here would just be migration churn.

CREATE TABLE research.sessions (
    session_id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    calendar            text NOT NULL,
    trading_date        date NOT NULL,
    open_utc            timestamptz NOT NULL,
    close_utc           timestamptz NOT NULL,
    label               text NOT NULL CHECK (label IN ('RTH', 'GTH')),
    is_half_day         boolean NOT NULL DEFAULT false,
    -- Which version of the session-generation logic (holiday/half-day rules) produced this row —
    -- the same "don't silently trust old derived data after the generator changes" role that
    -- normalization_version plays for observations (migration 003) and selector_version plays for
    -- node_assignments.
    generator_version   smallint NOT NULL,
    UNIQUE (calendar, trading_date, label),
    CHECK (close_utc > open_utc)
);

-- The lookup this table exists for: "what session (if any) covers this UTC instant" and "give me
-- every session for a calendar over a date range" both scan forward from a UTC bound.
CREATE INDEX sessions_calendar_open_idx ON research.sessions (calendar, open_utc);

-- ============ backfill campaigns ============
-- One row per (instrument, whatToShow, barSize, useRth, target range) the operator has asked the
-- platform to fill in. A job is a declaration of intent; backfill_requests below is where the actual
-- TWS request slices and their outcomes live.

CREATE TABLE research.backfill_jobs (
    job_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name             text NOT NULL UNIQUE,
    instrument_id    smallint NOT NULL REFERENCES research.instruments,
    con_id           integer, -- NULL for jobs that walk multiple contracts over time (e.g. ES rolls)
    what_to_show     text NOT NULL,
    bar_size         text NOT NULL,
    use_rth          boolean NOT NULL,
    target_from      timestamptz NOT NULL,
    target_to        timestamptz NOT NULL,
    priority         integer NOT NULL DEFAULT 0, -- higher drains first; an arbitrary operator scale, not an enum
    status           text NOT NULL DEFAULT 'pending'
                         CHECK (status IN ('pending', 'running', 'paused', 'complete', 'failed')),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);

-- ============ backfill requests — the checkpoint ============
-- One row per concrete TWS reqHistoricalData slice. This table, not any in-memory tracker, is what
-- makes backfill resumable: a restart re-derives "what's left to do" by querying state here rather
-- than replaying a job from scratch or losing track of in-flight slices.
--
-- THE IDEMPOTENCY KEY: UNIQUE (job_id, con_id, end_time_utc, duration, what_to_show, bar_size,
-- use_rth) on the exact TWS request parameters is what guarantees a rerun of the same slice produces
-- zero new rows instead of duplicate historical-data requests. end_time_utc may legitimately be NULL
-- for "now"-anchored requests (a live top-up walking forward to the current moment rather than a
-- fixed backfill boundary) — plain UNIQUE treats every NULL as distinct from every other NULL, which
-- would silently defeat the idempotency guarantee for exactly the requests that need it most (repeated
-- top-up calls). UNIQUE NULLS NOT DISTINCT (Postgres 15+; this repo targets postgres:17) closes that
-- hole by treating NULLs as equal for uniqueness purposes, the same choice migration 001 makes for
-- research.instruments' (symbol, kind, option_trading_class) key.
CREATE TABLE research.backfill_requests (
    request_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id            bigint NOT NULL REFERENCES research.backfill_jobs,
    con_id            integer NOT NULL,
    end_time_utc      timestamptz, -- NULL = "now"-anchored request; see idempotency-key note above
    duration          text NOT NULL, -- TWS duration string, e.g. '1 D', '2 W'
    what_to_show      text NOT NULL,
    bar_size          text NOT NULL,
    use_rth           boolean NOT NULL,
    state             text NOT NULL DEFAULT 'pending'
                          CHECK (state IN ('pending', 'inflight', 'succeeded', 'empty', 'failed', 'permanent')),
    attempts          integer NOT NULL DEFAULT 0,
    error_code        integer,
    error_message     text,
    bars_returned     integer,
    first_bar_utc     timestamptz,
    last_bar_utc      timestamptz,
    requested_at      timestamptz,
    completed_at      timestamptz,
    UNIQUE NULLS NOT DISTINCT (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth)
);

-- The coordinator's core query on every tick: "what's still pending/inflight for this job".
CREATE INDEX backfill_requests_job_state_idx ON research.backfill_requests (job_id, state);

-- ============ landed historical bars ============
-- ts_utc is the bar START time, UTC. The platform requests formatDate=2 (epoch seconds) precisely
-- so exchange-local TWS timestamp strings (formatDate=1) never need parsing against a timezone table
-- — the same "no hidden timezone conversion" doctrine Sessions.cs documents for ISessionClock.
-- trading_date is authoritative for daily bars, which carry no meaningful intraday time component
-- (a '1 day' TRADES bar's TWS timestamp is a date, not an instant); it is NULL for intraday bar sizes,
-- where ts_utc alone is the source of truth.
--
-- PRIMARY KEY (con_id, what_to_show, bar_size, use_rth, ts_utc) is what makes re-ingestion an
-- idempotent no-op: the same bar re-requested (retry, overlapping backfill/top-up windows, a
-- resumed job re-walking a slice it does not yet know succeeded) inserts with ON CONFLICT DO
-- NOTHING and lands zero new rows instead of a duplicate.
--
-- PARTITIONING DECISION — yearly, not monthly, and created up front rather than maintained at
-- runtime:
-- The full backfill target is roughly 10M rows / 1-2 GB total, spanning 1993 (SPY head) through
-- today, with SPX from 2004, VIX from 2005, and ES from about three years per rolled contract. The
-- original plan called for monthly partitions; at this total row count that is ~400 partitions
-- averaging ~25k rows each, which buys real query-planning overhead (partition pruning cost, catalog
-- bloat) for no benefit — there is no per-partition maintenance job for bars pruning old partitions
-- or needing them small. Yearly partitions instead give roughly 46 partitions (1990-2035), trivial
-- for the planner, and comfortably cover every instrument's full history without ever needing
-- PartitionMaintainer-style runtime partition creation the way the daily-partitioned raw event
-- tables (migration 003) do — bars are landed in bulk by known-ahead-of-time backfill jobs, not by
-- an always-on stream, so there is no future date to run ahead of.
--
-- Every yearly partition from 1990 through 2035 is created below, up front, by a DO block, plus one
-- DEFAULT partition as a safety net matching the pattern migration 003 established for the raw event
-- tables. This is deliberate, not just cheap insurance: this repo verified directly against live
-- Postgres 17 that once a row for some range has landed in a DEFAULT partition, Postgres permanently
-- refuses to create the real partition covering that range afterward ("updated partition constraint
-- for default partition ... would be violated by some row") — see PartitionMaintainer's remarks for
-- the same finding. Creating every yearly partition here, before this table has ever received a row,
-- is what makes that failure mode a non-issue for bars: there is no window in which a real bar could
-- land in DEFAULT for an in-range date. DEFAULT exists only to catch a genuinely out-of-range date
-- (something outside 1990-2035) without a hard insert failure.
CREATE TABLE research.bars (
    con_id            integer NOT NULL,
    instrument_id     smallint NOT NULL REFERENCES research.instruments,
    bar_size          text NOT NULL,
    what_to_show      text NOT NULL,
    use_rth           boolean NOT NULL,
    ts_utc            timestamptz NOT NULL,
    trading_date      date, -- authoritative for daily bars; NULL for intraday bar sizes (see above)
    open              numeric NOT NULL,
    high              numeric NOT NULL,
    low               numeric NOT NULL,
    close             numeric NOT NULL,
    volume            numeric, -- NULL for instruments TWS reports no volume for (e.g. index TRADES bars)
    wap               numeric,
    bar_count         integer,
    source            text NOT NULL CHECK (source IN ('backfill', 'topup')),
    -- Lineage back to the fetching request. NULL is reserved for a future non-backfill ingestion
    -- path (e.g. a live top-up not yet request-tracked); every bar landed by this phase's backfill
    -- coordinator carries a request_id.
    request_id        bigint REFERENCES research.backfill_requests,
    ingested_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (con_id, what_to_show, bar_size, use_rth, ts_utc)
) PARTITION BY RANGE (ts_utc);

CREATE TABLE research.bars_default PARTITION OF research.bars DEFAULT;

-- Up-front yearly partitions. Boundaries are constructed via AT TIME ZONE 'UTC' rather than plain
-- date literals so the partition bounds are exact UTC midnights regardless of the session timezone
-- the migration happens to run under — the same UTC-canonical discipline the rest of this schema
-- follows, applied to DDL as well as data. Safe to run after bars_default exists above: this table
-- is empty at migration time, so there is no row in DEFAULT for any of these ranges yet (see the
-- comment above the table).
DO $$
DECLARE
    yr integer;
BEGIN
    FOR yr IN 1990..2035 LOOP
        EXECUTE format(
            'CREATE TABLE research.bars_%1$s PARTITION OF research.bars FOR VALUES FROM (%2$L) TO (%3$L)',
            yr,
            (make_date(yr, 1, 1)::timestamp AT TIME ZONE 'UTC'),
            (make_date(yr + 1, 1, 1)::timestamp AT TIME ZONE 'UTC')
        );
    END LOOP;
END $$;

-- Reading a full instrument history (e.g. ES across every rolled contract) is by instrument_id, not
-- con_id — con_id changes every roll, instrument_id does not.
CREATE INDEX bars_instrument_idx ON research.bars (instrument_id, what_to_show, bar_size, use_rth, ts_utc);
