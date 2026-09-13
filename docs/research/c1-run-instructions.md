# C1 Study Pipeline — Operator's Run Instructions

The C1 earnings study pipeline executes six verbs in sequence — `universe`, `events`, `timing`, `chains`, `closes`, and `compute` — over the frozen data directory `data/earnings-c1/`, writing intermediate tables and a deliverable memo. The governing registration is `docs/research/c1-preregistration-v2.md` (a pre-result amendment, blind intact). The v0 registration (`docs/research/c1-preregistration-v0.md`) is the superseded record kept in force for the universe rule, gates, window, measures, and splits per v2 §6. The memo written by the final verb, `data/earnings-c1/c1_memo.md`, is the Monday deliverable.

## Prerequisites

- **Net 10 SDK.** Exported environment variables (from `CLAUDE.md`):
  ```bash
  export DOTNET_CLI_HOME=/tmp/dotnet_home
  export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
  export DOTNET_NOLOGO=1
  mkdir -p /tmp/dotnet_home
  ```
- **Build with** `-p:SkipClientApp=true`: `dotnet build TradingStuff.slnx -p:SkipClientApp=true`
- **Theta Terminal** running locally on its default port (25503).
- **TWS** on the paper account (`DU` prefix) with IbkrGateway service running on its default port (8080); closes go through its HTTP endpoint, never a second socket.
- **EDGAR_USER_AGENT** environment variable set to a string declaring the operator and a contact per the SEC's fair-access policy (e.g., `"Research Bot contact@example.com"`). The tool refuses to run without it and never invents one.

## Run the three live tests first

Run these before the full pipeline to pin external dependencies:

```bash
dotnet test tests/TradingStuff.EarningsStudy.Tests/TradingStuff.EarningsStudy.Tests.csproj \
  --filter "Category=RequiresEdgar" -p:SkipClientApp=true
```

This pins the Eastern reading of EDGAR acceptance times: Apple's 2024-02-01 8-K must show acceptance hour 16.

```bash
TRADING_TEST_THETA=127.0.0.1:25503 \
dotnet test tests/TradingStuff.EarningsStudy.Tests/TradingStuff.EarningsStudy.Tests.csproj \
  --filter "Category=RequiresThetaTerminal" -p:SkipClientApp=true
```

This pins the Theta Terminal's EOD column header, whether `underlying_price` exists, the price scale, and whether EOD is served on the subscription or the minute fallback fires.

```bash
TRADING_TEST_GATEWAY_URL=http://127.0.0.1:8080 \
dotnet test tests/TradingStuff.EarningsStudy.Tests/TradingStuff.EarningsStudy.Tests.csproj \
  --filter "Category=RequiresGateway" -p:SkipClientApp=true
```

This pins a real gateway bars request against the paper account.

## Pipeline verbs

### 1. universe

```bash
dotnet run --project src/TradingStuff.EarningsStudy -p:SkipClientApp=true -- universe
```

Seeds from the frozen CBOE optionable directory, joins SEC ticker/CIK/exchange data, applies name-level gates (CIK-mapped; primary listing NYSE, Nasdaq, or NYSE American). Reads `data/earnings-c1/universe-seed-cboe-2026-09-12.csv`, writes `data/earnings-c1/universe.csv`. Expect roughly 5,300 seed rows.

### 2. events

```bash
EDGAR_USER_AGENT="<operator contact>" \
dotnet run --project src/TradingStuff.EarningsStudy -p:SkipClientApp=true -- events
```

Pulls EDGAR submissions JSON and emits every 8-K Item 2.02, applies event-level gates (not an 8-K/A, inside the registered window, one event per CIK-calendar-quarter). Caches all fetches under `data/earnings-c1/raw/edgar/` offline on reruns. Reads `data/earnings-c1/universe.csv`, writes `data/earnings-c1/events.csv` and `data/earnings-c1/shares_facts.csv`. One SEC request per eligible name at ≤10/s, so on the order of 10–15 minutes plus companyfacts requests.

### 3. timing

```bash
dotnet run --project src/TradingStuff.EarningsStudy -p:SkipClientApp=true -- timing
```

Classifies each kept event as BMO, AMC, or INTRADAY from EDGAR acceptance time (Eastern wall clock), resolves measurement dates on the NYSE calendar. Reads `data/earnings-c1/events.csv`, writes `data/earnings-c1/event_timing.csv`. Takes seconds.

### 4. chains

```bash
dotnet run --project src/TradingStuff.EarningsStudy -p:SkipClientApp=true -- chains
```

Pulls front-expiration chains from the Theta Terminal at pre-entry, entry, and exit dates (EOD report primary, 15:45 ET minute fallback), caches under `data/earnings-c1/raw/chains/`, computes IM and tier measures. For a smoke run, use `--limit 10` to test a few events; for full run, expect one Terminal request per event plus one per root, cached so reruns are offline. Reads `data/earnings-c1/event_timing.csv`, writes `data/earnings-c1/option_measures.csv`. How long the full run takes depends on the Terminal's request rate on your subscription, which this repository has not measured: smoke-run with `--limit 10`, time it, and extrapolate to the event count before committing to the full pull.

### 5. closes

```bash
dotnet run --project src/TradingStuff.EarningsStudy -p:SkipClientApp=true -- closes \
  --gateway-url http://127.0.0.1:8080
```

Pulls daily TRADES bars for every eligible symbol through the gateway's `POST /ibkr/history/bars`, caches each series under `data/earnings-c1/raw/bars/` with atomic completion markers for safe resume, joins pre-entry, entry, and exit closes. One gateway request per name under the pacing governor's ~54-per-10-minute historical budget, so on the order of 11 hours for ~3,500 names — start it early and it resumes safely. Reads `data/earnings-c1/event_timing.csv`, writes `data/earnings-c1/closes.csv`.

### 6. compute

```bash
dotnet run --project src/TradingStuff.EarningsStudy -p:SkipClientApp=true -- compute
```

Joins every table, applies remaining gates with counts, computes primary and secondary statistics with the week-clustered bootstrap (10,000 replications, default seed from registration). Writes `data/earnings-c1/c1_event_table.csv` and `data/earnings-c1/c1_memo.md`. Takes seconds.

## Deadline fallback: partial-window runs and final reruns

The v2 deadline fallback (§3) applies automatically: when the full registered window has not completed by Monday 09:00 CT, the compute verb restricts to the most recent K fully-fetched calendar quarters, K maximized, labels the memo PROVISIONAL, and prints the per-quarter coverage table. Nothing is deliverable when the run of fully covered quarters ending at 2025Q4 is empty; the compute verb then writes the memo with the coverage table and no verdict and exits non-zero. The full-window run remains owed whatever a provisional memo says.

To ensure whole recent quarters complete, `chains` processes the most recent print dates first (tied events broken by event ID). Start `closes` first and let it finish: it pulls one series per symbol, and a quarter counts as covered only when every event in it has both an option row and a closes row, so an incomplete `closes` run leaves every quarter uncovered no matter how far `chains` got. The final (full-window) run should pass `--require-full-window` so a provisional memo is refused rather than written.

## Reading the memo

`data/earnings-c1/c1_memo.md` is the deliverable. Consult these sections first:

- **Section 1.3: Coverage table** — per-quarter fetch completion status across the registered window.
- **Section 2: Exclusion table** — every gate, considered count, removed count.
- **Section 3: Quarantine rate** — timing-unresolvable events separated and counted.
- **Section 5: Verdict** — the mean(RF/IM) point estimate and its week-clustered 95% confidence interval, which decides the memo. Median(RF/IM) and P(RF < IM) are descriptive readouts (not deciding). The straddle hold-through cross-check prints whether its sign agrees with the verdict.
- **Section 9.2, three key tables:**
  - Parity-vs-close deviation quantiles (spot source quality).
  - Spot-source and price-source tallies (data coverage).
  - IM-collapse cross-tab (price-source dependencies).
- **Section 9.3: What the registered criterion does and does not establish** — the scope of the claim.

Note: two fetch statuses — `chain_empty` and `no_paired_strike` — are expected to read zero by construction.

## Live pins before unblinding any memo

No memo is unblinded before these tests pass:

```bash
dotnet test tests/TradingStuff.EarningsStudy.Tests/TradingStuff.EarningsStudy.Tests.csproj \
  --filter "Category=RequiresEdgar" -p:SkipClientApp=true
```

```bash
TRADING_TEST_THETA=127.0.0.1:25503 \
dotnet test tests/TradingStuff.EarningsStudy.Tests/TradingStuff.EarningsStudy.Tests.csproj \
  --filter "Category=RequiresThetaTerminal" -p:SkipClientApp=true
```

```bash
TRADING_TEST_GATEWAY_URL=http://127.0.0.1:8080 \
dotnet test tests/TradingStuff.EarningsStudy.Tests/TradingStuff.EarningsStudy.Tests.csproj \
  --filter "Category=RequiresGateway" -p:SkipClientApp=true
```

## Commit the results

After a successful run, commit the derived tables under `data/earnings-c1/` (not `raw/`, which is gitignored) and the memo:

```bash
git add data/earnings-c1/*.csv data/earnings-c1/c1_memo.md
git commit -m "C1 study results, <date>, <operator name>"
```

The `raw/` directories can be retained for faster reruns or deleted to recover disk space; they are not committed.

