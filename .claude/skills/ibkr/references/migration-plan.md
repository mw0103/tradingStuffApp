# Staged migration: deterministic provider → IBKR

`DeterministicOptionMarketDataProvider` is the only reason the 6 tests in
`tests/TradingStuff.Tests/TradingWorkflowTests.cs` are repeatable. It does not get deleted — it
becomes the default provider behind a switch, and stays the one used by tests and offline work.

Selection key: `MarketData:Source` (already read by the provider and already set by AppHost to
`ibkr-deterministic-paper-feed`). Register on that value:

```csharp
builder.Services.AddSingleton<IOptionMarketDataProvider>(sp =>
    sp.GetRequiredService<IConfiguration>()["MarketData:Source"] switch
    {
        "ibkr-live" or "ibkr-delayed" => sp.GetRequiredService<IbkrOptionMarketDataProvider>(),
        _ => sp.GetRequiredService<DeterministicOptionMarketDataProvider>(),
    });
```

Extracting `IOptionMarketDataProvider` (`GetQuotes`, `GetOptionChain`) from the existing concrete
class is step 0 — `MarketDataService/Program.cs` currently injects the concrete type directly.

---

## Stage 1 — Connection only

`src/TradingStuff.IbkrGateway`: one `EClientSocket`, `EWrapper` implementation, the `EReader` pump
thread, reconnect-with-backoff, and `AddServiceDefaults()` for the shared auth/telemetry.

Endpoints: `GET /ibkr/status` returning connected, clientId, serverVersion, accounts, marketDataType,
last error. No market data, no orders.

Wire into AppHost per the parameter change in `SKILL.md`. Point
`GET /market-data/ibkr/status` at it.

**Done when:** `aspire start` with TWS paper running shows connected + a `DU` account; with TWS
*not* running, the service starts, reports unhealthy, and retries without crashing the AppHost.

## Stage 2 — Contract resolution

`reqContractDetails` → conId, with an in-memory cache keyed on underlying + expiry + strike + right +
currency, and the TCS bridge from `references/tws-api.md`.

Resolve the `OptionContract`-carries-`ConId` question here (trap #2 in `SKILL.md`) **before**
anything depends on it. Recommended: keep `OptionContract` unchanged and hold conIds in an
adapter-side cache, so `PaperExecutionEngine`'s record-equality dictionary lookups keep working.

`POST /ibkr/contracts/resolve` taking `OptionContract[]`.

**Done when:** a known-good SPY option resolves to a conId; a bogus strike returns a clean error
(200) rather than hanging; the existing 6 tests still pass untouched.

## Stage 3 — Chains

`reqSecDefOptParams` behind `GET /market-data/options/chains/{underlying}`. Deduplicate the
per-exchange callbacks, prefer `SMART`, filter to a strike window around spot (the deterministic
provider's ±5 strikes × 2 rights is the shape to preserve).

**Done when:** the endpoint returns real expirations/strikes with `MarketData:Source=ibkr-delayed`,
and still returns the deterministic chain on the default source.

## Stage 4 — Quotes and Greeks

`reqMktData` streaming, accumulate `tickPrice` + `tickOptionComputation` per tickerId, emit a
`QuoteSnapshot` when bid/ask/model-greeks are all present or a timeout fires. Guard every
`double.MaxValue` sentinel. Always `cancelMktData`. Set `Source` on the snapshot to `ibkr-live` /
`ibkr-delayed` so audit records show provenance.

Start on `reqMarketDataType(3)` (delayed) — it needs no OPRA subscription and exercises the whole
path.

**Done when:** `POST /market-data/options/quotes` returns live-shaped quotes with non-zero Greeks;
subscription count returns to zero after each call; the 6 tests still pass on the deterministic
source.

## Stage 5 — Account and positions (read-only)

`reqAccountSummary` / `reqPositions` → replace the stub behind
`ExecutionService/PortfolioProvider.cs` so `PortfolioSnapshot.BuyingPower` and `ExistingGreeks`
reflect the real paper account. Risk checks in `PortfolioRiskEvaluator` get meaningful inputs here —
this is what makes the existing risk limits real, and it carries zero order-placement risk.

**Done when:** `RiskEvaluationRequest.Portfolio` reflects actual paper-account buying power and
positions.

## Stage 6 — Order placement (paper only, last)

Only after 1–5 are stable. Combo/BAG construction, `placeOrder`, `orderStatus` → lifecycle mapping,
`execDetails` → `FillReport` with `FillLiquidity.BrokerReported` and `ExecId` dedupe.

Guards, all of them required:

- `IBKR:AllowLiveTrading` defaults to `false`, never set true in `AppHost` defaults, any
  `appsettings*.json`, or any test fixture.
- Startup assertion: if live trading is not enabled and the connected account does not start with
  `DU`, fail fast.
- Persist the IBKR orderId + permId against the internal `Guid OrderId` **before** transmitting.
- `PaperExecutionEngine` stays the default path; IBKR routing is opt-in per environment.

**Done when:** a 1-lot vertical submits against paper TWS, reaches `Filled`, and produces per-leg
`FillReport`s with `BrokerReported` liquidity — and no test in the suite can reach `placeOrder`.

---

## Test strategy

The 6 existing tests are unit tests with fake clients — they must never touch a socket. Keep them on
the deterministic provider permanently.

New IBKR tests split in two:

- **Unit** — combo construction (ratio/GCD/spread-count arithmetic, credit sign), `orderStatus` →
  lifecycle mapping, tick sentinel guarding, `decimal`↔`double` rounding. No socket. These cover the
  logic most likely to be wrong.
- **Integration** — marked with a trait so they are excluded by default, requiring a running paper
  TWS. Never in the default `dotnet test` run.

Record the pinned `IBApi` version in `docs/STATE.md`; callback signatures change between releases.
