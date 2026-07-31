# IBKR data capability matrix

What IBKR actually provides through this account, verified — not assumed. This document is the
authority for what research designs are feasible. Re-verify after any TWS upgrade (procedure at
the end); persist future probe results to `research.capability_probes` once Phase 0 lands.

Evidence classes: **RUNTIME** = verified by live read-only wire probe on 2026-07-31 (TWS paper,
API server version 187, live entitlements active, all data farms up); **DOC** = current official
IBKR documentation, not exercised; **UNKNOWN** = needs a Phase 0 probe. Probes placed no orders
and changed no settings or subscriptions.

## Per-instrument matrix

| Capability | SPX (IND) | SPX options (SPX/SPXW) | ES (FUT/CONTFUT) | VIX (IND) | SPY (STK) |
|---|---|---|---|---|---|
| Contract discovery | RUNTIME ✅ conId 416904 | RUNTIME ✅ via chain | RUNTIME ✅ | RUNTIME ✅ | RUNTIME ✅ |
| Option-chain discovery | n/a | RUNTIME ✅ `reqSecDefOptParams`: SPX class 20 expiries → 2031-12; SPXW 39 expiries incl. 0DTE → 2027-06 | n/a | not probed | not probed |
| Live top-of-book | RUNTIME ✅ last only (indices have no bid/ask) | RUNTIME ✅ bid/ask/last + sizes | RUNTIME ✅ full L1 | RUNTIME ✅ last only | RUNTIME ✅ full L1 |
| Live Greeks + IV | n/a | RUNTIME ✅ `tickOptionComputation` variants 10/11/12/13 (bid/ask/last/model): IV, delta, gamma, vega, theta, undPrice | n/a | n/a | n/a |
| Option volume / OI | n/a | RUNTIME ✅ generic ticks 100/101; OI cadence undocumented — treat as daily until measured | n/a | n/a | n/a |
| Historical 1-min bars | RUNTIME ✅ pulled at 2010 and 2021; head 2004-03-04; RTH only | RUNTIME ⚠️ works but **weeks deep** (see constraints) | RUNTIME ✅ CONTFUT live-anchored incl. overnight; past paging blocked | RUNTIME ✅ today incl. GTH values from 02:15 CT; deep floor UNKNOWN | RUNTIME ✅ pulled at 2005; head 1993-01-29 |
| Historical daily bars | DOC (decades) | n/a (shallow) | RUNTIME ✅ CONTFUT 3Y pulled | RUNTIME ✅ 10Y pulled; head 2005-10 | DOC |
| whatToShow | TRADES only — MIDPOINT rejected (error 162, RUNTIME) | TRADES ✅ + BID_ASK ✅ RUNTIME | TRADES ✅ RUNTIME; BID_ASK DOC | TRADES ✅ RUNTIME | TRADES ✅ RUNTIME; BID/ASK/MIDPOINT DOC |
| Head timestamp | RUNTIME ✅ 2004-03-04 | RUNTIME ✅ per contract (shallow) | RUNTIME ✅ CONTFUT 2022-06-19; single ESU6 contract 2023-08-20 | RUNTIME ✅ 2005-10-03 | RUNTIME ✅ 1993-01-29 |
| Global-hours data | index computed RTH only | RUNTIME ✅ `useRTH=0` bars from 19:15 CT (20:15 ET GTH open) ⇒ live GTH quotes expected (DOC) | RUNTIME ✅ overnight bars present | RUNTIME ✅ GTH values in 1-min bars | DOC (extended hours) |
| 5-sec historical bars | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | RUNTIME ✅ (30 min pulled) |

VX futures (CFE): **UNKNOWN** — entitlement not probed; excluded from the initial universe until a
Phase 0 probe verifies it.

## Runtime-verified constraints that shape the platform

1. **SPX option history is weeks deep, not years — even for long-listed contracts.** SPX Aug-2026
   monthly 7500C (conId 800237324): BID_ASK head timestamp **2026-06-10**, TRADES head
   2026-05-11; a 1-min BID_ASK request ending 2026-04-30 returns "HMDS query returned no data".
   A ~26-DTE SPXW contract had BID_ASK only from ≈ its listing date. **No historical option-chain
   backtest is possible from IBKR data. Prospective recording is the only source of option
   research data, and every unrecorded day is unrecoverable.**
2. **Expired options: no data at all** (DOC, consistent with probes). Backfilling only
   currently-listed options would be survivorship-biased anyway; with (1) it is moot.
3. **CONTFUT cannot page into the past** — a past `endDateTime` is rejected with error 10339
   (RUNTIME). Deep ES intraday backfill must walk individual expired quarterlies with
   `includeExpired=1`. Docs guarantee ~2 years per contract post-expiry; the ESU6 head timestamp
   showed ≈3 years — treat 2y as the floor and discover per contract.
4. **Indices have no bid/ask and no volume** (RUNTIME: SPX/VIX stream last/high/low/close only).
   SPY and ES carry the tradable-price and spread information.
5. **Historical timestamps arrive in exchange-local timezones** with `formatDate=1`
   (US/Central for CBOE/CME requests, US/Eastern for SPY — RUNTIME). Use `formatDate=2` (epoch)
   so local-timezone strings never enter parsing.
6. **Contract-resolution traps (RUNTIME):** SPX-class (AM-settled) chain dates are Thursdays;
   SPXW (PM) are Fridays; requesting a computed calendar date returns error 200. Omitting
   `tradingClass` on an SPXW contract also returns error 200. Always resolve from the chain's own
   expiration strings with an explicit trading class. (Consistent with the SPX/SPXW guidance
   already in `docs/STATE.md`.)
7. **Live streaming entitlements ARE shared to the paper account** (RUNTIME: `marketDataType=1`
   returned for SPX, SPY, ES, and an SPXW option; usfarm/usopt/cashfarm/ushmds/secdefil all
   connected). Cboe indexes + OPRA + CME subscriptions active. The AppHost default of
   `ibkr-market-data-type=3` is safe but no longer necessary for research recording.
8. **Broker Greeks are IBKR model outputs** (all four computation variants observed). They are
   inputs and cross-checks — never the platform's canonical surface.

## Pacing, lines, and limits (verified against current official docs)

- **100 market-data lines default, per username**, shared across TWS watchlists and all API
  clients; scales with commissions/equity. Snapshots return-and-release.
- Historical pacing: no identical request within 15 s; <6 same-contract requests per 2 s;
  ≤60 requests per 10 min with **BID_ASK counting double**.
- Bars ≤30 s: unavailable older than 6 months. 1-sec bars: max 2000 s per request. 1-min and
  coarser: long durations allowed per request (probe per-instrument maxima in Phase 0 — this sets
  backfill request counts).
- ~50 outbound messages/sec socket limit (exceeding it disconnects). ≤32 API clients per TWS.
- `reqHistoricalTicks`: 1000 ticks/request — impractical as a bulk source.
- **Live tick-by-tick does not work for options** (historical only); capped at 5% of lines.
- `reqRealTimeBars`: 5-second bars only; counts against line and pacing budgets.
- Greeks require both the option and underlying subscriptions.
- **Headless-ops gotcha (official FAQ):** the paper user shares the live user's subscriptions; if
  live and paper sessions run simultaneously they must be on the same device or the paper session
  receives no data. This belongs in the recorder's ops runbook with a
  connected-but-zero-events alert.
- SPX/SPXW GTH session 20:15–09:15 ET (Cboe + IBKR overnight-trading page; probe-consistent).

## Data-gap consequences

| Gap | Status | Consequence |
|---|---|---|
| Deep SPX option quote history | Does not exist (RUNTIME) | Prospective recorder is the only option-data source; start it as early as possible |
| Survivorship-free historical chains | Impossible from IBKR | Never present active-contract backfills as chain backtests |
| ES deep intraday | Per-contract walk, ~2–3 y each | SPX (2004+) and SPY (1993+) carry the deep history |
| VIX intraday depth | Unknown (daily 10y+ verified) | Phase 0 probe; daily VIX suffices for regime features initially |
| Option OI freshness | Daily (assumed; cadence undocumented) | Unusable intraday; daily positioning context only |
| Index bid/ask | None | Use SPY/ES for spread/microstructure features |
| Depth-of-book | Not available | No queue-position/HFT research; execution studies limited to L1 realism |
| Paper fills | IBKR's own simulation | Operational testing only; never execution calibration |

## Re-probe procedure

The probes are pure wire-protocol scripts (no IBApi dependency) against 127.0.0.1:7497: v100+
handshake, `startApi` with a high client id, then read-only requests
(`reqContractDetails`, `reqSecDefOptParams`, `reqHeadTimeStamp`, `reqHistoricalData`,
`reqMktData` snapshot/brief stream + cancel). `~/tws-api-probe.py` (referenced in
`docs/STATE.md`) covers the handshake half. Re-run after every TWS/Gateway upgrade and record:
server version, marketDataType per instrument, head timestamps, one historical slice per
instrument/whatToShow, one option stream with generic ticks `100,101,106`, and the farm-status
notices. Phase 0 turns this into `CapabilityProbeRunner` + the `research.capability_probes`
table so probe facts are data, not lore. Never include account identifiers in recorded results.
