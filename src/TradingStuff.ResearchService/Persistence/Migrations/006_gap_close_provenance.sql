-- 006: distinguish an OBSERVED gap close from an INFERRED one, and make a dead recorder's gap
-- reconcilable at all.
--
-- Found by running the gateway rather than by reading it. `recorder_gaps` is opened by the process
-- that loses recording and closed by that same process when the first tick returns. Nothing closes
-- a gap whose process never comes back. Kill the gateway mid-lease — a crash, an OOM, a redeploy,
-- or a plain Ctrl-C, all of which happen — and the row keeps `ended_at IS NULL` permanently.
--
-- That would be merely untidy if it did not feed a query. CoverageMonitor treats a gap as
-- overlapping a window when `started_at < window_end AND (ended_at IS NULL OR ended_at > window_start)`,
-- so an orphaned row overlaps EVERY future window, forever. One ungraceful shutdown in July makes
-- every coverage report for the rest of the platform's life carry a permanent unexplained gap —
-- and coverage is what gates a recorded day into a study (docs/plans/ibkr-edge-research-roadmap.md,
-- Phase 1 acceptance: "all gaps explained"). A gate that is permanently red is a gate nobody reads.
--
-- Reconciling at startup is the fix, but it must not be allowed to launder the record. The interval
-- was genuinely unrecorded, so `ended_at` is real; what is NOT real is the implication that some
-- process watched recording resume at that instant. Those are different facts about data quality
-- and a study-time filter needs to tell them apart, so the provenance of the close is stored rather
-- than inferred from the shape of the row.

ALTER TABLE gateway.recorder_gaps
    -- How this gap's end was established:
    --   'observed'  — the recorder saw recording resume for this scope and closed its own gap.
    --   'inferred'  — a later process found the row open, knew it could not still be ongoing (it
    --                 holds no such subscription), and bounded it at its own startup. The true
    --                 resume instant is unknown and is NOT this value; treat `ended_at` as an
    --                 upper bound on when the outage ended.
    -- NULL means the gap is still open. Backfilled below for the rows that already exist.
    ADD COLUMN closed_by text
        CHECK (closed_by IS NULL OR closed_by IN ('observed', 'inferred'));

-- Existing rows predate the column, so they must be made to satisfy the invariant BEFORE it is
-- declared — ADD CONSTRAINT validates the existing table immediately and an already-closed row with
-- a NULL closed_by fails it. (Learned the direct way: the first draft ordered these the other way
-- round and the migration aborted against a database with one closed gap in it.)
-- Every close written before this migration came from CloseGapAsync, which only runs when the
-- recorder observed the scope recover.
UPDATE gateway.recorder_gaps SET closed_by = 'observed' WHERE ended_at IS NOT NULL;

-- The invariant, held by the engine rather than by whichever code path writes the row: a closed gap
-- always says how it was closed, and an open gap never claims to have been closed. Without this,
-- 'inferred' is a convention that the next writer of an UPDATE ... SET ended_at forgets.
ALTER TABLE gateway.recorder_gaps
    ADD CONSTRAINT recorder_gaps_close_provenance
        CHECK ((ended_at IS NULL) = (closed_by IS NULL));

-- Deliberately NOT closing the rows that are open right now. A gap left open by a process that is
-- gone is indistinguishable at this instant from one held open by a process that is running, and
-- guessing here would corrupt the live one. Startup reconciliation owns that decision, because only
-- the starting process knows that it, specifically, holds no subscriptions yet.
