# Model implementation and training plan

How each candidate in `docs/research/model-candidates.md` gets built, trained and judged.
Written 2026-08-02 as a session handoff: a fresh session should be able to execute any numbered
item from this file plus the candidates doc alone. Read order for context:
`CLAUDE.md` ("What this is for") → `docs/research/model-candidates.md` → this file.

---

## 1. Ground rules that apply to every candidate

**One class, one catalog entry.** A model is a `VolResidualMethod` implementation added to
`VolResidualMethodCatalog.Exploratory`. It sees a `VolResidualFoldContext` (train-frozen shared
statistics, previously fitted methods) and returns a `VolResidualFittedMethod`. Dependencies on
other methods go through `context.Require`, which makes catalog order a named error rather than
a wrong number.

**"Training" is per-fold refit inside the walk-forward harness.** There is no global model
store and no train-once-serve-forever artifact. Every fold refits every method from scratch on
that fold's training block. This is a feature, not a limitation: no stale-weights problem, and
every number is reproducible from (git sha, fold config, seed) — which is exactly what
`research.registered_trials` records.

**Hyperparameter selection is inner blocked CV on train, grids declared at registration.**
The elastic net's α ∈ {0, 0.5, 1} with blocked 5-fold CV is the template. A declared grid does
not multiply registry variants; a *changed* grid or feature set does.

**Determinism is mandatory.** Seeds are registered (the bootstrap seed rides
`registered_trials.seed`); no wall-clock, no unseeded RNG in any fit path. A method whose fit
is not bit-reproducible cannot be characterization-tested and does not enter the catalog.

**Every new method gets three kinds of test before its first dev run:**
1. *Mechanics* — the fit does what the class doc says (see `CandidateMethodTests` for the
   pattern: the average is exactly the average, the attenuation centre is train-frozen).
2. *No-look-ahead* — corrupting evaluation-window data moves nothing it should not
   (`VolResidualFoldRunnerRetransformationTests` pattern).
3. *Characterization* — golden first/last/mean forecasts on the fixed synthetic fold, added as
   new keys with the existing keys unmoved.

**Dev before registered, always.** Dev runs land in `research.dev_vol_residual_runs`, consume
no slot, and are kept whatever they say. Promotion to a registered variant spends one of the
remaining slots (N=1 spent of 10) and is judged at 0.05/N after the placebo gate.

---

## 2. Per-candidate implementation notes

| # | Candidate | Key | Fitter | New plumbing | Status |
|---|---|---|---|---|---|
| B1 | Equal-weight HARX+VIX | `EW-HARX-VIX` | none | none | **done** |
| A1 | HARQ-X | `HARQX` | OLS | `RqDMinus1` on the row | **done** |
| A2 | SHAR-X | `SHARX` | OLS | semivariances on the row | next |
| A3 | HAR-CJ-X | `HARCJX` | OLS | bipower/jump on the row | next |
| B2 | Granger–Ramanathan | `GR-NNLS` | NNLS over member forecasts | none | next |
| B3 | Regime weights | `GR-REGIME` | NNLS ×2 (VIX halves) | none | after B2 |
| B4 | Discounted-QLIKE weights | `EW-DISC` | closed form | none | after B1 dev |
| Q1 | Quantile HAR-X (§3) | `QHARX` | pinball-loss linear fit | quantile solver | design |
| A4 | Term-structure features | (feature tier) | — | strip series (§4) | later |

**A2 (SHAR-X).** Plumb `UpsideVariance`/`DownsideVariance` of d−1 through the row exactly as
`RqDMinus1` was. Replace the daily lag with two features, `log(RS⁻_{d−1})` and `log(RS⁺_{d−1})`
(floor at a small epsilon before the log — a session can have a zero semivariance). Keep the
weekly/monthly/VIX block unchanged. OLS + standard retransformation.

**A3 (HAR-CJ-X).** Plumb `BipowerVariation`/`JumpVariation` of d−1. Features: `log(BV_{d−1})`
(floored) and the jump share `J/(J+BV)` bounded in [0,1] — the share, not the level, so it needs
no separate normalization. OLS + retransformation.

**B2 (Granger–Ramanathan).** First *combination with estimated weights*: NNLS of train-window
`ActualVariance` on the member forecasts (HAR-X, VIX, HAR — all available via
`context.Require(...).Forecast` applied to train rows). NNLS keeps weights non-negative, which
is the standard sanity constraint here and reuses the existing solver. No intercept in v1
(declare it; an intercept version is a different declared config). No further retransformation —
the members are calibrated and the weights are fitted on levels.

**B3 (regime weights).** B2 estimated separately on the two train-defined VIX halves (the
train-median split the fold runner already computes — reuse that exact rule, train-frozen). A
test row is routed by its own `LogPriorVix2` against the frozen median. Falls back to the
pooled B2 weights for a half with fewer than a floor of training rows (declare the floor).

**B4 (discounted-QLIKE weights).** Weights ∝ 1/(exponentially discounted train QLIKE of each
member), discount λ declared (start 0.99/day), normalized to sum to one. Closed form, no
regression, adapts after regime breaks. This is the online-learning idea (exponentially
weighted forecaster) in its simplest defensible form.

---

## 3. Machine learning, considered honestly

Two ML models have **already run**: the elastic net is rung 3 (registered, `negative`), and
gradient-boosted trees are rung 4 (exploratory, recorded dead end). "Consider ML" therefore
means: what, beyond those, has a mechanism that fits the two live diagnoses — power and regime —
rather than just a different function family on the same twelve features. The GBT result is
evidence that family-swapping alone adds nothing here.

**The binding constraint is sample size, not model capacity.** The full 2010–2023 sample is
~3,300 trading days and the registered feature set is ≤15 columns. At that scale, capacity is
cheap and information is scarce — which is why the candidates below are ranked by what *new
thing* they produce, not by expressiveness.

**Q1 — Quantile HAR-X (recommended; the one genuinely new ML direction).** Linear quantile
regression (pinball loss) on the HAR-X features at τ ∈ {0.25, 0.5, 0.75, 0.9}. Two reasons it
earns a slot ahead of any nonlinear model:
- *It serves the goal question directly.* "Sell, avoid, or size" needs the **distribution** of
  future RV, not its mean — sizing against the 75th/90th percentile is exactly the fair-value
  error bar the registration says the forecast exists to provide. No current model produces
  anything but a point.
- *It is rung-3-compatible.* Linear in the registered features, so it is not gated behind the
  rung-4 ban. Implementation: pinball loss is a linear program; a simple iteratively reweighted
  or subgradient solver over ≤15 features × ~3,000 rows is small, deterministic C#. Evaluation
  needs a declared quantile score (pinball) alongside QLIKE for the median — an evaluation
  extension to declare at registration, not smuggle in.

**Bayesian linear / Gaussian-process regression (second priority).** Same mean model, but with
predictive *intervals* — the other route to decision-grade uncertainty. Bayesian linear
regression (conjugate, closed form) is nearly free and deterministic; a full GP at n≈3,000 is
feasible but a real implementation project in C#. Start with Bayesian linear if Q1's intervals
prove too crude; treat a full GP as gated by demonstrated need.

**ML models worth exploring, ranked by mechanism.** Each entry names the diagnosis it
targets (power / regime / decision) and its gate status. "Statable mechanism" is the admission
ticket: a model with none is a family swap, and family swaps are priced.

| Model | Mechanism it targets | Gate status | Cost |
|---|---|---|---|
| **Q1 Quantile HAR-X** | decision — distribution, not point | rung 3, open | small |
| **TVP-HAR (Kalman)** | regime — coefficients drift over time | rung 3 (linear-Gaussian), open | small–medium |
| **Markov-switching HAR** | regime — discrete calm/stress states | borderline rung 3/4; declare | medium |
| **Bayesian linear (conjugate)** | decision — predictive intervals | rung 3, open | small |
| **Mixture of linear experts (2, VIX-gated)** | regime — soft version of B3 | rung 4 (nonlinear gate fn) | medium |
| **Gaussian process on HAR-X features** | decision + power — intervals, smooth nonlinearity | rung 4 | large |
| **Kernel ridge (RBF on 6 features)** | power — smooth nonlinearity, sample-efficient | rung 4 | medium |

- **TVP-HAR** is the sleeper. The registration's own mechanism #3 — "HAR under-reacts after vol
  spikes and over-persists after calm" — is literally the hypothesis that HAR's *coefficients
  move*. A Kalman filter over the HAR-X coefficients (random-walk state, declared state noise)
  is linear-Gaussian, closed-form, deterministic, and refits per fold like everything else. It
  is the regime diagnosis expressed as a model rather than as a weighting scheme.
- **Markov-switching HAR** (2-state, EM-fitted) states the same hypothesis discretely. EM needs
  seed discipline and a declared restart count; classify it honestly at registration time
  rather than arguing it into rung 3.
- The rung-4 rows (mixture, GP, kernel ridge) stay dev-only until a rung-3 variant passes the
  gate, and each must ship with its mechanism written down before its first dev run.

**ML ensembles worth exploring.** The combination tier in §2 is deliberately non-ML (fixed or
convex weights). These are the learned versions, in order of discipline required:

| Ensemble | Idea | Why it might beat §2 | Status |
|---|---|---|---|
| **Bagged elastic net** | refit the corrector on block-bootstrap resamples of train (registered seed), average coefficients | directly attacks the power diagnosis: DM failed on estimation noise, and bagging shrinks exactly that variance | rung 3, open — cheapest ML ensemble on offer |
| **Hedge / EWA forecaster** | multiplicative-weights over the member pool, declared learning rate | regret-bounded online adaptation; B4 is its one-step approximation | rung 3 (convex weights), open |
| **Bayesian model averaging** | weights from members' marginal likelihoods on train | principled uncertainty over *models*, feeds the decision layer | rung 3, open; needs the Bayesian linear members first |
| **Linear OOF stacking** | NNLS meta-weights fitted on *out-of-fold* member forecasts within train | the leakage-safe version of stacking; B2 with OOF discipline | rung 3, open |
| **Quantile ensemble** | per-quantile combination of members' quantile forecasts | decision-layer aggregation once Q1 exists | after Q1 |

The earlier rejection of "stacking with a learned meta-model" stands as written — it was about
*nonlinear* meta-learners on in-fold predictions. Linear stacking on out-of-fold forecasts is a
different object: convex, leakage-safe by construction, and one registration.

**Rejected, with reasons.** Random forests / deeper boosting / SVR: family swaps on existing
features; the GBT dead end priced them. Shallow MLP: rung 4 with no stated mechanism at this
sample size. Transformers: ~3,000 ordered observations. **LSTM / sequence models**: remain
rejected for the daily study — the HAR lags *are* the sequence structure that matters at this
horizon, and the archived repo's LSTM was a construction stub. Honest revisit trigger: the
registration's deferred v1.2 intraday targets (30/60-min RV), where sequence data is three
orders of magnitude richer; sequence models would get their own registration there.

**Training infrastructure for the ML tier.** Stay C#-native while models are this small: one
runtime, the existing determinism/test discipline, no serialization boundary. The roadmap's
deferred Python service becomes worth revisiting only if a gated rung-4 exploration with a
stated mechanism genuinely needs torch — that decision goes through `docs/FOLLOWUP.md`, not a
side door. If it happens: the Python side must reproduce the fold splits bit-for-bit from the
same config, seeds registered, and its forecasts re-scored by the C# harness so QLIKE/DM/
bootstrap arithmetic has exactly one implementation.

---

## 4. A4 term-structure features — the data project

The only candidate needing new data work, sequenced separately:

1. Build a short-dated model-free implied variance series (~9-day constant maturity) from
   `research.option_chain_quotes` via the existing `ImpliedVarianceSeriesBuilder` with a
   shorter-target `ConstantMaturityOptions` (the Phase 9 strip validated the 30-day version
   against VIX; the 9-day equivalent is the same machinery, different bracketing window).
2. Persist it alongside the run inputs; extend the dataset builder with slope
   (`log(IV9²) − log(IV30²)`) as a Tier-1-style feature, prior-close only.
3. Coverage honesty: chain history is shallow (weeks deep at ingestion start, growing as the
   recorder runs). The feature enters as an explicitly short-sample ablation — same framing the
   registration gave Tier-2 ES features — until depth accumulates.

---

## 5. Sequence for the next sessions

1. **Dev-run B1 + HARQ-X on real data** (task #3): app host up against live Postgres, POST the
   dev run with the exploratory catalog on, read margin / DM-scale / VIX-half split per
   candidate. Numbers land in the dev-run store, kept regardless.
2. **Implement A2, A3** (same plumb-through pattern as A1, half a day together) and
   **B2 → B3 → B4** (one shared combination pattern). Dev-run the batch.
3. **Implement Q1** (quantile HAR-X) with its declared pinball evaluation; dev-run. Then the
   two cheapest ML follow-ons if the numbers invite them: **bagged elastic net** (the direct
   attack on the DM failure) and **TVP-HAR** (the regime diagnosis as a model).
4. **Promotion decisions** per the registry: best-supported candidate becomes variant #2 at
   threshold 0.025, placebo first. Reserve discipline: five slots stay unspent until something
   earns them.
5. **A4 data project** in parallel with 2–4 as capacity allows.
6. **The decision layer**: whatever survives feeds the VRP-conditioning study's sizing arms —
   where "does a better estimate improve the decision" is actually answered.
