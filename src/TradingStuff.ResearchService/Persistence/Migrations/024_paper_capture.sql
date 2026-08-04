-- Raw capture for the paper-run protocol's shadow record items 6-11
-- (docs/plans/paper-run-protocol.md; docs/plans/paper-test/plan-c-capture.md).
--
-- These two tables hold RAW BROKER READINGS ONLY. Nothing here is derived, netted, marked, or
-- adjusted, and nothing here may become so. The distinction is the whole reason the tables exist:
-- the derived analytics the protocol asks for (item 10's idealized variance-swap P&L, item 11's
-- independently reconstructed counterfactual) are reproducible from these rows at any later date,
-- whereas a fill or a margin figure that was never captured at the time is gone permanently. So the
-- rule is: write down what TWS said, in the units TWS said it, and compute later.
--
-- Append-only for the same reason the trial registry is (migration 015): a capture layer that can
-- be rewritten after the outcome is known is not evidence about what the broker reported, it is a
-- record of what someone later believed. Corrections arrive as NEW rows - TWS itself re-issues a
-- corrected execution under a new exec_id suffix - and the capture timestamps say which is which.

CREATE TABLE IF NOT EXISTS research.paper_fills (
    fill_id             bigserial PRIMARY KEY,

    -- When this process wrote the row, and which trading date's capture pass produced it. The
    -- trading date comes from ISessionClock, never from a UTC calendar date: an execution at
    -- 20:15 UTC belongs to the session that closed, not to the next UTC day.
    captured_at         timestamptz NOT NULL DEFAULT now(),
    trading_date        date        NOT NULL,

    account_id          text        NOT NULL,

    -- TWS's own execution identifier, and the reason a re-run of the same day adds nothing: it is
    -- unique per execution report and stable across a reconnect replay.
    exec_id             text        NOT NULL,

    -- The three broker-side identifiers, all recorded because they join to different things:
    -- perm_id survives a TWS restart and is what the order map reconciles on (migration 014),
    -- ibkr_order_id is the per-session id, client_id says which API client placed it (an order
    -- placed by hand in TWS carries a different one, and that is a fact worth keeping).
    perm_id             bigint,
    ibkr_order_id       integer,
    client_id           integer,

    -- The achieved contract, verbatim. Recorded as separate columns rather than a parsed
    -- OptionContract because this is a broker reading: a leg whose expiry or right this platform's
    -- option model could not represent must still land here intact.
    con_id              integer     NOT NULL,
    symbol              text        NOT NULL,
    sec_type            text        NOT NULL,
    expiration          date,
    strike              numeric(18, 4),
    option_right        text,
    trading_class       text,
    multiplier          integer,

    -- 'BOT'/'SLD' exactly as TWS reports it. Not normalised to an OrderSide enum: the enum can gain
    -- members or change spelling, and this column is meant to still mean the same thing in a year.
    side                text        NOT NULL,
    quantity            numeric(18, 4) NOT NULL,
    price               numeric(18, 6) NOT NULL,

    -- TWS reports the execution time as a bare string whose timezone convention depends on the
    -- server version and on the TWS API timezone setting. The verbatim string is therefore the
    -- record and the instant is a PARSE of it: NULL when the shape was not one the adapter
    -- recognises, rather than a capture-time substitute that would silently mis-date the fill.
    -- See IbkrExecutionsClient.TryParseExecutionTime.
    executed_at_raw     text        NOT NULL,
    executed_at         timestamptz,

    exchange            text,

    -- Commission arrives on a SEPARATE TWS callback that may not land before the executions request
    -- terminates, so NULL means "not reported within the capture pass", not "free". Absence renders
    -- as absence; a zero here would be a fabricated cost basis.
    commission          numeric(18, 6),
    commission_currency text,
    realized_pnl        numeric(18, 6),

    -- Which read produced the row, e.g. 'ibkr-gateway/reqExecutions'. Provenance, not decoration:
    -- a fill captured from a different surface later must be distinguishable from these.
    capture_source      text        NOT NULL,

    schema_version      integer     NOT NULL DEFAULT 1,

    CONSTRAINT paper_fills_exec_id_unique UNIQUE (exec_id)
);

CREATE INDEX IF NOT EXISTS paper_fills_trading_date_idx
    ON research.paper_fills (trading_date DESC, executed_at DESC NULLS LAST);

COMMENT ON TABLE research.paper_fills IS
    'RAW, NOT DERIVED: one row per broker execution report exactly as TWS reported it, append-only. '
    'Shadow record items 6-7 depend on it and cannot be backfilled; derived P&L belongs elsewhere.';

CREATE TABLE IF NOT EXISTS research.paper_account_snapshots (
    snapshot_id         bigserial PRIMARY KEY,

    trading_date        date        NOT NULL,
    snapshot_at         timestamptz NOT NULL,

    -- NULL on a refusal row and only there - see the CHECK below and the refusal columns.
    account_id          text,

    -- The margin and equity tags the protocol's item 8 needs, in the account's base currency, as
    -- reported. Individually NULL-able on purpose: TWS does not serve every tag for every account
    -- type, and a defaulted zero for maintenance margin would read as an unmargined position.
    net_liquidation     numeric(18, 4),
    maintenance_margin  numeric(18, 4),
    init_margin         numeric(18, 4),
    excess_liquidity    numeric(18, 4),
    available_funds     numeric(18, 4),
    buying_power        numeric(18, 4),
    gross_position_value numeric(18, 4),
    currency            text,

    -- Every summary tag TWS returned, unparsed: [{tag, value, currency}, ...]. The typed columns
    -- above are a convenience projection of this; the array is the provenance, so a tag nobody
    -- thought to add a column for is still captured the day it mattered.
    summary             jsonb,

    -- One entry per open position: con_id, symbol, secType, expiration, strike, right, quantity,
    -- averageCost, and market price/value WHERE TWS REPORTS THEM. reqPositionsMulti does not carry
    -- a mark, so those keys are absent rather than zero on a positions-stream capture - see
    -- PaperCaptureService. No Greeks: v1 captures raw quotes and positions and derives Greeks later
    -- from the chain data, per plan C's non-goals.
    positions           jsonb,
    position_count      integer,

    -- How many paper_fills rows exist for this trading date, counted inside the capture's own
    -- transaction. Deliberately the TABLE's count and not the number of executions the pass pulled:
    -- the two differ whenever TWS replays an execution an earlier pass already captured, and only
    -- the former is a claim a later reader can reconcile against the rows. Zero is a real answer (a
    -- session with no trades) and is not the same as a refusal.
    fill_count          integer,

    -- A named refusal, or NULL on a capture. A pass that could not read the broker writes a row
    -- SAYING SO rather than writing nothing: an evening with the gateway down must not be
    -- indistinguishable from an evening with a flat account. refusal_kind is the short stable name
    -- ('gateway-unreachable', 'broker-not-connected', ...), refusal the detail.
    refusal_kind        text,
    refusal             text,

    -- Which read produced the row, and WHEN it was taken relative to the session it is keyed to.
    -- 'ibkr-gateway/account-streams' is a snapshot that still describes the session's END state;
    -- '...@late' is a recovery pass, whose account figures are of the moment it ran and not of that
    -- session's close. The account read is always of NOW, so a Monday pass recovering Friday records
    -- Monday's margin against Friday's date; that reading is worth keeping (it is the only one that
    -- date will ever have) but must not be read as the close, and append-only means the distinction
    -- cannot be added later. Anything computing margin AT the close filters on this column.
    capture_source      text        NOT NULL,
    schema_version      integer     NOT NULL DEFAULT 1,

    -- A row is either a capture or a refusal, never half of each. Without this a partially-written
    -- pass could leave a row that reads as a successful snapshot of an account holding nothing.
    CONSTRAINT paper_account_snapshots_capture_or_refusal CHECK (
        (refusal_kind IS NULL AND refusal IS NULL
            AND account_id IS NOT NULL AND positions IS NOT NULL
            AND position_count IS NOT NULL AND fill_count IS NOT NULL)
        OR
        (refusal_kind IS NOT NULL AND refusal IS NOT NULL
            AND positions IS NULL AND position_count IS NULL AND fill_count IS NULL)
    )
);

-- Idempotency, and the reason the capture pass can simply re-run: at most ONE successful snapshot
-- per trading date. A second pass on the same date conflicts and does nothing.
CREATE UNIQUE INDEX IF NOT EXISTS paper_account_snapshots_one_capture_per_date
    ON research.paper_account_snapshots (trading_date)
    WHERE refusal_kind IS NULL;

-- Refusals are deduplicated by REASON, not suppressed. "The gateway was unreachable for the
-- 2026-08-03 close" is one fact however many retries observed it; a different reason on the same
-- date is a different fact and gets its own row.
CREATE UNIQUE INDEX IF NOT EXISTS paper_account_snapshots_one_refusal_per_kind
    ON research.paper_account_snapshots (trading_date, refusal_kind)
    WHERE refusal_kind IS NOT NULL;

COMMENT ON TABLE research.paper_account_snapshots IS
    'RAW, NOT DERIVED: one post-close reading of the paper account (margin, equity, positions) '
    'exactly as TWS reported it, or a named refusal, append-only. Shadow record item 8 depends on '
    'it and cannot be backfilled; no Greeks and no marks are computed here.';

-- Append-only, both tables.
--
-- Its own function rather than research.reject_registry_mutation (migration 015): that message
-- talks about pre-registration, which is not why these are locked. Here the point is that a
-- capture is a measurement, and a measurement that can be edited after the fact is not one.
CREATE OR REPLACE FUNCTION research.reject_paper_capture_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        'research.% is append-only: it records what the broker reported at capture time, and a '
        'reading that can be rewritten afterwards is not evidence. Capture a new row instead.',
        TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER paper_fills_append_only
    BEFORE UPDATE OR DELETE ON research.paper_fills
    FOR EACH ROW EXECUTE FUNCTION research.reject_paper_capture_mutation();

CREATE TRIGGER paper_account_snapshots_append_only
    BEFORE UPDATE OR DELETE ON research.paper_account_snapshots
    FOR EACH ROW EXECUTE FUNCTION research.reject_paper_capture_mutation();
