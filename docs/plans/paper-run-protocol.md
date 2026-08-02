# Paper-run protocol — constant one vega, QCJ in shadow

Adopted 2026-08-02, after the confirmatory scale-down failure
(`docs/research/confirmatory-scale-down-result.md`). This document governs task #10. It exists
so no future session drifts the paper run back into being a test of the rejected hypothesis.

## The question the paper account answers

**Not**: does QCJ-conditioned exposure work. The frozen confirmatory run answered that at this
granularity — no — and paper performance cannot make a statistical result disappear.

**But**: *what does constant short-volatility harvesting actually look like when implemented
through real option structures?* Strike selection, skew, vega drift, discrete rolls, bid–ask,
expiry behavior, margin consumption, stale quotes, rejected orders, partial fills, and the gap
between theoretical and executable marks. The headline research result — constant one-vega
dominates the tested conditioning rules under an idealized payoff — is only a tradable baseline
if constant one vega in the experiment maps coherently to constant risk in an actual option
portfolio. One vega can conceal very different gamma, convexity, skew, liquidity, and margin
across dates. That mapping is what this run measures.

## Closed, and staying closed

- QCJ scale-down as a risk-control strategy.
- QCJ gating as evidence of crash avoidance.
- Further threshold or bucket tuning on the 2010–2023 sample.
- Levering the high buckets because their average payoff is higher — the tail lives there too.
- Interpreting paper returns as a second chance for any of the above.

## Trading rule

**Constant one-vega-equivalent exposure. QCJ does not determine whether to trade or how much.**
The structure is the short-vol put credit spread (`SpyShortVolPlanner`), 1 lot, the existing
arming chain untouched (DU-only, router/portfolio/market-data checks, session cap, hard
per-spread risk cap).

## The shadow record — logged alongside every decision, influencing none

1. QCJ forecast (21-day variance)
2. HAR-X forecast
3. Implied variance (VIX²-derived, decision close)
4. Forecast−implied spread and train-frozen bucket, per arm
5. Intended exposure (constant), and the hypothetical QCJ / HAR-X / VIX-only allocations,
   **clearly marked as shadow calculations**
6. Actual contracts selected and achieved Greeks
7. Simulated fill and the contemporaneous market quote
8. Margin requirement
9. Actual paper P&L
10. Idealized variance-swap P&L for the same window
11. Counterfactual constant-one-vega P&L, **reconstructed independently** (not derived from #9)

This gives QCJ a legitimate *prospective* test — whether its ranking keeps appearing in
genuinely new observations — without letting it influence the traded path.

## Success criteria — operational, never Sharpe

The run is judged on coverage of state transitions and failure paths, not on returns. A paper
period is too short and its fills too synthetic for performance inference. Success means:

1. Only information actually available at each decision time is used (verified, not assumed).
2. Intended and achieved exposure reconcile.
3. Positions survive rolls, expirations, data outages, and order failures correctly.
4. P&L can be independently reconstructed.
5. The discrepancy between idealized and option-structure payoffs is measured and understood.
6. Margin and tail exposures are captured well enough to define a safe eventual live pilot.

"QCJ beats HAR-X in shadow" is explicitly NOT a success criterion of this run.

## What paper cannot establish

Real queue position, slippage under stress, market impact, whether displayed liquidity fills,
the operational consequences of real losses, or a statistically credible live Sharpe. Paper is
necessary for implementation validation and is not final evidence of tradability; some effects
are measurable only by a tightly capped live pilot, which this run's §Success item 6 exists to
make definable.

## Priority

**A4 (term-structure slope) is the more important research path** — it is the only candidate
that could observe something before the high-bucket tail events, which is now the named
problem. Task #10 is the necessary engineering path and is deliberately simpler because of the
negative result: no adaptive sizing in v1.

## Phases

1. **Shadow marks only.** Daily record of items 1–5 plus the planner's intended structure and
   its quoted marks, no orders. Runs until the record demonstrably reconciles.
2. **Paper orders.** The automation loop armed with `Structure=short-vol-credit-put` at 1 lot,
   full record 1–11. Entry requires a registered decision that the paper run may proceed on
   dev-provenance infrastructure (the signal's provenance refusal is amended by that decision
   for PAPER only, never live).
3. **Review against §Success.** Only after that: the question of a capped live pilot, which is
   a separate decision this document does not authorize.

## Phase 1 operational state (2026-08-02)

- Migration 021 applied to the live research database; `research.vol_shadow_marks` exists.
- **First mark persisted** for 2026-07-31 via `POST /research/shadow-marks/run`: 710 training
  rows (2023-08-31..2026-07-01, all labels closed), VIX 15.99, QCJ bucket 2 / HAR-X bucket 2 /
  VIX-only bucket 3, shadow allocations 0.5/0.5/1.0. Planner intent recorded as a named refusal
  (gateway down, weekend) — the record is honest about what could not be quoted.
- **Standing operational requirement**: one `POST /research/shadow-marks/run` per trading day
  after the SPX close (and after the day's bars land in `research.bars`). The endpoint is
  idempotent per date. Until a scheduler owns this, it is a manual step and missed days are
  visible as gaps in `GET /research/shadow-marks` — absence renders as absence, per house rule.
- The service must be running for the endpoint to exist; the durable home is the Aspire app
  host, not the ad-hoc process used to bootstrap the first mark.
