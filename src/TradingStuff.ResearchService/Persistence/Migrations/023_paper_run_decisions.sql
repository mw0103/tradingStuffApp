-- The registered decision that the PAPER run may proceed on dev-provenance infrastructure
-- (docs/plans/paper-run-protocol.md § Phases 2).
--
-- The protocol says entry "requires a registered decision that the paper run may proceed on
-- dev-provenance infrastructure (the signal's provenance refusal is amended by that decision for
-- PAPER only, never live)". A row here is that decision. It is the KEY; ConstantExposureSignal is
-- the lock, and nothing on the AUTOMATED path opens a position while this table is empty. The
-- manual trigger (POST /research/automation/manual-order) deliberately bypasses the signal and is
-- recorded as trigger=manual, signal_state=not-evaluated: this table gates the loop, not the
-- operator.
--
-- The row is a signed statement by a human, so its columns are the parts of a signature that make
-- it attributable later: WHO signed it, WHAT they signed, WHICH document they signed against, and
-- WHEN. A decision nobody can attribute is worse than no decision (docs/LESSONS.md §8), which is
-- why signed_by and statement are NOT NULL and the endpoint rejects blank ones rather than
-- recording an anonymous authorization.
--
-- Append-only in practice: withdrawal is revoked_at on the existing row, never a DELETE. The
-- decision WAS made, and a table that can forget it cannot answer "what authorized the orders
-- placed last Tuesday?".

CREATE TABLE IF NOT EXISTS research.paper_run_decisions (
    decision_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    decided_at      timestamptz NOT NULL DEFAULT now(),

    -- The "never live" clause in schema form. The protocol amends the provenance refusal for PAPER
    -- only, so the single legal value is enforced here rather than trusted to callers: a future
    -- endpoint that tried to register a live-scoped decision fails at the database, not at review.
    scope           text NOT NULL CHECK (scope = 'paper'),

    -- Which document the signer read. Recorded, not derived: the protocol can be superseded, and a
    -- decision has to name the version of the rules it was made under.
    protocol_ref    text NOT NULL,
    statement       text NOT NULL CHECK (length(btrim(statement)) > 0),
    signed_by       text NOT NULL CHECK (length(btrim(signed_by)) > 0),

    -- NULL means active. Revocation flips the signal back to a refusal on its next evaluation
    -- without deleting the history of what was once authorized.
    revoked_at      timestamptz,
    revoked_reason  text,

    schema_version  integer NOT NULL DEFAULT 1
);

COMMENT ON TABLE research.paper_run_decisions IS
    'Operator-signed authorization that the PAPER run may proceed on dev-provenance infrastructure '
    '(docs/plans/paper-run-protocol.md). Scope is CHECK-constrained to paper: this never authorizes live.';

-- At most one active decision, enforced by the database rather than by a read-then-insert in the
-- store. The endpoint's own check and this index answer different questions: the check produces the
-- readable refusal, the index makes "two concurrent POSTs both saw no active decision" impossible.
-- A lock whose key can be cut twice is not a lock.
CREATE UNIQUE INDEX IF NOT EXISTS paper_run_decisions_one_active_idx
    ON research.paper_run_decisions (scope)
    WHERE revoked_at IS NULL;
