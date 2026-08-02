# Model and ensemble candidates

What to try next, given what has already failed and why. Written 2026-08-02, after the first H1
adjudication; the reasoning below is conditioned on that result and should be revisited when it
changes.

Companion to `volatility-forecast-residual-study.md` (the gates any candidate is judged against)
and the trial protocol (`research.registered_trials`, `TrialProtocol`). Nothing in this file is a
registration. It is a menu with reasons, and the argument for an ordering.

---

## 1. What the H1 failure actually said

The registered rung-3 candidate (elastic net on HAR-X residuals) produced, on 2010–2023, holdout
untouched:

| | | |
|---|---|---|
| Pooled QLIKE margin vs HAR-X | **+3.02%** | passed the ≥2% bar |
| Diebold-Mariano (margin-adjusted) | p = **0.30** | failed |
| Block-bootstrap lower bound | **−6.1e−3** | failed |
| Folds positive | 2 of 3 (−0.65% on the COVID-test fold) | passed, barely |
| Both VIX halves positive | +7.07% / +1.45% | passed |

Read plainly: **the average improvement is there, and it cannot be told apart from noise.** The
candidate did not fail for lack of flexibility — the exploratory GBT (rung 4, run outside the
ladder) was a recorded dead end, which is evidence that *more* flexibility on the same inputs is
not the missing ingredient.

Two diagnoses fit the pattern, and they prescribe different medicine:

- **A power problem.** The margin is real but small relative to the day-to-day dispersion of QLIKE
  differentials. Remedy: reduce the variance of the forecast errors — which is what forecast
  combination does — or find information orthogonal to what HAR-X already sees.
- **A regime problem.** The gain concentrates in calm halves (+7.07% low-VIX vs +1.45% high) and
  went negative through the COVID test fold. Remedy: candidates that adapt what they trust by
  regime, and evaluation attention on the high-VIX half, where a short-vol decision actually
  bites.

Everything below is chosen against those two diagnoses. "Another model family on the same twelve
features" is exactly what is *not* on this list.

---

## 2. Tier A — single models on data already in hand

The estimator already computes realized quarticity, bipower variation, jump variation, and signed
semivariances for every session (`RealizedVolatilityDay`). None of them are used by any current
model. These three candidates are literature-standard, cheap, and each is one catalog class plus
one registered variant.

### A1. HARQ — measurement-error-aware HAR (strongest single candidate)

Bollerslev–Patton–Quaedvlieg (2016). The daily HAR coefficient is attenuated in proportion to
realized quarticity: on days when RV is measured noisily, lean on the weekly/monthly components;
on cleanly measured days, trust the daily lag more.

Why it fits *this* failure: QLIKE punishes under-forecasts hardest exactly when a noisy daily RV
drags the forecast down; HARQ's correction acts precisely there, and published gains are largest
in turbulent stretches — the high-VIX half where our candidate is weakest. Uses
`RealizedQuarticity`, already persisted. Applied as HARQ-X (the existing VIX block retained) so
the comparison against the gate is clean.

### A2. SHAR — signed-semivariance HAR

Patton–Sheppard (2015). Split the daily lag into upside and downside semivariance; the downside
component carries most of the predictive content. Uses `UpsideVariance`/`DownsideVariance`.
Cheapest possible test of "the leverage asymmetry is worth something at the daily horizon".

### A3. HAR-CJ — continuous/jump decomposition

Andersen–Bollerslev–Diebold (2007). Separate bipower (continuous) from the jump component; jumps
mean-revert faster than diffusive variance, so forcing one coefficient across both blurs each.
Uses `BipowerVariation`/`JumpVariation`. Weakest prior of the three — published gains are modest —
but it is one class and it completes the decomposition family.

### A4. Term-structure features from our own strip

Phase 9 ingested ThetaData chains and validated the model-free strip against VIX. That makes a
**short-dated implied variance** (a VIX9D-equivalent from our own strip) and therefore a
**term-structure slope** computable from data we already hold. Slope and curvature of implied
variance are known forecast improvers *orthogonal to the VIX level* — new information, not a new
functional form, which is what diagnosis 1 asks for.

This one is a feature-tier addition rather than a model: it extends HAR-X's feature block, and
under the registration an amended feature set is a new `feature_set_hash` — a new variant.

---

## 3. Tier B — combinations and ensembles

The direct attack on the power problem. A combination's error variance is lower than its members'
whenever the members' errors are imperfectly correlated, and our members disagree by construction
(HAR sees only history; VIX sees only the option market; HAR-X sees both but linearly). The
combination literature's embarrassing, durable finding — simple averages beat estimated weights —
cuts our way: the simplest variants consume the least budget and are the hardest to overfit.

### B1. Equal-weight average of HAR-X and calibrated VIX

The null hypothesis of combining. Two forecasts, zero estimated parameters beyond the members'
own fits, and the strongest prior from the literature. If B1 does not beat HAR-X on DM, estimated
weighting schemes are unlikely to and the whole tier can be closed cheaply.

### B2. Granger–Ramanathan under NNLS

Regress realized variance on member forecasts over the training window, weights non-negative
(`NonNegativeLeastSquares` — already in the codebase), applied out of fold. One estimated layer,
still convex, still interpretable: the weights say which member the data trusts.

### B3. Regime-switching weights (train-defined VIX split)

B2, but weights estimated separately for the train-defined low/high VIX halves the fold runner
already computes. This is the candidate the +7.07%/+1.45% asymmetry argues for: nothing about our
current models can shift trust between members as the regime changes, and this is the smallest
mechanism that can. Threshold discipline is already solved — the regime split is train-frozen in
`VolResidualFoldRunner`.

### B4. Discounted-QLIKE adaptive weights

Weights proportional to inverse exponentially-discounted training QLIKE per member. Adapts faster
than B2 after regime breaks (the COVID-fold failure mode) without estimating a regression. One
tuning constant (the discount), declared in the registration, not searched.

**Not proposed: stacking with a learned meta-model.** A nonlinear combiner is a rung-4 shaped
object under the ladder's logic, and the leak surface (meta-features from in-fold predictions) is
exactly the kind the pipeline's causality tests exist to catch. If the linear tier fails, that
failure is informative; skipping to stacking would not be.

---

## 4. Tier C — gated, deferred, or rejected

- **Any further rung-4 model (random forests, deeper GBT, kernel methods).** Banned by the
  registration until a rung-3 variant passes the gate. The GBT dead end also removes the appetite:
  flexibility on these inputs has been tried.
- **LSTM / neural sequence models.** Deliberately left in the archived repo. Outside the ladder
  entirely, needs its own registration, and the Python service it implies is deferred in the
  roadmap. Revisit only with a specific hypothesis simpler models cannot express.
- **Overnight/ES features (Tier-2).** Registered already as a 2023-08+ ablation; short usable
  history makes it a low-power test today. Becomes more attractive as the recorder accumulates.
- **Realized semivariance of *implied* (strip) changes, intraday VIX, options flow.** Genuinely
  new information, not yet in the data layer. Feature-pipeline work first; listed so the idea is
  not lost.

---

## 5. Protocol accounting — the part that spends real budget

The cap is **10 registered variants** before the holdout opens; the gate threshold deflates as
0.05/N. Every candidate above, when run against real data with intent to interpret, is one
variant. Hyperparameters declared inside one registration (the elastic-net α grid, B4's discount)
do not multiply variants; a *changed* feature set or model family does.

**First, an honesty item: register the variant that already ran.** The elastic-net CORRECTED
configuration was adjudicated before the registry existed in `main`. Its look at the data
happened; the count is wrong until it is recorded. Register it retroactively as variant #1 with
its known outcome (`negative`), rationale stating it predates the registry. N is then 1 and the
next variant is judged at 0.05/2 — which is simply the truth of how many looks have occurred.

**Recommended sequence, and the budget it spends:**

| # | Variant | Slot | Rationale |
|---|---|---|---|
| 1 | CORRECTED (retroactive) | spent | the look already happened |
| 2 | **B1** equal-weight HARX+VIX | 1 | cheapest, strongest prior, calibrates the whole combination tier |
| 3 | **A1** HARQ-X | 1 | strongest single-model prior; attacks the high-VIX weakness |
| 4 | **B3** regime weights | 1 | the asymmetry says this is where the money is |
| 5 | **A4** term-structure slope into HAR-X | 1 | first genuinely new information source |

Five slots spent through step 5, five in reserve — enough to follow one surprise wherever it
leads, which is what reserves are for. A2/A3/B2/B4 run as *dev* explorations first (no slot,
`Registrable: false`) and are promoted to registrations only if their dev numbers justify the
spend. That is the pattern the H1 run already established, now with the bookkeeping the registry
enforces.

Two standing rules, restated because every one of these candidates will tempt against them:

- The pipeline-validity placebo (residuals shuffled within VIX-tercile blocks ⇒ ≈0 improvement)
  runs before any real result is believed — combinations included.
- A negative dev result is recorded in the run store like any other. Deleting a disappointing dev
  run and re-running "to check" is the behaviour the whole apparatus exists to make impossible at
  the registered tier; extending the same discipline to dev runs costs nothing and keeps the
  file drawer empty.
