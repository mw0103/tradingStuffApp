# Next-session handoff — written 2026-08-02

Read order: `CLAUDE.md` ("What this is for") → `docs/research/a4-data-readiness.md` →
`docs/plans/paper-run-protocol.md` → `docs/research/hedged-carry-menu.md`. Branch:
`claude/ibkr-research-platform-plan-0b4f74` (pushed through `63cf66f`). Live research DB:
Aspire container `postgres-934a0e61` (127.0.0.1:36279, db `trading`). ThetaData terminal:
127.0.0.1:25503, v3.

## The work, in order

1. **Freeze the A4 slope construction** — its own document, BEFORE computing any series value.
   Must fix: maturity bracketing rule for the 9d and 30d points, interpolation space (total
   variance), strike-range rule from per-date two-sided quote presence, snapshot time,
   session-calendar keying (`research.bars` sessions, never quote presence — see readiness doc
   caveat 1), underlying from own bars. The gate result and its three caveats bind this freeze.
2. **Seed chain ingestion** (2012→, SPXW + SPX) via `POST /research/options/jobs` once the app
   host is up. Ingestion requests need generous timeouts + retries + an explicit fetch-failed
   state distinct from absence (the audit's own error-mode lesson). Serves #11 and #12.
3. **Build the two constant-maturity series + slope**, prior-close only, per the frozen doc.
4. **A4 backtest under registered-trial discipline** — hypothesis already stated on task #11;
   judged at the deflated threshold; placebo first; holdout untouched.
5. In parallel, keep **#10 Phase 1** alive: one idempotent `POST /research/shadow-marks/run`
   per trading day after bars land. First mark (2026-07-31) is persisted.
6. Later: **#12** hedged-carry structure backtests on the ingested chains (menu frozen; sizing-
   down is the dominance baseline).

## Standing constraints (do not relearn these)

- The claim-verdict table in `decision-layer-2026-08-02-first-run.md` §7 governs what may be
  asserted. QCJ scale-down is closed; the paper run is constant one vega with QCJ in shadow.
- Rule/threshold tuning on the 2010–2023 sample is closed. The reserved holdout
  (2024-01-01..2026-07-31) is spent once, at the end, or not at all.
- Frozen docs (`confirmatory-scale-down-protocol.md`, `hedged-carry-menu.md`) do not get edited
  to fit results.
- Paper ports only (7497/4002); live trading paths untouched.

## Environment notes

- The ResearchService instance that produced the first shadow mark was ad-hoc
  (`dotnet run` against the worktree, port 5710); it does not survive reboot. Durable home is
  the Aspire app host.
- Postgres test isolation and the parallelism flake notes are in the repo docs; full suite
  green at 1701 as of `63cf66f`.
