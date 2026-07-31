-- 009: stop a backfill job from reporting "complete" when parts of it were never fetched, and stop
-- the bars figure from counting the same bar several times over.
--
-- Two independent ways the Phase 2 backfill surface overstated itself, both found by an adversarial
-- review of the drain rather than by any test:
--
-- (1) A slice that exhausts its attempt budget is settled — it is never claimed again, because
--     `attempts` has no reset path anywhere — and `IsJobSettledAsync` folds that into "nothing
--     outstanding", so the job flipped to `complete` at 100% with holes in it. The exhausted COUNT
--     was reported separately (deliberately, and that stays), but a status of `complete` and a
--     progress bar at 100% are what an operator actually reads, and both said the job was clean.
--     Worse, `complete` dropped the job out of ClaimAsync's `bj.status IN ('pending','running')`
--     filter, so raising Backfill__MaxAttempts — the natural operator response — could not reopen
--     it: the rows became claimable while the job stayed locked out.
--
--     `complete_with_gaps` is a distinct TERMINAL status rather than a flag on `complete` so that
--     the plain string an operator greps for, sorts on, or renders in a status column cannot be
--     mistaken for the clean one. It is claimable, exactly like `running`, so raising the attempt
--     cap is a working way back in; the job returns to `running` on its own as soon as anything is
--     outstanding again, because BackfillStore.RefreshJobStatusAsync derives the status from the
--     checkpoint counts every pass rather than latching it.
--
-- (2) `bars_returned` is what TWS handed back for one request, BEFORE research.bars' primary key
--     deduplicates it. Overlap is designed into this pipeline in three places — the historical
--     planner's leading slice deliberately over-reaches its grid boundary, a top-up window covers
--     four buckets so a missed run self-heals, and the forward extension re-requests the current
--     day every day — so summing `bars_returned` per job does not approximately equal the rows
--     that landed, it exceeds them by design and by an amount nobody can bound by reading the
--     number. `GET /research/backfill` reported that sum as the job's "Bars", which is the same
--     failure mode as (1): a figure that can only ever flatter the job.

ALTER TABLE research.backfill_jobs
    DROP CONSTRAINT backfill_jobs_status_check;

ALTER TABLE research.backfill_jobs
    ADD CONSTRAINT backfill_jobs_status_check
        CHECK (status IN ('pending', 'running', 'paused', 'complete', 'complete_with_gaps', 'failed'));

ALTER TABLE research.backfill_requests
    -- How many rows this request actually INSERTED into research.bars, as opposed to how many bars
    -- TWS returned for it (`bars_returned`). The two differ by every bar that was already there
    -- under research.bars' (con_id, what_to_show, bar_size, use_rth, ts_utc) primary key.
    --
    -- Recorded at land time rather than derived on read: research.bars has no index on request_id
    -- (its primary key leads with con_id), so a per-job `count(*) ... WHERE request_id IN (...)`
    -- would be a sequential scan of a ten-million-row partitioned table on every poll of a page
    -- that auto-refreshes every 30 seconds. The INSERT ... ON CONFLICT DO NOTHING already knows the
    -- answer exactly — it is its own row count — so this costs one integer per request row.
    --
    -- NULL means "landed before this column existed"; the backfill below removes that case for
    -- every row that has bars, so NULL after this migration means a request that landed nothing.
    ADD COLUMN bars_landed integer;

-- One-time reconciliation of the rows that predate the column. A single sequential pass over
-- research.bars, grouped, rather than a correlated subquery per request: this runs once, on a table
-- that at Phase 2 holds at most a partial drain, and getting it wrong in the cheap direction (a
-- succeeded request left at NULL) would make the new figure UNDER-report, which is the opposite of
-- the defect but still a wrong number on the same page.
UPDATE research.backfill_requests r
SET bars_landed = b.landed
FROM (SELECT request_id, count(*) AS landed FROM research.bars WHERE request_id IS NOT NULL GROUP BY request_id) b
WHERE r.request_id = b.request_id;

-- A succeeded request that landed nothing is a real, distinct outcome (every bar it returned was
-- already present), so it is recorded as 0 rather than left indistinguishable from "not measured".
UPDATE research.backfill_requests
SET bars_landed = 0
WHERE bars_landed IS NULL AND state IN ('succeeded', 'empty');
