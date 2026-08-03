# Plan C — Raw capture for shadow-record items 6–11

## Goal

The protocol's shadow record items 6–11 (achieved contracts and Greeks, simulated fill vs
contemporaneous quote, margin, actual paper P&L, idealized variance-swap P&L, independently
reconstructed counterfactual) need RAW inputs that cannot be backfilled later: fills,
positions, account/margin state, and quotes at decision time. This plan captures the raw
layer from day one. The DERIVED analytics (items 10–11 computations) are explicitly deferred
— they are reproducible from captured data at any time; missing capture is forever.

## Deliverables

1. **Migration 024**, two tables:
   - `research.paper_fills`: order id/ref, con_id, contract description (expiry/strike/right),
     side, quantity, fill price, fill time, commission if reported, capture_source. One row
     per fill event, append-only.
   - `research.paper_account_snapshots`: snapshot_at, account id (DU…), net liquidation,
     maintenance margin, init margin, excess liquidity, positions jsonb (con_id, description,
     qty, avg cost, market price/value as reported), capture_source. One row per snapshot,
     append-only. House style throughout; `COMMENT ON TABLE` states the
     raw-not-derived contract.
2. **Gateway surface**: whatever read endpoints the IbkrGateway lacks for account summary /
   positions / executions, added read-only (`/ibkr/account/...`). No order-path changes. TWS
   pacing: these are low-frequency reads; reuse the existing client wrapper patterns and its
   pacing governor.
3. **`PaperCaptureService`** (new hosted service in ResearchService, own file, own options):
   - After each session close (session clock, not wall clock): one account snapshot + pull
     the day's executions into `paper_fills`.
   - Idempotent per day (snapshot keyed by trading date + a claim, like shadow marks).
   - Gateway down ⇒ a recorded named refusal row pattern (absence visible), retry next pass.
4. **Decision-time quote capture**: extend the planner-intent record (already persisted with
   shadow marks / automation decisions) to include the contemporaneous NBBO of each leg it
   selected, if it does not already. Verify first — `SpyShortVolPlanner` may already quote
   legs; if so, confirm it lands in the persisted record and close the item with a test.
5. **Tests**: RequiresPostgres round-trips for both tables; idempotency (same day twice ⇒
   one snapshot); unit tests for the session-close trigger arithmetic. Gateway endpoint gets
   the existing gateway test treatment (Category=RequiresTws where a live socket is needed —
   note CLAUDE.md's warning that this suite is not a reliable single-run gate).

## Constraints and non-goals

- Do NOT touch `PaperAutomationService`, the planners' selection logic, or signal/gate code.
  This plan OBSERVES; it never influences.
- No Greeks computation in v1 — capture the raw quotes/positions; Greeks are derivable later
  from captured chain data. (Recording what TWS reports per-position is fine if the account
  read returns it for free.)
- Migration number 024; renumber if Plan A has not landed 023 first.

## Done means

After a session with a fill: `paper_fills` has the fill, `paper_account_snapshots` has the
close snapshot with margin fields populated, both idempotent on re-run, refusals recorded
when the gateway is down. Full suite green.
