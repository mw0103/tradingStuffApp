using System.Globalization;

namespace TradingStuff.EarningsStudy.Options;

/// <summary>One option quote at a snapshot. Prices in dollars, parsed to decimal straight from the feed's text.</summary>
public sealed record ChainQuote(decimal Strike, bool IsCall, decimal Bid, decimal Ask)
{
    public bool HasTwoSidedMarket => Bid > 0 && Ask > 0 && Ask >= Bid;
    public decimal Mid => (Bid + Ask) / 2m;
}

/// <summary>The spot implied by put-call parity at the strike where the call and put mids are closest.</summary>
public sealed record ParitySpot(decimal Spot, decimal Strike, decimal CallMid, decimal PutMid, int PairsConsidered)
{
    /// <summary>
    /// True when this estimate is an interpolation rather than an extrapolation: the minimising
    /// strike has a qualifying strike on both sides of it AND <see cref="Spot"/> itself falls within
    /// the span of the qualifying strikes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are load-bearing and each catches what the other misses. If the minimising strike
    /// is the top or bottom of the ladder, the chain may simply have been truncated above or below
    /// spot, and the "nearest" strike is then the ladder's edge rather than a match — the failure
    /// class that collapsed the 54-node research grid (docs/STATE.md, Phase 1+2 review). If instead
    /// the minimising strike is interior but the quotes are not parity-consistent, K + C - P can land
    /// far outside the listed strikes; that is a number nothing in the chain supports, and the second
    /// half is what notices.
    /// </para>
    /// <para>
    /// Deliberately <c>false</c> by default, so a <see cref="ParitySpot"/> assembled positionally
    /// without the ladder context asserts nothing about bracketing rather than claiming the safe
    /// answer (docs/LESSONS.md 8: "assumed" is not "verified"). Only
    /// <see cref="ChainSelection.ImpliedSpot"/> is in a position to set it.
    /// </para>
    /// </remarks>
    public bool Bracketed { get; init; }
}

/// <summary>The ATM call/put pair selected for one snapshot.</summary>
public sealed record AtmPair(decimal Strike, ChainQuote Call, ChainQuote Put)
{
    public decimal StraddleMid => Call.Mid + Put.Mid;
    public decimal CombinedSpread => (Call.Ask - Call.Bid) + (Put.Ask - Put.Bid);

    /// <summary>
    /// The invariant <see cref="ChainSelection.SelectAtm"/> always produces: a call leg, a put leg,
    /// and both of them at <see cref="Strike"/>. Exposed rather than enforced in the constructor so
    /// that a hand-assembled pair can be asserted in a test without this record throwing inside a
    /// CSV round trip. Nothing downstream of <c>SelectAtm</c> can be malformed; a pair built by hand
    /// can, and both tier predicates are symmetric in the two legs, so a swap would not otherwise
    /// show up.
    /// </summary>
    public bool IsWellFormed => Call.IsCall && !Put.IsCall && Call.Strike == Strike && Put.Strike == Strike;
}

/// <summary>
/// Why a selection returned nothing. These are the values WP3b writes into
/// <c>option_measures.fetch_status</c>: a refusal is a recorded reason, never a missing row, because
/// the exclusion table is counted from rows that exist (docs/LESSONS.md 3).
/// </summary>
public static class ChainSelectionReasons
{
    /// <summary>No quote rows at all for the snapshot.</summary>
    public const string ChainEmpty = "chain_empty";

    /// <summary>Rows exist, but no strike carries a two-sided market on both legs, so parity has nothing to solve.</summary>
    public const string NoTwoSidedStrike = "no_two_sided_strike";

    /// <summary>Rows exist, but no single strike carries both a call row and a put row.</summary>
    public const string NoPairedStrike = "no_paired_strike";

    /// <summary>The nearest paired strike is the top or the bottom of the paired ladder: the pair would be an edge clamp, so the ATM selection is refused.</summary>
    public const string SpotOutsideStrikeLadder = "spot_outside_strike_ladder";
}

/// <summary>The outcome of an ATM selection: exactly one of <paramref name="Pair"/> and <paramref name="Reason"/> is set.</summary>
public sealed record AtmSelection(AtmPair? Pair, string? Reason, string? Detail);

/// <summary>The outcome of a parity solve: exactly one of <paramref name="Parity"/> and <paramref name="Reason"/> is set.</summary>
public sealed record ParitySpotResult(ParitySpot? Parity, string? Reason, string? Detail);

/// <summary>
/// WP3a. The pure selection semantics that define the primary sample: which expiration spans the
/// event, what the spot is, which strike is ATM, and which tier a pair falls in. No I/O. Every
/// rule here is quoted from the pre-registration and pinned by tests that include the degenerate
/// shapes (ties, one-sided quotes, same-day expiration, wide and asymmetric markets).
/// </summary>
/// <remarks>
/// <para>
/// The failure this module is built against is not a crash. It is a well-formed, plausible answer
/// that points at the wrong contract: an unbounded "nearest strike to the target" clamped 54 node
/// roles onto roughly 24 contracts, and every downstream report read healthy because each role was
/// pointing at a real, liquid, correctly-recorded option — the wrong one (docs/STATE.md, Phase 1+2
/// adversarial review). The structural lesson from that fix is reused here: <b>an edge clamp can
/// never satisfy a bracketing requirement</b>, at any strike increment or ladder width, so
/// <see cref="SelectAtm"/> refuses an edge pick instead of returning it. A refusal is recorded as a
/// reason on the event's row; it is never a dropped event.
/// </para>
/// <para>
/// Tie rule, everywhere in this file: <b>the lower strike wins</b>. Both selections are otherwise
/// order-dependent on the feed's row order, and "whichever the feed listed first" is not a rule that
/// survives a refetch. The choice of lower over higher is arbitrary, and it is written down here and
/// pinned by a test so it stays arbitrary-but-fixed rather than becoming incidental.
/// </para>
/// </remarks>
public static class ChainSelection
{
    /// <summary>
    /// A straddle mid outside this band of the reference spot is a unit error, not a market.
    /// See <see cref="LooksLikeDollarScale"/> for what the bounds are chosen to separate.
    /// </summary>
    public const decimal MinPlausibleStraddleFraction = 0.002m;

    /// <inheritdoc cref="MinPlausibleStraddleFraction"/>
    public const decimal MaxPlausibleStraddleFraction = 1.0m;

    /// <summary>The front expiration spanning the event: the earliest listed expiration on or after <paramref name="exitDate"/>. Null if none is listed.</summary>
    /// <remarks>
    /// An expiration falling exactly ON the exit date qualifies. The print is public before that
    /// close, so the straddle measured there is pure intrinsic — the cleanest exit measurement the
    /// study can take, not a degenerate one. DTE is recorded and never capped: a long-DTE front
    /// dilutes IM, and the pre-registration handles that with a registered split (DTE &lt;= 7 vs
    /// &gt; 7), not with a filter, because a DTE cap is a selection rule and selection rules are
    /// frozen. The input is whatever the feed listed — unsorted, possibly with duplicates — so this
    /// takes a minimum rather than assuming an order.
    /// </remarks>
    public static DateOnly? FrontExpiration(IEnumerable<DateOnly> listedExpirations, DateOnly exitDate)
    {
        DateOnly? front = null;

        foreach (var expiration in listedExpirations)
        {
            if (expiration >= exitDate && (front is null || expiration < front))
            {
                front = expiration;
            }
        }

        return front;
    }

    /// <summary>
    /// Parity-implied spot: at the strike minimising |call mid − put mid| over strikes with both
    /// sides two-sided, spot = K + C − P. Carry is ignored: at the DTEs this study sees the term is
    /// far below quote noise. Null when no strike has both sides quoted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carry, quantified rather than waved away: the parity term dropped is K(1 − e^(−rT)) plus the
    /// present value of any dividend before expiry. At 5% and 7 days that is about 0.1% of K — ten
    /// cents on a $100 strike, inside one tick of the bid/ask width an ATM option carries into an
    /// earnings print. A dividend ex-date inside the front window is the larger of the two omissions
    /// and biases the estimate down by the dividend's PV; it is bounded downstream, not here, because
    /// <c>option_measures</c> carries the official close beside this estimate
    /// (<c>spot_feed_entry</c> next to <c>spot_parity_entry</c>) precisely so the two can be
    /// reconciled per event instead of one of them being trusted.
    /// </para>
    /// <para>
    /// Both legs must be two-sided (bid &gt; 0, ask &gt;= bid). A zero bid means the mid is not a
    /// price anyone would trade at, and a crossed market means the two sides are not from the same
    /// instant; either turns K + C − P into arithmetic on noise. Ties go to the lower strike. The
    /// minimisation is over MIDS — not bids, not asks — so an asymmetric market at the money does not
    /// move the chosen strike.
    /// </para>
    /// </remarks>
    public static ParitySpot? ImpliedSpot(IReadOnlyList<ChainQuote> chain) =>
        ImpliedSpotWithReason(chain).Parity;

    /// <summary>
    /// <see cref="ImpliedSpot"/>, with the reason it returned nothing. WP3b writes
    /// <see cref="ParitySpotResult.Reason"/> into the event's <c>fetch_status</c> so that "no spot"
    /// is a counted row rather than an absence.
    /// </summary>
    public static ParitySpotResult ImpliedSpotWithReason(IReadOnlyList<ChainQuote> chain)
    {
        if (chain.Count == 0)
        {
            return new ParitySpotResult(null, ChainSelectionReasons.ChainEmpty, "The chain holds no quote rows.");
        }

        var ladder = PairByStrike(chain);
        var solvable = ladder
            .Where(s => s.Call is { HasTwoSidedMarket: true } && s.Put is { HasTwoSidedMarket: true })
            .ToArray();

        if (solvable.Length == 0)
        {
            var bothRows = ladder.Count(s => s.Call is not null && s.Put is not null);
            var twoSidedCalls = ladder.Count(s => s.Call is { HasTwoSidedMarket: true });
            var twoSidedPuts = ladder.Count(s => s.Put is { HasTwoSidedMarket: true });

            return new ParitySpotResult(null, ChainSelectionReasons.NoTwoSidedStrike, string.Create(
                CultureInfo.InvariantCulture,
                $"None of the {ladder.Count} listed strikes has a two-sided market on both legs " +
                $"({bothRows} strike(s) carry both rows; {twoSidedCalls} have a two-sided call, " +
                $"{twoSidedPuts} a two-sided put)."));
        }

        // Ordered by strike ascending (PairByStrike), then |C - P| ascending with the lower strike
        // breaking the tie explicitly rather than relying on the sort being stable.
        var best = solvable
            .OrderBy(s => Math.Abs(s.Call!.Mid - s.Put!.Mid))
            .ThenBy(s => s.Strike)
            .First();

        var callMid = best.Call!.Mid;
        var putMid = best.Put!.Mid;
        var spot = best.Strike + callMid - putMid;

        var low = solvable[0].Strike;
        var high = solvable[^1].Strike;
        var bracketed = best.Strike > low && best.Strike < high && spot >= low && spot <= high;

        return new ParitySpotResult(
            new ParitySpot(spot, best.Strike, callMid, putMid, solvable.Length) { Bracketed = bracketed },
            null,
            null);
    }

    /// <summary>
    /// The ATM pair: the strike nearest <paramref name="spot"/> at which both a call and a put quote
    /// exist (a quote row, not necessarily a positive bid). Ties resolve to the lower strike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A null here is a refusal, not an absence.</b> If the nearest paired strike is the top or
    /// the bottom of the paired ladder, this returns nothing and WP3b records
    /// <see cref="ChainSelectionReasons.SpotOutsideStrikeLadder"/> against the event. Returning the
    /// edge strike instead would be an edge clamp: a real, liquid, correctly-quoted pair that is not
    /// the ATM pair, indistinguishable in every downstream report from a correct selection — which is
    /// exactly how the 54-node grid collapsed while reporting full health. A bracketing requirement
    /// cannot be satisfied by a clamp at any strike increment or ladder width, which is why the guard
    /// is written as bracketing and not as a distance tolerance.
    /// </para>
    /// <para>
    /// The ladder the guard measures against is the <b>paired</b> ladder — the strikes eligible to be
    /// selected — not every strike present in the chain. Measuring against the whole chain would let
    /// the very failure through: with a call-only row listed at 50 and paired strikes at 90/100/110,
    /// a spot of 60 clamps to 90, and 90 is not the chain's lowest strike, so a whole-chain test
    /// would pass a clamp. Every strike that is the whole chain's extremum is also the paired
    /// ladder's extremum, so this is the strictly stronger of the two readings, in the direction the
    /// incident says to be strict.
    /// </para>
    /// <para>
    /// Consequences worth knowing before reading a refusal count: a ladder of fewer than three paired
    /// strikes can never produce a pair, and a spot sitting exactly on the lowest or highest paired
    /// strike is refused too (nothing in the chain says whether the ladder was truncated there). On a
    /// real front-expiry chain, spot sits well inside the ladder, so these refusals should be close
    /// to zero. If WP3b's exclusion table shows otherwise, the fetch is truncating chains — which is
    /// the thing this refusal exists to make visible.
    /// </para>
    /// </remarks>
    public static AtmPair? SelectAtm(IReadOnlyList<ChainQuote> chain, decimal spot) =>
        SelectAtmWithReason(chain, spot).Pair;

    /// <summary>
    /// <see cref="SelectAtm"/>, with the reason it refused. WP3b writes
    /// <see cref="AtmSelection.Reason"/> into the event's <c>fetch_status</c> and
    /// <see cref="AtmSelection.Detail"/> into its note, so a refused event is a counted row in the
    /// exclusion table rather than a silent drop.
    /// </summary>
    public static AtmSelection SelectAtmWithReason(IReadOnlyList<ChainQuote> chain, decimal spot)
    {
        if (chain.Count == 0)
        {
            return new AtmSelection(null, ChainSelectionReasons.ChainEmpty, "The chain holds no quote rows.");
        }

        var ladder = PairByStrike(chain);
        var paired = ladder.Where(s => s.Call is not null && s.Put is not null).ToArray();

        if (paired.Length == 0)
        {
            return new AtmSelection(null, ChainSelectionReasons.NoPairedStrike, string.Create(
                CultureInfo.InvariantCulture,
                $"None of the {ladder.Count} listed strikes carries both a call row and a put row."));
        }

        var nearest = paired
            .OrderBy(s => Math.Abs(s.Strike - spot))
            .ThenBy(s => s.Strike)
            .First();

        var low = paired[0].Strike;
        var high = paired[^1].Strike;

        if (nearest.Strike <= low || nearest.Strike >= high)
        {
            return new AtmSelection(null, ChainSelectionReasons.SpotOutsideStrikeLadder, string.Create(
                CultureInfo.InvariantCulture,
                $"Spot {spot:F4} is nearest the paired strike {nearest.Strike:F4}, which is the " +
                $"{(nearest.Strike <= low ? "bottom" : "top")} of the paired ladder {low:F4}-{high:F4} " +
                $"({paired.Length} paired strike(s)). The nearest listed strike is the ladder's edge " +
                $"rather than a bracketing pair, so this is a clamp and not an ATM selection."));
        }

        return new AtmSelection(new AtmPair(nearest.Strike, nearest.Call!, nearest.Put!), null, null);
    }

    /// <summary>Quotable: ATM call AND put bid &gt; 0 at the entry snapshot.</summary>
    /// <remarks>
    /// Straight from the pre-registration, and AND is the whole content of it: a straddle with one
    /// zero bid is a straddle nobody could have sold, so its mid is not a price. The all-quotable
    /// tier is the study's secondary sample; the tradable tier below is the primary one.
    /// </remarks>
    public static bool IsQuotable(AtmPair pair) =>
        pair.Call.Bid > 0 && pair.Put.Bid > 0;

    /// <summary>Tradable tier: combined ATM spread &lt;= 15% of the straddle mid. Requires quotable.</summary>
    /// <remarks>
    /// Equality passes: the pre-registration says "&lt;= 15%", and a boundary event belongs to the
    /// primary sample, not to the rounding. Written as a multiplication rather than a division so
    /// that no ratio is formed — a zero straddle mid would otherwise throw, and (worse) the degenerate
    /// 0/0 market satisfies <c>0 &lt;= 0.15 * 0</c>, so an entirely unquoted pair would be reported
    /// tradable if the quotable gate were not also required here.
    /// </remarks>
    public static bool IsTradable(AtmPair pair) =>
        IsQuotable(pair) && pair.CombinedSpread <= C1Registration.TradableSpreadFraction * pair.StraddleMid;

    /// <summary>
    /// Sanity-checks the units of a chain: is its ATM straddle a plausible fraction of a reference
    /// spot taken from another source (the official close)? True = plausible, false = implausible,
    /// <b>null = not measurable</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This module cannot tell dollars from cents by looking at a quote — 2.50 and 250 are both
    /// well-formed. What it can do is refuse to be the only thing standing between a feed's unit
    /// change and the study's headline number: IM is a straddle divided by spot, so a 100x scale
    /// error multiplies every IM by 100 and drives RF/IM to zero, which reads as an enormous,
    /// beautifully consistent volatility premium. Give WP3b a reference price from IBKR's official
    /// closes and this becomes a startup assertion rather than a hope (docs/LESSONS.md 9: when two
    /// sources must agree, check them together and refuse).
    /// </para>
    /// <para>
    /// The band is deliberately wide, because it is separating scale errors and nothing else, and
    /// what it catches is worth stating exactly rather than loosely. The upper bound of 100% flags a
    /// 100x error on any straddle above 1% of spot; the lower bound of 0.2% flags a 1/100th error on
    /// any straddle below 20% of spot. A front-expiry ATM straddle over an earnings print runs
    /// roughly 3%-15% of spot, so both errors are caught across the whole range this study sees —
    /// and a cents-scale error on a straddle fatter than 20% of spot is the case that would slip
    /// through. Nothing real reaches the upper bound: at these DTEs even a 200% implied vol gives an
    /// ATM straddle near 22% of spot. Tighten the bounds per snapshot kind through the optional
    /// parameters if a caller has something better than this to assert.
    /// </para>
    /// <para>
    /// Null when there is no ATM straddle to measure — no selectable pair, a leg without a two-sided
    /// market, or a non-positive reference price. Returning false in those cases would report a
    /// missing chain as a unit fault, and a permanently-red gate is a gate nobody reads
    /// (docs/LESSONS.md 10); "not measured" is a first-class answer (docs/LESSONS.md 8).
    /// </para>
    /// </remarks>
    public static bool? LooksLikeDollarScale(
        IReadOnlyList<ChainQuote> chain,
        decimal referencePrice,
        decimal minFraction = MinPlausibleStraddleFraction,
        decimal maxFraction = MaxPlausibleStraddleFraction)
    {
        if (referencePrice <= 0)
        {
            return null;
        }

        var pair = SelectAtm(chain, referencePrice);

        if (pair is null || !pair.Call.HasTwoSidedMarket || !pair.Put.HasTwoSidedMarket)
        {
            return null;
        }

        var fraction = pair.StraddleMid / referencePrice;

        return fraction >= minFraction && fraction <= maxFraction;
    }

    /// <summary>
    /// The chain collapsed to one row per strike, ascending, with each side attached where it exists.
    /// </summary>
    /// <remarks>
    /// The ascending order is what makes "ties go to the lower strike" and the ladder's low/high ends
    /// well defined without re-sorting at each use. A strike carrying two rows for the same side is
    /// not expected from an EOD chain; if one ever appears, the first row in feed order wins, which is
    /// at least deterministic — it is not a merge, and nothing here pretends to reconcile them.
    /// </remarks>
    private static IReadOnlyList<StrikeRows> PairByStrike(IReadOnlyList<ChainQuote> chain) =>
        chain
            .GroupBy(q => q.Strike)
            .OrderBy(g => g.Key)
            .Select(g => new StrikeRows(
                g.Key,
                g.FirstOrDefault(q => q.IsCall),
                g.FirstOrDefault(q => !q.IsCall)))
            .ToArray();

    private sealed record StrikeRows(decimal Strike, ChainQuote? Call, ChainQuote? Put);
}
