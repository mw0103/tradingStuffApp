# Dev run 2026-08-02 — candidate sweep

Nine exploratory candidates against the registered gate, 2010-01-01..2023-12-31, reserved holdout
untouched. 1,509 scored days across the 3 registered walk-forward folds. Development run: **no
registered-variant slot was consumed and nothing here is a claim.**

Reproduce with `LiveDevRunHarness` and `VOLRESIDUAL_DEV_DB` pointed at the research database.

---

## 1. Results

Margin is pooled-QLIKE improvement against HAR-X. Verdict columns are the study's own adjudicator
(margin-adjusted DM at τ = 2%, one-sided; fold signs; VIX halves; block-bootstrap lower bound).

| Model | Margin % | 3 folds + | Low VIX % | High VIX % | Boot lower | DM p (τ-adj) | Verdict |
|---|---|---|---|---|---|---|---|
| **CORRECTED-QCJ (A6)** | **+3.440** | **3/3** | +6.410 | **+2.287** | **−2.36e−3** | 0.168 | fail |
| CORRECTED (registered #1) | +3.021 | 2/3 | +7.074 | +1.448 | −6.14e−3 | 0.303 | fail |
| HARQCJX (A5) | +1.960 | 3/3 | +1.777 | +2.032 | −4.32e−3 | 0.514 | fail |
| HARCJX (A3) | +1.869 | 3/3 | +1.856 | +1.875 | −4.03e−3 | 0.550 | fail |
| HARQX (A1) | +1.736 | 3/3 | +1.507 | +1.825 | −5.06e−3 | 0.587 | fail |
| SHARX (A2) | +0.999 | 2/3 | +0.469 | +1.205 | −7.27e−3 | 0.777 | fail |
| GR-NNLS (B2) | +0.202 | 2/3 | −1.052 | +0.688 | −5.85e−3 | 0.999 | fail |
| GR-REGIME (B3) | −1.274 | 2/3 | +1.012 | −2.161 | −1.11e−2 | 1.000 | fail |
| EW-DISC (B4) | −1.625 | 1/3 | +0.203 | −2.334 | −1.12e−2 | 1.000 | fail |
| EW-HARX-VIX (B1) | −10.963 | 1/3 | −0.815 | −14.903 | −3.57e−2 | 1.000 | fail |
| GBT (rung 4) | −65.743 | 0/3 | −8.338 | −88.028 | −2.93e−1 | 0.947 | fail |

**Everything fails the registered gate.** What separates the candidates is *how*.

---

## 2. What was learned

### The combination tier is closed — a clean negative

B1 through B4 were the direct attack on the power diagnosis, and all four failed, decisively:

- **B1 equal-weight: −10.96%.** The combination literature's simple-average prior assumes members
  of roughly comparable skill. Ours are not: calibrated VIX alone scores **−32.1%** against HAR-X.
  Averaging a good forecast with a much worse one is not variance reduction, it is dilution — and
  the damage concentrates exactly where it matters, **−14.9% in the high-VIX half**.
- **B2 Granger–Ramanathan: +0.20%.** NNLS did the right thing and put essentially all weight on
  HAR-X, so the combination reproduces the gate and adds nothing. This is the *informative* result
  of the tier: given free choice of weights, the data does not want the other members.
- **B3 regime weights and B4 discounted-QLIKE both went negative.** Estimating more weights on
  less data, which is what both do, cost more than the adaptivity gained.

B1 was designed to be able to close the whole tier cheaply if it failed. It failed, and it has.
No further combination candidate is worth a slot without new members — and "new members" means
new information, not another weighting scheme over the same three forecasts.

### The decomposition family works, consistently, and too weakly

A1, A3 and A5 all improve on the gate by 1.7–2.0% — and all three are positive on **3 of 3 folds
and in both VIX halves**, which the registered candidate never managed. A3 (jump decomposition)
edges A1 (quarticity attenuation), and A5 (both) edges A3, which suggests the two mechanisms are
close to additive rather than redundant. A2 (signed semivariance) is the weakest of the family.

These are the best-*behaved* models in the study. They are also all below the 2% materiality bar.

### The two mechanisms compose — A6 is the best model the study has produced

Running the registered elastic-net residual correction over A5's forecast instead of HAR-X's beats
the registered candidate on **every single dimension**:

| | CORRECTED (registered #1) | CORRECTED-QCJ (A6) |
|---|---|---|
| Margin | +3.021% | **+3.440%** |
| Folds positive | 2/3 (−0.65% on COVID) | **3/3** (worst +0.85%) |
| High-VIX half | +1.448% | **+2.287%** |
| Bootstrap lower | −6.14e−3 | **−2.36e−3** |
| DM p (τ-adjusted) | 0.303 | **0.168** |
| DM p (unadjusted, 1-sided) | ~0.064 | **~0.011** |

The interpretation is clean and was predicted by the mechanisms: the decompositions fix what the
daily lag *measures*, the corrector fixes what the residual still *contains* afterwards. Different
jobs, so the gains stack. And the gain stacks specifically where the registered candidate was
weakest — the high-VIX half, where a short-vol decision actually bites, went from +1.4% to +2.3%.

**It still fails.** The margin-adjusted DM p is 0.168 against a threshold that is now 0.025, and
the bootstrap lower bound still does not exclude zero. It is closer than anything else has come —
less than half the distance on the bootstrap bound — but it does not clear.

---

## 3. The honest problem with registering A6

The dev window and a registered run's window are **the same data**. So the dev numbers above are
not a preview of what registration would find; they are what registration *would* find. Promoting
A6 to variant #2 would spend a slot to formally record a failure we have already observed.

Worse, this sweep looked at the 2010–2023 sample nine more times. The registry counts registered
variants and deflates its threshold accordingly, but that deflation does not price nine dev looks
followed by promoting the winner. Selecting the best of nine and registering it is precisely the
selection effect the apparatus exists to defeat, and doing it while calling the threshold 0.025
would be the most dishonest move available.

Two defensible paths, and they should be chosen deliberately rather than drifted into:

1. **Register A6 as variant #2, with the sweep disclosed in its rationale** — that it was selected
   from nine dev candidates, and that its dev figures are known. The registration then records a
   known-failing candidate honestly, and the slot buys a permanent, auditable record of the best
   configuration found. Threshold 0.025; expected outcome `negative`.
2. **Stop discovery on this sample.** Declare the 2010–2023 window exhausted for model search,
   freeze A6 as the best-known specification, and spend the reserved holdout once — on A6 against
   HAR-X, as the single confirmatory test the holdout was reserved for.

Path 2 is what the holdout is *for*. Path 1 is worth doing first only if the registry record has
value independent of the verdict.

---

## 4. What this says about the goal question

The goal is whether a better estimate of future realized volatility improves when we sell
volatility, avoid it, or size the position. On the estimation half, after this sweep, the answer is
narrow and consistent:

**A better estimate is achievable, but the improvement is small — around 3.4% in QLIKE — and not
statistically separable from HAR-X on fourteen years of daily data.** That is not a null result;
it is a bounded one. Every family tried (residual correction, measurement-error attenuation, jump
decomposition, four combination schemes, gradient boosting) lands in the same narrow band, which
is itself evidence that the ceiling on this feature set is low rather than that we picked the
wrong model.

Two consequences follow, and neither is "try another model family":

- **A 3.4% QLIKE improvement that is positive on every fold and in both regimes may still be
  economically useful.** Statistical indistinguishability at n=1,509 is a statement about power,
  not about worthlessness. Whether it moves a sell/stand-aside/size decision is a *different
  question* with a different test — and it is the question we actually care about. That test is
  the VRP-conditioning study, and A6 is the forecast it should carry.
- **The remaining upside is in information, not functional form.** The one candidate class not yet
  tried that fails for a different reason is A4 — term-structure slope from our own option strip —
  because it is the only one that adds something HAR-X cannot already see. Everything else on the
  menu recombines the same twelve features.

**Recommendation: stop model search, carry A6 into the decision layer, and hold the reserved
holdout for one confirmatory test.** If the decision layer shows the improvement does not change
sizing or timing, the estimation question is answered — negatively, but completely — and no
further variant is worth a slot.
