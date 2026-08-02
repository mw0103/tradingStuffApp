# Confirmatory scale-down result — 2026-08-02

One run, executed against the protocol frozen at commit `a036f14`
(`confirmatory-scale-down-protocol.md`). The protocol file is unedited. 1,488 decision dates,
3 folds, 72 thinned windows, holdout untouched. Reproduce with `ConfirmatoryScaleDownHarness`.

## Verdict: NOT SUPPORTED

The frozen criteria, applied mechanically:

| # | Criterion | Result | Pass |
|---|---|---|---|
| 1 | QCJ downside dev/vega below BOTH HAR-X and VIX-only | 3.5449 vs HAR-X 3.5540 ✓, vs VIX-only 3.5372 ✗ | **FAIL** |
| 2 | ≥85% of always-sell carry per unit vega | 6.064 vs 5.469 = 110.9% | pass |
| 3 | Average vega within 10% across scale-downs | 0.8523 / 0.8542 / 0.8501 | pass |
| 4 | Bucket-1 mean payoff strictly worst | 2.23 vs 3.06 / 2.63 / 5.18 / 8.55 | pass |

Criterion 1 fails; the verdict is conjunctive. Per §6 of the protocol: the negative is recorded,
the paper implementation proceeds as a plumbing test at constant one vega, and the scale-down
channel is closed at this granularity pending genuinely new information.

## The full frozen table

| Strategy | Avg vega | Mean/vega | **Downside dev/vega** | Sharpe | Max DD |
|---|---|---|---|---|---|
| constant-1-vega | 1.0000 | 5.469 | **3.282** | 3.286 | 32.80 |
| vix-only-scale-down | 0.8542 | 6.133 | 3.537 | 3.223 | 32.80 |
| harx-scale-down | 0.8501 | 5.948 | 3.554 | 3.093 | 32.80 |
| qcj-scale-down | 0.8523 | 6.064 | 3.545 | 3.172 | 32.80 |

## What the numbers actually say

**The mechanism the rule assumed does not exist at this granularity.** Every scale-down strategy
has a WORSE per-unit downside deviation than the constant short (3.54 vs 3.28), and all four
share the identical max drawdown (32.80) and worst window (−88.7). The reason is visible in the
monotonicity table: the low-spread buckets the rule de-risks (bucket 1: +2.23, bucket 2: +3.06)
still have positive mean payoffs, while the catastrophic windows sit in the HIGH buckets — the
crash was not preceded by an unattractive spread, it arrived during attractive-looking ones. So
reducing vega in buckets 1–2 sheds carry (numerator) and exposure (denominator) without touching
the tail, and per-unit downside gets worse, not better.

This also reinterprets the earlier one-third drawdown reduction under `sell-top-quintiles`: that
came from being flat on ~37% of days by luck of which windows were skipped, not from the ranking
identifying danger. The frozen run was designed to distinguish those two explanations, and it did.

**The ranking is real, but it ranks OPPORTUNITY, not danger.** The monotonicity across buckets
4–5 (+5.18, +8.55) is strong and criterion 4 passed. QCJ knows where the premium is fat. It does
not know where the losses are — consistent with the review's observation that the crash windows
are what a conditional-mean forecast is least equipped to flag.

**The forecast-tier uncertainty, as the review required:**

| Test | Advantage | Stat | p (one-sided) |
|---|---|---|---|
| Overlapping HAC lag 25 (not honest) | +0.00937 | 0.981 | 0.163 |
| Thinned lag 5 (honest, n=72) | +0.00589 | 0.561 | 0.288 |
| Block bootstrap 90% CI | [−0.0045, +0.0249] | — | — |

The QCJ 21-day QLIKE advantage is positive at the point estimate and **not statistically
established** — the honest interval includes zero comfortably. The claim "QCJ contains
incremental information" stays *provisionally* supported on the strength of the cross-horizon
pattern, not on any single significant test.

## Consequences

1. **Scale-down is closed at this granularity.** No re-tuned mapping will be run on this sample.
2. **The paper implementation proceeds at constant one vega** — a plumbing test under the narrow
   mandate (shadow marks → paper orders), which finding 2's carry numbers do still motivate.
3. **The tail problem is now the named problem.** If the forecast family is to earn a sizing
   role, it needs an input that moves BEFORE high-bucket crashes — which is the A4 term-structure
   slope's hypothesized behaviour (inverted short-dated implied), and a concrete, falsifiable
   reason to prioritize that data project over further rule iterations.
4. **Updated claim table**: "QCJ can reduce drawdown under selective exposure" moves from
   *promising, not definitive* to **rejected at this granularity** — the earlier drawdown
   reduction was a participation artifact.
