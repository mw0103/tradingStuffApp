# Volatility forecast residual study — pre-registration

Study 1 of the research backlog (`docs/plans/ibkr-edge-research-roadmap.md`). This document is the
pre-registration: hypothesis, features, labels, validation design, and gates are fixed here BEFORE
final testing. Changes after data has been seen require a new registered study, not an edit.

## Hypothesis

One underlying (SPX). One decision timestamp: **09:25 ET** (features only from bars fully closed
by 09:20 — a 5-minute settlement buffer). One label: RTH session realized variance, 09:30–16:00,
materializing 16:05, usable for training from the next day. Baseline: HAR-RV (log form), refit
per fold on train data only.

- **H0:** conditional on the HAR baseline forecast, the pre-registered feature set carries no
  incremental out-of-sample predictive information (corrected-model expected QLIKE ≥ HAR's).
- **H1:** the corrected model reduces pooled OOS mean QLIKE by ≥ 2% vs HAR, Diebold-Mariano
  p < 0.05 (HAC), improvement positive in ≥ 2 of 3 walk-forward folds AND in both VIX halves.

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

**Falsification ("no edge"):** pooled QLIKE gain < 1%, or DM p ≥ 0.05, or negative sign in ≥ 2
folds, or > 50% of the gain from a single calendar year ⇒ study declared negative, frozen,
registry-recorded with all variants. **Statistical vs economic:** 1–2% QLIKE gain is real but
likely unactionable. Economically meaningful: ≥ 5% pooled QLIKE gain AND ≥ 0.5 annualized-vol-point
RMSE reduction in the top VIX tercile. Between 2–5%: validated, flagged "insufficient economic
magnitude," used only to tighten the variance-gap study's uncertainty band.

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

0. Unconditional train-window mean of log RV →
1. rolling 22-day mean; EWMA (λ = 0.94) →
2. **HAR-RV (log form, refit per fold) — the gate baseline** (GARCH(1,1) reported as a daily
   reference row, never a gate) →
3. elastic net on the residual target (α ∈ {0, 0.5, 1}; λ by inner blocked 5-fold CV on train) →
4. gradient-boosted trees (depth ≤ 3, ≤ 200 trees, min-child ≥ 50) — **only if rung 3 passes**
   the H1 gate. Running GBT after a linear failure is the canonical false-discovery move and is
   banned; rung 4 must beat rung 3 by a further ≥ 2% QLIKE to justify itself.

## Validation design

- **Spans:** SPX 1-min assumed 2010→ (probe toward the 2004 head during backfill); VIX tier
  ~2016→; ES tier 2023-08→.
- **Walk-forward (expanding origin):** F1 train 2010–16 / val 2017 / test 2018–19; F2 train
  2010–18 / val 2019 / test 2020–21 (COVID lands in TEST, deliberately); F3 train 2010–20 /
  val 2021 / test 2022–23.
- **Untouched holdout: 2024-01 → 2026-07.** Opened exactly once, after the full pipeline config
  hash is committed to the trial registry; a single scripted run; the result stands; never reused
  by successor studies of this hypothesis family.
- **Unit:** the trading day, never the bar. **Losses:** QLIKE primary; MSE of log RV secondary
  (reported, never gated on); other losses forbidden in code.
- **Tests:** DM on daily QLIKE differentials with Newey-West HAC (lag 5; lag 9 + non-overlapping
  subsample for the 5-day label); stationary block bootstrap by day (mean block 20 days, 10k
  resamples); concentration check (no single year > 50% of gain).
- **Regimes:** VIX terciles with TRAIN-defined thresholds; pre/post 2020-02; per-year table.
- **Trial registry:** every executed variant (feature-set hash, model family, hyperparameters,
  fold config, seed, git sha) appended immutably before results are viewed. Gate p-threshold
  deflated to 0.05/N over N registered variants; N > 5 additionally triggers an SPA test of the
  family vs HAR. **Hard cap: 10 registered variants before the holdout opens.** Exhausting the
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
