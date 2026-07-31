-- 005: backfill claim ownership + lease expiry, job kind, and per-job slice duration.
--
-- Migration 004 shipped the checkpoint table, but a row could enter 'inflight' and never leave it.
-- `requested_at` records WHEN a slice was sent and nothing records WHO sent it or WHEN that claim
-- stops being believable, so a slice claimed by a coordinator that then crashed stays 'inflight'
-- forever: no query can distinguish it from a request that is genuinely still in the air, and no
-- recovery path can exist. That is a crash state machine the schema does not support — the same
-- split-path-lifetime class that produced most of the Phase 1 review's confirmed defects. This
-- migration gives the crash path the two columns it needs and then makes the invariant that matters
-- ("an inflight row is always reclaimable") a CHECK rather than a convention.

ALTER TABLE research.backfill_requests
    -- Which coordinator instance holds this claim. Deliberately a free-text instance token
    -- (machine:pid:guid) rather than a foreign key: there is no coordinator registry table, and the
    -- value's only job is to (a) stop a coordinator that has lost its lease from writing the
    -- outcome anyway, and (b) name the dead owner in the reclaim audit trail.
    ADD COLUMN claimed_by       text,
    -- When the claim stops being believable. The reaper reclaims 'inflight' rows past this instant.
    -- Set generously past the longest possible request (the gateway's own historical timeout plus
    -- HTTP overhead) so a slow-but-live request is never reclaimed underneath its owner.
    ADD COLUMN lease_expires_at timestamptz;

-- The invariant, enforced by the engine rather than by whichever code path happens to write the
-- row: an 'inflight' row ALWAYS carries an owner and an expiry, so it is always reclaimable. There
-- is no way to write an inflight row that the reaper cannot see, including by hand in psql.
ALTER TABLE research.backfill_requests
    ADD CONSTRAINT backfill_requests_inflight_has_lease
        CHECK (state <> 'inflight' OR (claimed_by IS NOT NULL AND lease_expires_at IS NOT NULL));

-- The reaper's only query: expired leases, and nothing else. Partial on state so it stays tiny
-- (inflight rows are a handful at any instant, against potentially millions of terminal rows).
CREATE INDEX backfill_requests_expiring_lease_idx
    ON research.backfill_requests (lease_expires_at)
    WHERE state = 'inflight';

-- The claim query's access path: candidate rows for a job, newest slice first (backfill walks
-- backward from recent history, and recent data is the more useful half if a long drain is
-- interrupted).
CREATE INDEX backfill_requests_claimable_idx
    ON research.backfill_requests (job_id, state, end_time_utc DESC);

ALTER TABLE research.backfill_jobs
    -- 'historical' walks a fixed [target_from, target_to] backward once; 'topup' re-anchors forward
    -- to the current 15-minute bucket every run and is never "finished". They are different
    -- lifecycles, not one lifecycle with a flag on it, and the planner dispatches on this rather
    -- than on a name prefix.
    ADD COLUMN kind text NOT NULL DEFAULT 'historical'
        CHECK (kind IN ('historical', 'topup')),
    -- Overrides the TWS duration the planner would otherwise derive from bar_size ('1 D' for
    -- minute bars, '1 Y' for daily). Lives on the JOB ROW, not in configuration, deliberately:
    -- slice boundaries must be a pure function of persisted job columns, or an ambient config
    -- change silently re-plans a job into a second, overlapping set of request rows that the
    -- idempotency key cannot collapse. Changing it re-plans the job from scratch by design;
    -- research.bars deduplicates the re-fetched bars, but the request rows are genuinely new.
    ADD COLUMN slice_duration text;

-- ============ the top-up idempotency contradiction, resolved ============
-- Migration 004 designed repeated now-anchored top-up requests to share a constant NULL
-- end_time_utc, which under UNIQUE NULLS NOT DISTINCT means they collide BY DESIGN. Combined with
-- checkpoint semantics where 'succeeded' means "never re-request", that made every top-up run after
-- the first a silent no-op: the insert is swallowed by ON CONFLICT DO NOTHING, the coordinator sees
-- one already-succeeded row, logs a clean pass, and the recent tail stops advancing with nothing
-- anywhere reporting a problem.
--
-- Resolved in the planner, not here: a top-up slice is anchored to a CONCRETE end_time_utc, floored
-- to the 15-minute bucket the run falls in. Each bucket is therefore a genuinely distinct row (the
-- tail advances), while two runs inside the SAME bucket still collapse to zero new rows (the
-- idempotency guarantee survives), and 'succeeded' keeps meaning "never re-request" with no state
-- resets and no mutation of completed checkpoint rows. Nothing the coordinator writes is ever
-- NULL-anchored.
--
-- end_time_utc stays nullable and UNIQUE NULLS NOT DISTINCT stays as migration 004 wrote it: the
-- constraint is still the correct backstop for a NULL that reaches the table some other way, and
-- tightening it to NOT NULL would break nothing this coordinator does while invalidating migration
-- 004's own idempotency test. The coordinator instead refuses to CLAIM a NULL-anchored row (it
-- cannot construct a reproducible request from one) and reports the count of any such rows on
-- /research/backfill, so a hand-inserted one is loud rather than silently stuck.
