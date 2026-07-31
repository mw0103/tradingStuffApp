# Trading Execution Service Plan

> Amended 2026-07-31. Scope changed during implementation — see "Scope Changes" at the end for what
> changed and why. Current build status lives in `docs/STATE.md`, not here.

## Summary

Build a local-first Aspire monorepo for a trading platform. The first milestone is an end-to-end
options trade: an internal service submits a multileg options order, auth succeeds, market data
supplies quotes and Greeks, portfolio risk approves or rejects the order, execution produces fills,
state is persisted, lifecycle events are published, and audit surfaces show the workflow.

C# owns execution, risk, data processing, APIs, orchestration, and service contracts. Python ML is
deferred until the execution foundation is stable.

## Trading Modes

"Paper" became ambiguous once real broker routing existed — it meant both "fills invented locally"
and "real orders sent to a paper brokerage account". These are different risk profiles and must be
named separately.

| Mode | Selected by | What happens |
|---|---|---|
| **Simulated** | `Execution:Router=paper` (default) | `PaperExecutionEngine` computes fills from quote snapshots. No broker contact. |
| **Paper brokerage** | `Execution:Router=ibkr` + a `DU`-prefixed IBKR account | Real orders transmitted to IBKR, matched by the real venue, settled in simulated money. |
| **Live** | A funded (`U`-prefixed) account | **Out of scope for v1.** Gated by `IBKR:AllowLiveTrading`, which defaults to false. |

Both non-live modes are in scope. Simulated is the default everywhere, including every test.
Selecting paper brokerage is a deliberate per-environment act, and no default configuration or test
may reach it.

## Target Services

- `TradingStuff.ExecutionService`: REST order API, request validation, order lifecycle, order
  routing (simulated or broker), event publishing boundary.
- `TradingStuff.RiskService`: portfolio risk approval, max-loss checks, buying-power checks,
  duplicate-order protection, Greeks exposure limits.
- `TradingStuff.MarketDataService`: option contract/quote/Greeks boundary. Serves either deterministic
  generated data or real IBKR data, selected by `MarketData:Source`.
- `TradingStuff.IbkrGateway`: sole owner of the TWS socket. Contract resolution, option chains,
  streaming quotes and Greeks, combo order placement, and open-order reconciliation. Exists because a
  TWS connection is stateful and single-owner per client id, so no other service may connect.
- `TradingStuff.AuditDashboard`: local operator surface for service links and audit visibility.
- `TradingStuff.AppHost`: Aspire orchestration for services plus Postgres, RabbitMQ, Keycloak, and
  TWS connection parameters.
- `third_party/IBApi`: vendored IBKR TWS API client. Not ours; replaced wholesale on upgrade.
- Future `ml-service`: Python signal/model service, not part of v1.

## Execution Scope

- Asset class: options.
- Trading modes: simulated and paper brokerage. Live trading is explicitly out of scope for v1.
- Broker target: Interactive Brokers via the TWS socket API. Order placement is isolated in
  `TradingStuff.IbkrGateway`, which is the only component permitted to call `placeOrder`.
- Supported strategy families: verticals, calendars, diagonals, straddles, and strangles.
- Supported order behavior: market, limit, stop, stop-limit, cancel, and replace.
- Instrument note: equity options (SPY) trade regular hours only. Index options (SPX/SPXW) trade
  nearly 24x5 and are the instrument to use outside 09:30-16:15 ET.

### Milestone 1 acceptance

Submit an internal API request, pass risk, consume option quote/Greek data, produce fills, persist
state, and publish lifecycle events.

Persistence and event transport remain unmet — see `docs/STATE.md`. The milestone is not complete.

## Interfaces

Execution, risk, and market data:

- `POST /orders`: submit an order.
- `GET /orders`: list orders.
- `GET /orders/{id}`: fetch order state.
- `GET /orders/{id}/events`: fetch lifecycle events.
- `POST /orders/{id}/cancel`: request cancellation.
- `POST /orders/{id}/replace`: request replace.
- `POST /risk/evaluate-order`: evaluate pre-trade risk.
- `GET /risk/limits`: inspect configured risk limits.
- `POST /market-data/options/quotes`: fetch option quote snapshots.
- `GET /market-data/options/chains/{underlying}`: fetch an option chain.
- `GET /market-data/ibkr/status`: inspect IBKR market-data dependency status.

IBKR gateway (internal; not part of the public trading surface):

- `GET /ibkr/status`: TWS socket state, managed accounts, whether trading is permitted.
- `GET /ibkr/account/portfolio`: buying power, daily P&L, positions, and aggregate Greeks, with flags
  for whatever the read could not establish.
- `POST /ibkr/contracts/resolve`: resolve option contracts to IBKR conIds.
- `GET /ibkr/options/chains/{underlying}`: chain segment for a trading class.
- `POST /ibkr/options/quotes`: streaming quotes with Greeks.
- `POST /ibkr/orders`: place a combo order.
- `GET /ibkr/orders`, `GET /ibkr/orders/{id}`: order state from this process.
- `GET /ibkr/orders/open`: reconcile against what TWS actually holds, including orders this process
  never placed.
- `POST /ibkr/orders/{id}/cancel`: cancel.

Event contracts use stable names, order IDs, event IDs, correlation IDs, timestamps, and lifecycle
statuses.

## Data And Infrastructure

- Postgres is the intended durable store for orders, fills, quote snapshots, risk decisions, and
  audit records.
- RabbitMQ is the intended event transport for execution lifecycle events.
- Keycloak is the intended local OIDC issuer.
- Aspire models Postgres, RabbitMQ, Keycloak, all C# services, and TWS connection parameters.

**All three are currently started but unconnected.** No service holds a connection string to
Postgres or RabbitMQ, and authentication still uses a development bearer token rather than Keycloak.
`aspire start` therefore presents a fuller stack than the code actually uses. Closing this is the
bulk of the remaining milestone-1 work.

## Risk Model

V1 risk checks:

- Buying power.
- Max loss per order.
- Max position size/contracts per order.
- Max daily loss.
- Duplicate client order detection.
- Delta, gamma, theta, and vega exposure limits.
- Rejection of uncovered short volatility spreads.

All are implemented, and real portfolio inputs are now available: with `Portfolio:Source=ibkr` the
provider reads buying power, daily P&L, positions, and position Greeks from the IBKR account through
the gateway. The default remains the fixed development figures — a fixed buying power, zero daily
P&L, zero existing Greeks, and no positions — under which the daily loss check cannot fire and the
Greek limits measure only the incoming order.

**`Portfolio:Source` must be set to `ibkr` whenever `Execution:Router` is**, or real orders are
approved against fabricated inputs. Two limits remain even on the real source: equity and futures
positions cannot be represented by `PositionSnapshot`, so their exposure is reported as a warning
rather than counted; and a position whose Greeks cannot be quoted is flagged rather than estimated.

Every risk decision should retain inputs, quote snapshot references, computed exposure, result, and
reason codes once persistence is added.

## Test Plan

- Strategy validation tests for supported options shapes.
- Risk approval/rejection tests for every breach code.
- Fill tests for market and limit behavior.
- End-to-end workflow tests with fake risk and market-data clients.
- Broker adapter unit tests for logic that cannot be exercised without a live TWS: combo ratio
  arithmetic, credit signs, tick sentinel handling, fill attribution, error classification, chain
  segment selection.
- Later integration tests for Postgres, RabbitMQ, Keycloak JWT validation, IBKR market data, and
  Aspire runtime startup. These require a running paper TWS and must be excluded from the default
  test run.

No test may reach a broker. The default `dotnet test` run stays entirely on simulated execution and
the deterministic market-data provider.

Coverage is currently inverted against risk: the broker adapter is heavily tested while the risk
engine has one of twelve breach codes covered. Correcting that precedes further feature work.

## Milestone 2: IBKR market-research platform

Planned 2026-07-31. Full definition in `docs/plans/ibkr-edge-research-roadmap.md`; data-feasibility
evidence in `docs/research/ibkr-data-capability-matrix.md`; the pre-registered first study in
`docs/research/volatility-forecast-residual-study.md`; evidence base in
`docs/research/literature-evidence-matrix.md`.

Objective: discover, validate, and — equally — credibly reject trading edge in SPX volatility using
only IBKR data. Two tracks: (A) an immediately backtestable realized-volatility forecast-residual
study on deep SPX/SPY/VIX/ES underlying history, and (B) prospective recording of a standardized
SPX option-surface node set, which is time-critical because runtime probes established that IBKR
option history is only weeks deep and expired options return nothing.

Structure: extend `TradingStuff.IbkrGateway` (pacing governor, historical data, standing
subscription leases, raw-event recording, order-id persistence — the pacing and persistence gaps
are also milestone-1 debts); add `TradingStuff.ResearchService` + `TradingStuff.ResearchContracts`;
wire the already-modeled Postgres properly (`AddPostgres` + Npgsql). No new messaging or analytics
infrastructure. Research phases 0–8 are sequenced in the roadmap; nothing in milestone 2 places
live orders, and paper fills are schema-flagged as non-calibration data.

## Assumptions

- The workspace is greenfield.
- Local target stack is .NET 10, Aspire 13.4, Docker, and Python 3.14.
- TWS or IB Gateway runs locally, with socket clients enabled and this host trusted.
- IBKR publishes no NuGet package for the C# API, so it is vendored from the official installer and
  used under the IB API Non-Commercial License. Commercial operation requires a separate license.
- TWS Precautionary Settings block combo orders over the API until the percentage constraint is
  cleared. This is a per-workstation prerequisite, not a code concern.
- No orders are placed against a funded account in v1.
- Python ML remains out of scope until execution/risk/data behavior is stable.

## Scope Changes

Recorded so the deviations are deliberate and reviewable rather than silent.

**2026-07-31 — real broker order placement brought into v1.** The plan originally said "no live
order placement in milestone one" and "no live broker orders are placed in v1", with live placement
"isolated behind future adapters". Order routing to an IBKR paper brokerage account was implemented
and a round trip executed.

The original intent — never risk real money in v1 — is unchanged and now stated more precisely as
the trading-mode table above. What changed is that "paper" now also covers real orders to a
simulated-money brokerage account, which the original wording did not distinguish from locally
invented fills.

Consequence to be aware of: order placement was built ahead of real portfolio data, so orders now
reach a broker through risk checks whose inputs are stubbed. See the Risk Model section.

**2026-07-31 — IBKR modelled as a socket, not an HTTP service.** The plan described a "required
external IBKR Gateway URL". A TWS connection is a socket protocol, not HTTP, so this became
host/port/client-id parameters plus a dedicated gateway service.
