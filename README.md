# TradingStuff

Greenfield .NET Aspire trading microservice workspace focused on paper options execution.

## Current Slice

- C# execution service with REST order APIs.
- C# risk service with portfolio, max-loss, buying-power, duplicate-order, and Greeks-aware checks.
- C# market-data service serving either deterministic quotes or real IBKR data, selected by config.
- C# IBKR gateway service owning the single TWS socket: contract resolution, option chains, and streaming quotes with Greeks.
- C# audit dashboard with local operator links.
- Shared contracts for options, multileg orders, quotes, fills, risk decisions, and lifecycle events.
- Aspire AppHost wiring for Postgres, RabbitMQ, Keycloak, TWS connection parameters, and all services.
- xUnit coverage (125 tests) for strategy validation, risk rejection, fills, workflow orchestration, and the IBKR adapter logic.

## Run

Use a writable .NET CLI home in this environment:

```bash
mkdir -p /tmp/dotnet_home
export DOTNET_CLI_HOME=/tmp/dotnet_home
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
```

Build and test:

```bash
dotnet build TradingStuff.slnx
dotnet test tests/TradingStuff.Tests/TradingStuff.Tests.csproj -m:1 --no-restore
```

Start the distributed app:

```bash
aspire start --non-interactive
```

## IBKR / TWS

The broker connection is a **TWS socket**, not HTTP. AppHost parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `ibkr-host` | `127.0.0.1` | Host running TWS or IB Gateway |
| `ibkr-port` | `7497` | 7497 TWS paper, 7496 TWS live, 4002 Gateway paper, 4001 Gateway live |
| `ibkr-client-id` | `11` | Must be unique per connected API client |
| `ibkr-market-data-type` | `3` | 1 live, 2 frozen, 3 delayed, 4 delayed-frozen |

In TWS: enable **Configure → API → Settings → Enable ActiveX and Socket Clients**, and add this host
to **Trusted IPs**.

### Pulling real data

Market data defaults to the deterministic feed. To route through IBKR, set on `marketdataservice`:

```text
MarketData__Source=ibkr-delayed     # or ibkr-live
```

Delayed needs no OPRA subscription, which makes first-run setup work without market data
entitlements. Anything unrecognised falls back to the deterministic feed rather than silently
hitting the broker.

Gateway endpoints (bearer `dev-internal-token`):

```bash
curl -H "Authorization: Bearer dev-internal-token" localhost:<port>/ibkr/status
curl -H "Authorization: Bearer dev-internal-token" "localhost:<port>/ibkr/options/chains/SPY?window=3"
curl -H "Authorization: Bearer dev-internal-token" -H 'Content-Type: application/json' \
  -d '{"contracts":[ ... ]}' localhost:<port>/ibkr/options/quotes
```

Only the gateway connects to TWS. A TWS connection is stateful and single-owner per client id, so no
other service may open its own — they all go through the gateway over internal HTTP.

### Placing orders

Order routing is **opt-in twice**:

```text
Execution__Router=ibkr        # on executionservice; "paper" (default) simulates fills
IBKR__AllowLiveTrading=true   # on the gateway; only needed for a non-DU account
```

Anything other than the exact string `ibkr` stays on the simulated engine. `IbkrOrderClient` is the
only code that calls `placeOrder`, and it checks the trading gate first.

```bash
curl -H "Authorization: Bearer dev-internal-token" localhost:<port>/ibkr/orders        # this run
curl -H "Authorization: Bearer dev-internal-token" localhost:<port>/ibkr/orders/open   # what TWS holds
```

**TWS rejects API combo orders out of the box.** Error 163 ("price exceeds the Percentage constraint
of 3%") comes from TWS's own Precautionary Settings before the order reaches an exchange — it rejects
even a marketable spread priced at the natural against a live book. Clear or widen *Percentage* under
**Global Configuration → Presets → Options → Precautionary Settings**.

### Extended hours: use SPX, not SPY

SPY options are regular-hours only and have no book pre-market. SPXW trades nearly 24×5:

```bash
curl -H "Authorization: Bearer dev-internal-token" \
  "localhost:<port>/ibkr/options/chains/SPX?tradingClass=SPXW&window=2"
```

`tradingClass=SPXW` matters — plain `SPX` is the AM-settled monthly series that does not trade
extended hours. Orders outside 09:30–16:15 ET also need `IBKR__OutsideRegularTradingHours=true`.

### Safety

No orders against a funded account in v1 — see the trading-mode table in `docs/PLAN.md`.
*Simulated* (fills invented locally) and *paper brokerage* (real orders to a `DU` account) are both
in scope and are not the same thing.
`IBKR:AllowLiveTrading` defaults to false; if the connected account is not `DU`-prefixed while it is
false, the gateway logs a critical warning and blocks order placement while still serving market data.

The vendored IBKR API is subject to the **IB API Non-Commercial License**; commercial operation
requires a separate license from IBKR. See `third_party/IBApi/README.md`.

## Auth

Services currently use a local bearer token scheme for internal calls while Keycloak is modeled in Aspire:

```text
Authorization: Bearer dev-internal-token
```

The next production step is to replace `DevelopmentJwtAuthenticationHandler` with real OIDC/JWT bearer validation against Keycloak.

## Known Follow-ups

- Replace in-memory repositories/outbox with Postgres-backed order state and RabbitMQ publishing.
  Both containers currently start unused, as does Keycloak.
- Replace the stubbed `PortfolioProvider` with real IBKR account/position data. Until then the risk
  engine runs on fabricated buying power, zero daily P&L, and zero existing Greeks.
- Cover the remaining 11 risk breach codes with tests.
- Add the Python ML signal service after execution/risk/data behavior is stable.
- Address Aspire 13.4.2 transitive `MessagePack` vulnerability warnings when an upstream patched Aspire package is available.
