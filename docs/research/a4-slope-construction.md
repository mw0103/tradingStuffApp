# A4 slope construction — frozen 2026-08-02, before any series value

This document fixes every construction choice for the A4 term-structure series BEFORE the first
value is computed. It was drafted from the data-readiness gate result and its three caveats
(`a4-data-readiness.md`), the existing `TradingStuff.Volatility.ImpliedVolatility` conventions,
and a-priori reasoning only — no chain data was inspected while writing it, and no candidate
constructions were computed and compared. Amendments follow the rule in §10; the document is
never edited to fit results.

## 1. The two series and the slope

Two daily constant-maturity model-free implied variance series from our own SPX/SPXW strip:

- **σ²₉(t)** — 9-calendar-day constant maturity (the VIX9D-equivalent point).
- **σ²₃₀(t)** — 30-calendar-day constant maturity (the VIX-equivalent point).

The slope is the **single primary definition**, fixed now:

> **S(t) = ln( σ₉(t) / σ₃₀(t) )**, where σ = √(annualized constant-maturity variance).
> S > 0 means the short-dated point sits above the 30-day point — an inverted term structure.

Rationale, recorded a priori: the A4 hypothesis is about *inversion/steepness as a stress
signal*, and a ratio is scale-invariant — two vol points of inversion at VIX 15 and at VIX 60
are different animals, while a log-ratio compares like with like and is symmetric around zero.
No alternate slope definitions (differences, curvatures, other maturity pairs) are computed
before the primary's backtest is adjudicated; any later alternate is a new declared variant
with its own registration, not a refinement of this one.

## 2. Session-calendar keying (gate caveat 1)

Series dates come from **our SPX session calendar** (`ISessionClock` / `research.bars` SPX
sessions), never from quote presence. A date with Cboe global-trading-hours quotes but no SPX
session (the 2023-01-16 MLK case in the gate) is not a series date. Every session date in the
sample window gets a row — usable or unusable-with-reason (§8) — so gaps stay visible.

## 3. Snapshot

**15:30:00 ET, NBBO.** The same snapshot the readiness gate audited, so its availability
conclusions carry to this construction without re-verification. It sits inside liquid regular
trading hours and away from the closing-auction window. Reconstruction at the snapshot uses
only rows carrying that snapshot interval's timestamp (gate caveat 3) — no forward- or
back-filling of quotes from other intervals.

## 4. Eligible expirations

- **Roots: SPXW and SPX, both.** A slice is keyed by (root, settlement moment). If two slices
  share an identical settlement moment, the one with more two-sided strikes at the snapshot is
  kept and the other discarded.
- **Settlement moments**: AM-settled contracts (standard SPX) settle at 09:30 ET on the
  settlement date; PM-settled contracts (SPXW, PM-settled SPX) at 16:00 ET on the expiration
  date. Time to expiry is measured in **minutes from the snapshot to the settlement moment**
  (the existing `ConstantMaturityVariance` minutes convention).
- **Floor**: slices with fewer than **1,440 minutes (1 calendar day)** from snapshot to
  settlement are ineligible. Matches the VIX9D convention; the final day's quotes are dominated
  by settlement mechanics, not variance expectation.
- No maximum. Eligibility is not capped by DTE; the bracketing rule (§5) selects what is used.

## 5. Maturity bracketing rule

For each target τ ∈ {9, 30} calendar days, define the target moment as
**snapshot + τ × 1,440 minutes**. Then:

- **Near leg** = the eligible slice with the latest settlement moment **at or before** the
  target moment.
- **Far leg** = the eligible slice with the earliest settlement moment **after** the target
  moment.
- Both legs must exist and be usable (§6): **no extrapolation, ever**
  (`AllowExtrapolation = false`). A date where either point cannot be bracketed is
  unusable-with-reason for that date.
- **No bracket-width cap, and specifically NOT the VIX 23–37-day window** in
  `ConstantMaturityOptions`' defaults. Recorded reason: the gate found 32/32 sampled sessions
  bracket both points with plain bracketing, while the early era (~4–7 maturities ≤45d in
  2012–2016) would violate a fixed window on some dates — a width cap would fabricate exactly
  the holes the gate exists to find, and choosing a cap later would be a tuning knob. Achieved
  near/far DTEs and bracket width are recorded per date as diagnostics, not filters.

## 6. Per-expiration variance

The existing `ModelFreeVariance.Compute` (CBOE VIX methodology), with its defaults frozen as
the construction's parameters:

- Forward from put–call parity at the two-sided strike pair with minimum |C − P|; K₀ = largest
  strike at or below the forward.
- Out-of-the-money strip: puts below K₀, calls above, averaged at K₀; walking outward stops
  after **2 consecutive strikes without a two-sided NBBO** at the snapshot.
- **The strike set for a date is derived from that date's snapshot quote presence** — never
  from the all-time strike listing, which is not survivorship-safe (gate caveat 3).
- Minimum 5 strikes per leg; truncation flags (`TruncatedLowSide`/`TruncatedHighSide`) recorded;
  a leg failing `IsUsable` fails its bracket, making that point unusable-with-reason.

## 7. Interpolation, underlying, rates

- **Interpolation space: total variance**, weights in minutes, then rescaled to the target
  window and annualized — the existing `ConstantMaturityVariance` implementation, which is the
  arbitrage-consistent choice (variance is additive in time; volatility is not).
- **Underlying** (diagnostics and sanity checks only — the forward comes from parity, and the
  vendor's index history is behind a higher tier, gate caveat 2): the `research.bars` SPX
  1-minute bar at or last before 15:30 ET on the series date.
- **Risk-free rate**: the **4-week Treasury bill discount rate (FRED series DTB4WK)**,
  converted to a continuously compounded rate, carried forward across non-publication days
  (`HistoricalRiskFreeRate`). Declared now because the flat 0.45% used in the Phase 9 VIX
  cross-check is explicitly inadequate for a 2012–2023 sample (the short rate traverses
  0→5%→…), and the 4-week tenor is the closest published match to the 9–30-day horizons.
  Sensitivity at these maturities is small; the point of declaring it is that it never gets
  chosen twice.

## 8. Usability, gaps, and fetch failures

- A series date is **usable** iff both constant-maturity points interpolate from usable legs.
  Unusable dates are emitted with a reason attached (the `ImpliedVarianceDay` pattern), never
  dropped — absence renders as absence.
- **Fetch failure is not absence** (the gate's own error mode): a date with any fetch-failed
  leg is *unresolved*, distinct from *absent*, and is retried per the ingestion job's retry
  accounting before it may be classified. No date enters the backtest sample as absent while
  unresolved.

## 9. Timing for downstream use

The value stamped to session date *t* is computable at 15:30 ET on *t* and may inform
forecasts or decisions made **at or after the close of session *t*** — the same prior-close
discipline as every other feature. In HAR-X feature terms: S(t−1) and its lags are the inputs
to a forecast formed at the close of *t−1* for horizons beginning *t*.

## 10. What this document does not do, and how it changes

It fixes the *construction* only. Thresholds, buckets, signal rules, feature lags beyond §9,
and evaluation criteria belong to the A4 trial registration, not here. If computing the series
reveals the construction is *infeasible as written* (not merely inconvenient), the amendment is
appended here with date and reason **before** any value produced under the amended rule is
examined. Results-motivated edits are prohibited; the failure mode this rule exists to prevent
is a construction quietly reshaped until the slope "works".
