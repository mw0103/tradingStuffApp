# TWS API (IBApi) mechanics

The API is vendored at `third_party/IBApi` — **TWS API 10.45.01**. Read the actual signatures from
`third_party/IBApi/src/EWrapper.cs` rather than trusting an online tutorial; callbacks change
between releases, and most published examples predate 10.3x. Two renames that break older samples:

- `commissionReport` → **`commissionAndFeesReport`** (`CommissionAndFeesReport`)
- `error(int, int, string, string)` → **`error(int id, long errorTime, int code, string msg, string advancedOrderRejectJson)`**

Official reference: <https://interactivebrokers.github.io/tws-api/>

Everything below was verified against a live paper account (TWS server version 223).

## Connection lifecycle

The C# API is **callback-driven with a message pump you own**. There is no built-in async.

```csharp
var signal = new EReaderMonitorSignal();
var client = new EClientSocket(wrapper, signal);   // wrapper : EWrapper
client.eConnect(host, port, clientId, extraAuth: false);

var reader = new EReader(client, signal);
reader.Start();
new Thread(() => {                       // background message pump — required
    while (client.IsConnected()) {
        signal.waitForSignal();
        reader.processMsgs();
    }
}) { IsBackground = true }.Start();
```

Nothing is delivered until `processMsgs()` runs. Forgetting the pump thread presents as "connected
but every request hangs forever".

`connectAck` fires on connect; `nextValidId(int orderId)` follows and seeds the order ID sequence.
`managedAccounts(string accounts)` gives the account list — check the `DU` prefix here.

### Bridging callbacks to async

The service layer wants `Task<QuoteSnapshot>`. Bridge with a request-id-keyed map of
`TaskCompletionSource`:

- Allocate a monotonic `reqId` (`Interlocked.Increment`), store the TCS, issue the request.
- The `...End` callback (`contractDetailsEnd`, `securityDefinitionOptionParameterEnd`) completes it.
- The `error` callback with that same `reqId` **faults** it — otherwise a bad request hangs forever.
- Always apply a timeout and always remove the entry in a `finally`.

Every pending request must be reachable from both the success callback and the error callback. This
is the single most common source of hung requests in TWS API adapters.

### Disconnects

TWS and IB Gateway **restart daily** on a configured schedule; the socket dies and does not
self-heal. `connectionClosed` fires; error codes 1100 (connectivity lost), 1101 (restored, data
lost — resubscribe everything), 1102 (restored, data maintained).

On reconnect: use a **new** `clientId`-scoped state — re-request `nextValidId`, re-subscribe all
market data, and rebuild the conId cache only if it was invalidated. Reconnect with backoff; do not
tight-loop `eConnect`.

**Back off on short-lived sessions, not just failed connects.** TWS will accept the TCP connection
and then immediately `RST` it (*"Connection reset by peer"* out of `EReader`) when a modal dialog is
awaiting input, when the client id is already in use, or when the API connection limit is reached.
A backoff that resets on a *successful* connect then reconnects at the base interval forever, because
each attempt "succeeds" before dying a second later. Treat a session shorter than ~30s as a failure.

## Contract resolution — always resolve conId first

Never construct a `Contract` by hand and place an order on it. Resolve it:

```csharp
var c = new Contract {
    Symbol = "SPY", SecType = "OPT", Exchange = "SMART", Currency = "USD",
    LastTradeDateOrContractMonth = "20260821",   // yyyyMMdd
    Strike = 450, Right = "C", Multiplier = "100",
};
client.reqContractDetails(reqId, c);   // -> contractDetails / contractDetailsEnd
```

`ContractDetails.Contract.ConId` is the unambiguous identifier. Cache it — resolution is a round
trip and the pacing limits below are real. Cache key: underlying + expiry + strike + right + currency.

- Error **200 "No security definition has been found"** = the contract does not exist (bad expiry
  date, wrong strike increment, non-trading-day expiry). It is not a transient error; do not retry.
- Ambiguous results (multiple `contractDetails` for one request) mean the contract is
  under-specified — usually a missing `TradingClass` (e.g. `SPXW` vs `SPX`) or `Multiplier`.
- Expirations must be real trading days. Weeklies, monthlies, and AM/PM-settled variants differ by
  `TradingClass`.

## Option chains

```csharp
client.reqSecDefOptParams(reqId, "SPY", "", "STK", underlyingConId);
// -> securityDefinitionOptionParameter(reqId, exchange, underlyingConId,
//        tradingClass, multiplier, HashSet<string> expirations, HashSet<double> strikes)
// -> securityDefinitionOptionParameterEnd(reqId)
```

Notes that matter for `GET /market-data/options/chains/{underlying}`:

- You must resolve the **underlying's** conId (`SecType = "STK"`) first.
- It returns expirations and strikes as **separate sets**, not a validated cross-product. Not every
  (expiry, strike) pair exists. Filter to a strike window around spot rather than materialising
  everything — the current deterministic provider returns 11 strikes × 2 rights; that is a sane
  default window to preserve.

### Do not select the chain segment by `SMART`

One callback arrives per (exchange, tradingClass) — **39 of them for SPY**. Two traps, both
confirmed against a live paper account:

1. **Adjusted option classes sit alongside the standard one.** A corporate action leaves behind a
   digit-prefixed trading class (`2SPY`) listing a handful of strikes. Treat one as the chain and
   you get a near-empty, untradeable result.
2. **There is often no `SMART` segment for the standard class.** SPY's *only* `SMART` row is the
   adjusted `2SPY` class — 3 expirations, 3 strikes. The real chain (`tradingClass = SPY`, 35
   expirations, 489 strikes) appears on `NASDAQOM`, `AMEX`, `CBOE`, and the rest, but **never on
   `SMART`**. Preferring `SMART` actively selects the wrong segment, and it fails quietly: you get a
   plausible-looking chain with a handful of strikes.

Select on **`tradingClass == underlying symbol`**, breaking ties by strike count. `SMART` is still
the correct exchange to route and quote on — it just is not how the segment is identified.
Implemented in `IbkrMarketDataClient.SelectChainSegment`, with the SPY case pinned in tests.

### Index options (SPX) differ in four ways

Confirmed live. Any of these alone stops SPX working:

1. **The underlying is `IND`, not `STK`.** SPX resolves to nothing as a stock. It is `SecType = "IND"`
   on `CBOE` (conId 416904). `ResolveUnderlyingAsync` probes STK then IND, so any index works
   without a hard-coded symbol list. The same `SecType` must then be passed as
   `reqSecDefOptParams`'s `underlyingSecType`.
2. **Two series share every strike and expiration.** `SPX` is AM-settled monthlies (20 expirations,
   574 strikes); **`SPXW`** is PM-settled weeklies and dailies (39 expirations, 728 strikes) — and
   SPXW is what trades in extended hours. `tradingClass == symbol` picks the monthlies, so pass an
   explicit trading class for index options.
3. **`Contract.TradingClass` must be set when resolving.** Otherwise `reqContractDetails` matches both
   SPX and SPXW at the same strike/expiry and resolution takes whichever arrives first. This is why
   `OptionContract.TradingClass` exists and is part of `OptionContractKey`.
4. **`Order.OutsideRth = true` is required outside 09:30–16:15 ET.** SPX/SPXW run nearly 24×5 in
   global trading hours; without the flag a pre-market order is held rather than worked.

SPXW quotes pre-market are real and tight — e.g. 7435C 32.30/32.60 with delta 0.662 at ~08:15 ET,
where SPY at the same moment had no book at all.

## Market data and Greeks

```csharp
client.reqMarketDataType(1);   // 1 live, 2 frozen, 3 delayed, 4 delayed-frozen
client.reqMktData(tickerId, contract, genericTickList: "", snapshot: false,
                  regulatorySnapshot: false, mktDataOptions: null);
```

- `tickPrice(tickerId, field, price, attrib)` — field **1 = BID, 2 = ASK, 4 = LAST**, 6 = HIGH,
  7 = LOW, 9 = CLOSE. Delayed equivalents are 66/67/68 — if you see those, you are on delayed data
  and the `reqMarketDataType` call did not take effect as intended.
- `tickSize(tickerId, field, size)` — 0 = BID_SIZE, 3 = ASK_SIZE, 5 = LAST_SIZE.
- `tickOptionComputation(tickerId, field, tickAttrib, impliedVol, delta, optPrice, pvDividend,
   gamma, vega, theta, undPrice)` — **this is where Greeks come from**, delivered automatically for
  option contracts. field **13 = MODEL_OPTION** is the one to map into `OptionGreeks`; 10/11/12 are
  bid/ask/last computations.
- Values can arrive as `double.MaxValue` (or negative sentinels) meaning "not computed yet".
  **Guard every field** before casting to `decimal` — this crashes adapters constantly.
- A quote is built from *several* callbacks arriving at different times. Accumulate into a
  per-tickerId mutable snapshot and only emit a `QuoteSnapshot` once bid, ask, and model greeks are
  all populated, or a timeout fires.
- `snapshot: true` gives a one-shot quote and auto-cancels; it does **not** deliver
  `tickOptionComputation` reliably. For Greeks use a streaming subscription and `cancelMktData`.
- **Always `cancelMktData(tickerId)`.** Default limit is 100 concurrent lines; leaking subscriptions
  ends in error 10197 / "Max number of market data lines exceeded".
- No market data subscription → error 354 ("Requested market data is not subscribed"). Options data
  requires the OPRA subscription; without it, use `reqMarketDataType(3)` (delayed) for development.

## Combo (multi-leg) orders

A `SubmitOrderRequest` with N legs becomes **one** BAG contract and **one** order:

```csharp
var bag = new Contract {
    Symbol = "SPY", SecType = "BAG", Currency = "USD", Exchange = "SMART",
    ComboLegs = legs.Select(l => new ComboLeg {
        ConId = l.ConId,                                  // resolved per leg, required
        Ratio = l.Quantity,                               // ratio, not absolute size
        Action = l.Side == OrderSide.Buy ? "BUY" : "SELL",
        Exchange = "SMART",
        OpenClose = 0,                                    // 0 = SAME
    }).ToList(),
};

var order = new Order {
    Action = "BUY",              // direction of the combo as a whole
    OrderType = "LMT",
    TotalQuantity = spreadCount, // number of spreads, NOT total contracts
    LmtPrice = netPrice,         // net debit positive, net credit NEGATIVE
    Tif = "DAY",
    Transmit = true,
};
client.placeOrder(orderId, bag, order);
```

- `ComboLeg.Ratio` is a **ratio**, and `Order.TotalQuantity` is the **number of spreads**. A 2-lot
  1×1 vertical is `Ratio = 1` on each leg with `TotalQuantity = 2` — not `Ratio = 2`. Getting this
  wrong silently trades the wrong size. `OrderLegRequest.Quantity` in Contracts is per-leg absolute
  quantity, so the adapter factors out the GCD to derive ratios + spread count
  (`IbkrOrderBuilder.SpreadCount` / `.Ratios`).
- Net credit is expressed as a **negative** `LmtPrice`. `PaperExecutionEngine.CalculateNetDebit`
  produces a signed net with the same convention (buy positive, sell negative) — but it multiplies by
  leg quantity, making `SubmitOrderRequest.LimitPrice` a **whole-order** net, while TWS wants the net
  for **one combo unit**. `IbkrOrderBuilder.PerSpreadPrice` divides by the spread count. The two are
  identical at one spread, which is exactly why this is easy to miss.
- Set `Order.Action = "BUY"` and carry direction in the leg actions. Setting `"SELL"` on the combo
  inverts every leg.
- Prefer `"LMT"`. Market orders on option combos are commonly rejected or fill poorly.
### Combo fills: three executions for a two-leg spread

A filled combo produces an execution for the **BAG itself** carrying the net price, **plus one per
leg**. Confirmed on a filled SPXW vertical: `BAG @ 3.80`, `leg @ 36.40`, `leg @ 32.60`.

- **Skip the BAG row** (`contract.SecType == "BAG"`). It is a summary of the others, not a third
  fill; counting it invents a leg that does not exist and records the net as a leg price.
- **Attribute the leg rows by conId**, not by arrival order. Legs do not fill in request order, and
  one leg can fill in several executions while the other has not started — so a running counter
  mislabels them. Keep a conId → leg-index map from before the order is transmitted.
- Dedupe on `ExecId`; executions replay after a reconnect.

### A combo's average fill price is signed

`orderStatus.avgFillPrice` for a combo filled at a net credit arrives **negative**. Convert it with a
signed converter — running it through a price converter that rejects negatives (correct for an option
quote, where negative means "no quote") silently reports every credit fill as zero.

### TWS Precautionary Settings will reject your first orders

**Error 163** — *"price exceeds the Percentage constraint of 3%"* — is not a bug in your order. TWS
applies client-side *Precautionary Settings* before an order ever reaches the exchange, and the
defaults reject any limit more than a few percent from the market. A deliberately unmarketable test
price is refused outright, and so is a realistic one when the market is closed and TWS has no
reference price to compare against.

It is **not** about having a stale price, and not fixed by pricing the order sensibly. It rejected a
marketable SPXW vertical priced exactly at the natural (3.80 debit) against a live, tight, two-sided
book. For a combo TWS appears to compare the *net* price against a leg/underlying reference, so any
spread net is a huge percentage away and every BAG order is refused.

Fix in TWS: **Global Configuration → Presets → (instrument type, e.g. Options) → Precautionary
Settings**. Clear or widen *Percentage*; the sibling limits (*Size Limit*, *Total Value Limit*,
*Number of Ticks*) reject on the same principle. Expect to clear *Percentage* before any combo order
will work over the API.

Treat 163 as terminal for the order — see `IbkrErrorCodes.IsOrderRejection`. Left unmapped, the
order sits at `PendingSubmit` forever while being dead.

### Terminal states must not be overwritten

The real sequence for a rejected order is **163** (rejected) → **202** (cancelled) → **161** if
anything then tries to cancel it (*"not in a cancellable state"*). Applying callbacks blindly leaves
the order reporting the trailing 161 notice as its outcome. Once an order is terminal, ignore later
status and error chatter.

## Order IDs and status

- Seed from `nextValidId`; increment with `Interlocked.Increment`. Unique and increasing, per
  connection. Reusing an ID **modifies** the existing order.
- `placeOrder` with an existing ID and changed fields = the replace path for
  `POST /orders/{id}/replace`. `cancelOrder(orderId, manualOrderCancelTime)` for cancel.
- Persist the IBKR order ID against the internal `Guid OrderId` **before** transmitting. A crash
  between `placeOrder` and the first `orderStatus` otherwise leaves an untracked live order.

`orderStatus(orderId, status, filled, remaining, avgFillPrice, permId, parentId, lastFillPrice,
clientId, whyHeld, mktCapPrice)` → `OrderLifecycleStatus`:

| IBKR `status` | `OrderLifecycleStatus` |
|---|---|
| `PendingSubmit`, `PreSubmitted` | `Submitted` |
| `Submitted` | `Submitted` (or `PartiallyFilled` if `filled > 0 && remaining > 0`) |
| `Filled` | `Filled` |
| `Cancelled`, `ApiCancelled` | `Cancelled` |
| `PendingCancel` | keep prior status until terminal |
| `Inactive` | `Failed` — inspect `whyHeld` and the paired `error` |

`permId` is stable across reconnects; `orderId` is not portable across sessions. Store both.

Fills: `execDetails(reqId, contract, execution)` then `commissionReport(commissionReport)` — matched
by `execution.ExecId`. Use `FillLiquidity.BrokerReported`. `execDetails` can replay on reconnect, so
**dedupe on `ExecId`** or you will double-count fills.

## Error codes worth special-casing

`error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)` —
the `errorTime` parameter was added around API 10.30; older versions omit it.

**Informational, not errors** (log at debug, never fail on these):
2104, 2106, 2158 (market data / HMDS farm connection OK), 2107 (farm inactive), 2100–2110 generally.

**The delayed-data family is the subtle one.** TWS reports these as errors on a market-data request
*while still streaming usable ticks*. Faulting the request discards data that is already on its way —
this is what a paper account without an OPRA subscription hits on every option quote:

| Code | Meaning | Fault the request? |
|---|---|---|
| 10090 | Part of the data is unsubscribed; independent ticks still stream | **No** |
| 10091 | Needs a subscription, but **delayed data is available** | **No** |
| 10167 | Not subscribed; displaying delayed market data | **No** |
| 10168 | Not subscribed **and delayed data not enabled** — nothing will arrive | **Yes** |

| Code | Meaning | Handling |
|---|---|---|
| 200 | No security definition found | Permanent — fault the request, do not retry |
| 201 | Order rejected — see message | → `OrderLifecycleStatus.Failed`, surface the text |
| 202 | Order cancelled | → `Cancelled` |
| 321 | Server error validating request | Malformed request — fix, do not retry |
| 354 | Market data not subscribed | Fall back to delayed (`reqMarketDataType(3)`) in dev |
| 1100 | Connectivity lost | Mark unhealthy, stop accepting orders |
| 1101 | Restored, **data lost** | Resubscribe all market data |
| 1102 | Restored, data maintained | Resume |
| 10197 | No market data during competing session | Same account logged in elsewhere |

Classification lives in `IbkrErrorCodes`; add codes there rather than at call sites.

`id` is `-1` for connection-level messages, otherwise the `reqId`/`orderId` — route it to the right
pending TCS.

## Pacing limits

- **~50 messages/second** to TWS. Exceed it and TWS disconnects you.
- **100 concurrent market data lines** by default.
- Historical data: no more than 60 requests per 10 minutes; identical requests within 15 seconds are
  rejected.
- Chain and contract-detail requests are expensive — cache aggressively, and rate-limit the adapter
  centrally rather than at each call site.

## Why not the Client Portal Web API

Recorded for the record, in case the decision is revisited:

- REST + websocket against a local Java gateway on `https://localhost:5000` (self-signed cert), which
  is where the current `ibkr-gateway-url` default came from.
- Blocker: requires an **interactive browser login**, plus a `/tickle` keepalive roughly every 60s,
  and the session still expires (~24h). Unattended Aspire startup cannot satisfy that.
- Greeks come from numbered snapshot fields, and snapshot endpoints must be polled repeatedly before
  they return complete data.

It is the better choice only if the app must run somewhere TWS/Gateway cannot (a container without a
desktop session). That is not the case for a local-first Aspire workspace.
