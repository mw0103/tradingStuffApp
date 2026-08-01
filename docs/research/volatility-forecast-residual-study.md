# Volatility forecast residual study — pre-registration

Study 1 of the research backlog (`docs/plans/ibkr-edge-research-roadmap.md`). This document is the
pre-registration: hypothesis, features, labels, validation design, and gates are fixed here BEFORE
final testing. Changes after data has been seen require a new registered study, not an edit.

## Hypothesis

One underlying (SPX). One decision timestamp: **09:25 ET** (features only from bars fully closed
by 09:20 — a 5-minute settlement buffer). One label: RTH session realized variance, 09:30–16:00,
materializing 16:05, usable for training from the next day. Baseline: HAR-RV (log form), refit
per fold on train data only.

- **H0:** conditional on the **HAR-X** baseline forecast (HAR terms *plus the same Tier-1 VIX
  inputs the candidate receives* — see the ladder), the pre-registered feature set carries no
  incremental out-of-sample predictive information.
- **H1 — primary attribution gate:** the corrected model reduces pooled OOS mean normalized QLIKE
  by ≥ 2% **vs HAR-X**, Diebold-Mariano p < 0.05 (HAC), improvement positive in ≥ 2 of 3
  walk-forward folds AND in both VIX halves, **and** a one-sided block-bootstrap CI on the mean
  loss advantage excluding zero. The 2 % supplies economic materiality; the CI addresses sampling
  noise. Neither alone is sufficient.
- **H2 — secondary dominance gate (reported, not the scientific claim):** the candidate versus the
  lowest-pooled-loss standalone baseline, plus a Model Confidence Set over the candidate and every
  registered baseline. See "Claim language" below.

> **Amended 2026-08-01. H1 previously read "≥ 2% vs HAR", and that gate was passable without the
> architecture contributing anything.** The candidate receives HAR terms *and* VIX-derived inputs.
> Beating a HAR that never sees VIX, or a VIX-only model that never sees realized history,
> establishes only that the union of two free public signals beats each half. Concretely, with
> HAR indexed to 100: VIX-only 96, HEAVY-RM 95, **candidate 92**, and a plain linear model on the
> candidate's own inputs at **90**. The old gate passes that candidate. It should fail — it is
> beaten by a simple model with the same information.
>
> Hence HAR-X, information-matched by construction, as the primary comparator. This separates
> three claims the previous wording ran together: **incremental information** (do the new inputs
> add content? — answered by the ablations), **modeling improvement** (does this mapping of
> existing inputs forecast better? — answered by H1), and **trading alpha** (does it earn money
> net of costs? — answered only by the variance-gap study and the execution simulator, never
> here).

**Normalized QLIKE, frozen.** All "% improvement" figures in this document mean

```
L_Q(y, ŷ) = y/ŷ − log(y/ŷ) − 1          improvement = 100 × (1 − L̄_candidate / L̄_HAR-X)
```

This definition is load-bearing, not pedantry. QLIKE has several formulations differing by
model-invariant additive terms: rankings and loss *differences* are identical under all of them,
but *percentage* reductions are not — so "a 2 % QLIKE gain" was ambiguous as previously written,
and the ambiguity was large enough to decide the gate. Frozen here.

**Claim language, fixed in advance** so a result cannot be narrated upward after the fact:

| Condition | The only sentence permitted |
|---|---|
| Clears HAR | "improves on HAR" |
| Clears HAR-X | "adds value beyond the same information in a simple model" |
| Lowest mean QLIKE in the ladder | "best observed model" |
| MCS eliminates all registered baselines | "statistically dominates the registered ladder" |

Failing H1 while beating HAR is **not** "no edge" — it is "does not outperform a simple
information-matched model", which is a different and weaker negative. Record it as such.

**Mechanism, in decreasing prior order:** (1) calendar/periodicity mis-modeling — HAR ignores
day-of-week/opex/event clustering; a baseline mis-specification no arbitrage force removes;
(2) overnight/global information — overnight ES range and gap contain realized information a
close-to-close daily model structurally cannot see; (3) response asymmetry — HAR under-reacts
after vol spikes and over-persists after calm, the one channel that would make the corrected
forecast economically valuable for the implied-vs-forecast study.

**Honest framing:** H1 is a claim about beating *a model*, not the market. Economic role:
(a) the fair-value leg — with trusted error bars — of the implied-vs-forecast variance study;
(b) a falsifiable statistical claim in its own right. If this study fails, the variance-gap study
degrades to implied-vs-uncorrected-HAR with wider no-trade bands. That fallback is stated here to
remove any incentive to torture this study into significance.

**Falsification ("no edge"):** pooled normalized-QLIKE gain **vs HAR-X** < 1%, or DM p ≥ 0.05, or
negative sign in ≥ 2 folds, or > 50% of the gain from a single calendar year ⇒ study declared
negative, frozen, registry-recorded with all variants.

All thresholds here — 1%, 2%, 5% — are measured against **HAR-X** in the frozen normalized QLIKE.
They were written against HAR, which is a materially easier comparator, and the numbers were
deliberately *not* rescaled when the gate moved: rescaling them to preserve the old pass rate
would restore exactly the leniency the amendment exists to remove. The study is now harder to
pass, and that is the intent, not a side effect.

Note also what a *near-miss* means under the new gate. Beating HAR but failing HAR-X is **not**
"no edge" — it is "does not outperform a simple model with the same information", a weaker and
more specific negative that must be recorded in those words (see Claim language). Only the
conditions in the first paragraph declare the study negative.

**Statistical vs economic:** 1–2% is real but likely unactionable. Economically meaningful:
≥ 5% pooled gain AND ≥ 0.5 annualized-vol-point RMSE reduction in the top VIX tercile. Between
2–5%: validated, flagged "insufficient economic magnitude," used only to tighten the variance-gap
study's uncertainty band. **None of these establish trading alpha** — a forecasting edge under
QLIKE is a claim about forecasts. Alpha requires the variance-gap study and the execution
simulator to show the improvement survives spreads, fees, slippage, hedging and risk limits.

## Realized-variance estimator

Subsampled 5-minute RV from 1-min bars (mean over the five offset grids) — the
literature-supported frontier at this grain (Liu-Patton-Sheppard 2015; see the literature matrix).
SPX 1-min TRADES defines the RTH target. **ES supplies overnight features only, never the target**
(no roll-stitching artifacts in labels; ES returns computed within-contract only). The overnight
gap is excluded from the v1 target (session RV only; a close-to-close variant is defined but
deferred). The 09:30–09:35 SPX staleness window is flagged; a 09:35-start sensitivity variant
runs, and if conclusions differ the conservative one governs. All bars normalized to UTC +
exchange trading date at ingestion; DST-transition tests are mandatory.

## Targets

| Version | Target | Decision cutoff |
|---|---|---|
| v1 | Session RV 09:30–16:00 (non-overlapping, one per day) | 09:25 ET |
| v1.1 | Next-day session RV; 5-day mean session RV | 15:45 ET |
| v1.2 | 30/60-min intraday RV — only with a periodicity-aware baseline (train-only jump-robust time-of-day profile built INTO the baseline) | rolling |

## Features (≤ 15, all strictly pre-cutoff)

- **Tier-0 (2010+):** HAR triplet (log RV d−1; mean log RV d−5..d−1; mean log RV d−22..d−1);
  day-of-week one-hots; days-to-monthly-opex; mean of the last 5 signed baseline residuals
  (computed causally from the frozen per-fold baseline).
- **Tier-1 (~2016+, pending the VIX intraday-floor probe):** log VIX prior close; VIX 5-day
  change; SPX−VIX divergence interaction (z-scores from train-window moments only).
- **Tier-2 (2023-08+):** overnight ES log return 16:00→09:20 ET; overnight ES realized range;
  overnight ES subsampled 5-min RV; ES-implied gap vs prior SPX close (trailing-median basis,
  train-causal). Evaluated as an explicitly lower-power ablation on the 2023-08+ subsample.
- **Excluded:** macro-event calendars (not available IBKR-only). Accepted, stated v1 bias: event
  days will dominate residuals and the model may learn weak event proxies.

## Leakage rules

Labels enter the training store only after the horizon fully elapses plus a 1-session
finalization lag. Purge 5 trading days (10 for the 5-day label); embargo 5 (10) after each test
block. The feature builder computes all fold boundaries from config and hard-asserts
`source-bar UTC < decision timestamp` (tested). Half days are dropped as label days and skipped
for lag indexing. Days with > 5% missing label-window bars are dropped and logged; > 2
consecutive drops triggers a data-quality investigation before any model run.

## Baseline ladder and promotion gates

**Reference rows** — context, never a gate: unconditional train-window mean of log RV; rolling
22-day mean; EWMA (λ = 0.94); GARCH(1,1); **HAR-RV** (log, refit per fold).

HAR-RV is demoted from gate to reference deliberately. It remains the field's benchmark and every
result is still reported against it — but it is not information-matched to the candidate, so it
cannot answer the question H1 asks.

**Standalone baselines**, each estimated on train data only, each frozen to one specification:

- **B1 — calibrated VIX.** `RV̂ₜ₊₁ = exp(a + b·log qₜ)` with `qₜ = (VIXₜ/100)²`, (a, b) fitted by
  minimizing training-window QLIKE. Answers: *is the VIX level alone sufficient?*
  This is a **training-calibrated** forecast, **not** a variance-premium-debiased one. A static
  intercept and slope absorb average premium bias, 30-day-vs-1-day maturity mismatch,
  calendar-vs-session time, overnight and weekend variance, and annualization scaling all at once.
  The variance premium is time-varying and is not structurally identified or removed here. Naming
  it otherwise would imply an economic decomposition this is not.
- **B2 — HEAVY-RM(1,1).** `μₜ₊₁ = ω + α·RVₜ + β·μₜ`, positivity and stationarity constrained,
  one-step horizon, same folds and origin as everything else. Chosen over Realized GARCH for v1
  — not because it dominates, but because it is directly a realized-measure forecasting model,
  its original evaluation scores under QLIKE, and its gains are strongest at short horizons.
  **The lag order is fixed at (1,1) in advance and may not be selected after results are seen.**
  Log-linear Realized GARCH(1,1) is an optional later robustness row, relevant mainly if the
  return distribution or option-pricing implications come into scope.
- **B3 — HAR-X — THE PRIMARY GATE.** The registered HAR terms plus **exactly the Tier-1 VIX
  features the candidate receives** (log prior-close VIX², 5-day VIX change, SPX−VIX divergence),
  as a fixed OLS specification with a positivity constraint. Answers the question that actually
  matters: *does the correction architecture add anything beyond a simple model on the same
  information?*
  Scope note: HAR-X is information-matched on the **contested** dimension — VIX is the free public
  signal the candidate might merely be relaying. It deliberately excludes Tier-0 calendar and
  Tier-2 overnight-ES terms, because a fully information-matched linear model differs from the
  rung-3 elastic net only by regularization and residual-vs-direct targeting, collapsing the gate
  into a test of the candidate against itself. An all-tiers `HAR-X-full` may be reported as a
  robustness row **with that convergence stated**; it is not the gate.

**Candidate:**

3. elastic net on the residual target (α ∈ {0, 0.5, 1}; λ by inner blocked 5-fold CV on train) →
4. gradient-boosted trees (depth ≤ 3, ≤ 200 trees, min-child ≥ 50) — **only if rung 3 passes**
   the H1 gate. Running GBT after a linear failure is the canonical false-discovery move and is
   banned; rung 4 must beat rung 3 by a further ≥ 2% normalized QLIKE to justify itself.
5. *(optional, counts as exactly ONE registered variant)* a fixed ensemble — permitted only if
   member models, weighting formula, estimation window, weight constraints, missing-forecast
   handling, and whether weights are fixed/rolling/expanding are **all** frozen before evaluation.
   "Try five weighting schemes and report the best" is five variants regardless of what the
   registry is told.

**Retransformation, and why it is ladder-wide.** Every log-form model — HAR, HAR-X, B1, and the
candidate alike — is estimated by directly minimizing training-window QLIKE, not by OLS on log RV
followed by `exp()`. Exponentiating an OLS log fit targets something near the conditional *median*
of RV, while QLIKE is minimized by the conditional *mean*; the resulting handicap is systematic.
Applying the correction to only some rungs would change their ranking by construction — and would
do so in the direction that flatters the candidate, which is exactly why it is stated once, here,
and applied uniformly.

**Baselines do not consume challenger slots, but they are not free.** Each adds estimation
windows, lag orders, constraints, convergence and failure handling — researcher degrees of freedom
every bit as exploitable as a challenger's. Each is frozen to one specification before any result
is viewed, to the same standard as the ten registered variants, and one baseline winning on
sampling noise is an outcome the MCS in H2 exists to absorb.

## Validation design

- **Spans:** SPX 1-min assumed 2010→ (probe toward the 2004 head during backfill); VIX tier
  ~2016→; ES tier 2023-08→.
- **Walk-forward (expanding origin):** F1 train 2010–16 / val 2017 / test 2018–19; F2 train
  2010–18 / val 2019 / test 2020–21 (COVID lands in TEST, deliberately); F3 train 2010–20 /
  val 2021 / test 2022–23.
- **Untouched holdout: 2024-01 → 2026-07.** Opened exactly once, after the full pipeline config
  hash is committed to the trial registry; a single scripted run; the result stands; never reused
  by successor studies of this hypothesis family.
- **Unit:** the trading day, never the bar. **Losses:** normalized QLIKE primary, in the frozen
  form given under Hypothesis; MSE of log RV secondary (reported, never gated on); other losses
  forbidden in code.
- **Model Confidence Set** (Hansen-Lunde-Nason) over the candidate and every registered baseline,
  for H2. Selecting the lowest-loss baseline on the evaluation sample makes that comparator an
  order statistic rather than a fixed benchmark — conservative, power-reducing, and no longer
  described by unadjusted pairwise tests. The MCS is what handles that; "best-of" on its own is a
  promotion criterion, not an inferential one. Where "best" is used it means **the single baseline
  with the lowest pooled OOS mean loss** — never the best per date, never the best within each
  fold, never a post-hoc switching rule, all of which are unattainable oracles.
- **VIX source: Cboe's official daily index history**, not an IBKR index `TRADES` bar. An index
  has no trades; a reconstructed bar carries ambiguous timestamp and construction semantics that a
  registered baseline should not inherit. Day-*t* close forecasts day-*t+1* RV only. (v1 is already
  safe here — Tier-1 specifies *prior* close and the decision stamp is 09:25 for the same session
  — so this is a constraint on later horizons, not a correction to v1.)
- **Tests:** DM on daily QLIKE differentials with Newey-West HAC (lag 5; lag 9 + non-overlapping
  subsample for the 5-day label); stationary block bootstrap by day (mean block 20 days, 10k
  resamples); concentration check (no single year > 50% of gain).
- **Regimes:** VIX terciles with TRAIN-defined thresholds; pre/post 2020-02; per-year table.
- **Trial registry:** every executed variant (feature-set hash, model family, hyperparameters,
  fold config, seed, git sha) appended immutably before results are viewed. Gate p-threshold
  deflated to 0.05/N over N registered variants; N > 5 additionally triggers an SPA test of the
  family vs **HAR-X** (the H1 comparator, matching the gate it defends — it was "vs HAR" before
  the 2026-08-01 amendment). **Hard cap: 10 registered variants before the holdout opens.** Exhausting the
  cap = negative result; continuing requires data accumulated after re-registration.
- **Pipeline-validity placebo:** residuals shuffled within VIX-tercile blocks must produce ≈ 0
  improvement before any real result is believed.
- **Pre-registered ablations:** ± VIX features conditional on HAR lags (< 0.5% pooled QLIKE
  contribution ⇒ dropped); Tier-2 overnight block on the 2023-08+ subsample.

## Companion study: VrpConditioningStudy

Daily grain, shares this pipeline entirely: is `VIX²(t) − RV(t, t+21d)` predictably wider or
narrower given state (lagged RV, VIX level, recent SPX drawdown)? ~120 effective non-overlapping
windows over ~10y — bootstrap CIs only, no significance claims; produces conditioning knowledge
for the variance-gap study, not P&L. Falsified if the conditioning variable fails to produce a
monotone OOS spread in forward VRP across state terciles with a bootstrap CI excluding zero.
