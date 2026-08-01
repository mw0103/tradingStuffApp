-- 015: the trial registry the volatility-forecast-residual study pre-registration requires.
--
-- The registration (docs/research/volatility-forecast-residual-study.md) says: "every executed
-- variant (feature-set hash, model family, hyperparameters, fold config, seed, git sha) appended
-- immutably before results are viewed. Gate p-threshold deflated to 0.05/N over N registered
-- variants ... Hard cap: 10 registered variants before the holdout opens. Exhausting the cap =
-- negative result."
--
-- None of that is enforceable by convention. The whole construction exists to stop a specific,
-- entirely ordinary sequence of events: run a variant, see a disappointing number, adjust
-- something, run again, and report the version that worked as though it had been the plan. Nobody
-- has to intend it. The defence is that N is counted from rows written BEFORE their own results
-- existed, so a registry that can be edited afterwards defends nothing at all.
--
-- Hence two tables rather than one, and hence append-only.
--
-- research.registered_trials holds the declaration and is written before the run. It cannot carry
-- the outcome, because a row that is updated with its own result is a row that can be updated for
-- other reasons too, and no reader could tell which happened. research.trial_outcomes holds what
-- came back, references the declaration, and is likewise append-only. Registered-but-no-outcome is
-- a legitimate, visible state: a variant that was declared and then abandoned still counts against
-- N, which is exactly the arithmetic that makes the cap bite.
--
-- Immutability is enforced by triggers rather than by revoking UPDATE/DELETE, because the
-- application connects as the owner and grants would be trivially reversible by the same code path
-- that would do the tampering. A trigger raises where the mistake happens, names the table, and
-- appears in the logs; a permission error at best fails obscurely and at worst is fixed by granting
-- the permission. Neither stops a determined superuser, and neither is meant to. The point is that
-- amending the registry cannot happen by accident, in passing, as part of some other change.
--
-- The cap and the p-threshold deflation are deliberately NOT enforced here. Both are properties of
-- a study's state at a moment ("has the holdout opened yet"), and encoding a workflow in a CHECK
-- constraint produces a schema that has to be migrated every time the protocol is clarified.
-- TrialRegistry computes and enforces them, and its tests are where they are pinned. What the
-- database guarantees is narrower and more useful: the count is honest.

CREATE TABLE research.registered_trials (
    trial_id          bigserial PRIMARY KEY,

    -- Which pre-registration this variant is executed under. Free text rather than an enum: the
    -- registration names a companion study (VrpConditioningStudy) that shares this pipeline, and
    -- successor studies of the same hypothesis family will want their own independent counts.
    study             text        NOT NULL,

    -- Position within the study's cap. Assigned by the writer under a serializable transaction,
    -- not by the sequence: trial_id is global across studies and would make "the 10 for this
    -- study" a derived question at exactly the moment it needs to be a settled one.
    variant_ordinal   integer     NOT NULL,

    registered_at     timestamptz NOT NULL DEFAULT now(),

    -- The five things the registration enumerates. Stored separately rather than as one blob so a
    -- reader can see at a glance whether two variants differ in their features or only their
    -- seed — the difference between an ablation and a re-roll.
    feature_set_hash  text        NOT NULL,
    model_family      text        NOT NULL,
    hyperparameters   jsonb       NOT NULL,
    fold_config       jsonb       NOT NULL,
    seed              bigint      NOT NULL,

    -- The commit the run was executed from. Without it the other five fields describe a
    -- configuration but not the code that interpreted it, and a change in the estimator is
    -- indistinguishable from a change in the variant.
    git_sha           text        NOT NULL,

    -- What the variant is for, in a sentence. Read by a human reconstructing why ten of these
    -- exist; the machine never uses it.
    rationale         text        NOT NULL,

    CONSTRAINT registered_trials_study_ordinal_unique UNIQUE (study, variant_ordinal),
    CONSTRAINT registered_trials_ordinal_positive CHECK (variant_ordinal >= 1),
    CONSTRAINT registered_trials_study_not_blank CHECK (length(btrim(study)) > 0),
    CONSTRAINT registered_trials_git_sha_not_blank CHECK (length(btrim(git_sha)) > 0)
);

CREATE INDEX registered_trials_study_idx ON research.registered_trials (study, variant_ordinal);

CREATE TABLE research.trial_outcomes (
    outcome_id        bigserial   PRIMARY KEY,
    trial_id          bigint      NOT NULL REFERENCES research.registered_trials (trial_id),
    recorded_at       timestamptz NOT NULL DEFAULT now(),

    -- QLIKE is the registration's primary and only gated loss. MSE of log RV is recorded because
    -- the registration says to report it, and named so it cannot be mistaken for a gate.
    pooled_qlike              double precision NOT NULL,
    pooled_qlike_gain         double precision NOT NULL,
    reported_log_mse          double precision NOT NULL,

    diebold_mariano_statistic double precision NOT NULL,
    diebold_mariano_p_value   double precision NOT NULL,

    -- The deflated threshold this outcome was judged against, stored rather than recomputed. N
    -- grows as later variants register, so a threshold recomputed at read time would silently
    -- restate what an earlier decision had been made against.
    p_threshold_applied       double precision NOT NULL,

    folds_improved            integer          NOT NULL,
    folds_total               integer          NOT NULL,
    largest_year_share        double precision NOT NULL,

    -- The registration's falsification rules produce a verdict, not a number. Recorded as text so
    -- a later reader sees the call that was made, not just the inputs to it.
    verdict                   text             NOT NULL,

    CONSTRAINT trial_outcomes_one_per_trial UNIQUE (trial_id),
    CONSTRAINT trial_outcomes_folds_sane CHECK (folds_improved >= 0 AND folds_improved <= folds_total),
    CONSTRAINT trial_outcomes_p_in_range CHECK (diebold_mariano_p_value >= 0 AND diebold_mariano_p_value <= 1)
);

-- Append-only, both tables.
--
-- RETURNS trigger with a RAISE is the whole implementation: the exception aborts the statement, so
-- the UPDATE or DELETE never happens. The message names the table and says why, because the person
-- who hits this will be trying to do something reasonable and needs to know it is deliberate.
CREATE OR REPLACE FUNCTION research.reject_registry_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        'research.% is append-only: a trial registry that can be rewritten after results are seen '
        'provides no evidence about what was planned. Register a new variant instead.',
        TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER registered_trials_append_only
    BEFORE UPDATE OR DELETE ON research.registered_trials
    FOR EACH ROW EXECUTE FUNCTION research.reject_registry_mutation();

CREATE TRIGGER trial_outcomes_append_only
    BEFORE UPDATE OR DELETE ON research.trial_outcomes
    FOR EACH ROW EXECUTE FUNCTION research.reject_registry_mutation();
