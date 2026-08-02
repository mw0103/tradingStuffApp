# Decision layer — first run, 2026-08-02

Five forecast arms × four declared decision rules, 2010-01-01..2023-12-31, 1,488 scored decision
dates across the 3 registered folds, reserved holdout untouched. Payoff is the idealized 21-day
variance-swap short per unit vega notional (no spreads, no margin, no crush, no unwind) — these
numbers **rank rules and arms**; they do not estimate live P&L.

Reproduce with `LiveDecisionLayerHarness`.

## 1. Forecast quality transfers to the 21-day horizon

| Arm | 21-day QLIKE | vs HAR-X |
|---|---|---|
| QCJ_CORRECTED | 0.38348 | **+2.386%** |
| HARX (gate) | 0.39285 | — |
| CORRECTED | 0.39285 | −0.000% |
| CALIBRATED_VIX | 0.39720 | −1.108% |
| UNCONDITIONAL | 0.68566 | −74.5% |

Two findings here. **The decomposition candidate survives the horizon change** — +2.39% at 21
days, close to its +1.96% at 1 day, on features built only from data in hand at the decision
close. And **the registered corrector dies at this horizon**: CORRECTED is bit-identical to
HAR-X because the elastic net selected the null model (the documented `CorrectionIsInoperative`
behaviour). The one-day corrector's edge does not survive 21-day aggregation; the QCJ base's
edge does. That is now the strongest evidence in the whole programme that the decomposition
mechanism is real.

## 2. The decision grid

Thinned = non-overlapping stride-21 series, 71 observations (~12/year). Units: annualized vol
points per unit max vega per 21-day window.

| Rule | Arm | Mean/day | Thinned mean | Thinned Sharpe | Particip | Worst day | Max DD |
|---|---|---|---|---|---|---|---|
| always-sell | (any) | 5.532 | 5.469 | **3.286** | 100% | −88.7 | 32.8 |
| sell-when-positive | (all but UNCOND) | 5.532 | 5.469 | 3.286 | 100% | −88.7 | 32.8 |
| sell-top-quintiles | QCJ_CORRECTED | 4.596 | **4.265** | **2.569** | 64% | −84.6 | 21.8 |
| sell-top-quintiles | HARX / CORRECTED | 4.548 | 3.993 | 2.313 | 63% | −84.6 | 32.8 |
| sized | QCJ_CORRECTED | 6.356 | 6.002 | 2.526 | 77% | −88.7 | 38.2 |
| sized | HARX / CORRECTED | 6.300 | 5.748 | 2.375 | 76% | −88.7 | 43.7 |

## 3. What it says — three findings, one uncomfortable

**Finding 1 — the premium is always there.** `sell-when-positive` equals `always-sell` because
implied exceeded every reasonable forecast on ~100% of days: the variance risk premium is
essentially never negative ex ante. The binary trade/no-trade margin does not exist at threshold
zero. Whatever a better forecast is for, it is not for deciding *whether* the premium is positive.

**Finding 2 — the uncomfortable one: conditioning did not beat harvesting.** On risk-adjusted
terms, `always-sell` (Sharpe 3.29) beat every forecast-conditioned rule (2.3–2.6). Standing
aside in low-spread quintiles cost more in missed carry than it saved in avoided losses — and
none of the rules dodged the COVID window (worst day −84.6 vs −88.7 unconditioned; the crash was
not preceded by a low spread). At this payoff's granularity, the answer to "does a better
estimate improve the decision?" is currently: **the estimate improves the ranking, not the
harvest.**

**Finding 3 — but *given* a conditioned rule, the better forecast is the better input.** Within
`sell-top-quintiles`, QCJ's ranking beats the gate's: +0.27 thinned mean (4.27 vs 3.99), +0.26
Sharpe, and **max drawdown 21.8 vs 32.8** — a third less. Same rule, same participation, only
the forecast differs. The forecast-quality ordering survives into the economics ordering. The
chain works; it is the rule family that is weak, not the forecast.

## 4. Caveats before anyone gets excited in either direction

- A 3.3 Sharpe for naive VRP harvesting is an artifact of the idealized payoff: no transaction
  costs, no crush path, no margin calls, no early unwind, strike at exactly VIX. Real short-vol
  implementations keep a fraction of this. The *comparisons* between rules/arms are meaningful;
  the levels are not.
- 71 independent windows. Sharpe differences of ±0.3 on 71 observations are suggestive, not
  established. No DM test has been run between strategies yet.
- The worst day is −88.7 on a mean of 5.5: the left tail is ~16 windows of average carry. This
  is the well-known shape of the trade; nothing here abolishes it.
- 15+ forecast candidates and 4 rules have now been evaluated on this same sample.

## 5. Ways to use what we have found

Ranked by evidence-to-effort:

1. **Sizing, not timing.** Both findings point the same way: participation gating destroys value
   (finding 2), better ranking adds value (finding 3). The natural rule family to explore next is
   *always short, vega scaled by the forecast spread* — never flat, never levered past a declared
   cap. That uses the forecast where it demonstrably helps (ranking) without paying the
   stand-aside cost. One new declared rule; no new fitting.
2. **Drawdown control as the objective.** QCJ's top-quintile rule cut max drawdown by a third at
   equal participation. If the decision criterion is drawdown-adjusted rather than
   Sharpe-adjusted, conditioning may already be winning. Compute Calmar-style ratios on the
   existing grid — zero new code beyond a division.
3. **The paper account tests plumbing, not edge.** With ~12 independent windows a year, a paper
   run cannot establish the Sharpe differences above; it CAN validate execution (fills, sizing
   arithmetic, session handling, the arming chain) on the structure we intend to trade. Task #8's
   short-vol planner is the missing piece; run it sized off the `sized` rule with QCJ.
4. **Fair-value marks.** The QCJ forecast gives a defensible fair value for 21-day variance
   daily. Even with no automated trading, publishing forecast-vs-implied to the dashboard is a
   direct, safe use of the finding.
5. **The holdout question is sharpening.** The confirmatory test the holdout is reserved for is
   now concrete: QCJ_CORRECTED vs HAR-X at both horizons, plus the sized-rule economics, once —
   after the rule family above is frozen.
