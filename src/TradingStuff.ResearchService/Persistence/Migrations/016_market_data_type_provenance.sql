-- 016: record WHICH market data type TWS actually reported for a recorded tick — live, frozen,
-- delayed, or delayed-frozen — not which one the gateway asked for.
--
-- THE FAILURE THIS EXISTS FOR. IBKR__MarketDataType defaults to 3 (delayed) so first-run setup
-- works without an OPRA subscription (see AppHost's comment on ibkr-market-data-type). TWS can also
-- silently DOWNGRADE a request: ask for 1 (live) and receive 2 (frozen) or 3 (delayed) back when the
-- entitlement is missing — IbkrClientWrapper.marketDataType(reqId, marketDataType) exists precisely
-- to report that answer, and until now the gateway only logged it. So a whole SPX surface session
-- recorded on 15-minute-old quotes was, after the fact, indistinguishable from a live one: nothing
-- in gateway.option_quote_events or gateway.underlying_tick_events said which regime was in force
-- when a row was captured. Recorded ticks cannot be re-collected, so this is the one dataset with no
-- second chance to get provenance right.
--
-- WHY NULLABLE WITH NO DEFAULT, AND WHY NULL MEANS UNKNOWN RATHER THAN LIVE OR DELAYED. Exactly the
-- shape research.checksum_source (013) and gateway.ibkr_order_map.perm_id_state (014) already use:
-- a value can be MEASURED, or it can be ABSENT, and collapsing "absent" into either extreme is a
-- fabrication. Here NULL covers two cases that cannot be told apart after the fact — a row recorded
-- before this column existed, and a row recorded before TWS's marketDataType callback had arrived
-- for that ticker (there is a real window between reqMktData and the callback; see the C# side of
-- this change) — and both must read as "we do not know", never as "assume live". The safe direction
-- matters here specifically: guessing 'live' on an unmeasured row would silently launder exactly the
-- data quality problem this column exists to expose, and guessing 'delayed' would falsely condemn
-- rows that really were live. Only what TWS actually reported is written; the requested value
-- (IbkrOptions.MarketDataType) is NEVER stamped here — see the C# routing this migration supports.
--
-- DOMAIN. smallint, matching IbkrOptions.MarketDataType's own documented range: 1 live, 2 frozen,
-- 3 delayed, 4 delayed-frozen. A CHECK constraint holds the domain in the schema rather than in
-- convention (docs/DECISIONS.md §10) — the same reasoning research.schema_migrations.checksum_source
-- and gateway.ibkr_order_map.perm_id_state's domain CHECKs already use.
--
-- ORDERING. Migration 006 hit the trap of declaring a CHECK before backfilling the rows it would
-- have rejected, and aborted against a database with a real closed gap in it. That trap does not
-- apply here: every row that exists before this migration runs gets market_data_type = NULL (the
-- column has no default and nothing backfills a value), and NULL satisfies
-- "market_data_type IS NULL OR ..." trivially. The CHECK can therefore be declared in the same
-- statement set as the column, with nothing to backfill first.
--
-- PARTITIONS. gateway.option_quote_events and gateway.underlying_tick_events are daily
-- RANGE-partitioned with a DEFAULT partition (migration 003), and migration 012's header records
-- that a row landing in DEFAULT is a standing, permanent hazard on these two tables specifically.
-- Verified directly against Postgres 17 (this environment) before writing this file: ALTER TABLE
-- ADD COLUMN / ADD CONSTRAINT against the PARENT is metadata-only and applies atomically to every
-- existing partition, including the DEFAULT one — pg_attribute shows the identical attnum on the
-- parent, a real dated partition, and the DEFAULT partition after one ALTER TABLE each, and an
-- INSERT of an out-of-domain value into the dated partition is rejected by the parent's CHECK. No
-- per-partition DDL is needed, and none is run here. (This is a materially different situation from
-- migration 012's own hazard, which is about a partition failing to be CREATED at all, not about a
-- column or constraint failing to propagate to one that already exists.)
--
-- CHECKSUMS. This file is content, not behaviour with a separate backfill step, so it plays with the
-- checksum machinery (010, 013) the same way every migration since 010 does: the runner computes and
-- records its checksum in the same transaction as this DDL when it first applies, with
-- checksum_source = 'verified'. Nothing here needs the ADD COLUMN IF NOT EXISTS / backfill shape
-- 013 needed — that shape is only for a column the RUNNER's own bootstrap must lay down before any
-- migration in the pass can run (research.schema_migrations.checksum / checksum_source themselves).
-- This column belongs to ordinary migrated schema, not to the runner's bootstrap, so a plain
-- ADD COLUMN is correct and matches every other migration's ALTER on these tables.

ALTER TABLE gateway.option_quote_events
    -- What TWS reported for the ticker this row's tick arrived on, at the time it arrived:
    --   1 live | 2 frozen | 3 delayed | 4 delayed-frozen
    -- NULL means UNKNOWN, not "assume live": either this row predates this column, or it was
    -- recorded before TWS's marketDataType callback had reported anything for that ticker yet.
    -- Never backfilled and never inferred from IbkrOptions.MarketDataType (the REQUESTED type) —
    -- TWS can and does downgrade a request silently, and stamping the request would fabricate
    -- exactly the "recorded live" claim this column exists to make honest.
    ADD COLUMN market_data_type smallint,
    ADD CONSTRAINT option_quote_events_market_data_type_chk
        CHECK (market_data_type IS NULL OR market_data_type IN (1, 2, 3, 4));

ALTER TABLE gateway.underlying_tick_events
    ADD COLUMN market_data_type smallint,
    ADD CONSTRAINT underlying_tick_events_market_data_type_chk
        CHECK (market_data_type IS NULL OR market_data_type IN (1, 2, 3, 4));
