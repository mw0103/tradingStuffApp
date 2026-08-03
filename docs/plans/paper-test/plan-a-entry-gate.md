# Plan A — Constant-exposure entry signal behind a registered paper decision

## Goal

The automation loop can currently never fire: `VolResidualSignal.Interpret` refuses every
path, including registered runs ("no entry rule has been defined... Automation does not
invent one" — `Automation/VolResidualSignal.cs`). That refusal is deliberate scaffolding, and
this plan replaces it consciously for the PAPER run only: a constant-exposure entry signal
that trades **because the protocol mandates constant short-vol exposure**, not because any
forecast says to, gated on a persisted, operator-signed paper decision.

## Why it is shaped this way

`docs/plans/paper-run-protocol.md` §Trading rule: constant one-vega-equivalent exposure, QCJ
influencing nothing; §Phases: "Entry requires a registered decision that the paper run may
proceed on dev-provenance infrastructure (the signal's provenance refusal is amended by that
decision for PAPER only, never live)." The decision is the KEY; this plan builds the LOCK.

## Deliverables

1. **Migration 023** `research.paper_run_decisions`: decision_id identity PK, decided_at
   timestamptz, scope text CHECK (scope = 'paper') — the CHECK is the "never live" clause in
   schema form — protocol_ref text, statement text, signed_by text, revoked_at timestamptz
   NULL. House migration style: header comment, `COMMENT ON TABLE`, schema_version column.
2. **`ConstantExposureSignal : IAutomationSignal`** (new class, `Automation/`): returns
   Trade=true with reason "constant one-vega mandate per paper-run-protocol, decision
   <id>" IFF an unrevoked decision row exists; else a named refusal explaining exactly what
   is missing. It reads NO forecast, NO market state — constancy is the point. Selected via
   `PaperAutomation:Signal` config ('vol-residual' default, 'constant-exposure' opt-in), so
   the existing signal remains the default and live paths are untouched.
3. **Decision store + endpoints**: `POST /research/paper-run/decision` (creates; requires
   non-empty signed_by and statement; refuses a second active decision), `GET
   /research/paper-run/decision` (current state, honest about absence),
   `POST /research/paper-run/decision/revoke`. Anonymous like the rest of `/research/*`.
4. **Tests**: unit tests for the signal's both branches; RequiresPostgres test for
   store round-trip + the single-active-decision refusal + revocation flipping the signal
   back to refusal.

## Constraints and non-goals

- Do NOT modify `VolResidualSignal` semantics; it stays the default and keeps refusing.
- Do NOT touch `PaperAutomationService`'s loop body (Plan B owns it). The signal plugs into
  the existing `IAutomationSignal` seam; if the seam needs a decision-id passthrough for the
  decision record, extend `SignalResult` additively (new optional member).
- No agent creates a decision row outside tests. The endpoint exists; the operator calls it.

## Done means

`PaperAutomation:Signal=constant-exposure` + an operator-created decision row + the existing
arming chain ⇒ the loop would submit (Plan B's cap/lifecycle permitting); with no decision or
a revoked one ⇒ named refusal in `paper_automation_decisions`. Full suite green.
