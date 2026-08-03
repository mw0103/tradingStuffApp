# Paper-test work plans — parallel execution guide

Written 2026-08-03. These four plans carry task #10 from Phase 1 (shadow marks, running) to
Phase 2 (paper orders) of `docs/plans/paper-run-protocol.md`. Each plan is self-contained and
can be handed to a separate agent. Read this file first; it defines the seams that keep the
plans from colliding.

## Read order for every agent, before its own plan

1. `CLAUDE.md` — build/test commands, the "What this is for" framing, test-suite rules.
2. `docs/plans/paper-run-protocol.md` — the frozen protocol this work implements. It is not
   edited to fit the implementation.
3. Your plan file. Nothing in a plan overrides the protocol or the standing constraints.

## Standing constraints (bind every plan)

- **Paper ports only** (7497/4002). No live trading paths. The DU-only check and the existing
  arming chain are load-bearing; extend behind them, never around them.
- **Constant one vega. QCJ/HAR-X/VIX forecasts influence NOTHING.** Shadow only.
- No Sharpe-chasing features, no adaptive sizing, no tuning. v1 is deliberately dumb.
- Absence renders as absence: refusals and gaps are recorded with reasons, never papered over.
- `dotnet test -m:1` green before any commit; new Postgres-touching code gets a
  `Category=RequiresPostgres` test (see `TermStructureSeriesBuilderPostgresTests` for the
  house pattern: fresh GUID database + `MigrationRunner.ApplyOnceAsync`).
- Two PRE-EXISTING failures in the RequiresPostgres suite (`AScoreableRunProducesFourArms…`,
  `A_topup_jobs_default_window…`) are date-sensitive and unrelated; do not "fix" them in
  passing and do not let them block a merge.

## The plans and their seams

| Plan | Owns | Must NOT touch |
|------|------|----------------|
| A — entry gate | `Automation/` signal classes, the paper-decision gate, migration 023 | `PaperAutomationService` loop body, capture tables |
| B — lifecycle | `PaperAutomationService` evaluation/exit branches, planner exit orders | signal/gate classes, capture tables |
| C — capture | NEW capture store/tables (migration 024), a new hosted snapshot service, gateway account read endpoint | `PaperAutomationService` loop body, signal/gate |
| D — operations | Aspire AppHost wiring, schedulers, runbook | all application logic |

Migration numbers: A takes **023**, C takes **024**. If either lands first with the other
number free, renumber before merge — ordinal file order is the applied order.

**Merge order: A → B → C → D.** A and B touch neighbouring code; B rebases on A. C is additive
and mostly orthogonal. D goes last because it wires up whatever exists.

Each agent works in its own git worktree off `claude/ibkr-research-platform-plan-0b4f74` and
produces one reviewed commit series; no agent force-pushes the shared branch.

## What is deliberately NOT in any plan

- The **registered decision** authorizing paper orders on dev-provenance infrastructure.
  That is a human sign-off (Madison's), recorded via Plan A's mechanism but never fabricated
  by an agent. Plan A builds the lock; only the operator turns the key.
- Anything touching the A4 research track (term structure, chain ingestion, backtests).
- Any change to frozen documents.
