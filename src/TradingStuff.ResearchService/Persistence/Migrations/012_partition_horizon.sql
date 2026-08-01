-- 012: create the daily raw-event partitions at MIGRATION time, so a database is never writable
-- without them.
--
-- THE FAILURE THIS EXISTS FOR. Migration 003 creates gateway.option_quote_events /
-- gateway.underlying_tick_events together with their DEFAULT partitions, in one transaction. From
-- the instant it commits, the recorder's COPY succeeds — into DEFAULT, because no daily partition
-- exists yet. PartitionMaintainer was the only thing that created those, it lives in a different
-- process (ResearchService) from the recorder (IbkrGateway), and on a cold start its first sweep ran
-- concurrently with migrations: every statement failed against tables that did not exist yet, the
-- sweep dropped into its 1-minute failure retry, and by the time the second sweep ran, ticks for
-- TODAY were already in DEFAULT. Postgres then permanently refuses to create that date's partition
-- ("updated partition constraint for default partition ... would be violated by some row" — verified
-- directly against Postgres 17 in this project), so the rest of that UTC day — roughly 7M rows —
-- accumulates in DEFAULT with no partition to export or DROP. The sweep looked healthy throughout,
-- because the per-date isolation added in the Phase 1 review means every OTHER date still got its
-- partition.
--
-- WHY THE FIX IS IN THE SCHEMA AND NOT IN A STARTUP ORDER. The obvious repairs are "gate the
-- maintainer on migrations" or "gate recording on the maintainer's first sweep". Both are rules
-- about two processes starting together, and the two processes here deliberately do NOT share a
-- lifetime: docs/STATE.md is explicit that ResearchService is expected to redeploy and restart
-- underneath a gateway that keeps recording (that is the stated reason the recorder lives in the
-- gateway at all, and Phase 2's immortal-gap incident is what happens when that is forgotten). A
-- start-order rule is therefore only true in the case that was never the problem. Stated as a schema
-- invariant instead — "after migrations, partitions exist for the next two weeks" — it holds for
-- every start order, for a ResearchService that is switched off, and for a gateway that outlives
-- several of them. PartitionMaintainer's job narrows to EXTENDING that horizon (it now also waits
-- for the schema to exist instead of thrashing against it) and to reporting anything already
-- stranded.
--
-- HORIZON. 14 days, matching PartitionMaintainer.DaysAhead — keep the two in step. The horizon's
-- real job is to survive a ResearchService outage while the gateway records; the previous 3 days
-- made "nobody looked at this over a long weekend" sufficient to strand a day permanently.
--
-- BOUNDS. Bare date literals, character for character what PartitionMaintainer emits, so the
-- partitions this file creates and the ones it creates tomorrow are exactly adjacent. That form
-- resolves against the SESSION time zone, unlike migration 004's explicit AT TIME ZONE 'UTC' — a
-- real fragility, deliberately not "corrected" here, because changing the bound form would make the
-- new partition overlap any partition an earlier build already created on a non-UTC server, and on
-- these tables a failed CREATE is precisely how a date gets stranded. PartitionMaintainer detects
-- and reports a non-UTC session instead, once per process.
--
-- PER-DATE ISOLATION. Each CREATE is wrapped in its own exception block, for the same reason the
-- Phase 1 review added a per-date try/catch to the C# sweep: on an existing database, today may
-- ALREADY be stranded, and one un-creatable date must not abort the migration and leave the service
-- unable to start. A skipped date raises a WARNING and is then reported, by date and with its
-- remedy, by PartitionMaintainer's DEFAULT-partition review on every startup.

DO $$
DECLARE
    target   record;
    for_date date;
    horizon  constant integer := 14;  -- keep in step with PartitionMaintainer.DaysAhead
BEGIN
    FOR target IN
        SELECT *
        FROM (VALUES
            ('gateway', 'option_quote_events'),
            ('gateway', 'underlying_tick_events')
        ) AS t(schema_name, table_name)
    LOOP
        FOR for_date IN
            SELECT generate_series(
                (now() AT TIME ZONE 'UTC')::date,
                (now() AT TIME ZONE 'UTC')::date + horizon,
                interval '1 day')::date
        LOOP
            BEGIN
                EXECUTE format(
                    'CREATE TABLE IF NOT EXISTS %1$I.%2$I PARTITION OF %1$I.%3$I '
                    'FOR VALUES FROM (%4$L) TO (%5$L)',
                    target.schema_name,
                    target.table_name || '_' || to_char(for_date, 'YYYYMMDD'),
                    target.table_name,
                    for_date,
                    for_date + 1
                );
            EXCEPTION WHEN others THEN
                RAISE WARNING
                    'Could not create the % partition for % (%). Continuing; PartitionMaintainer '
                    'reports this date and its remedy on every sweep.',
                    target.table_name, for_date, SQLERRM;
            END;
        END LOOP;
    END LOOP;
END $$;
