# A4 data-readiness gate — result

Executed 2026-08-02 per the narrow mandate: **availability only**. No crash associations were
calculated, no cutoffs inspected, no maturities chosen by results, no slope definitions compared.
Artifacts: `scratchpad/a4audit/` (sweep log, coverage.json, re-verify log); scripts are in the
session scratchpad and reproducible against the local ThetaData terminal (v3, port 25503).

## Classification: **Outcome 1 — sufficient historical depth (2012–2023)**

Freeze the exact slope construction next, then proceed with A4. The construction freeze is a
separate, declared document — it was deliberately NOT drafted while looking at this audit's
detail tables.

## What was measured

38 sampled dates: 2 per year 2012–2023 plus the known high-loss windows (2015-08-21/24,
2018-02-02/05, 2018-10-10, 2018-12-19/24, 2020-02-24/27, 2020-03-09/16/18, 2022-06-13,
2022-09-13). Per date: expirations with two-sided NBBO at the 15:30 snapshot (SPXW + SPX),
DTE range, whether maturities bracket both the ~9d and ~30d constant-maturity points, and
two-sided strike counts on the nearest leg.

## Results

- **32 valid session dates** (6 sampled dates were MLK holidays — non-sessions, excluded).
- After the error-aware re-verify: **32 of 32 bracket both 9d and 30d** with dense two-sided
  strike coverage (71–458 strikes on the ≤9d leg; typically 150–400 from 2014 on).
- **Every high-loss window is fully covered**, including the days themselves: Volmageddon
  (2018-02-05: 14 maturities), peak COVID (2020-03-16/18: 381 two-sided ≤9d strikes each — the
  densest dates in the sample), 2015-08-24 (full ladder DTE 4→39).
- Maturity density grows over the era: ~4–7 maturities ≤45d in 2012–2016, ~14–28 from 2017 (the
  Mon/Wed/Fri weekly listings). The 9d bracket exists in every year sampled.
- The only TRUE vendor absence found: one far leg (SPXW 2013-08-23, DTE 39) on one date —
  irrelevant, its neighbours cover the 30d point.

## The audit's own error mode, and why the re-verify existed

The first sweep reported four dates with missing near or far legs (2013-07-15, 2015-07-15,
2015-08-21, 2015-08-24). All four were **fetch timeouts, not data holes**: the sweep's 45s
per-request cap preferentially killed the largest payloads, which are exactly the nearest, most
heavily quoted expiries. The re-verify (120s, 3 retries, fetch-failure explicitly distinguished
from zero-row absence) found dense ladders on all four — e.g. 2015-08-21 had a DTE-7 weekly
with 314 two-sided strikes the sweep called absent. **Lesson carried into the ingestion job
design: per-expiry requests need generous timeouts, retry accounting, and an explicit
fetch-failed state — a timeout recorded as absence would fabricate exactly the kind of hole
this gate exists to find.**

## Caveats that bind the construction (recorded now, before it is frozen)

1. **Calendar keying.** 2023-01-16 (MLK, market closed, no SPX session) shows 28 maturities of
   two-sided option quotes — Cboe global trading hours. Decision dates must come from OUR
   session calendar (`ISessionClock` / `research.bars`), never from quote presence, or holiday
   GTH quotes fabricate decision dates with no SPX close.
2. **Underlying source.** The vendor's index-price history is behind a higher subscription
   tier; the underlying at the snapshot comes from our own `research.bars` SPX 1-minute series
   (2009-12→), which is also the better choice for timestamp alignment.
3. **Snapshot semantics.** Quotes are NBBO rows with their own timestamps at fixed intervals;
   reconstruction at a snapshot uses only that timestamp's rows. Strike sets for a date must be
   derived from that date's quote presence (two-sided at the snapshot), not from the all-time
   strike listing, which is not survivorship-safe.

## Recorder state and remediation

`research.option_chain_quotes` is EMPTY — the ingestion pipeline (migration 019, coordinator,
`POST /research/options/jobs`) is complete but **no job was ever created**. Remediation is an
API call once the app host runs, not a code change. Urgency is moderate, not emergency: the
vendor IS the historical archive, so un-recorded days remain fetchable later; the prospective
risk is vendor dependence (subscription lapse, retention policy), which seeded provenance-
controlled capture removes. Seeding the 2012→ SPXW/SPX chain history also serves task #12
(structure-level hedging backtests) with the same pull.

## What happens next (in order)

1. Freeze the A4 slope construction in its own document: exact maturity bracketing rule,
   interpolation space, strike-range rule, snapshot time, and the session-calendar keying —
   all before any series value is computed.
2. Seed the chain ingestion jobs (2012→, SPXW + SPX) so the data is locally held.
3. Build the two constant-maturity series and the slope, prior-close only.
4. The A4 backtest under the registered trial discipline: hypothesis already stated on task
   #11 — inverted/steep short-dated implied precedes the high-bucket crash windows that
   realized-measure forecasts cannot flag.
