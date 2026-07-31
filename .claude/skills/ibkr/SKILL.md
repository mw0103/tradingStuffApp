---
name: ibkr
description: Interactive Brokers integration for TradingStuff — TWS/IB Gateway socket API (IBApi), contract resolution, option chains, streaming quotes and Greeks, combo (multi-leg) order placement, and the paper/live safety rules. Use whenever work touches the IBKR adapter, MarketDataService quote sourcing, real order routing, DeterministicOptionMarketDataProvider replacement, conId/contract resolution, or anything named IBKR/TWS/Gateway/IBApi.
---

# IBKR integration for TradingStuff

## Decision: TWS socket API (IBApi), not the Client Portal Web API

The chosen broker interface is the **TWS API** — the socket protocol served by TWS or IB Gateway,
consumed via IBKR's `IBApi` C# library.

Why, for this project specifically:

- Options are the only asset class here. `reqSecDefOptParams` and `tickOptionComputation` give
  chains and per-contract Greeks (delta/gamma/theta/vega/IV) directly. The Contracts already model
  `OptionGreeks` on every `QuoteSnapshot`, so the socket API's model-greeks tick is a 1:1 fill.
- Multi-leg is the core use case (`StrategyKind` = Vertical/Calendar/Diagonal/Straddle/Strangle).
  The TWS API expresses these as a single `BAG` contract with `ComboLegs` and one net limit price —
  which matches `SubmitOrderRequest` (a list of legs plus one `LimitPrice`) almost exactly.
- The Client Portal Web API needs an interactive **browser login** plus a `/tickle` keepalive, and
  the session expires. That is a poor fit for an Aspire service expected to start unattended.

If this decision is ever revisited, the fallback is the Client Portal Web API (local gateway, REST +
websocket, `https://localhost:5000`) — see `references/tws-api.md` for the comparison notes.

## The one hard constraint: a single socket owner

A TWS connection is **stateful and single-owner per `clientId`**:

- Request IDs, order IDs, and market-data ticker IDs are connection-scoped integers you allocate.
- `nextValidId` seeds the order ID sequence **once per connection**. Order IDs must be unique and
  increasing. Reusing one is a modify, not a new order.
- Only `clientId = 0` (or the client set as TWS's Master API client ID) receives order and execution
  events for orders placed by *other* clients or manually in TWS.
- Market data lines are capped per account (100 concurrent streaming lines by default).

So **do not** let `MarketDataService` and `ExecutionService` each open their own connection. Two
independent ID sequences against one account is how you get orphaned orders and lost fills.

### Implemented shape

`src/TradingStuff.IbkrGateway` owns exactly one `EClientSocket` and exposes internal HTTP to the rest
of the mesh, using the same bearer-token pattern as the other services
(`ServiceClientConfiguration.ConfigureInternalClient`, now in `ServiceDefaults`).

```
MarketDataService ─┐
                   ├─► IbkrGateway (1 socket, 1 clientId) ─► TWS / IB Gateway
ExecutionService ──┘
```

`MarketDataService` keeps its public contract (`POST /market-data/options/quotes`,
`GET /market-data/options/chains/{underlying}`) unchanged and swaps its provider implementation.
Nothing downstream of it needs to know IBKR exists.

## Aspire wiring

The broker was originally modelled as an HTTP external service
(`AddExternalService("ibkr-gateway", "http://localhost:5000")`). **A TWS socket is not HTTP**, so
that was replaced with host/port/clientId parameters plus a real adapter project:

```csharp
var ibkrHost     = builder.AddParameter("ibkr-host", "127.0.0.1", publishValueAsDefault: true);
var ibkrPort     = builder.AddParameter("ibkr-port", "7497", publishValueAsDefault: true); // paper TWS
var ibkrClientId = builder.AddParameter("ibkr-client-id", "11", publishValueAsDefault: true);
var ibkrMarketDataType = builder.AddParameter("ibkr-market-data-type", "3", publishValueAsDefault: true);
```

Ports: **7497** TWS paper, 7496 TWS live, **4002** IB Gateway paper, 4001 IB Gateway live.
TWS/Gateway must have *Enable ActiveX and Socket Clients* on and the host IP in Trusted IPs.

`marketdataservice` deliberately has **no `WaitFor(ibkrGateway)`**: the gateway reports unhealthy
whenever TWS is down, and waiting on health would stop the whole mesh from starting just because TWS
is closed.

`GET /market-data/ibkr/status` proxies the gateway's real socket state; `GET /ibkr/status` on the
gateway is the source of truth (connected, clientId, serverVersion, managed accounts, market data
type, whether trading is permitted, in-flight request count).

## Mapping to the existing Contracts

| TradingStuff (`TradingContracts.cs`) | IBKR (`IBApi`) |
|---|---|
| `OptionContract.Underlying` | `Contract.Symbol` |
| `OptionContract.Expiration` (`DateOnly`) | `Contract.LastTradeDateOrContractMonth` — format `"yyyyMMdd"` |
| `OptionContract.Strike` | `Contract.Strike` (`double` — deliberate precision loss, see below) |
| `OptionContract.Right` | `Contract.Right` — `"C"` / `"P"` |
| `OptionContract.Multiplier` | `Contract.Multiplier` — string `"100"` |
| `OptionContract.Exchange` / `.Currency` | `Contract.Exchange` (`"SMART"`) / `Contract.Currency` |
| `OptionContract.TradingClass` | `Contract.TradingClass` — required for SPX vs SPXW |
| `OptionContract.Symbol` | **nothing** — this is a synthetic local key, never send it |
| `OptionGreeks` | `tickOptionComputation` callback (delta, gamma, vega, theta) |
| `QuoteSnapshot.Bid/.Ask/.Last` | `tickPrice` tick types 1 / 2 / 4 |
| `SubmitOrderRequest.Legs` | one `BAG` contract + `ComboLeg[]` |
| `SubmitOrderRequest.LimitPrice` | `Order.LmtPrice` — combo **net**; negative means net credit |
| `OrderType` Market/Limit/Stop/StopLimit | `"MKT"` / `"LMT"` / `"STP"` / `"STP LMT"` |
| `TimeInForce` Day/GTC/IOC/FOK | `"DAY"` / `"GTC"` / `"IOC"` / `"FOK"` |
| `FillLiquidity.BrokerReported` | `execDetails` + `commissionAndFeesReport` |
| `OrderLifecycleStatus` | `orderStatus` string — mapping in `references/tws-api.md` |

### Three traps in this mapping

1. **`decimal` vs `double`.** Every price in Contracts is `decimal`; every price in `IBApi` is
   `double`. Convert only at the adapter boundary and round strikes to the contract's tick before
   comparing — `450.0d` round-tripped can miss an exact-match lookup on `decimal 450.00m`.

2. **Never key a dictionary on the whole `OptionContract` record.** *(Resolved — keep it that way.)*
   `OptionContract` is a `record`, so equality is structural over *every* property. `PaperExecutionEngine`
   used to do `quotes.ToDictionary(q => q.Contract)`, which threw `KeyNotFoundException` the moment a
   broker-backed quote carried a field the inbound leg lacked.

   The fix is `OptionContractKey` in `TradingContracts.cs` — underlying, expiration, strike, right,
   currency, and trading class, with case normalisation — reached via `contract.Key()`. Correlate
   quotes, legs, and fills on that. `OptionContract` is deliberately **not** carrying `ConId`: conIds
   live in an adapter-side cache in `IbkrMarketDataClient` keyed on `OptionContractKey`, so the record
   stays a stable identity. `TradingClass` is the one exception, because SPX and SPXW are genuinely
   different instruments rather than broker metadata — and providers echo contracts back as supplied
   rather than enriching them, so both sides of a lookup stay in agreement.

3. **Market orders on option combos.** IBKR frequently rejects or badly fills `MKT` on multi-leg
   BAG orders. `PaperExecutionEngine` currently treats `OrderType.Market` as always executable.
   Against real IBKR, prefer marketable limits; if `MKT` is kept, expect rejects and surface them as
   `OrderLifecycleStatus.Failed` rather than assuming a fill.

## Safety rules — non-negotiable

`docs/PLAN.md` scopes v1 to two non-live modes — *simulated* (fills invented locally) and
*paper brokerage* (real orders to a `DU` account, simulated money). Orders against a funded
(`U`-prefixed) account are out of scope. Hold that line.

- **Default to paper.** Port 7497/4002 and a `DU`-prefixed account. A `U`-prefixed account is live money.
- **Live routing requires an explicit, separate opt-in** — a config flag that is `false` by default
  and is never set in `AppHost` defaults, `appsettings*.json`, or test fixtures. Refuse to place a
  live order on a code path that a unit test can reach.
- **Log and assert the account prefix at connect time.** If `IBKR:AllowLiveTrading` is false and the
  connected account does not start with `DU`, fail startup loudly rather than trading.
- Never write account numbers, session tokens, or full position dumps into logs, test fixtures,
  or committed files.
- `placeOrder` is irreversible the instant it lands. Treat adding or changing any `placeOrder` call
  site as a change requiring explicit confirmation, not a routine edit.
- **Never let an HTTP client retry an order.** The standard resilience handler retries on its
  per-attempt timeout, and an order resting longer than that is re-sent as a *second* live broker
  order under a new order id while the caller sees only the last attempt. This happened on
  2026-07-31. Order-routing clients go through `ServiceClientConfiguration.DisableAutomaticRetries`,
  and `IbkrOrderTracker.TryTrack` enforces one broker order per internal order independently.

## Staged migration — current position

All six stages are implemented and **verified against a live paper account**. Details and acceptance
criteria in `references/migration-plan.md`.

**Use SPY, not SPX/SPXW, when you need a combo to actually fill.** As of 2026-07-31 every SPXW combo
parks at `PreSubmitted` with no error — at any price, including `MKT`, inside regular hours, with and
without `OutsideRth`. SPY combos on the identical code path fill immediately. Unexplained and
account-level rather than a code defect; see the open question in `docs/STATE.md`.

| Stage | Status |
|---|---|
| 1. Connection lifecycle, pump, reconnect, health | **Done** |
| 2. Contract resolution + conId cache | **Done** |
| 3. Chains (`reqSecDefOptParams`) | **Done** |
| 4. Streaming quotes + Greeks | **Done** |
| 5. Account/position sync → `PortfolioProvider` | **Done — verified live with an open position** |
| 6. Order placement (paper only, opt-in) | **Done — round trip filled on the paper account** |

### Account and positions (stage 5)

`IbkrAccountClient` serves `GET /ibkr/account/portfolio` from `reqAccountSummary` +
`reqPositionsMulti` + `reqPnL`. ExecutionService consumes it through `IbkrPortfolioProvider` when
`Portfolio:Source=ibkr` (opt-in on the same footing as `Execution:Router`; anything unrecognised
stays on the fixed development figures).

Three rules the implementation holds to:

- **Open those three streams once per connection; never subscribe per read.** TWS caps concurrent
  `reqAccountSummary` subscriptions at **two**, and `cancelAccountSummary` does not release them —
  verified live on TWS 223, where the third consecutive portfolio read fails with `error 322`
  despite a well-formed cancel after every read. The cap is undocumented. Keep the subscriptions
  registered for the connection's life, key the feed on `ConnectedAt` so a reconnect rebuilds it,
  and read from the pushed values. `reqPnL` has no `...End` callback and settles on its first
  non-sentinel push.
- **Greeks come from quoting positions**, because IBKR exposes no portfolio-Greeks API. Scaled by
  quantity × multiplier so they sum with `PortfolioRiskEvaluator`'s order exposure.
- **Never default a missing input.** `DailyPnLAvailable`, `GreeksComplete`, and
  `NonOptionPositionCount` ride along on the response, and an unreadable portfolio raises
  `PortfolioUnavailableException` (503, no order placed) rather than falling back to stub figures.
  A defaulted zero daily P&L silently disables `MAX_DAILY_LOSS`.

The deterministic provider remains the default and is what the test suite uses; it is not going away.
Selection is via `MarketData:Source` (`MarketDataSources.UsesIbkrGateway`), and anything
unrecognised falls back to deterministic rather than silently hitting a broker.

### Order routing (stage 6)

`IbkrOrderClient` is the **only** caller of `placeOrder`, and it calls
`IbkrConnection.EnsureTradingPermitted()` first. Routing is opt-in twice over:

- `Execution:Router` on ExecutionService — `paper` (default) simulates fills; only the exact string
  `ibkr` routes to the broker. A typo stays on paper.
- `IBKR:AllowLiveTrading` on the gateway — false by default; a non-`DU` account with it false blocks
  placement while still serving market data.

The gateway exposes `POST /ibkr/orders`, `GET /ibkr/orders/{id}`, `POST /ibkr/orders/{id}/cancel`,
and `GET /ibkr/orders/open` — the last being the honest answer to "is anything resting?", since the
in-memory tracker only knows about the current run.

**TWS's Precautionary Settings percentage constraint blocks combo orders until cleared** — see
`references/tws-api.md`.

Verified end to end on a `DU` paper account: a 1-lot SPXW 7435/7440 call vertical opened at a
3.80 debit and closed at a 3.40 credit, both filling at the natural, with correct per-leg fills and
commissions. Use **SPX/SPXW, not SPY**, outside regular hours — SPY has no pre-market book at all.

## References

- `references/tws-api.md` — connection lifecycle, EReader loop, contract resolution, chains, tick
  types, combo orders, orderStatus mapping, error codes, pacing limits.
- `references/migration-plan.md` — the staged sequence above with per-stage acceptance criteria.

Official docs: <https://interactivebrokers.github.io/tws-api/> — the C# API source ships in the TWS
API installer, not on an official NuGet feed. Vendor `IBApi` into the repo (or reference a community
package deliberately) and record the API version in `docs/STATE.md`; callback signatures do change
between versions.
