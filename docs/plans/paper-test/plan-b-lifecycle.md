# Plan B — Position lifecycle: one declared exit rule, no discretion

## Goal

`PaperAutomationService` evaluates entries and submits through ExecutionService's order API,
but nothing manages an open position: no exit, no roll, no expiration handling. SPY options
are American-style with physical settlement, so hold-to-expiry means assignment mechanics we
do not handle. This plan adds the minimal lifecycle: **close the spread with a closing order
at a declared DTE threshold. That is the entire rule.**

## Why it is shaped this way

`hedged-carry-menu.md` §6 records the standing suspicion of unwind rules: they are the most
tunable (= most overfittable) item. So v1 declares ONE exit — time-based, parameter fixed in
config, no P&L triggers, no vol triggers, no "unless" branches. The protocol's success
criterion 3 ("positions survive rolls, expirations, data outages, and order failures
correctly") is exercised by the simplest rule that avoids expiration mechanics entirely.

## Deliverables

1. **`PaperAutomationOptions.ExitDteThreshold`** (int, default 7): positions in the managed
   structure are closed when their expiration is ≤ N calendar days out. Config-declared,
   logged at startup, recorded on every exit decision.
2. **Exit evaluation branch** in `PaperAutomationService`: on each evaluation pass, before
   entry logic — query current positions (via the existing ExecutionService/portfolio read
   the arming chain already uses), identify open managed spreads at/below threshold, submit a
   closing order (same order API, opposite side), record the decision row with reason
   'exit-dte'. The session order cap counts exits; an exit is never blocked by the cap
   (raise the cap check to entries only — an uncloseable position is worse than an extra
   order).
3. **Entry-when-flat guard**: entry logic is skipped while a managed spread is open or a
   closing order is pending — constant exposure means one spread at a time, not stacking.
4. **Failure paths recorded**: a rejected/ignored closing order is a recorded decision with
   the error text, retried next pass; repeated failure keeps appearing in the record rather
   than being escalated silently away (protocol: coverage of failure paths IS the point).
5. **Tests**: unit tests for threshold arithmetic (calendar days, expiration boundary cases,
   AM/PM irrelevant for SPY weeklies but pin the convention) and the entry-when-flat guard;
   a RequiresPostgres test for exit-decision rows landing idempotently (an exit evaluated
   twice submits once — same claim discipline the entry path uses).

## Constraints and non-goals

- Do NOT touch signal/gate classes (Plan A) or add capture tables (Plan C).
- No roll logic (close-then-reenter emerges from exit + entry-when-flat on later passes).
- No market-condition exits of any kind. If the tail shows up, the defined-risk long wing is
  the hedge — that is menu item 1's job, not this rule's.
- Rebase on Plan A's merge before yours; you share the file's neighbourhood.

## Done means

With a position open at ≤7 DTE, the next evaluation pass submits its closing order and
records why; with it filled, the following pass re-enters (decision permitting). Both visible
in `paper_automation_decisions`. Full suite green.
