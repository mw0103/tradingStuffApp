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

### Required shape

Add one project, `src/TradingStuff.IbkrGateway`, that owns exactly one `EClientSocket` and exposes
internal HTTP to the rest of the mesh — the same bearer-token internal-call pattern the other
services already use (`ServiceClientConfiguration.ConfigureInternalClient`).

```
MarketDataService ─┐
                   ├─► IbkrGateway (1 socket, 1 clientId) ─► TWS / IB Gateway
ExecutionService ──┘
```

`MarketDataService` keeps its public contract (`POST /market-data/options/quotes`,
`GET /market-data/options/chains/{underlying}`) unchanged and swaps its provider implementation.
Nothing downstream of it needs to know IBKR exists.

## Aspire wiring change this requires

`src/TradingStuff.AppHost/Program.cs` currently models the broker as an HTTP external service:

```csharp
var ibkrGatewayUrl = builder.AddParameter("ibkr-gateway-url", "http://localhost:5000", ...);
var ibkrGateway = builder.AddExternalService("ibkr-gateway", ibkrGatewayUrl);
```

**A TWS socket is not HTTP**, so `AddExternalService` with a URL is the wrong model. Replace it with
host/port/clientId parameters and make the adapter a real project:

```csharp
var ibkrHost     = builder.AddParameter("ibkr-host", "127.0.0.1", publishValueAsDefault: true);
var ibkrPort     = builder.AddParameter("ibkr-port", "7497", publishValueAsDefault: true); // paper TWS
var ibkrClientId = builder.AddParameter("ibkr-client-id", "11", publishValueAsDefault: true);
var ibkrAccount  = builder.AddParameter("ibkr-account", "DU0000000", publishValueAsDefault: true);

var ibkr = builder.AddProject("ibkrgateway", "../TradingStuff.IbkrGateway/TradingStuff.IbkrGateway.csproj")
    .WithEnvironment("Authentication__DevelopmentToken", devInternalToken)
    .WithEnvironment("IBKR__Host", ibkrHost)
    .WithEnvironment("IBKR__Port", ibkrPort)
    .WithEnvironment("IBKR__ClientId", ibkrClientId)
    .WithEnvironment("IBKR__AccountId", ibkrAccount);
```

Ports: **7497** TWS paper, 7496 TWS live, **4002** IB Gateway paper, 4001 IB Gateway live.
TWS/Gateway must have *Enable ActiveX and Socket Clients* on and the host IP in Trusted IPs.

Keep `GET /market-data/ibkr/status` — extend it to report real connection state (connected,
clientId, serverVersion, last error code, delayed-vs-live data mode) instead of the current
static placeholder.

## Mapping to the existing Contracts

| TradingStuff (`TradingContracts.cs`) | IBKR (`IBApi`) |
|---|---|
| `OptionContract.Underlying` | `Contract.Symbol` |
| `OptionContract.Expiration` (`DateOnly`) | `Contract.LastTradeDateOrContractMonth` — format `"yyyyMMdd"` |
| `OptionContract.Strike` | `Contract.Strike` (`double` — deliberate precision loss, see below) |
| `OptionContract.Right` | `Contract.Right` — `"C"` / `"P"` |
| `OptionContract.Multiplier` | `Contract.Multiplier` — string `"100"` |
| `OptionContract.Exchange` / `.Currency` | `Contract.Exchange` (`"SMART"`) / `Contract.Currency` |
| `OptionContract.Symbol` | **nothing** — this is a synthetic local key, never send it |
| `OptionGreeks` | `tickOptionComputation` callback (delta, gamma, vega, theta) |
| `QuoteSnapshot.Bid/.Ask/.Last` | `tickPrice` tick types 1 / 2 / 4 |
| `SubmitOrderRequest.Legs` | one `BAG` contract + `ComboLeg[]` |
| `SubmitOrderRequest.LimitPrice` | `Order.LmtPrice` — combo **net**; negative means net credit |
| `OrderType` Market/Limit/Stop/StopLimit | `"MKT"` / `"LMT"` / `"STP"` / `"STP LMT"` |
| `TimeInForce` Day/GTC/IOC/FOK | `"DAY"` / `"GTC"` / `"IOC"` / `"FOK"` |
| `FillLiquidity.BrokerReported` | `execDetails` + `commissionReport` |
| `OrderLifecycleStatus` | `orderStatus` string — mapping in `references/tws-api.md` |

### Three traps in this mapping

1. **`decimal` vs `double`.** Every price in Contracts is `decimal`; every price in `IBApi` is
   `double`. Convert only at the adapter boundary and round strikes to the contract's tick before
   comparing — `450.0d` round-tripped can miss an exact-match lookup on `decimal 450.00m`.

2. **Adding `ConId` to `OptionContract` will break `PaperExecutionEngine`.** You need the IBKR
   `conId` to build combo legs, and the natural move is to add it to the record. But
   `PaperExecutionEngine.Execute` does:

   ```csharp
   var quoteByContract = quotes.ToDictionary(quote => quote.Contract);
   ...
   var quote = quoteByContract[leg.Contract];   // KeyNotFoundException
   ```

   `OptionContract` is a `record`, so lookup is structural equality over *all* properties. The
   moment quotes come back carrying a resolved `ConId` and the inbound request legs don't, every
   lookup throws. Either key the dictionary on a stable subset instead of the whole record, or hold
   the conId in an adapter-side cache keyed by the existing five identity fields. Decide this
   before touching the record.

3. **Market orders on option combos.** IBKR frequently rejects or badly fills `MKT` on multi-leg
   BAG orders. `PaperExecutionEngine` currently treats `OrderType.Market` as always executable.
   Against real IBKR, prefer marketable limits; if `MKT` is kept, expect rejects and surface them as
   `OrderLifecycleStatus.Failed` rather than assuming a fill.

## Safety rules — non-negotiable

`docs/PLAN.md` states: *"No live broker orders are placed in v1."* Hold that line.

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

## Staged migration

Do not swap the deterministic provider out in one step — it is the only thing making the test suite
repeatable. Sequence in `references/migration-plan.md`; the short version:

1. `IbkrGateway` project + connection lifecycle + `/health`. No market data, no orders.
2. Contract resolution (`reqContractDetails` → conId) with a cache.
3. Chains (`reqSecDefOptParams`) behind `GET /market-data/options/chains/{underlying}`.
4. Streaming quotes + Greeks; put it behind `MarketData:Source` so the deterministic provider stays
   selectable and keeps the existing 6 tests green.
5. Read-only account/position sync to replace `PortfolioProvider`.
6. Order placement — **paper only**, behind the opt-in flag, last.

## References

- `references/tws-api.md` — connection lifecycle, EReader loop, contract resolution, chains, tick
  types, combo orders, orderStatus mapping, error codes, pacing limits.
- `references/migration-plan.md` — the staged sequence above with per-stage acceptance criteria.

Official docs: <https://interactivebrokers.github.io/tws-api/> — the C# API source ships in the TWS
API installer, not on an official NuGet feed. Vendor `IBApi` into the repo (or reference a community
package deliberately) and record the API version in `docs/STATE.md`; callback signatures do change
between versions.
