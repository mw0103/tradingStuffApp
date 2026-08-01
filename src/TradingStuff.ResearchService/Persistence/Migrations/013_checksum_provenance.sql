-- 013: say whether a recorded migration checksum was VERIFIED or merely ASSUMED.
--
-- Migration 010 added research.schema_migrations.checksum so that a migration file edited after it
-- was applied fails startup instead of being silently trusted. Its header frames the problem as two
-- environments that "both report 'applied'" while their schemas differ, and says "Nothing before
-- this migration could tell the two apart."
--
-- The backfill 010 relies on does not tell them apart either. MigrationRunner.ApplyAsync writes the
-- checksum of whatever the assembly embeds TODAY into every row recorded before the column existed,
-- and then treats that value as the verified baseline. It is not the checksum of what ran. Take the
-- case 010's own header enumerates: environment A applied 003_recorder.sql, the file was later
-- hand-patched, and the checksum feature then shipped. A's first startup records checksum(current
-- 003) and logged, at Information, that "this becomes the baseline future runs are checked against."
-- A fresh environment B records the identical value. The ledgers are byte-identical, both report
-- applied, and the schemas differ — exactly the state 010 says nothing could distinguish, now
-- reached THROUGH the mechanism meant to prevent it. Detection begins only for edits made after the
-- upgrade, which excludes every database that existed when the feature shipped.
--
-- A checksum cannot be recovered for a migration that ran before checksums existed. That is
-- inherent, and no column added here changes it. What CAN be fixed is the claim: a backfilled
-- baseline is an assumption, it must be recorded as one, and it must not read as evidence anywhere
-- it is surfaced. That is what this column is for — the same provenance shape migration 006 gave
-- recorder_gaps.closed_by ('observed' vs 'inferred'), and for the same reason: the row is real, the
-- implication that somebody watched it is not, and a consumer needs to tell those apart.
--
-- ADD COLUMN IF NOT EXISTS, not a bare ADD COLUMN, and for the same reason 010 needed it: the
-- runner's bootstrap defines this column directly (CREATE TABLE for a new database, ALTER ... IF NOT
-- EXISTS for an existing one) before ANY migration in the pass runs, because the backfill that
-- writes it happens before this file could possibly have executed. On every database this ALTER is
-- therefore a no-op; it exists so the ledger carries an explicit, dated entry for when and why
-- provenance was introduced, rather than the column silently predating the migration that added it.
ALTER TABLE research.schema_migrations
    -- Where this row's checksum came from, and therefore what it is evidence of:
    --   'verified' — computed by the runner from the SQL it was about to execute, written in the
    --                same transaction as that DDL. The bytes on disk ARE the bytes that ran.
    --   'assumed'  — backfilled onto a row that predates the checksum column, from whatever the
    --                assembly embedded at upgrade time. An assumption about what ran.
    --   'unknown'  — the row predates THIS column: it carries a 010-era checksum that could equally
    --                have come from a real apply or from 010's backfill, and nothing in the ledger
    --                distinguishes them after the fact. See the UPDATE below.
    -- NULL only for a ledger row with no checksum at all — a migration file since deleted or
    -- renamed, which the backfill deliberately leaves alone because there is nothing to compute a
    -- baseline from.
    ADD COLUMN IF NOT EXISTS checksum_source text;

-- Rows that already exist must satisfy the invariant BEFORE it is declared. Migration 006 learned
-- this the direct way: ADD CONSTRAINT validates the whole table immediately, so declaring first
-- aborts the migration against the very rows it is meant to protect.
--
-- 'unknown' rather than 'verified' or 'assumed', because on a 010-era database this is genuinely
-- undecidable: a fresh install inserted these checksums at apply time (verified) and an upgraded
-- install had them blessed by the backfill (assumed), and the two produce identical rows. The weaker
-- claim is the only honest one, and it is the safe direction — an over-cautious 'unknown' on a clean
-- database costs a startup warning, whereas a generous 'verified' on a diverged one restores exactly
-- the false confidence this migration exists to remove. On a database that has never seen 010 this
-- statement matches nothing: the bootstrap adds both columns together, so the runner's backfill has
-- already written 'assumed' alongside each checksum before this file runs. On a database created
-- after this migration ships it also matches nothing, because every row was inserted 'verified'.
UPDATE research.schema_migrations
   SET checksum_source = 'unknown'
 WHERE checksum IS NOT NULL
   AND checksum_source IS NULL;

ALTER TABLE research.schema_migrations
    ADD CONSTRAINT schema_migrations_checksum_source_domain
        CHECK (checksum_source IS NULL OR checksum_source IN ('verified', 'assumed', 'unknown'));

-- The invariant, held by the engine rather than by whichever code path writes the row: a checksum
-- always says where it came from, and a row with no checksum never claims a provenance for one.
-- Without this, an INSERT that forgets the column silently produces a checksum with no provenance —
-- which every reader would then have to guess about, and the safe guess (unverified) would quietly
-- degrade real baselines while the unsafe guess (verified) reintroduces the defect.
ALTER TABLE research.schema_migrations
    ADD CONSTRAINT schema_migrations_checksum_provenance
        CHECK ((checksum IS NULL) = (checksum_source IS NULL));
