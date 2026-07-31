# Staged migration: deterministic provider → IBKR

> **Status (2026-07-31): stages 1–4 and 6 complete**, verified live against a `DU` paper account
> on TWS server version 223 — real chains, real conIds, real Greeks, and a filled SPXW vertical
> round trip. Only stage 5 (account/position sync) remains.

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

## Stage 1 (DONE) — Connection only

`src/TradingStuff.IbkrGateway`: one `EClientSocket`, `EWrapper` implementation, the `EReader` pump
thread, reconnect-with-backoff, and `AddServiceDefaults()` for the shared auth/telemetry.

Endpoints: `GET /ibkr/status` returning connected, clientId, serverVersion, accounts, marketDataType,
last error. No market data, no orders.

Wire into AppHost per the parameter change in `SKILL.md`. Point
`GET /market-data/ibkr/status` at it.

**Done when:** `aspire start` with TWS paper running shows connected + a `DU` account; with TWS
*not* running, the service starts, reports unhealthy, and retries without crashing the AppHost.

## Stage 2 (DONE) — Contract resolution

`reqContractDetails` → conId, with an in-memory cache keyed on underlying + expiry + strike + right +
currency, and the TCS bridge from `references/tws-api.md`.

Resolved as trap #2 in `SKILL.md`: conIds live in an adapter-side cache keyed on
`OptionContractKey`, never on the whole `OptionContract` record.

`POST /ibkr/contracts/resolve` taking `OptionContract[]`.

**Done when:** a known-good SPY option resolves to a conId; a bogus strike returns a clean error
(200) rather than hanging; the existing 6 tests still pass untouched.

## Stage 3 (DONE) — Chains

`reqSecDefOptParams` behind `GET /market-data/options/chains/{underlying}`, filtered to a strike
window around spot (the deterministic provider's ±5 strikes × 2 rights is the shape preserved).

Segment selection is by **`tradingClass == symbol`**, *not* by `SMART` — SPY's only `SMART` segment
is the adjusted `2SPY` class with 3 strikes, while the real 489-strike chain has no `SMART` row at
all. See `references/tws-api.md`.

**Done when:** the endpoint returns real expirations/strikes with `MarketData:Source=ibkr-delayed`,
and still returns the deterministic chain on the default source.

## Stage 4 (DONE) — Quotes and Greeks

`reqMktData` streaming, accumulate `tickPrice` + `tickOptionComputation` per tickerId, emit a
`QuoteSnapshot` when bid/ask/model-greeks are all present or a timeout fires. Guard every
`double.MaxValue` sentinel. Always `cancelMktData`. Set `Source` on the snapshot to `ibkr-live` /
`ibkr-delayed` so audit records show provenance.

Start on `reqMarketDataType(3)` (delayed) — it needs no OPRA subscription and exercises the whole
path.

**Done when:** `POST /market-data/options/quotes` returns live-shaped quotes with non-zero Greeks;
subscription count returns to zero after each call; the 6 tests still pass on the deterministic
source.

## Stage 5 (not started) — Account and positions (read-only)

`reqAccountSummary` / `reqPositions` → replace the stub behind
`ExecutionService/PortfolioProvider.cs` so `PortfolioSnapshot.BuyingPower` and `ExistingGreeks`
reflect the real paper account. Risk checks in `PortfolioRiskEvaluator` get meaningful inputs here —
this is what makes the existing risk limits real, and it carries zero order-placement risk.

**Done when:** `RiskEvaluationRequest.Portfolio` reflects actual paper-account buying power and
positions.

## Stage 6 (DONE) — Order placement (paper only)

Combo/BAG construction, `placeOrder`, `orderStatus` → lifecycle mapping,
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

The suite is 91 unit tests using fake clients — none of them touch a socket, and none may. Keep them
on the deterministic provider permanently.

Already covered: tick sentinel guarding (price vs Greek sign rules), delayed-tick-field handling,
partial-quote settling, request/error correlation, error-code classification, chain segment
selection, provider selection fallback, and the `OptionContractKey` regressions.

Also covered: combo construction (ratio/GCD/spread-count arithmetic, credit sign), `orderStatus` →
lifecycle mapping, BAG-summary exclusion, and conId-based fill attribution.

Still to add when stage 5 lands:

- **Integration** — marked with a trait so they are excluded by default, requiring a running paper
  TWS. Never in the default `dotnet test` run.

Record the pinned `IBApi` version in `docs/STATE.md`; callback signatures change between releases.
