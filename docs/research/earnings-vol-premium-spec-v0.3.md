# Earnings Vol Premium Study — Spec v0.3

Status: hypothesis-ledger entry. SUPERSEDES v0.2 (closed record, retained).
Registers claims, definitions, pipeline, and kill criteria before any run.
Every run enters the trials registry and pays deflation.

## Changelog v0.2 -> v0.3

- F1 split into F1-A (event/distress item codes, unchanged from v0.2) and
  F1-B (insider & ownership flags: clustered buys, sells, blackout
  anomalies, active-13D).
- New conditioner family L1 (fragility): Amihud level/trend and a
  float-concentration proxy. New claim C6.
- 13D-ACTIVE registered as DUAL hypotheses (exclusion stratum vs
  amplified-RF conditioner) with a pre-registered adjudication rule.
- Side studies registered and sequenced: SS1 (13D announcement-day IV
  underpricing), SS2 (5(c) metaorder reconstruction, F2-gated).
- New section: Rejected Designs (RD1 stealth-window float study) —
  anti-relitigation log.

## 1. Falsifiable claims

- C1: Implied earnings moves exceed realized on average, our universe,
  our window. Fails -> program stops.
- C2: Defined-risk short-vol (condor/fly) entered pre-event, exited
  post-event, has positive expectancy NET of wings and pessimistic
  fills somewhere on the (k1, k2) grid.
- C3: Trailing 8-event RF/IM conditioning beats unconditional.
- C4: Expectancy vs wing distance has an interior optimum.
- C5: Filing-derived flags (F1-A, F1-B) identify events where the market
  under-adjusts: higher RF/IM and/or P(RF > IM) vs MATCHED unflagged.
  Dispersion conditioning only; direction never tested or used.
- C6: Fragility is under-priced into event IV: high/rising-Amihud and
  concentrated-float names show higher RF/IM than matched liquid names.

Mechanism: pre-event hedging demand + lottery buying pay the premium.
C5/C6 mechanism (inference under test): event IV marks in thin chains
are set from realized history and heuristics; filing-visible conditions
and liquidity fragility are under-reflected. Monetization channel is the
IV mark, not the stock price.

## 2. Definitions

Unchanged from v0.2: event, entry/exit snapshots, IM, RG, RF, crush,
structure in IM units, P&L from actual NBBO, filing flag windows
W in {30, 90}, RF/IM as the primary conditioner target, entry-anchored
measurement (features act on the post-entry residual).

Added:

- AMIHUD-LEVEL: mean(|daily return| / dollar volume), trailing 60
  trading days, log-scaled, as of entry.
- AMIHUD-TREND: trailing-20d Amihud / trailing-60d Amihud, as of entry.
- FLOAT-CONC: (insider holdings from Form 3/4/5 aggregates + 13F
  blockholder positions >= 5%) / shares outstanding (XBRL). Quarterly
  cadence, known lags (13F: 45 days). Explicitly a SLOW proxy; never
  treated as current.
- INS-CLUSTER-BUY: >= 2 distinct insiders with open-market P-code buys,
  ex-10b5-1, filed in the inter-earnings window since the prior print.
  Conditions the NEXT event. (10b5-1 checkbox exists only from 2023;
  earlier years use footnote inference — logged data limitation.)
- INS-SELL: analogous sell flag. Registered for symmetry; expected null
  (sales are diversification/tax noise). Let the data surprise us.
- INS-BLACKOUT-ANOM: any non-plan insider trade within 21 calendar days
  before the print. Rare by construction (blackout policies suppress
  the window); leans on the incidence gate.
- 13D-ACTIVE-TTM: any active Schedule 13D on the name filed within
  trailing 12 months as of entry.

## 3. Fill model — unchanged (the gate, not a check)

f in {0.25, 0.5, 1.0}; headline at 0.5; sign flip between 0.25 and 0.5
kills the claim; EV-vs-f curve mandatory; zero-bid exclusions counted.

## 4. Data sources & QA

Unchanged from v0.2: EDGAR bulk submissions (8-K Item 2.02) as the
events source; acceptanceDateTime BMO/AMC classification with the
two-signal price-inference QA and quarantine; dedup rules; CIK<->ticker
temporal join via securities master; companyfacts (shares outstanding,
fiscal calendars); compliance limits; forward calendar deferred;
tradability screen; survivorship-free universe; pre-registered
exclusions.

Added:

- Insider transactions: SEC structured insider-transaction datasets
  (quarterly bulk) + Form 4 stream from submissions JSON. No document
  parsing for F1-B.
- 13D/G presence and dates: submissions JSON (form type + CIK + date).
  Item 5(c) transaction CONTENT is exhibit parsing -> F2 only (SS2).
- 13F aggregates: blockholder positions for FLOAT-CONC. 45-day lag
  accepted; the proxy is slow by design.
- Float caveat (recorded): shares outstanding is published and current;
  FLOAT is a vendor estimate that updates FROM ownership filings and is
  stale during accumulation windows by construction. No feature may
  depend on knowing true float in real time.

## 5. Feature families

### F1-A — event/distress item codes (unchanged from v0.2)

1.01, 2.03, 2.05, 2.06, 4.02, 5.02, NT 10-K/Q, 3.01, and
COMPOSITE-DISTRESS = union{2.06, 4.02, NT, 3.01}. Incidence gate: <~2%
coverage marked underpowered, lean on composite. Dispersion only.

### F1-B — insider & ownership flags (new)

INS-CLUSTER-BUY, INS-SELL, INS-BLACKOUT-ANOM, 13D-ACTIVE-TTM as defined
above. Each (flag, W) is a registry trial. Blackout structure means
pre-print insider-activity windows are suppressed and 10b5-1-selected;
therefore F1-B buy/sell flags are measured INTER-EARNINGS and condition
the next print. Dispersion only.

13D-ACTIVE-TTM dual hypotheses, pre-registered adjudication:
- H-excl: flagged names carry non-earnings headline risk that
  contaminates event measurement -> exclusion stratum.
- H-cond: reduced effective float amplifies moves -> richness
  conditioner (higher RF/IM).
Adjudication: if flagged events show elevated contamination metrics
(quarantine rate, corporate-action-in-window rate, non-event gap rate)
-> H-excl wins regardless of RF/IM effect. Else if RF/IM effect
survives matched comparison + deflation -> H-cond. Else flag is
calendar-only metadata.

### L1 — fragility conditioners (new)

AMIHUD-LEVEL, AMIHUD-TREND, FLOAT-CONC. Enter S2 as a registered
family under C6. Computed from bars + ownership aggregates already
ingested; cause-agnostic by design (float, concentration, neglect —
the measured fragility is the tradable quantity, not its cause).

### F2 — deep extraction (gated, unchanged)

Gate: C1 AND C2 pass AND >= 1 F1 flag survives sect. 7. No code, no
exploratory notebooks until then.

## 6. Pipeline stages

- S0: universe + securities master + EDGAR ingestion (events, timing
  QA, F1-A/B flags, companyfacts, insider datasets, 13F aggregates).
- S1: per-event IM, RG, RF, crush. O(10k) rows.
- S2: descriptives + registered conditioner families: T1 (trailing
  RF/IM), F1-A, F1-B, L1. Controls: sector, size, front DTE. All flag
  and fragility tests are matched or regression-controlled — raw
  flagged-vs-unflagged is banned (flags and fragility cluster in small
  caps).
- S3: structure grid unchanged; strata re-runs for surviving flags and
  top/bottom fragility terciles.
- S4: portfolio; same-week clustering measured, not assumed.

## 7. Nulls, validity, kill criteria

N1 (unconditional), N2 (shuffled conditioner within matched strata,
applied per flag and per L1 measure), N3 (calendar-matched non-event
weeks). Regime splits: 2012-2017, 2018-2019, 2020, 2021-now. Deflated
Sharpe over the FULL registry: (k1, k2, f) grid x all (flag, W) x L1
measures. Kills unchanged from v0.2, plus:

- C6: no L1 measure survives matched comparison + deflation at f = 0.5
  -> fragility family closed; Amihud remains a descriptive statistic.
- C5 gate closure rule unchanged (F2 stays shut if F1 fully fails).

## 8. Side studies (registered, sequenced BEHIND the core program)

- SS1: 13D announcement-day IV underpricing — do thin chains underprice
  the documented filing-day repricing? Separate ledger entry; consumes
  the same ingestion; opens only after core S1-S3 complete.
- SS2: 5(c) metaorder reconstruction — rebuild accumulation from Item
  5(c) trade lists, measure drift vs pre-accumulation baseline.
  F2-gated (exhibit parsing). Low priority: n is dozens/year in band
  and the square-root impact law is already established on vastly
  larger institutional datasets. Educational value > alpha value.

## 9. Rejected designs (anti-relitigation log)

- RD1: "Stealth window" study — estimate new-float price dynamics from
  trades after accumulation ends and before the 13D files. REJECTED,
  reasons recorded:
  (a) Window boundaries unobservable ex ante; reconstructable only from
      5(c) ex post (F2), and often width ~zero — filers historically
      bought through the disclosure window (motivating the 2023 cut of
      the 13D deadline to 5 business days).
  (b) Contamination: documented pre-filing run-up, leakage, and copycat
      accumulation; window flow is abnormal by construction.
  (c) Inverted signature: Collin-Dufresne & Fos (2015) show measured
      liquidity/impact IMPROVES on activist accumulation days —
      patient informed flow supplies liquidity — so the window reveals
      distorted-good microstructure, not true new-float dynamics.
  (d) Power: 5-8% float shifts move impact coefficients second-order vs
      daily vol noise, over days-wide windows, dozens of events/year.
  Superior instrument exists for the float->dynamics question: lockup
  expirations and index float adjustments (known date, known size,
  pre-announced, hundreds of events) — logged under Deferred as a
  possible future event class, not part of this program.

## 10. Deferred

F2 (until gate), term-structure event vol, assignment/ex-div, intraday
entry timing, dispersion overlay, forward calendar vendor, lockup-
expiration event class as a separate future program.
