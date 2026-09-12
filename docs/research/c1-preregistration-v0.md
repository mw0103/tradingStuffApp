# C1 Pre-Registration — v0 (FREEZE BEFORE COMPUTE)

Frozen: Saturday 2026-09-12, prior to any query execution.
Deadline: Monday 2026-09-14, 09:00 America/Chicago.
Rule: this file is not edited after the first query runs. Changes require
C1-PREREG v2 in the ledger with a stated reason.

## Claim under test

Across the mechanically defined universe below, option-implied earnings
moves exceed realized moves on average:
  PASS = median(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for
         P(RF < IM) excludes 0.5.
  FAIL = otherwise.

## Universe rule (applied as-of each event's entry; zero discretion)

- Event source: EDGAR 8-K Item 2.02 per spec v0.3 (dedup: one event per
  CIK-quarter, first filing wins; 8-K/A ignored).
- Timing: acceptanceDateTime classification with two-signal price QA.
  Quarantined events excluded and COUNTED.
- Security: US common stock, primary listing NYSE/Nasdaq/NYSE American.
  ADRs / foreign private issuers excluded (20-F/6-K filers are outside
  the 8-K method regardless).
- Price >= $10 at entry close.
- Options: listed chain with front expiry spanning the event. DTE at
  entry recorded, not capped (long-DTE front dilutes IM; see splits).
- Quotable: ATM call AND put bid > 0 at entry snapshot.
- Tradable tier (PRIMARY sample): combined ATM spread <= 15% of
  straddle mid. All-quotable tier reported as secondary.
- No name-level inclusion or exclusion beyond these gates. High-profile
  names (TSLA-class) remain in sample.

## Window

- Full target: 2012-06 -> present (options data floor).
- v0 deliverable may subset by TIME ONLY: most recent 4 complete years.
  Name-based subsetting is prohibited in every version.

## Measures (definitions per spec v0.3)

- IM = ATM straddle mid / spot at entry snapshot (BMO -> prior close,
  AMC -> same-day close). v0 proxy: EOD quotes; snapshot age logged.
- RF = |close(T+1 after print) / entry close - 1|.
- Primary statistics: median(RF/IM); P(RF < IM).
- Secondary: mean log(RF/IM) (and 1% trimmed); ATM straddle
  hold-through return at mids (diagnostic only).
- Inference: bootstrap clustered by earnings week (same-week events
  co-move through the market factor), 10,000 reps, 95% CI.

## Pre-registered splits (readouts, not gates)

- Market-cap quintile. Registered non-binding prediction: premium
  weakens in the top quintile (attention gradient). This is the
  TSLA prior, converted from a selection temptation into a test.
- ATM-spread quintile.
- DTE <= 7 vs > 7.
- BMO vs AMC.

## Accounting (mandatory outputs)

- Exclusion table: event counts removed per gate, in order applied.
- Quarantine rate from timing QA.
- Zero silent drops. Outliers stay in the primary statistics;
  trimming appears only in the labeled secondary.

## Known v0 biases (logged, with direction)

- Survivorship (universe seeded from current optionable list):
  delisted blowups missing -> RF understated -> biases TOWARD PASS.
  Therefore: v0 PASS is provisional pending survivorship-complete
  rerun; v0 FAIL is strong evidence.
- EOD entry proxy vs true last-pre-print snapshot (age logged).
- Recent-window regime coverage (2018/2020 regimes absent from v0).

## Consequences

- PASS: proceed to C2 preparation; survivorship-complete and
  full-window reruns scheduled before any capital decision.
- FAIL: per spec v0.3, program stops. Full-window + survivorship check
  runs once before the stop is final. No feature-family work (F1, L1)
  proceeds in the interim.

## Deliverable, Monday 09:00 CT

One memo: universe rule reference (this file), exclusion table,
primary statistics with CIs, the four splits, straddle-return
diagnostic, limitations section. The number is the number.
