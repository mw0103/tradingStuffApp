-- 011: record WHY a node is bound to the conId it is bound to.
--
-- node_assignments held (node_id, con_id) and nothing else, so nothing anywhere compared the strike
-- a node was assigned against the strike it was selected FOR. That is precisely how the bootstrap
-- grid could collapse nine roles per DTE bucket onto four contracts and still look perfect: every
-- role pointed at a live, well-recorded contract, coverage read ~100% for all 54 nodes, and
-- /research/nodes reported only (node_id, con_id). The selection is now recorded alongside the
-- assignment so the mismatch is a column a human can read, not an inference from 54 conIds.
--
-- Deliberately nullable, and deliberately NOT backfilled: rows written before this migration were
-- produced by selector_version 1, which never computed a reference price worth recording. A
-- fabricated provenance value would be worse than an absent one — this table is the role -> conId
-- ground truth Phase 4's study identity reads, and "we do not know" must stay distinguishable from
-- a number.

ALTER TABLE research.node_assignments
    ADD COLUMN expiration      date,     -- the listed expiration this conId belongs to
    ADD COLUMN strike          numeric,  -- the strike actually assigned
    ADD COLUMN target_strike   numeric,  -- reference_price * (1 + option_nodes.strike_target)
    ADD COLUMN reference_price numeric;  -- the underlying spot the target was computed from

COMMENT ON COLUMN research.node_assignments.target_strike IS
    'The strike this node was selected FOR. (strike - target_strike) / reference_price is the deviation; NodeSelector refuses a pick beyond its tolerance rather than binding to it.';

-- ---------------------------------------------------------------------------------------------
-- NOT added here, on purpose: a partial UNIQUE INDEX on (con_id) WHERE assigned_to IS NULL.
--
-- "One conId must not play two node roles at once" IS a true invariant — a conId is one (expiration,
-- strike, right), so two roles resolving to it means one of them is a lie, and it is how a 54-node
-- grid can quietly become ~24 contracts. It is enforced in NodeSelector, before any row is written,
-- and NOT in the schema, for two concrete reasons:
--
--   1. A legitimate handoff is transiently a duplicate. When a roll moves node A off conId X and
--      node B onto it in the same pass, the pass is correct only if A's row closes before B's
--      opens; a database constraint would turn an ordering detail into a unique violation.
--   2. That violation would be indistinguishable from the one the existing partial unique index on
--      (node_id) raises. NodeSelector.UpsertAssignmentAsync catches SQLSTATE 23505 and retries once,
--      on the documented assumption that it means the node_id race from migration 003. A con_id
--      constraint would route a permanent, unretryable condition into that retry, which would fail
--      identically the second time and take down the whole bootstrap pass -- trading a silently
--      duplicated node for a loudly dead selector.
--
-- The selector refuses the duplicate instead: the losing node is left UNASSIGNED with
-- 'duplicate-con-id' and shows up as such on /research/nodes, which is the visible-and-recoverable
-- outcome the constraint was wanted for in the first place.
