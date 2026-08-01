-- Paper automation decision log.
--
-- Every evaluation writes a row, including — especially — the ones where nothing was traded. This
-- table exists BECAUSE of docs/LESSONS.md §3: a table that only records submissions is empty on a
-- day the automation was refusing to arm, was killed, or saw no signal, and an empty table renders
-- as health. The question "what did automation do today?" must be answerable with a row count, and
-- "nothing, because X" must be a row rather than an absence.
--
-- Nothing here is a source of truth about an ORDER. The order record lives in ExecutionService and
-- the broker order at TWS; order_id/correlation_id are the join keys back to those, and
-- lifecycle_status is what ExecutionService reported at the moment of submission and is never
-- updated afterwards (a later fill or cancel does not rewrite history here). See the column comment.

CREATE TABLE IF NOT EXISTS research.paper_automation_decisions (
    decision_id          bigserial PRIMARY KEY,
    decided_at           timestamptz NOT NULL,

    -- 'scheduled' = the automated loop. 'manual' = the operator-triggered single-shot endpoint.
    -- These must never be confusable: a manual order carries operator-supplied inputs the automated
    -- path cannot produce, and reading one as the other would credit automation with a decision a
    -- human made. Constrained rather than documented.
    trigger              text NOT NULL CHECK (trigger IN ('scheduled', 'manual')),

    armed                boolean NOT NULL,
    arm_state            text NOT NULL,
    arm_reason           text NOT NULL,

    -- What ISessionClock said, not a wall clock. NULL calendar/label means "outside any session on
    -- the configured calendar", which is a decision input, not missing data.
    session_calendar     text,
    session_label        text,
    session_trading_date date,
    in_session           boolean NOT NULL,

    signal_state         text NOT NULL,
    signal_reason        text NOT NULL,
    study_run_id         uuid,

    action               text NOT NULL,
    action_reason        text NOT NULL,

    -- "An order id was established for this decision", NOT "an order exists at the venue". Those come
    -- apart in exactly one case and it matters: when ExecutionService is handed an order and no
    -- outcome comes back, the order may well be live and there is no id here to name it by. That case
    -- is action = 'outcome-unknown' with order_submitted false, and the per-session cap counts BOTH —
    -- see PaperAutomationStore.CountSubmittedOnAsync. A cap that ignored the ambiguous case would let
    -- a gateway timeout buy an extra order.
    order_submitted      boolean NOT NULL,
    order_id             uuid,
    correlation_id       uuid,

    -- ExecutionService's status AT SUBMISSION. Deliberately a point-in-time snapshot: this row is a
    -- record of a decision, not a mirror of an order's lifecycle.
    lifecycle_status     text,

    limit_price          numeric(18, 4),
    -- 'computed-marketable' (derived from live quotes) or 'operator-supplied' (the manual endpoint).
    -- A price nobody can attribute is worse than no price; see docs/LESSONS.md §8.
    limit_price_source   text,

    orders_this_session  integer NOT NULL,
    order_cap            integer NOT NULL,

    detail               jsonb,

    -- An order id can only belong to a row that claims one was submitted, and vice versa. Without
    -- this a bookkeeping slip produces a row that reads as "automation traded" with nothing to
    -- reconcile against, or a submitted order with no id to find it by.
    CONSTRAINT paper_automation_order_id_matches_submitted
        CHECK ((order_submitted AND order_id IS NOT NULL) OR (NOT order_submitted AND order_id IS NULL))
);

CREATE INDEX IF NOT EXISTS paper_automation_decisions_decided_at_idx
    ON research.paper_automation_decisions (decided_at DESC);

CREATE INDEX IF NOT EXISTS paper_automation_decisions_submitted_idx
    ON research.paper_automation_decisions (decided_at DESC)
    WHERE order_submitted;
