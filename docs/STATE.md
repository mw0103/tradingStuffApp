# State

Updated: 2026-07-31

## Done

- Aspire/.NET solution scaffolded.
- Execution, risk, market-data, audit dashboard, contracts, service defaults, and AppHost projects created.
- Paper options workflow implemented with strategy validation, Greeks-aware risk, deterministic quotes, fills, lifecycle events, and local auth.
- Tests pass: 91/91. Full solution builds.

### IBKR integration (stages 1-4 and 6 of `.claude/skills/ibkr/references/migration-plan.md`)

- **IBApi vendored** at `third_party/IBApi` — TWS API **10.45.01**, extracted from the official
  Windows installer (IBKR publishes no NuGet package). See `third_party/IBApi/README.md`.
- **`TradingStuff.IbkrGateway`** service owns the single TWS socket: connection lifecycle, EReader
  pump, reconnect with backoff, request/error correlation, and a health check that reports the real
  socket state.
- **Contract resolution** with a conId cache; **option chains** via `reqSecDefOptParams`;
  **streaming quotes with Greeks** via `reqMktData` + `tickOptionComputation`.
- **`MarketDataService` provider switch** on `MarketData:Source` — `ibkr-live` / `ibkr-delayed` route
  to the gateway, anything else (including a typo) falls back to the deterministic feed.
- **AppHost rewired**: the broker is no longer a bogus HTTP `AddExternalService`; it is
  `ibkr-host` / `ibkr-port` / `ibkr-client-id` / `ibkr-market-data-type` parameters plus the gateway project.
- **Verified live** against a `DU` paper account, TWS server version 223: real SPY chain
  (489 strikes, correct trading class), real conIds, real Greeks.

### IBKR order routing (stage 6)

- **`IbkrOrderBuilder`** builds combo (BAG) orders: GCD-reduced leg ratios + spread count, signed
  per-spread net price, order-type/TIF mapping, `orderStatus` → lifecycle mapping.
- **`IbkrOrderTracker`** assembles `orderStatus` / `execDetails` / `commissionAndFeesReport` into
  order state, dedupes executions on `ExecId`, and holds terminal outcomes against late chatter.
- **`IbkrOrderClient`** is the only caller of `placeOrder`, gated on `EnsureTradingPermitted()`.
- **`IOrderRouter`** in ExecutionService: `paper` (default) or `ibkr`, per `Execution:Router`.
- **Reconciliation**: `GET /ibkr/orders/open` reports what TWS considers open, including orders this
  process never placed.
- Request ids, ticker ids, and order ids now share **one sequence seeded from `nextValidId`** —
  previously both started at 1, so an `error(id, ...)` callback could not be routed unambiguously.
- **Index option support**: `IND` underlying resolution, `tradingClass` selection (SPX vs SPXW),
  `Contract.TradingClass` on resolution, and `Order.OutsideRth` for global trading hours.
- **A complete round trip filled on the paper account** (2026-07-31, pre-market):
  a 1-lot SPXW 7435/7440 call vertical opened at **3.80 debit** (order 9, permId 1749246243) and
  closed at **3.40 credit**, both at the natural, with correct per-leg fills and commissions.
  Account left flat with no resting orders.
- Reconnect backoff now treats a short-lived session as a failure. TWS can accept a socket and
  immediately reset it, and the previous logic reconnected at the base interval indefinitely.

### Bugs the live round trip exposed

- **Combo fills counted the BAG summary as a leg.** IBKR reports three executions for a two-leg
  spread — the BAG carrying the net price, plus one per leg. The BAG row is now skipped, and leg rows
  are attributed by conId rather than arrival order (legs do not fill in request order, and one leg
  can fill in pieces while the other has not started).
- **Net-credit fills reported an average price of 0.** A combo's `avgFillPrice` is signed and arrives
  negative for a credit; it was being run through the price converter, which correctly rejects
  negatives for an option quote but discards every credit here.

### Bugs fixed

- `PaperExecutionEngine` correlated quotes to legs on the whole `OptionContract` record. Record
  equality covers every property, so any broker-enriched quote threw `KeyNotFoundException`
  mid-order. Now keyed on `OptionContractKey`; an unquoted leg fails the order instead of throwing.
- `ServiceClientConfiguration` moved from `ExecutionService` to `ServiceDefaults` so other services
  can configure internal clients without depending on ExecutionService.
- `IbkrConnection.Dispose` threw `ObjectDisposedException` during host shutdown, surfacing as an
  unhandled crash on every stop.

## Left

- Replace in-memory order/event stores with Postgres.
- Replace in-memory event publisher with RabbitMQ.
- Replace dev bearer-token auth with Keycloak/OIDC JWT validation.
- IBKR stage 5: read-only account/position sync to replace the stubbed `PortfolioProvider`.
- Add Python ML signal service.
- Add richer audit dashboard.
- Address Aspire transitive `MessagePack` advisories when upstream package is patched.

## Trading prerequisites

**TWS Precautionary Settings block combo orders until cleared.** Error 163 — *"price exceeds the
Percentage constraint of 3%"* — comes from TWS's client-side presets, not an exchange, and it rejects
even a marketable spread priced at the natural against a live book: for a combo TWS compares the
*net* against a leg/underlying reference, so every spread net looks wildly off.

**Global Configuration → Presets → Options → Precautionary Settings → clear or widen *Percentage*.**
Check *Size Limit*, *Total Value Limit*, and *Number of Ticks* too. Once cleared, orders fill
normally — that is what unblocked the round trip above.

**TWS may reset API connections** (accepts the socket, then `RST`) when a modal dialog is awaiting
input. The gateway now backs off on short-lived sessions instead of reconnecting every few seconds,
but a flapping connection means TWS needs attention.

To route orders through IBKR, with the gateway running:

```bash
export Execution__Router=ibkr                # route orders to IBKR, not the simulated engine
export IBKR__OutsideRegularTradingHours=true # required outside 09:30-16:15 ET (SPX/SPXW)

curl -H "Authorization: Bearer dev-internal-token" localhost:<port>/ibkr/orders/open   # reconcile
```

Order routing stays opt-in: `Execution:Router` must be exactly `ibkr`, and the gateway's
`IBKR:AllowLiveTrading` must be true for any non-`DU` account.

### Trade SPX, not SPY, outside regular hours

SPY options are regular-hours only — pre-market they have no book at all (bid/ask 0). **SPXW** trades
nearly 24×5 and quotes properly: 7435C 32.30/32.60, delta 0.662, observed live at ~08:15 ET.

Index options need four things equities do not, all now handled:

- The underlying is `IND` on CBOE, not `STK` on SMART (`ResolveUnderlyingAsync` probes both).
- Pass `?tradingClass=SPXW` — plain `SPX` is the AM-settled monthly series that does not trade
  extended hours.
- `Contract.TradingClass` must be set when resolving conIds, or SPX and SPXW are ambiguous.
- `IBKR:OutsideRegularTradingHours` must be true, or the order is held until the regular session.

## Notes

- Market data defaults to **delayed** (`ibkr-market-data-type=3`) because it needs no OPRA
  subscription. Set to `1` for live once the account has market data subscriptions.
- `SubmitOrderRequest.LimitPrice` is a **whole-order** net (matching `PaperExecutionEngine`); TWS
  wants a **per-combo** net. `IbkrOrderBuilder.PerSpreadPrice` converts. They are identical at one
  spread, so the difference only appears on multi-lot orders.
