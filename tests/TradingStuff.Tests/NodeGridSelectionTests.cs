using TradingStuff.Contracts;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Universe;

namespace TradingStuff.Tests;

/// <summary>
/// Strike selection for the registered node grid, against a realistic SPX ladder.
/// </summary>
/// <remarks>
/// These exist because of a defect that destroyed the thing the platform is built to collect and
/// survived two adversarial reviews. <c>NodeSelector</c> asked for a chain window of <c>20</c>,
/// which the gateway reads as a half-COUNT of strikes — the 41 nearest spot, ±1.3% of SPX at 7,440 —
/// while the selector reasoned about it as a moneyness range. Every seeded target beyond ±2.5% fell
/// outside the window, and an unbounded nearest-match answered with the window's edge strike, so the
/// nine roles of a DTE bucket collapsed onto four contracts and the 54-node grid onto ~24 conIds.
/// <para>
/// It was invisible because every collapsed contract is real: 7340P at SPXW 2026-08-06 resolves to
/// conId 898728622 and ticks all session, so coverage reported ~100% for all 54 nodes and cleared
/// the ≥95% gate. Nothing anywhere compared an assigned strike against the target it was chosen for.
/// So the first test below states the mechanism as an executable fact, and the rest pin the two
/// checks that make it unrepeatable.
/// </para>
/// <para>
/// The fixture is measured, not invented: SPX spot 7437.63 and the SPXW 2026-08-06 ladder as TWS
/// returned them on 2026-08-01 (5-point increments near the money, coarsening outward; the
/// <c>strikeHalfCount: 20</c> window really did span 7340–7540).
/// </para>
/// </remarks>
public sealed class NodeGridSelectionTests
{
    private const decimal Spot = 7437.63m;

    /// <summary>The nine roles migration 003 seeds per DTE bucket, as (role, right, moneyness).</summary>
    private static readonly (string Role, OptionRight Right, decimal Moneyness)[] SeededGrid =
    [
        ("ATM-C", OptionRight.Call, 0.000m),
        ("ATM-P", OptionRight.Put, 0.000m),
        ("40D-C", OptionRight.Call, 0.025m),
        ("40D-P", OptionRight.Put, -0.025m),
        ("25D-C", OptionRight.Call, 0.060m),
        ("25D-P", OptionRight.Put, -0.060m),
        ("10D-C", OptionRight.Call, 0.110m),
        ("10D-P", OptionRight.Put, -0.110m),
        ("5D-P", OptionRight.Put, -0.150m),
    ];

    /// <summary>
    /// A listed SPXW ladder: 5-point increments over 6000–8000, 25-point outside, both rights.
    /// </summary>
    private static IReadOnlyList<OptionContract> Ladder(decimal low = 5000m, decimal high = 9000m)
    {
        var strikes = new List<decimal>();

        for (var strike = low; strike <= high; strike += strike is >= 6000m and < 8000m ? 5m : 25m)
        {
            strikes.Add(strike);
        }

        return
        [
            .. strikes.SelectMany(strike => new[]
            {
                Contract(strike, OptionRight.Call),
                Contract(strike, OptionRight.Put),
            }),
        ];
    }

    private static OptionContract Contract(decimal strike, OptionRight right) =>
        new($"SPXW20260806{(right == OptionRight.Call ? 'C' : 'P')}{strike:0.##}",
            "SPX", new DateOnly(2026, 8, 6), strike, right, TradingClass: "SPXW");

    /// <summary>The window a <c>strikeHalfCount: 20</c> request actually returns: 41 strikes, 7340–7540.</summary>
    private static IReadOnlyList<OptionContract> StrikeCountWindow() =>
        [.. Ladder().Where(c => c.Strike >= 7340m && c.Strike <= 7540m)];

    /// <summary>The window a <c>moneynessHalfWidth: 0.20</c> request returns.</summary>
    private static IReadOnlyList<OptionContract> MoneynessWindow() =>
        [.. Ladder().Where(c => c.Strike >= Spot * 0.80m && c.Strike <= Spot * 1.20m)];

    [Fact]
    public void The_shipped_defect_bound_nine_roles_to_four_contracts()
    {
        // Not a test of current code — an executable statement of what the old selector did, so the
        // failure it caused stays legible after the code that caused it is gone. This is verbatim
        // the v1 pick: nearest strike of the right kind, with no check that the winner is anywhere
        // near the target.
        var window = StrikeCountWindow();

        var picked = SeededGrid
            .Select(node => window
                .Where(c => c.Right == node.Right)
                .OrderBy(c => Math.Abs(c.Strike - (Spot * (1 + node.Moneyness))))
                .First())
            .ToArray();

        Assert.Equal(9, picked.Length);
        Assert.Equal(4, picked.Select(c => c.Key()).Distinct().Count());

        // All three OTM call roles land on the window's top edge, all four put roles on its bottom.
        Assert.Equal(7540m, picked[2].Strike);
        Assert.Equal(7540m, picked[4].Strike);
        Assert.Equal(7540m, picked[6].Strike);
        Assert.Equal(7340m, picked[3].Strike);
        Assert.Equal(7340m, picked[7].Strike);
        Assert.Equal(7340m, picked[8].Strike);
    }

    [Fact]
    public void A_target_outside_the_window_is_refused_rather_than_clamped_to_its_edge()
    {
        // The same window, through the selector as it is now. Two roles are legitimately inside it
        // (ATM), and the other seven are refused by name instead of quietly binding to an edge.
        var window = StrikeCountWindow();

        var results = SeededGrid
            .Select(node => (node.Role, Pick: NodeSelector.PickStrike(Spot, node.Moneyness, node.Right, window)))
            .ToArray();

        var bound = results.Where(r => r.Pick.Contract is not null).ToArray();
        var refused = results.Where(r => r.Pick.Contract is null).ToArray();

        Assert.Equal(2, bound.Length);
        Assert.Equal(["ATM-C", "ATM-P"], bound.Select(b => b.Role));
        Assert.Equal(7, refused.Length);
        Assert.All(refused, r => Assert.Equal(NodeUnassignedReasons.TargetOutsideWindow, r.Pick.Refusal));

        // The property that actually matters: no two roles share a contract.
        Assert.Equal(bound.Length, bound.Select(b => b.Pick.Contract!.Key()).Distinct().Count());
    }

    [Fact]
    public void A_moneyness_window_binds_all_nine_roles_to_nine_distinct_contracts()
    {
        var window = MoneynessWindow();

        var picks = SeededGrid
            .Select(node => NodeSelector.PickStrike(Spot, node.Moneyness, node.Right, window))
            .ToArray();

        Assert.All(picks, pick => Assert.Null(pick.Refusal));

        // The headline number: nine roles, nine contracts. Distinct OptionContractKey is distinct
        // conId — a key is (underlying, expiration, strike, right, trading class), which is exactly
        // what identifies a listed contract at the broker.
        Assert.Equal(9, picks.Select(p => p.Contract!.Key()).Distinct().Count());

        // And each one is genuinely near its own target rather than merely distinct.
        Assert.All(picks, pick => Assert.InRange(Math.Abs(pick.Deviation), 0m, 0.015m));

        Assert.Equal(
            [7440m, 7440m, 7625m, 7250m, 7885m, 6990m, 8250m, 6620m, 6320m],
            picks.Select(p => p.Contract!.Strike));
    }

    [Fact]
    public void A_ladder_too_sparse_to_reach_the_target_is_refused_even_though_it_brackets_it()
    {
        // The bracket check is structural but not sufficient on its own: a ladder can straddle the
        // target and still have nothing within a usable distance of it. 500-point increments bracket
        // a +11% target and miss it by 3.3% of spot.
        IReadOnlyList<OptionContract> sparse =
        [
            .. new[] { 6000m, 6500m, 7000m, 7500m, 8000m, 8500m, 9000m }.Select(s => Contract(s, OptionRight.Call)),
        ];

        var pick = NodeSelector.PickStrike(Spot, 0.110m, OptionRight.Call, sparse);

        Assert.Null(pick.Contract);
        Assert.Equal(NodeUnassignedReasons.StrikeDeviation, pick.Refusal);
    }

    [Fact]
    public void A_sparse_but_real_expiration_still_binds()
    {
        // The counterweight to the test above, and the reason the tolerance is 1.5% rather than
        // tighter. SPXW 2026-09-14 lists 25-point increments near the money and 100-point further
        // out; its worst genuine miss on 2026-08-01 was 0.75% of spot (+11% target 8255.8, nearest
        // listed 8200). Refusing that would leave a real bucket dark for no reason.
        IReadOnlyList<OptionContract> coarse =
        [
            .. Enumerable.Range(0, 40).Select(i => Contract(6300m + (i * 100m), OptionRight.Call)),
        ];

        var pick = NodeSelector.PickStrike(Spot, 0.110m, OptionRight.Call, coarse);

        Assert.NotNull(pick.Contract);
        Assert.Equal(8300m, pick.Contract!.Strike);
    }

    [Fact]
    public void A_window_with_no_contracts_of_the_needed_right_is_refused()
    {
        IReadOnlyList<OptionContract> callsOnly = [.. MoneynessWindow().Where(c => c.Right == OptionRight.Call)];

        var pick = NodeSelector.PickStrike(Spot, -0.110m, OptionRight.Put, callsOnly);

        Assert.Null(pick.Contract);
        Assert.Equal(NodeUnassignedReasons.NoCandidates, pick.Refusal);
    }

    [Fact]
    public void The_refusal_says_which_target_and_which_window_so_it_can_be_acted_on()
    {
        var pick = NodeSelector.PickStrike(Spot, -0.150m, OptionRight.Put, StrikeCountWindow());

        Assert.NotNull(pick.RefusalDetail);
        Assert.Contains("6321.99", pick.RefusalDetail);   // the target it was chosen for
        Assert.Contains("7340.00", pick.RefusalDetail);   // the window it was offered
    }
}
