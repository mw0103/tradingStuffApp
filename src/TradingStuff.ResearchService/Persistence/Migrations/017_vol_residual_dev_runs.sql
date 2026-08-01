-- 017: a minimal, unmistakably-dev artifact table for the volatility-forecast-residual study's
-- development runner (POST/GET /research/studies/vol-residual/*).
--
-- This is NOT the trial registry (migration 015). research.registered_trials / trial_outcomes exist
-- to make a specific claim defensible — "every executed variant was declared before its result was
-- seen" — and that claim is only meaningful for registered, holdout-respecting, gate-graded runs.
-- A development run against whatever fraction of research.bars happens to have landed so far is
-- none of those things: it consumes no variant slot, proves no gate, and is expected to be re-run
-- constantly while the backfill drains. Writing it into the registry would let a dev run silently
-- count against the pre-registration's hard cap of 10 variants, or worse, let one be mistaken for a
-- registered result later. Hence a separate, plainly-named table with no relationship to the
-- registry at all.
--
-- "Persist minimally" per the task that added this: one row per run, the full response body as
-- jsonb (the shape POST/GET already return), and nothing normalized out of it. There is exactly one
-- reader (GET .../latest, by generated_at DESC) and no query that needs the payload's internals in
-- SQL, so a results schema is not worth designing yet — see the study runner for where that
-- decision is made, not here.

CREATE TABLE research.dev_vol_residual_runs (
    run_id          uuid PRIMARY KEY,
    generated_at    timestamptz NOT NULL,
    status          text NOT NULL CHECK (status IN ('ok', 'insufficient-data')),
    payload         jsonb NOT NULL
);

-- The only query this table serves: "the most recent run".
CREATE INDEX dev_vol_residual_runs_generated_at_idx ON research.dev_vol_residual_runs (generated_at DESC);
