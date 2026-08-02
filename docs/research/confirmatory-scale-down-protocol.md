# Confirmatory protocol: the scale-down rule

**Status: FROZEN 2026-08-02, before execution.** This specification was written after the
exploratory decision-layer runs of 2026-08-02 and before the rule below was ever evaluated.
Everything in §2–§5 is fixed; any change to it after results are seen makes the run exploratory,
not confirmatory, and must be recorded as such. The reserved holdout (2024-01-01..2026-07-31) is
NOT touched by this protocol; it remains reserved for one final confirmation after this rule is
frozen or abandoned.

## 1. What is being tested, and why this rule

The exploratory runs established (dev sample, 2010–2023):

- The QCJ decomposition carries forecast information at both 1-day and 21-day horizons; the
  one-day residual corrector was a short-horizon effect (null at 21 days).
- The variance premium is essentially always positive ex ante: participation gating loses carry.
- Levering above one vega on high-spread days added exposure, not risk-adjusted return.
- The forecast's demonstrated economic value is *ranking* and *drawdown reduction*, i.e. the
  signal appears asymmetric: better at flagging unattractive conditions than exceptional ones.

The mechanistically motivated rule is therefore **scale-down-only**: never lever beyond one,
never necessarily flat, reduce exposure only when the modeled spread is unattractive. This
protocol freezes its exact form before evaluation.

## 2. The frozen rule

Buckets are the fold's train-frozen spread quintiles, exactly as already computed
(`VrpConditioningQuintiles`, training window only). Position in vega:

| Spread bucket (train-frozen) | Vega |
|---|---|
| 5 (spread most attractive) | 1.00 |
| 4 | 1.00 |
| 3 | 1.00 |
| 2 | 0.50 |
| 1 (least attractive) | 0.25 |

Declared, not tuned. **No variant of this mapping will be evaluated under this protocol.** If
this mapping fails, the honest conclusion is that the scale-down channel failed at this
granularity — not that a different mapping should be tried on the same sample.

## 3. The frozen comparison set

Four strategies, identical rule, identical 1.0 max vega, same test days:

1. **Constant one-vega short** (`always-sell`) — the economic baseline.
2. **VIX-only scale-down** — the UNCONDITIONAL arm's spread (implied − train constant) ranks
   days by the implied level alone. This is the most important competing explanation: if QCJ
   does not beat it, we have a better forecast but not a better sizing instrument.
3. **HAR-X scale-down** — the information-matched model gate.
4. **QCJ scale-down** — the candidate.

## 4. The frozen metrics

All computed on the stride-21 thinned series (offset 0, fixed), reported per unit of
**realized average vega** so no strategy gains by simply holding more:

- **PRIMARY: downside deviation** — root mean square of negative thinned P&L (per unit average
  vega). Chosen now, before results; with 71 windows, max drawdown and Calmar are one-path
  statistics and are demoted to secondary descriptive measures.
- Secondary (descriptive, no verdict weight): mean carry per unit average vega, thinned Sharpe,
  max drawdown, Calmar, worst window, realized average vega, participation.
- **Monotonicity**: mean next-window short payoff by QCJ spread quintile (test buckets), which
  must not be flat-or-inverted if the ranking claim is real.
- **Forecast-tier uncertainty**: the QCJ vs HAR-X 21-day QLIKE differential with the study's
  dependence-adjusted machinery — overlapping HAC (lag 25, marked non-honest), thinned HAC
  (lag 5, the honest test), and the stationary block bootstrap interval — reported alongside,
  since the point estimate's uncertainty is now the question.

## 5. The frozen verdict criteria

The scale-down channel is **supported** iff ALL of:

1. QCJ scale-down's primary downside deviation (per unit average vega) is lower than BOTH
   HAR-X scale-down's and VIX-only scale-down's.
2. QCJ scale-down retains at least **85%** of always-sell's thinned mean carry per unit
   average vega.
3. Realized average vega differs from the other scale-down strategies by less than **10%**
   (else the comparison is exposure-confounded and void — the scaled-always lesson).
4. The QCJ-quintile monotonicity table is not flat or inverted (bucket-1 mean payoff strictly
   the worst of the five).

Anything less is **not supported**; partial passes are reported as such with no
reinterpretation of which criteria "really mattered". The forecast-tier uncertainty (§4) is
reported alongside but does not gate this verdict — it gates the eventual holdout decision.

## 6. What happens after

- **Supported** → the rule is frozen as-is as the candidate for the single holdout
  confirmation, and the paper implementation (shadow marks → paper orders, per the narrow
  mandate) sizes with it.
- **Not supported** → recorded like every other negative; the paper implementation proceeds as
  a plumbing test at constant one vega, and the scale-down channel is closed at this
  granularity pending genuinely new information (e.g. A4 term-structure features).

One run. This document does not get edited to fit the outcome.
