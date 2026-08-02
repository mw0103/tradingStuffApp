# Hedged-carry menu — declared before any evaluation

Written 2026-08-02, before any hedge structure has been backtested or paper-traded. Motivated by
the standing critique of short volatility — consistently profitable until it is not — and by this
programme's own numbers: the constant one-vega short's worst 21-day window is −88.7 vol points
against a +5.5 mean (one bad window ≈ 16 windows of carry), no tested conditioning rule reduced
per-unit downside, and the crash windows sat in ATTRACTIVE-spread buckets. Timing the tail has
failed at this granularity; the open question is *structuring* against it.

## The framing that keeps this honest

Every hedge is negative carry, and tail protection is priced by the same market whose premium we
harvest — buying it back naively returns the edge to whoever sold it to us. So the question is
never "hedge or not"; it is **which slice of the premium do we keep, and which do we pay back for
survivability**. The metric pair for every candidate below, fixed now:

- **Carry retained**: mean 21-day P&L as a fraction of the unhedged constant short's.
- **Tail improvement**: worst-window and downside-deviation improvement, per unit of carry
  given up.

A candidate is interesting iff it buys tail relief at a better exchange rate than shrinking
position size does — because **sizing down is the null hedge**: halving vega halves both carry
and tail for free. Any structure that trades carry for tail at worse than 1:1 against simple
downsizing is dominated and gets discarded no matter how clever it looks.

## The menu (evaluation order, all declared now)

1. **Defined-risk spread width** (already built: `SpyShortVolPlanner`). The long wing IS a hedge;
   width choice sets the carry/tail exchange rate. Evaluate 2–3 declared widths, not a scan.
2. **Tranching / laddered entries.** Split the book across 3–4 overlapping 21-day tranches
   started a week apart. Pure path-diversification: no premium given up at all, reduces
   single-window concentration mechanically. Cheapest item on the menu and probably the best.
3. **Sizing from the observed tail** (the null hedge, formalized). Cap vega so that
   (worst historical window × vega) is a declared, survivable loss. Uses our own worst-window
   estimate directly; costs nothing but scale.
4. **Financed crash bone.** Spend a declared fixed fraction of expected carry (e.g. 10–20%) on
   far-OTM protection (deep SPX puts or VIX calls, held constantly). The classic answer; the
   known risk is that constant convexity is expensive enough to consume the edge — which is
   exactly what the metric pair measures.
5. **Conditional hedging on the A4 slope** (GATED on A4's backtest passing). Buy the crash bone
   only when the short-dated implied slope signals stress. This is the only menu item that could
   protect without paying the constant-insurance bill — and it is exactly the A4 hypothesis in
   hedge form. Not evaluable until A4 is.
6. **Early-unwind / stop rules.** Declared with suspicion: unwind rules change the payoff into a
   path-dependent one and are the most tunable (= most overfittable) item here. Anything tested
   must be declared before evaluation, and a rule that only helps in one historical episode is
   noise.

## Where each gets evaluated

**Historical first, paper second.** The ThetaData chain history (readiness audit in progress,
coverage dense from ~2014 including every stress window sampled) makes STRUCTURE-LEVEL
backtesting possible: actual spread and hedge P&L reconstructed from real quotes, not the
idealized variance swap. That is the right place to compare menu items — 200+ historical windows
instead of 12 per year of paper. The A4 data pull and this share the same ingestion work.

Paper then does what only paper can: margin behaviour of the hedged book, real bid–ask cost of
the wings and bones, assignment/expiry mechanics, and whether the structure's Greeks drift the
way the backtest assumed. The paper-run protocol's constant-one-vega mandate stays in force
until a hedged structure has EARNED promotion through the historical comparison + a registered
decision; the shadow record simply gains columns for the hedge candidates when they exist.

## Discipline note

This menu is a new rule-search space, and the adaptive-search dangers from the decision-layer
review apply verbatim: declared variants only, no width/fraction scans tuned to the sample, the
carry-retained/tail-improvement metric pair fixed in advance, sizing-down as the mandatory
dominance baseline, and the holdout untouched. The menu does not get edited to fit results;
losers get recorded.
