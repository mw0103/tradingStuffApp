-- 020: the development-run artifact table for the companion VRP-conditioning study
-- (POST/GET /research/studies/vrp-conditioning/*).
--
-- Same reasoning as migration 017, and it applies more strongly here. research.registered_trials
-- exists to make one claim defensible — "every executed variant was declared before its result was
-- seen". This companion study makes NO registered claim at all: the pre-registration
-- (docs/research/volatility-forecast-residual-study.md, "Companion study: VrpConditioningStudy")
-- permits it "bootstrap CIs only, no significance claims" and describes its output as "conditioning
-- knowledge for the variance-gap study, not P&L". A run that cannot support a claim must not occupy
-- a slot in the ledger of claims, or the pre-registration's hard cap of 10 registered variants gets
-- consumed by runs that could never have used it. VrpConditioningRunResponse.Registrable is false by
-- construction for the same reason.
--
-- One row per run, the full response body as jsonb, nothing normalized out of it. There is exactly
-- one reader (GET .../latest, by generated_at DESC). Normalizing the quintile table into columns
-- would be speculative design for queries nothing makes yet — and the payload's shape is expected to
-- churn while the study is being read.

CREATE TABLE research.dev_vrp_conditioning_runs (
    run_id          uuid PRIMARY KEY,
    generated_at    timestamptz NOT NULL,
    status          text NOT NULL CHECK (status IN ('ok', 'insufficient-data')),
    payload         jsonb NOT NULL
);

-- The only query this table serves: "the most recent run".
CREATE INDEX dev_vrp_conditioning_runs_generated_at_idx ON research.dev_vrp_conditioning_runs (generated_at DESC);
