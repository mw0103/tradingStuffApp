using TradingStuff.EarningsStudy.Options;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// WP3a selection semantics. These are the rules that decide which contracts the study measures, so
/// the shapes that matter are the degenerate ones: a chain truncated above or below spot, a strike
/// quoted on one side only, a crossed market, a tie, a ladder too short to bracket anything. Each
/// test was negative-controlled by reintroducing the defect it guards against and confirming it fails
/// (docs/LESSONS.md 2); the mutations are listed in the commit's report.
/// </summary>
public sealed class ChainSelectionTests
{
    private static ChainQuote Call(decimal strike, decimal bid, decimal ask) => new(strike, IsCall: true, bid, ask);

    private static ChainQuote Put(decimal strike, decimal bid, decimal ask) => new(strike, IsCall: false, bid, ask);

    /// <summary>
    /// A chain where put-call parity holds exactly at every strike: C − P = spot − K, so |C − P| is
    /// minimised at the strike nearest spot and the parity solve must return <paramref name="spot"/>
    /// to the cent. Time value is flat across strikes, which no real smile is — that is deliberate,
    /// because it makes the expected answer arithmetic rather than an approximation.
    /// </summary>
    private static List<ChainQuote> ParityChain(
        decimal spot,
        IEnumerable<decimal> strikes,
        decimal timeValue = 3.00m,
        decimal halfSpread = 0.10m)
    {
        var rows = new List<ChainQuote>();

        foreach (var strike in strikes)
        {
            var callMid = Math.Max(spot - strike, 0m) + timeValue;
            var putMid = Math.Max(strike - spot, 0m) + timeValue;

            rows.Add(Call(strike, callMid - halfSpread, callMid + halfSpread));
            rows.Add(Put(strike, putMid - halfSpread, putMid + halfSpread));
        }

        return rows;
    }

    private static List<ChainQuote> Scaled(IEnumerable<ChainQuote> chain, decimal factor) =>
        chain.Select(q => new ChainQuote(q.Strike, q.IsCall, q.Bid * factor, q.Ask * factor)).ToList();

    private static List<ChainQuote> Replace(IEnumerable<ChainQuote> chain, ChainQuote replacement) =>
        chain
            .Select(q => q.Strike == replacement.Strike && q.IsCall == replacement.IsCall ? replacement : q)
            .ToList();

    // ---------------------------------------------------------------- FrontExpiration

    [Fact]
    public void Front_expiration_is_the_earliest_listing_on_or_after_exit_however_the_feed_ordered_it()
    {
        DateOnly[] listed =
        [
            new(2024, 3, 15),
            new(2024, 2, 16),
            new(2024, 2, 23),
            new(2024, 1, 19),
            new(2024, 2, 16)
        ];

        Assert.Equal(new DateOnly(2024, 2, 16), ChainSelection.FrontExpiration(listed, new DateOnly(2024, 2, 2)));
    }

    [Fact]
    public void An_expiration_on_the_exit_date_spans_the_event()
    {
        DateOnly[] listed = [new(2024, 2, 9), new(2024, 2, 2), new(2024, 3, 15)];

        // The print is public before this close, so the straddle measured there is pure intrinsic.
        Assert.Equal(new DateOnly(2024, 2, 2), ChainSelection.FrontExpiration(listed, new DateOnly(2024, 2, 2)));

        // One day later the same listing no longer spans the event, and the next one does.
        Assert.Equal(new DateOnly(2024, 2, 9), ChainSelection.FrontExpiration(listed, new DateOnly(2024, 2, 3)));
    }

    [Fact]
    public void A_chain_that_expires_before_the_exit_date_has_no_front_expiration()
    {
        DateOnly[] listed = [new(2024, 1, 19), new(2024, 1, 26), new(2024, 2, 1)];

        Assert.Null(ChainSelection.FrontExpiration(listed, new DateOnly(2024, 2, 2)));
    }

    [Fact]
    public void No_listed_expirations_at_all_yields_no_front_expiration()
    {
        Assert.Null(ChainSelection.FrontExpiration([], new DateOnly(2024, 2, 2)));
    }

    // ---------------------------------------------------------------- ImpliedSpot

    [Fact]
    public void Parity_recovers_the_spot_exactly_on_a_symmetric_chain()
    {
        var parity = ChainSelection.ImpliedSpot(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]));

        Assert.NotNull(parity);
        Assert.Equal(100m, parity.Spot);
        Assert.Equal(100m, parity.Strike);
        Assert.Equal(3.00m, parity.CallMid);
        Assert.Equal(3.00m, parity.PutMid);
        Assert.Equal(5, parity.PairsConsidered);
        Assert.True(parity.Bracketed);
    }

    [Fact]
    public void Parity_minimises_over_mids_not_over_one_side_of_an_asymmetric_market()
    {
        // Parity-consistent mids (spot 100), but the markets around them are lopsided: at 95 the call
        // is quoted wide to the downside and at 100 the call is quoted wide to the upside. Minimising
        // over bids picks 105, over asks it returns 102.85 — only mids give 100 at strike 100.
        List<ChainQuote> chain =
        [
            Call(95m, 7.50m, 8.50m), Put(95m, 2.50m, 3.50m),
            Call(100m, 0.10m, 5.90m), Put(100m, 2.95m, 3.05m),
            Call(105m, 2.95m, 3.05m), Put(105m, 5.60m, 10.40m)
        ];

        var parity = ChainSelection.ImpliedSpot(chain);

        Assert.NotNull(parity);
        Assert.Equal(100m, parity.Strike);
        Assert.Equal(100m, parity.Spot);
        Assert.Equal(3.00m, parity.CallMid);
        Assert.Equal(3.00m, parity.PutMid);
    }

    [Fact]
    public void A_strike_quoted_on_one_side_only_is_excluded_from_the_parity_solve()
    {
        // The 100 put has no bid, so the 100 pair is not a market. The solve falls back to the 95/105
        // tie (|C − P| = 5 at both), and the lower strike still recovers spot exactly.
        var chain = Replace(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), Put(100m, 0m, 0.20m));

        var parity = ChainSelection.ImpliedSpot(chain);

        Assert.NotNull(parity);
        Assert.Equal(95m, parity.Strike);
        Assert.Equal(100m, parity.Spot);
        Assert.Equal(4, parity.PairsConsidered);
    }

    [Fact]
    public void A_crossed_strike_is_excluded_from_the_parity_solve()
    {
        // Ask below bid: the two sides are not from the same instant, and the mid of a crossed market
        // would win this solve outright (|C − P| = 0.20 at 100 against 5.00 elsewhere).
        var chain = Replace(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), Call(100m, 3.50m, 2.90m));

        var parity = ChainSelection.ImpliedSpot(chain);

        Assert.NotNull(parity);
        Assert.Equal(95m, parity.Strike);
        Assert.Equal(100m, parity.Spot);
        Assert.Equal(4, parity.PairsConsidered);
    }

    [Fact]
    public void A_tie_in_the_parity_objective_resolves_to_the_lower_strike()
    {
        // Spot exactly between two listed strikes: |C − P| = 2.50 at both 100 and 105.
        var parity = ChainSelection.ImpliedSpot(ParityChain(102.50m, [95m, 100m, 105m, 110m]));

        Assert.NotNull(parity);
        Assert.Equal(100m, parity.Strike);
        Assert.Equal(102.50m, parity.Spot);
    }

    [Fact]
    public void A_single_qualifying_strike_is_solved_and_flagged_unbracketed()
    {
        List<ChainQuote> chain = [Call(100m, 3.50m, 3.70m), Put(100m, 2.50m, 2.70m)];

        var parity = ChainSelection.ImpliedSpot(chain);

        Assert.NotNull(parity);
        Assert.Equal(101.00m, parity.Spot);
        Assert.Equal(1, parity.PairsConsidered);
        Assert.False(parity.Bracketed);
    }

    [Fact]
    public void An_empty_chain_has_no_parity_spot_and_says_which_refusal_it_is()
    {
        var result = ChainSelection.ImpliedSpotWithReason([]);

        Assert.Null(result.Parity);
        Assert.Null(ChainSelection.ImpliedSpot([]));
        Assert.Equal(ChainSelectionReasons.ChainEmpty, result.Reason);
    }

    [Fact]
    public void A_chain_with_no_two_sided_strike_has_no_parity_spot_and_counts_what_it_saw()
    {
        // Every row bid 0: a real shape for an illiquid name at the pre-entry snapshot, and the one
        // that must produce a counted row rather than a missing one.
        List<ChainQuote> chain =
        [
            Call(95m, 0m, 0.15m), Put(95m, 0m, 0.10m),
            Call(100m, 0m, 0.20m), Put(100m, 0m, 0.20m),
            Call(105m, 0m, 0.10m), Put(105m, 0m, 0.15m)
        ];

        var result = ChainSelection.ImpliedSpotWithReason(chain);

        Assert.Null(result.Parity);
        Assert.Equal(ChainSelectionReasons.NoTwoSidedStrike, result.Reason);
        Assert.Contains("3 listed strikes", result.Detail);
        Assert.Contains("3 strike(s) carry both rows", result.Detail);
    }

    [Fact]
    public void A_minimum_at_the_edge_of_the_ladder_is_returned_but_flagged_unbracketed()
    {
        // Spot 130 against a ladder that stops at 110: the minimum can only be the top strike, and
        // the parity estimate is then an extrapolation off the end of the chain.
        var parity = ChainSelection.ImpliedSpot(ParityChain(130m, [90m, 95m, 100m, 105m, 110m]));

        Assert.NotNull(parity);
        Assert.Equal(110m, parity.Strike);
        Assert.Equal(130m, parity.Spot);
        Assert.False(parity.Bracketed);
    }

    [Fact]
    public void An_interior_minimum_whose_implied_spot_leaves_the_ladder_is_flagged_unbracketed()
    {
        // Quotes that do not obey parity across strikes: the minimum sits at the interior strike 100,
        // yet K + C − P lands at 129, past the top of a ladder that ends at 110. Nothing in this chain
        // supports that number, and the strike-interior half of the check cannot see it.
        List<ChainQuote> chain =
        [
            Call(90m, 0.90m, 1.10m), Put(90m, 49.90m, 50.10m),
            Call(100m, 29.90m, 30.10m), Put(100m, 0.90m, 1.10m),
            Call(110m, 0.90m, 1.10m), Put(110m, 39.90m, 40.10m)
        ];

        var parity = ChainSelection.ImpliedSpot(chain);

        Assert.NotNull(parity);
        Assert.Equal(100m, parity.Strike);
        Assert.Equal(129m, parity.Spot);
        Assert.False(parity.Bracketed);
    }

    [Fact]
    public void An_interior_minimum_with_the_implied_spot_inside_the_ladder_is_bracketed()
    {
        var parity = ChainSelection.ImpliedSpot(ParityChain(103m, [90m, 95m, 100m, 105m, 110m]));

        Assert.NotNull(parity);
        Assert.Equal(105m, parity.Strike);
        Assert.Equal(103m, parity.Spot);
        Assert.True(parity.Bracketed);
    }

    [Fact]
    public void An_edge_minimum_is_unbracketed_even_when_the_implied_spot_lands_inside_the_ladder()
    {
        // Spot 101 against strikes 100/105/110: the estimate itself is fine, but the minimum sits on
        // the bottom strike, and from inside the chain there is no way to tell a ladder that starts at
        // 100 from one that was truncated there. Flagged conservatively, on purpose.
        var parity = ChainSelection.ImpliedSpot(ParityChain(101m, [100m, 105m, 110m]));

        Assert.NotNull(parity);
        Assert.Equal(100m, parity.Strike);
        Assert.Equal(101m, parity.Spot);
        Assert.False(parity.Bracketed);
    }

    [Fact]
    public void Pairs_considered_counts_only_the_strikes_the_solve_could_use()
    {
        var chain = Replace(
            Replace(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), Put(90m, 0m, 0.10m)),
            Call(110m, 3.20m, 2.80m));

        var parity = ChainSelection.ImpliedSpot(chain);

        Assert.NotNull(parity);
        Assert.Equal(3, parity.PairsConsidered);
    }

    // ---------------------------------------------------------------- SelectAtm

    [Fact]
    public void Atm_is_the_nearest_paired_strike_to_spot()
    {
        var pair = ChainSelection.SelectAtm(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 101.20m);

        Assert.NotNull(pair);
        Assert.Equal(100m, pair.Strike);
        Assert.True(pair.IsWellFormed);
        Assert.True(pair.Call.IsCall);
        Assert.False(pair.Put.IsCall);
    }

    [Fact]
    public void Atm_ties_resolve_to_the_lower_strike()
    {
        // Spot exactly between two listed strikes.
        var pair = ChainSelection.SelectAtm(ParityChain(100m, [95m, 100m, 105m, 110m]), 102.50m);

        Assert.NotNull(pair);
        Assert.Equal(100m, pair.Strike);
    }

    [Fact]
    public void Atm_skips_a_strike_that_is_missing_one_side_entirely()
    {
        // 100 is nearest to 100.40 but has no put row at all, so the pair must come from 105 — the
        // nearest strike that is actually a pair — not from the nearest strike.
        var chain = ParityChain(100m, [90m, 95m, 105m, 110m]);
        chain.Add(Call(100m, 2.90m, 3.10m));

        var pair = ChainSelection.SelectAtm(chain, 100.40m);

        Assert.NotNull(pair);
        Assert.Equal(105m, pair.Strike);
    }

    [Fact]
    public void A_zero_bid_row_is_still_a_listed_strike_for_selection()
    {
        // Quotable is a separate gate. Selection asks whether a row exists, not whether anyone bid.
        var chain = Replace(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), Put(100m, 0m, 0.05m));

        var pair = ChainSelection.SelectAtm(chain, 100.20m);

        Assert.NotNull(pair);
        Assert.Equal(100m, pair.Strike);
        Assert.False(ChainSelection.IsQuotable(pair));
    }

    [Fact]
    public void Atm_refuses_a_clamp_onto_the_bottom_of_the_ladder()
    {
        var result = ChainSelection.SelectAtmWithReason(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 80m);

        Assert.Null(result.Pair);
        Assert.Null(ChainSelection.SelectAtm(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 80m));
        Assert.Equal(ChainSelectionReasons.SpotOutsideStrikeLadder, result.Reason);
        Assert.Contains("bottom", result.Detail);
    }

    [Fact]
    public void Atm_refuses_a_clamp_onto_the_top_of_the_ladder()
    {
        var result = ChainSelection.SelectAtmWithReason(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 130m);

        Assert.Null(result.Pair);
        Assert.Equal(ChainSelectionReasons.SpotOutsideStrikeLadder, result.Reason);
        Assert.Contains("top", result.Detail);
    }

    [Fact]
    public void Atm_refuses_a_clamp_onto_the_paired_ladders_edge_even_when_one_sided_rows_are_listed_beyond_it()
    {
        // The guard has to be measured on the strikes that can actually be selected. A lone call row
        // at 50 makes 90 an interior strike of the CHAIN while it is still the bottom of the paired
        // ladder, and a spot of 60 clamping onto 90 is exactly the edge clamp the guard exists for.
        var chain = ParityChain(100m, [90m, 100m, 110m]);
        chain.Add(Call(50m, 49.90m, 50.10m));

        var result = ChainSelection.SelectAtmWithReason(chain, 60m);

        Assert.Null(result.Pair);
        Assert.Equal(ChainSelectionReasons.SpotOutsideStrikeLadder, result.Reason);
    }

    [Fact]
    public void Atm_refuses_a_spot_sitting_exactly_on_the_lowest_paired_strike()
    {
        // Nothing inside the chain distinguishes a ladder that begins at 90 from one truncated there.
        Assert.Null(ChainSelection.SelectAtm(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 90m));
        Assert.Null(ChainSelection.SelectAtm(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 110m));
    }

    [Fact]
    public void A_ladder_of_fewer_than_three_paired_strikes_can_never_bracket_a_spot()
    {
        Assert.Null(ChainSelection.SelectAtm(ParityChain(102m, [100m, 105m]), 102m));
        Assert.Null(ChainSelection.SelectAtm(ParityChain(100m, [100m]), 100m));
    }

    [Fact]
    public void An_empty_chain_is_refused_with_its_own_reason()
    {
        var result = ChainSelection.SelectAtmWithReason([], 100m);

        Assert.Null(result.Pair);
        Assert.Equal(ChainSelectionReasons.ChainEmpty, result.Reason);
    }

    [Fact]
    public void A_chain_with_no_paired_strike_is_refused_with_its_own_reason()
    {
        List<ChainQuote> callsOnly = [Call(95m, 7.90m, 8.10m), Call(100m, 2.90m, 3.10m), Call(105m, 0.90m, 1.10m)];

        var result = ChainSelection.SelectAtmWithReason(callsOnly, 100m);

        Assert.Null(result.Pair);
        Assert.Equal(ChainSelectionReasons.NoPairedStrike, result.Reason);
        Assert.Contains("3 listed strikes", result.Detail);
    }

    // ---------------------------------------------------------------- Tiers

    [Theory]
    [InlineData(0.05, 0.05, true)]
    [InlineData(0.00, 0.05, false)]
    [InlineData(0.05, 0.00, false)]
    [InlineData(0.00, 0.00, false)]
    public void Quotable_requires_a_bid_on_both_legs(decimal callBid, decimal putBid, bool expected)
    {
        var pair = new AtmPair(100m, Call(100m, callBid, 0.20m), Put(100m, putBid, 0.20m));

        Assert.Equal(expected, ChainSelection.IsQuotable(pair));
    }

    [Fact]
    public void Tradable_at_exactly_fifteen_percent_of_the_straddle_mid_passes()
    {
        // Straddle mid 4.00, combined spread 0.60 = 15.00% exactly. "<= 15%" includes the boundary.
        var pair = new AtmPair(100m, Call(100m, 1.70m, 2.00m), Put(100m, 2.00m, 2.30m));

        Assert.Equal(4.00m, pair.StraddleMid);
        Assert.Equal(0.60m, pair.CombinedSpread);
        Assert.True(ChainSelection.IsTradable(pair));
    }

    [Fact]
    public void Tradable_one_tick_past_fifteen_percent_fails()
    {
        // One cent wider on the put ask: spread 0.61 against a limit of 0.60075.
        var pair = new AtmPair(100m, Call(100m, 1.70m, 2.00m), Put(100m, 2.00m, 2.31m));

        Assert.Equal(0.61m, pair.CombinedSpread);
        Assert.True(ChainSelection.IsQuotable(pair));
        Assert.False(ChainSelection.IsTradable(pair));
    }

    [Fact]
    public void An_unquotable_pair_is_not_tradable_however_narrow_the_spread_looks()
    {
        // Spread 0.15 on a straddle mid of 2.075 is 7.2% — comfortably inside the tier — but the call
        // has no bid, so the mid is not a price and the pair is not in the primary sample.
        var pair = new AtmPair(100m, Call(100m, 0m, 0.10m), Put(100m, 2.00m, 2.05m));

        Assert.False(ChainSelection.IsQuotable(pair));
        Assert.False(ChainSelection.IsTradable(pair));
    }

    [Fact]
    public void A_pair_with_no_market_at_all_is_not_tradable()
    {
        // The degenerate case the multiplication form would otherwise wave through: 0 <= 0.15 * 0.
        var pair = new AtmPair(100m, Call(100m, 0m, 0m), Put(100m, 0m, 0m));

        Assert.Equal(0m, pair.StraddleMid);
        Assert.Equal(0m, pair.CombinedSpread);
        Assert.False(ChainSelection.IsTradable(pair));
    }

    // ---------------------------------------------------------------- Scale

    [Fact]
    public void A_dollar_scale_chain_looks_like_dollars()
    {
        // ATM straddle 6.00 against a reference close of 100: 6% of spot.
        var chain = ParityChain(100m, [90m, 95m, 100m, 105m, 110m]);

        Assert.True(ChainSelection.LooksLikeDollarScale(chain, 100m));
    }

    [Fact]
    public void A_chain_quoted_a_hundred_times_too_large_does_not()
    {
        // The failure this exists for: IM would come out 100x, RF/IM would collapse to zero, and the
        // study would report an enormous, beautifully consistent premium.
        var chain = Scaled(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 100m);

        Assert.False(ChainSelection.LooksLikeDollarScale(chain, 100m));
    }

    [Fact]
    public void A_chain_quoted_in_cents_does_not()
    {
        var chain = Scaled(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), 0.01m);

        Assert.False(ChainSelection.LooksLikeDollarScale(chain, 100m));
    }

    [Fact]
    public void A_chain_with_no_selectable_atm_pair_is_not_measurable_rather_than_wrong_scale()
    {
        // Reference price off the end of the ladder: there is no ATM straddle to measure, which is a
        // different statement from "the units are wrong" and must not be reported as one.
        var chain = ParityChain(100m, [90m, 95m, 100m, 105m, 110m]);

        Assert.Null(ChainSelection.LooksLikeDollarScale(chain, 130m));
        Assert.Null(ChainSelection.LooksLikeDollarScale([], 100m));
    }

    [Fact]
    public void An_unquoted_atm_pair_is_not_measurable_rather_than_wrong_scale()
    {
        var chain = Replace(ParityChain(100m, [90m, 95m, 100m, 105m, 110m]), Put(100m, 0m, 0.05m));

        Assert.Null(ChainSelection.LooksLikeDollarScale(chain, 100m));
    }

    [Fact]
    public void A_non_positive_reference_price_is_not_measurable()
    {
        var chain = ParityChain(100m, [90m, 95m, 100m, 105m, 110m]);

        Assert.Null(ChainSelection.LooksLikeDollarScale(chain, 0m));
        Assert.Null(ChainSelection.LooksLikeDollarScale(chain, -100m));
    }
}
