-- Shadow marks: the paper-run protocol's Phase 1 record (docs/plans/paper-run-protocol.md).
--
-- One row per decision date. The traded path is CONSTANT ONE VEGA; every model quantity here is
-- a shadow calculation logged prospectively and influencing nothing. The table is the prospective
-- test QCJ gets without a portfolio-control role: whether its ranking keeps appearing in
-- genuinely NEW observations, written down before the outcome is knowable.
--
-- Plain table, not append-only-triggered: rows are facts about what was computed at a decision
-- time, keyed by date; recomputing a date (e.g. after a bar backfill) legitimately replaces the
-- row, and generated_at says when. The registry's epistemics do not apply - nothing here is a
-- claim.

CREATE TABLE IF NOT EXISTS research.vol_shadow_marks (
    mark_date          date PRIMARY KEY,
    generated_at       timestamptz NOT NULL,

    -- Training window behind the fits (labels complete on or before train_to).
    train_from         date NOT NULL,
    train_to           date NOT NULL,
    train_rows         integer NOT NULL,

    -- Decision-time inputs (prior/decision close only; the harness asserts nothing later leaks in).
    vix_close          double precision NOT NULL,
    implied_variance   double precision NOT NULL,

    -- Shadow forecasts of 21-day cumulative variance.
    qcj_forecast       double precision NOT NULL,
    harx_forecast      double precision NOT NULL,

    -- Spread = implied - forecast, and the TRAIN-frozen quintile bucket per arm.
    qcj_spread         double precision NOT NULL,
    harx_spread        double precision NOT NULL,
    vix_spread         double precision NOT NULL,
    qcj_bucket         integer NOT NULL,
    harx_bucket        integer NOT NULL,
    vix_bucket         integer NOT NULL,

    -- The traded intention (constant), and the hypothetical allocations - SHADOW ONLY, and the
    -- column names say so, so no later reader mistakes them for what was traded.
    intended_vega              double precision NOT NULL DEFAULT 1.0,
    shadow_alloc_qcj           double precision NOT NULL,
    shadow_alloc_harx          double precision NOT NULL,
    shadow_alloc_vix           double precision NOT NULL,

    -- What the planner would have built at this mark, or its named refusal. JSON: structure,
    -- strikes, expiration, quoted credit, max loss - or {"refusal": "..."}.
    planner_intent     jsonb NOT NULL,

    schema_version     integer NOT NULL DEFAULT 1
);

COMMENT ON TABLE research.vol_shadow_marks IS
    'Paper-run protocol Phase 1: daily decision-time shadow record. Traded path is constant one '
    'vega; every model quantity is shadow-only and influences nothing.';
