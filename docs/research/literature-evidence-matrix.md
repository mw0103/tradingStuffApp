# Literature evidence matrix

Bounded review supporting the research roadmap: 7 families, ~30 unique primary sources. Every
citation was verified against its DOI / journal page / SSRN / arXiv / official page at review time
(2026-07-31); contradictory and null evidence was searched for explicitly per family. Sources are
treated as suppliers of hypotheses, methodology, baselines, and failure warnings — never as proof
of edge. Published profitability gets an ex-ante 30–60% haircut (McLean-Pontiff).

## Synthesis by family

1. **RV measurement.** Daily realized variance from sparse intraday sampling makes latent variance
   observable; log-RV is near-Gaussian and persistent (ABDL 2003). Across 400+ estimators nothing
   significantly beats (subsampled) 5-minute RV, and for index futures plain 1-minute
   transaction-price RV is top-ranked (Liu-Patton-Sheppard 2015) — IBKR's 1-min bars are the
   frontier, not a compromise. Noise-robust tick estimators are unnecessary at this grain and
   harmful on conflated feeds. Signed semivariance/jump decompositions (Patton-Sheppard 2015) are
   candidate features, not defaults.
2. **RV forecasting.** HAR-RV (Corsi 2009) is the undisputed benchmark; Hansen-Lunde (2005 JAE)
   is the canonical null — apparent improvements usually die under data-snooping-robust tests.
   Intraday work requires deterministic time-of-day periodicity removal first
   (Andersen-Bollerslev 1997). ML gains at the daily horizon are small (2–5% MSE with RV lags
   only; 8–11% with extra predictors, led by implied-vol covariates —
   Christensen-Siggaard-Veliyev 2023) and contested once HAR is re-estimated on rolling windows
   (Audrino-Chassot 2025). Intraday horizons are where ML gains are largest (~14–25% QLIKE over
   HAR at 10–65 min; Zhang et al. 2024).
3. **Overnight/session.** Overnight variance must enter whole-day RV via estimated weights, never
   naive scaling or addition (Hansen-Lunde 2005 JFEC); the construction choice can flip model
   rankings (Ahoniemi-Lanne 2013). Options price close-to-close variance, so RTH-only RV is the
   wrong target for implied-vs-realized work. ES dominates S&P price discovery (Hasbrouck 2003):
   measure overnight index behavior from ES, never the stale 09:30 SPX print. Overnight and
   intraday dynamics are distinct states (Blanc et al. 2014) — model separately.
4. **Implied vs realized / variance risk premium.** The index implied-realized gap is persistent,
   large, negative, and primarily a *risk premium*, not forecast error (Carr-Wu 2009;
   Bakshi-Kapadia 2003; Christensen-Prabhala 1998); its width forecasts equity returns
   (Bollerslev-Tauchen-Zhou 2009). Pricing concentrates at the front of the term structure
   (Dew-Becker et al. 2017) — never extrapolate the 30-day premium along the curve. VIX² is a
   usable 30-day implied-variance leg now; self-built strips need
   CBOE-methodology-faithful construction (and, as of 2026-08-01, historical chains rather than
   the recorder — see the roadmap's second amendment).

   **The result the whole family rests on** (Demeterfi-Derman-Kamal-Zou 1999, row 18a;
   Britten-Jones & Neuberger 2000): risk-neutral expected variance is replicable *exactly* by a
   static strip of options plus a delta hedge, with **no volatility model assumed**. Itô on
   `d(log S)` leaves `dS/S − d(log S) = (σ²/2)dt`, so `E^Q[∫σ²dt] = 2[rT − E^Q log(S_T/S₀)]`; the
   log payoff is then statically replicated by Carr-Madan, whose second derivative
   `f″(K) = 1/K²` **is** the famous weighting. Two consequences the platform depends on:
   (a) the 1/K² weights are a derivation, not a convention, so the wings dominate and the
   zero-bid truncation rule is the most consequential implementation choice in the method — which
   is why row 18's "K₀ failure modes" are diagnosable at all; (b) the replication yields `E^Q`,
   while realized variance is drawn from `P`, so the implied-realized gap is the premium **by
   construction** rather than by empirical accident. Anyone expecting a physical forecast to track
   VIX has mistaken the measure, and a forecast that *did* track VIX would be evidence of an
   implied-vol proxy rather than of a good forecast.

   Refinement, load-bearing when differencing VIX² against forecast RV: the log-contract
   replication is exact under diffusion but carries a third-order jump error, so VIX² only
   approximates the 30-day variance-swap rate (Carr-Wu 2009, row 14). Do not attribute that bias
   to the premium.
5. **Surface representation.** Index surfaces are low-dimensional (2–3 factors: level, skew,
   curvature — Cont-da Fonseca 2002), which is what makes a sparse 6–10-node recording viable.
   At that node count fit ≤3–4 parameters per expiry slice (SSVI-style / quadratic in log-forward
   moneyness), never full 5-parameter SVI. Use log-forward-moneyness or delta coordinates; derive
   forwards from recorded synchronized put-call pairs, never external dividend/rate assumptions —
   a 1-minute timestamp mismatch materially distorts IVs and manufactures fake "arbitrage"
   (Wallmeier 2024).
6. **Evaluation and false discovery.** QLIKE (primary) and MSE-on-variance are the only safe
   losses under noisy proxies (Patton 2011); DM tests with HAC lags ≥ horizon−1
   (Diebold-Mariano 1995); family-wide SPA/reality-check on every grid sweep (Hansen 2005);
   stationary bootstrap for dependent series (Politis-Romano 1994); PBO/CSCV and the Deflated
   Sharpe Ratio from a complete trial registry (Bailey et al.); a t-hurdle near 3.0 for new
   strategy claims (Harvey-Liu-Zhu guidance).
7. **Costs and contradictions.** Quoted-spread costs of ~4–5% of premium/month plus margin
   dynamics historically reversed the sign of the best short-vol strategies
   (Santa-Clara-Saretto 2009). Effective spreads can sit well inside quoted
   (Muravyev-Pearson 2020) — but that evidence is from penny-quoted equity options; SPX ATM
   spreads are ~3–5% of premium (Broadie-Chernov-Johannes 2009), so the platform must measure its
   own effective spreads prospectively. Put-writing "anomalies" are statistically
   indistinguishable from calibrated jump-model nulls (Broadie et al. 2009; contested by
   Chambers et al. 2014 — a genuine unresolved disagreement, preserved rather than resolved).

## Evidence and applicability matrix

Reproducibility classes under the IBKR-only constraint: **now** (historical underlying data
suffices) · **prospective** (needs the option recording to accumulate) · **adaptation** (method
must change; what is lost is noted) · **external** (unavailable without another data source).

| # | Source | Status | Repro. | Platform use |
|---|---|---|---|---|
| 1 | Andersen, Bollerslev, Diebold & Labys 2003, Econometrica 71(2), DOI 10.1111/1468-0262.00418 | peer-reviewed | now | RV target definition; log-RV modeling |
| 2 | Liu, Patton & Sheppard 2015, J. Econometrics 187(1), DOI 10.1016/j.jeconom.2015.02.008 | peer-reviewed | adaptation (1s/tick lost — irrelevant) | Sampling policy: subsampled 5-min RV benchmark; 60%-session-day filter |
| 3 | Patton & Sheppard 2015, REStat 97(3), DOI 10.1162/REST_a_00503 | peer-reviewed | now | Semivariance/signed-jump features |
| 4 | Barndorff-Nielsen, Hansen, Lunde & Shephard 2008, Econometrica 76(6), DOI 10.3982/ECTA6495 | peer-reviewed | adaptation (theory only) | Justifies NOT using noise-robust estimators at 1-min grain |
| 5 | Hansen & Lunde 2005, J. Financial Econometrics 3(4), DOI 10.1093/jjfinec/nbi028 | peer-reviewed | now | Whole-day RV with estimated overnight weights |
| 6 | Corsi 2009, J. Financial Econometrics 7(2), DOI 10.1093/jjfinec/nbp001 | peer-reviewed | now | HAR-RV permanent baseline |
| 7 | Hansen & Lunde 2005, J. Applied Econometrics 20(7), DOI 10.1002/jae.800 | peer-reviewed | now | Null-result discipline; GARCH(1,1) reference |
| 8 | Andersen & Bollerslev 1997, J. Empirical Finance 4(2-3), DOI 10.1016/S0927-5398(97)00004-2 | peer-reviewed | now | Time-of-day periodicity deflation (train-only) |
| 9 | Christensen, Siggaard & Veliyev 2023, J. Financial Econometrics 21(5), DOI 10.1093/jjfinec/nbac020 | peer-reviewed | partial (IV covariates need recorder) | Honest ML-gain magnitudes; insanity filter |
| 10 | Zhang, Zhang, Cucuringu & Qian 2024, J. Financial Econometrics 22(2), DOI 10.1093/jjfinec/nbad005 | peer-reviewed | partial | Intraday-horizon residual models; QLIKE magnitudes |
| 11 | Hasbrouck 2003, J. Finance 58(6), DOI 10.1046/j.1540-6261.2003.00609.x | peer-reviewed | adaptation (1-min too coarse for info shares) | ES-as-canonical-carrier justification |
| 12 | Ahoniemi & Lanne 2013, Int. J. Forecasting 29(4), DOI 10.1016/j.ijforecast.2013.03.006 | peer-reviewed | now | Overnight-inclusion policy; ranking-flip warning |
| 13 | Blanc, Chicheportiche & Bouchaud 2014, Physica A 402, DOI 10.1016/j.physa.2014.01.047 | peer-reviewed | now (approx.) | Overnight vs intraday as separate states |
| 14 | Carr & Wu 2009, RFS 22(3), DOI 10.1093/rfs/hhn038 | peer-reviewed | adaptation (VIX² at 30-day node) | VRP magnitude; synthetic-swap method later |
| 15 | Bollerslev, Tauchen & Zhou 2009, RFS 22(11), DOI 10.1093/rfs/hhp008 | peer-reviewed | now | VRP = VIX² − forecast RV, feasible today |
| 16 | Christensen & Prabhala 1998, JFE 50(2), DOI 10.1016/S0304-405X(98)00034-8 | peer-reviewed | now | Non-overlapping IV-vs-RV regressions |
| 17 | Bakshi & Kapadia 2003, RFS 16(2), DOI 10.1093/rfs/hhg002 | peer-reviewed | prospective | Delta-hedged-gain design for the variance-gap study |
| 18 | Cboe Volatility Index Mathematics Methodology 2024 (cdn.cboe.com governance doc) | exchange research | consume now / reproduce partially | Strip construction, zero-bid rules, K₀ failure modes |
| 18a | Demeterfi, Derman, Kamal & Zou 1999, *More Than You Ever Wanted To Know About Volatility Swaps*, Goldman Sachs Quantitative Strategies Research Notes (also J. Derivatives 6(4), 1999, DOI 10.3905/jod.1999.319129) | practitioner research note (the JoD version is peer-reviewed) | **now** — the replication is arithmetic over any chain, and `TradingStuff.Volatility/ImpliedVolatility/ModelFreeVariance.cs` already implements the integral | The derivation *underneath* row 18. Cboe's methodology gives the formula to compute; this gives the reason it is that formula, which is what makes its failure modes diagnosable instead of magic |
| 19 | Gatheral & Jacquier 2014, Quantitative Finance 14(1), DOI 10.1080/14697688.2013.819986 | peer-reviewed | now (live chain pulls) | Arbitrage-aware slice fitting; SSVI bounds |
| 20 | Cont & da Fonseca 2002, Quantitative Finance 2(1), DOI 10.1088/1469-7688/2/1/304 | peer-reviewed | prospective (~6 mo of surface days) | Surface factor structure; node-count justification |
| 21 | Fengler 2009, Quantitative Finance 9(4), DOI 10.1080/14697680802595585 | peer-reviewed | now (method) | Arbitrage-free smoothing of noisy quotes |
| 22 | Daglish, Hull & Suo 2007, Quantitative Finance 7(5), DOI 10.1080/14697680601087883 | peer-reviewed | adaptation | Sticky-rule baselines for surface dynamics |
| 23 | Wallmeier 2024, J. Futures Markets 44(5), DOI 10.1002/fut.22495 | peer-reviewed | adaptation (audit battery) | Synchronization/parity audit of own recordings |
| 24 | Patton 2011, J. Econometrics 160(1), DOI 10.1016/j.jeconom.2010.03.034 | peer-reviewed | now | QLIKE/MSE-only loss policy |
| 25 | Diebold & Mariano 1995, JBES 13(3), DOI 10.1080/07350015.1995.10524599 | peer-reviewed | now | Pairwise forecast tests w/ HAC |
| 26 | Bailey, Borwein, López de Prado & Zhu 2017, J. Computational Finance 20(4), DOI 10.21314/JCF.2016.322 | peer-reviewed | now | PBO/CSCV gate on trial matrices |
| 27 | Bailey & López de Prado 2014, J. Portfolio Management 40(5), DOI 10.3905/jpm.2014.40.5.094 | peer-reviewed | now | Deflated Sharpe from the trial registry |
| 28 | Hansen 2005, JBES 23(4), DOI 10.1198/073500105000000063 | peer-reviewed | now | SPA test on every grid sweep |
| 29 | Santa-Clara & Saretto 2009, J. Financial Markets 12(3), DOI 10.1016/j.finmar.2009.01.002 | peer-reviewed | prospective | Bid/ask + margin-path P&L simulation |
| 30 | Muravyev & Pearson 2020, RFS 33(11), DOI 10.1093/rfs/hhaa010 | peer-reviewed | adaptation (measure own spreads) | Cost-scenario bounds |
| 31 | Broadie, Chernov & Johannes 2009, RFS 22(11), DOI 10.1093/rfs/hhp032 | peer-reviewed | now (null machinery on underlying data) | Jump-model finite-sample nulls for strategy stats |
| 32 | McLean & Pontiff 2016, J. Finance 71(1), DOI 10.1111/jofi.12365 | peer-reviewed | external (used as prior) | 30–60% ex-ante haircut on published edges |

Contradiction sources logged during review (not full entries): Canina-Figlewski 1993 RFS (IV
uninformative — overturned by non-overlapping designs); Dew-Becker, Giglio, Le & Rodriguez 2017
JFE (VRP priced only at the front end); Bekaert-Hoerova 2014 (VRP estimate is model-dependent);
Audrino-Chassot 2025 IJF (rolling-window HAR matches ML at daily horizons); Chambers, Foy,
Liebner & Lu 2014 RFS (vs Broadie et al.); Martini-Mingone 2022 SIAM JFM (complete SVI
no-butterfly domain); Chen-Yu-Zivot 2012 IJF (overnight null for single names); Sévi 2014 EJOR
(jump-feature OOS decay); Diebold 2015 JBES (DM-test use and abuse); Hansen-Huang-Shek 2012 JAE
(Realized GARCH).

## Hypotheses by data requirement

**Reproducible now (IBKR historical underlying data):** HAR baseline + residual predictability on
SPX/SPY/ES; whole-day RV with estimated overnight weights; overnight-ES information content;
VRP(t) = VIX² − forecast RV (level, dynamics, return-forecast replication); time-of-day
periodicity; semivariance/jump features; jump-model finite-sample nulls.

**Requires prospective SPX option recording (months):** surface factor structure; delta-hedged
gains vs VRP state; self-built model-free implied-variance strips vs VIX; sticky-rule surface
baselines; GTH→RTH surface information; term-structure/skew studies; own effective-spread
measurement; margin-path P&L realism.

**Deferred — requires external data:** any full historical option-chain work (OptionMetrics /
Cboe DataShop class); deep cross-sectional anomaly work (CRSP/Compustat); historical
index-constituent screens; true-tick lead-lag/information-share estimation.

## Methodological controls promoted to platform requirements

1. Trial registry from day one: every run/variant/parameter/discard logged immutably; full T×N
   loss/P&L matrices retained for PBO and DSR.
2. QLIKE primary + MSE-on-variance secondary against the subsampled-5-min-RV proxy; forbidden
   losses (MSE-on-SD, MAE, proportional) rejected in code.
3. DM + HAC (lags ≥ horizon−1) pairwise; SPA/MCS for family sweeps; stationary bootstrap
   everywhere; IID resampling forbidden on dependent series.
4. Non-overlapping (or overlap-corrected) windows in every implied-vs-realized regression.
5. Variance target (whole-day vs RTH-only) declared per experiment; the overnight leg measured
   from ES/SPY, never the SPX 09:30 print; weekend/holiday overnights not exchangeable.
6. Time-of-day periodicity deflated with train-only jump-robust profiles, separate RTH/overnight.
7. Surface snapshots: joint underlying timestamping; forward from recorded put-call parity;
   log-forward-moneyness coordinates; ≤3–4 params per slice; bid-ask-band-constrained fits;
   static-arbitrage checks stored as data-quality flags; quote-age filter; no proxy splicing.
8. Cost scenarios on every economic result: quoted bid/ask baseline, mid + 25–50% of half-spread
   central, own-measured effective spread when available; margin-path simulation with forced
   liquidation; no midpoint-only headline numbers.
9. Ex-ante 30–60% haircut on published-strategy expectations; t ≈ 3.0 hurdle for new-edge claims;
   effective (not nominal) sample sizes for overlapping horizons; no short-vol conclusion without
   a stress episode in sample.
10. Every stored series versioned: estimator config, normalization version, methodology-regime
    stamps (e.g. VIX methodology changes over its history).

## Recommended baseline and evaluation suites

**Baselines:** unconditional time-of-day mean → rolling mean → EWMA (RiskMetrics λ) →
**HAR-RV / Log-HAR (permanent benchmark, rolling re-estimation)** → GARCH(1,1) daily reference →
regularized linear (elastic net) on the residual target → only then GBT / small NN at intraday
horizons. Store realized quarticity now to enable HARQ-style challengers later.

**Evaluation:** QLIKE + MSE-on-variance; DM w/ HAC; SPA/MCS per sweep; stationary-bootstrap CIs;
regime slices (VIX terciles, RV deciles, crisis-in/out); quantile calibration + interval coverage
for distributional forecasts; residual autocorrelation; walk-forward with multiple splits and
crisis-in-train vs crisis-in-test sensitivity; PBO + DSR from the registry; economic translation
(net expectancy under the cost ladder) before any deployment decision.
