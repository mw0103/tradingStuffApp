# TWS API (IBApi) mechanics

Verify signatures against the installed `IBApi` version before writing code — callback signatures do
change between TWS API releases (the `error` callback in particular). Official reference:
<https://interactivebrokers.github.io/tws-api/>

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
- Multiple callbacks arrive, one per exchange/tradingClass. Deduplicate; prefer the `SMART` row.

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
  quantity, so the adapter must factor out the GCD to derive ratios + spread count.
- Net credit is expressed as a **negative** `LmtPrice`. `PaperExecutionEngine.CalculateNetDebit`
  already produces a signed net using the same convention (buy positive, sell negative), so its sign
  convention carries over — but it multiplies by leg quantity, so divide back to per-spread.
- Prefer `"LMT"`. Market orders on option combos are commonly rejected or fill poorly.
- Fills arrive **per leg**, which matches `FillReport.LegIndex`. Map by `execDetails` conId → leg.

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
