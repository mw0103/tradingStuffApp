# Plan D — Operations: durable home, schedulers, runbook

## Goal

Everything currently runs as ad-hoc background processes that die on reboot, plus manual
daily triggers. Phase 2 cannot run on that. This plan makes the stack come up with one
command, keeps the daily cadence without a human, and writes down how to operate it. It goes
LAST (merge order A → B → C → D) because it wires up whatever the other plans produce.

## Current state (verified 2026-08-03, so you do not have to rediscover it)

- Durable home is `aspire start` (AppHost brings up Postgres/RabbitMQ/Keycloak containers);
  research DB container `postgres-934a0e61` (127.0.0.1:36279, db `trading`).
- Ad-hoc processes in flight that this plan retires: IbkrGateway on 5100 (client id 12,
  paper TWS 7497), ResearchService instances — 5710 (shadow marks), 5711/5712/5713 (chain
  ingestion drainers, env `OptionChains__Enabled=true OptionChains__LeaseSeconds=3600
  ThetaData__Timeout=00:30:00 ThetaData__SnapshotTimeOfDay=15:30:00`), 5714 (backfill topups,
  `Backfill__Enabled=true`). Stale leftovers from 2026-08-02 on random ports (34723/34733,
  wrong DB) can be killed on sight.
- TWS restarts itself daily around 01:00 local; the gateway must reconnect without help —
  verify the existing reconnect behaviour and file what you find (do not rewrite it).
- Daily-close data path quirk: backfill slices containing "now" are never claimed (by
  design), so same-evening daily closes come from the 1-day-cadence job
  `vix-daily-trades-2026h2` (claimable after 00:00 UTC) until the live recorder is proven to
  cover sessions. Schedule accordingly.

## Deliverables

1. **AppHost wiring**: ResearchService, ExecutionService, MarketDataService, IbkrGateway in
   the Aspire AppHost with the configuration the ad-hoc processes carry today (connection
   string, OptionChains/Backfill/ThetaData env). Chain-ingestion drainer count as a
   replica/config decision documented in the runbook. `aspire start --non-interactive` from
   cold boot ⇒ everything above healthy.
2. **Schedulers** (in-process timers on session-clock time, consistent with house style —
   no OS cron):
   - Shadow mark: daily at ~00:05 UTC while the backfill path supplies the close (see quirk
     above); move to ~16:20 ET once the recorder demonstrably lands same-day closes.
   - Paper capture (Plan C's service) after session close.
   - Both idempotent, both record refusals on missing inputs.
3. **Runbook** `docs/plans/paper-test/runbook.md`: cold start, health checks (the exact
   /research/* URLs), what runs where, how to pause ingestion (jobs are paused by operator
   decision — status column), what a TWS restart looks like in the logs, and the "who to
   believe" table for data gaps (recorder vs backfill vs vendor).
4. **Retirement**: with the AppHost proven, stop the ad-hoc processes and note their
   replacement in the runbook. Do not kill anything before its replacement is verified.

## Constraints and non-goals

- No application-logic changes. If a service cannot come up under Aspire without a code
  change, that finding goes back as a review comment to the owning plan, not a workaround.
- Do not touch live-trading configuration; paper ports only, DU-only checks stay.
- The reserved-holdout and research-track services (term structure, ingestion) keep working
  exactly as before under the new home.

## Done means

Cold boot → `aspire start --non-interactive` → within a few minutes: gateway connected to
paper TWS, drainers draining, shadow-mark endpoint live, schedulers armed; runbook accurate
against reality. Ad-hoc processes retired.
