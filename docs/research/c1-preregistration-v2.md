# C1 Pre-Registration — v2 (SUPERSEDES v0, PRE-RESULT AMENDMENT)

Amended: Saturday 2026-09-12, evening. No real-data query result exists;
the compute verb has not produced a memo. The blind is intact.
Stated reason: adversarial review PROVED the v0 criterion is satisfied by
a fairly priced market (zero-premium synthetic through the real compute
step: PASS, median 0.878, P(RF<IM) CI [0.505, 0.603]). Absolute returns
are right-skewed, so median(RF/IM) < 1 and P(RF<IM) > 0.5 hold with no
premium at all. The v0 criteria measured distribution shape, not premium.
Premium is a statement about means.

## 1. Corrected decision criterion

PASS = mean(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for
       mean(RF/IM) excludes 1.
FAIL = otherwise.

Null under fair per-event pricing (IM_i = E[RF_i]): mean(RF/IM) = 1
by the tower property. The criterion now carries the claim.

Demoted to descriptive readouts (reported, never deciding):
- median(RF/IM) and P(RF < IM)  — shape and win-rate; relevant to C2
  sizing, insufficient for premium existence.
- mean log(RF/IM), 1% trimmed  — secondary, as in v0.
- ATM straddle hold-through return at mids — economic cross-check;
  its sign should agree with the verdict.

## 2. Timing rule (ratifies the pre-run change)

Gate 09 uses the OUTCOME-BLIND pre-entry-only rule (pre-entry closes
against the trailing 20-day scale). The v0-era two-signal comparison
read the event move and was proven to remove the strongest PASS
evidence (removed-set P(RF<IM) 0.946 vs population 0.620; verdict flip
demonstrated at the boundary). The two-signal comparison may be
computed as a POST-HOC diagnostic only; it never affects inclusion.

## 3. Deadline fallback (pre-committed, blind)

If the full universe x window has not completed by Monday 09:00 CT:
- Deliverable subset = the most recent K complete calendar quarters
  with 100% fetch coverage across the frozen universe, K maximized.
- Symbol-partial subsets are prohibited in every form.
- A fallback memo labels itself PROVISIONAL pending the full-window
  rerun; the full run remains owed regardless of the fallback verdict.

## 4. Interpretations ratified (recorded readings of frozen text)

- Dedup: calendar quarter of acceptance date per CIK; dual-class names
  collapse to one kept event; ordinal-symbol tie-break.
- "Earnings week" = ISO week of the print date.
- "Front expiry spanning the event" = earliest listed expiration on or
  after the exit date.
- Entry = last NYSE close at or before the acceptance instant;
  a 16:00:00 acceptance counts as AMC. As-of joins are strict.

## 5. Live pins required BEFORE unblinding any memo

- EDGAR acceptance-time reading is Eastern: live test against Apple's
  2024-02-01 8-K (expected 16:30 ET). Every date in the study leans on
  this single row.
- EDGAR submissions/companyfacts JSON shapes: RequiresEdgar live test.
- First real Terminal EOD fetch: observed header logged; presence or
  absence of underlying_price recorded; if absent, parity-implied spot
  stands with the parity-vs-close quantiles reported in the memo.
- Gateway daily-bar path exercised for one name before the batch.

## 6. Unchanged from v0

Universe rule and gate order, window and time-only subsetting, measure
definitions (IM, RF, RG), spread tier, splits (cap, spread, DTE,
BMO/AMC), exclusion accounting and conservation requirement, known-bias
log (survivorship direction: toward PASS), consequences of PASS/FAIL.

## 7. Freeze

This file is not edited after the first real-data memo generates.
Further changes require v3 with a stated reason, and any post-result
change is an excuse, not methodology, per the standing rule.
