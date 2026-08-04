using System.Globalization;
using TradingStuff.Contracts;
using TradingStuff.ResearchService.Automation;

namespace TradingStuff.Tests;

/// <summary>
/// The exit rule's arithmetic and the closing order's shape, both without a broker or a clock.
/// </summary>
/// <remarks>
/// The threshold comparison is four characters of code and is the entire exit policy of this build,
/// so it is pinned at its boundaries rather than sampled in the middle: off by one day either way is
/// the difference between closing a week out and closing into an expiration this platform does not
/// handle, and neither mistake produces an error anywhere.
/// </remarks>
public sealed class SpyExitPlannerTests
{
    private static readonly DateOnly TradingDate = new(2026, 8, 5);

    // ---- threshold arithmetic --------------------------------------------------------------------

    [Theory]
    [InlineData("2026-08-12", 7)]   // exactly the threshold
    [InlineData("2026-08-13", 8)]   // one day outside it
    [InlineData("2026-08-06", 1)]
    [InlineData("2026-08-05", 0)]   // expires at today's close
    [InlineData("2026-08-04", -1)]  // already past
    public void Days_to_expiration_counts_calendar_days_from_the_trading_date(string expiration, int expected) =>
        Assert.Equal(expected, SpyExitPlanner.DaysToExpiration(DateOnly.Parse(expiration), TradingDate));

    [Fact]
    public void The_threshold_is_at_or_below_not_below()
    {
        // The boundary case, stated twice because it is the one a reader has to take on trust from
        // the operator "close at 7 DTE": seven days out IS due, eight is not.
        Assert.True(SpyExitPlanner.IsDue(new DateOnly(2026, 8, 12), TradingDate, 7));
        Assert.False(SpyExitPlanner.IsDue(new DateOnly(2026, 8, 13), TradingDate, 7));
    }

    [Fact]
    public void Expiration_day_and_anything_past_it_are_due()
    {
        // SPY weeklies are PM-settled: the expiration date is the last trading day and the contract
        // dies at that day's close, so 0 DTE means "expires tonight, still tradeable now" rather than
        // "already gone". A position past its expiration is due too — if the broker is still
        // reporting one, a closing order is what should be attempted, not silence.
        Assert.True(SpyExitPlanner.IsDue(TradingDate, TradingDate, 7));
        Assert.True(SpyExitPlanner.IsDue(TradingDate.AddDays(-3), TradingDate, 7));

        // And a threshold of zero still closes on expiration day rather than one day late.
        Assert.True(SpyExitPlanner.IsDue(TradingDate, TradingDate, 0));
        Assert.False(SpyExitPlanner.IsDue(TradingDate.AddDays(1), TradingDate, 0));
    }

    [Fact]
    public void Calendar_days_cross_month_year_and_daylight_saving_boundaries_unchanged()
    {
        // Calendar days, not trading days and not an elapsed duration: nothing here can be shortened
        // by a holiday or lengthened by the spring-forward hour. DateOnly has no clock at all, which
        // is the property being pinned — a future refactor onto DateTimeOffset would silently make
        // 2026-03-08 a 23-hour day and 7 DTE occasionally read as 6.
        Assert.Equal(7, SpyExitPlanner.DaysToExpiration(new DateOnly(2027, 1, 4), new DateOnly(2026, 12, 28)));
        Assert.Equal(7, SpyExitPlanner.DaysToExpiration(new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 4)));
        Assert.Equal(7, SpyExitPlanner.DaysToExpiration(new DateOnly(2026, 3, 5), new DateOnly(2026, 2, 26)));
    }

    // ---- grouping open positions -----------------------------------------------------------------

    [Fact]
    public void Only_the_configured_underlying_is_managed_and_flat_legs_are_not_positions()
    {
        var structures = SpyExitPlanner.ManagedStructures(
            [
                Position("SPY", new DateOnly(2026, 8, 12), 740m, -1),
                Position("SPY", new DateOnly(2026, 8, 12), 739m, 1),
                Position("QQQ", new DateOnly(2026, 8, 12), 500m, -1),
                Position("SPY", new DateOnly(2026, 8, 12), 730m, 0),
            ],
            "SPY");

        var structure = Assert.Single(structures);
        Assert.Equal(2, structure.Legs.Count);

        // Legs come back in strike order, which is what makes the exit key reproducible.
        Assert.Equal(739m, structure.Legs[0].Contract.Strike);
        Assert.Equal(740m, structure.Legs[1].Contract.Strike);
    }

    [Fact]
    public void Expirations_are_separate_structures_earliest_first()
    {
        var structures = SpyExitPlanner.ManagedStructures(
            [
                Position("SPY", new DateOnly(2026, 9, 4), 750m, -1),
                Position("SPY", new DateOnly(2026, 8, 12), 740m, -1),
            ],
            "SPY");

        Assert.Equal(2, structures.Count);
        Assert.Equal(new DateOnly(2026, 8, 12), structures[0].Expiration);
        Assert.Equal(new DateOnly(2026, 9, 4), structures[1].Expiration);
    }

    [Fact]
    public void The_exit_key_is_reproducible_from_a_fresh_read_and_changes_when_the_position_does()
    {
        var legs = new[]
        {
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1),
            Position("SPY", new DateOnly(2026, 8, 12), 739m, 1),
        };

        // Same position, read again in a different order: the same key. This is what lets a claim
        // taken in one pass be recognised in the next without anything being remembered.
        Assert.Equal(
            SpyExitPlanner.ManagedStructures(legs, "SPY")[0].ExitKey,
            SpyExitPlanner.ManagedStructures([legs[1], legs[0]], "SPY")[0].ExitKey);

        // A partially closed structure is a DIFFERENT position and gets a different key, so its
        // remainder is its own exit decision rather than a suppressed duplicate of the first.
        var partial = SpyExitPlanner.ManagedStructures(
            [legs[0], Position("SPY", new DateOnly(2026, 8, 12), 739m, 0)], "SPY")[0];

        Assert.NotEqual(SpyExitPlanner.ManagedStructures(legs, "SPY")[0].ExitKey, partial.ExitKey);
    }

    [Fact]
    public void The_exit_key_does_not_move_when_the_process_locale_does()
    {
        // The key is written to the decision log on one pass and compared against on a later one —
        // possibly by a process that started with a different CurrentCulture after an image change or
        // a host locale. Under de-DE the 739.50 strike formats as "739,50", and under ar-SA the year
        // itself changes; the same open position would then hash to a key matching nothing already
        // claimed, and the loop would send a second closing order for a spread whose first is resting
        // at the venue. Suppression must not depend on where the process is running.
        var legs = new[]
        {
            Position("SPY", new DateOnly(2026, 8, 12), 739.50m, 1),
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1),
        };

        var invariant = SpyExitPlanner.ManagedStructures(legs, "SPY")[0].ExitKey;
        var original = CultureInfo.CurrentCulture;

        try
        {
            foreach (var culture in new[] { "de-DE", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                Assert.Equal(invariant, SpyExitPlanner.ManagedStructures(legs, "SPY")[0].ExitKey);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        // The positive control: the key really does carry the two values a locale could mangle, so
        // this is not passing because there is nothing culture-sensitive in it.
        Assert.Contains("739.50", invariant);
        Assert.Contains("2026-08-12", invariant);
    }

    // ---- the shape it will close -----------------------------------------------------------------

    [Fact]
    public void The_managed_two_leg_vertical_is_closeable()
    {
        Assert.Null(SpyExitPlanner.ValidateShape(Structure(
            Position("SPY", new DateOnly(2026, 8, 12), 739m, 1),
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1))));
    }

    [Fact]
    public void A_lone_leg_is_refused_rather_than_closed()
    {
        var failure = SpyExitPlanner.ValidateShape(
            Structure(Position("SPY", new DateOnly(2026, 8, 12), 740m, -1)));

        Assert.NotNull(failure);
        Assert.Contains("1 leg(s)", failure);
    }

    [Fact]
    public void A_ratio_two_legs_on_one_side_and_a_mixed_right_are_all_refused()
    {
        // Every one of these would leave an uncovered leg or close something that is not the managed
        // structure. Automation did not build them and does not guess what closing them means.
        Assert.Contains("ratio", SpyExitPlanner.ValidateShape(Structure(
            Position("SPY", new DateOnly(2026, 8, 12), 739m, 2),
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1)))!);

        Assert.Contains("same side", SpyExitPlanner.ValidateShape(Structure(
            Position("SPY", new DateOnly(2026, 8, 12), 739m, -1),
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1)))!);

        Assert.Contains("mixes", SpyExitPlanner.ValidateShape(Structure(
            Position("SPY", new DateOnly(2026, 8, 12), 739m, 1, OptionRight.Call),
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1)))!);

        Assert.Contains("two strikes", SpyExitPlanner.ValidateShape(Structure(
            Position("SPY", new DateOnly(2026, 8, 12), 740m, 1),
            Position("SPY", new DateOnly(2026, 8, 12), 740m, -1)))!);
    }

    // ---- pricing the close -----------------------------------------------------------------------

    [Fact]
    public void Closing_a_credit_spread_prices_as_a_debit_and_the_buffer_pays_up()
    {
        // Open: short the 740 put, long the 739 wing. Closing buys the 740 back at its ask (0.58) and
        // sells the wing at its bid (0.17): a 0.41 natural debit, 0.46 with the buffer paid to cross.
        var legs = ClosingLegs(shortStrike: 740m, longStrike: 739m);

        var (net, failure) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(legs[0].Contract, 0.17m, 0.20m), Quote(legs[1].Contract, 0.55m, 0.58m)], legs, 0.05m);

        Assert.Null(failure);
        Assert.Equal(0.46m, net);
    }

    [Fact]
    public void Closing_a_debit_spread_prices_as_a_credit_and_the_buffer_gives_some_of_it_up()
    {
        // The mirror image, and the reason the sign convention is one formula rather than two
        // branches: long the 739 call, short the 740. Closing sells the 739 at its bid (2.10) and
        // buys the 740 back at its ask (1.60) — a 0.50 credit, 0.45 after giving some up to cross.
        var legs = ClosingLegs(shortStrike: 740m, longStrike: 739m, longQuantity: 1, shortQuantity: -1);

        var (net, failure) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(legs[0].Contract, 2.10m, 2.13m), Quote(legs[1].Contract, 1.57m, 1.60m)], legs, 0.05m);

        Assert.Null(failure);
        Assert.Equal(-0.45m, net);
    }

    [Fact]
    public void Rounding_always_moves_to_the_marketable_side_on_both_signs()
    {
        // Rounded UP on the signed axis in both directions: paying a fraction more for a debit and
        // accepting a fraction less on a credit are the same move. Rounding down would turn the one
        // order whose purpose is to get out into a resting one, with nothing saying so.
        var closing = ClosingLegs(shortStrike: 740m, longStrike: 739m);

        var (debit, _) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(closing[0].Contract, 0.171m, 0.20m), Quote(closing[1].Contract, 0.55m, 0.584m)], closing, 0m);

        Assert.Equal(0.42m, debit); // 0.584 - 0.171 = 0.413 -> 0.42

        var opening = ClosingLegs(shortStrike: 740m, longStrike: 739m, longQuantity: 1, shortQuantity: -1);

        var (credit, _) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(opening[0].Contract, 2.104m, 2.13m), Quote(opening[1].Contract, 1.57m, 1.60m)], opening, 0m);

        Assert.Equal(-0.50m, credit); // 1.60 - 2.104 = -0.504 -> -0.50
    }

    [Fact]
    public void A_missing_side_is_a_named_refusal_never_a_substituted_mid()
    {
        var legs = ClosingLegs(shortStrike: 740m, longStrike: 739m);

        // Measured against the live paper gateway on 2026-08-01: outside the regular session SPY
        // options come back 0/0. There is nothing to buy the short leg back against.
        var (noOffer, offerFailure) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(legs[0].Contract, 0.17m, 0.20m), Quote(legs[1].Contract, 0m, 0m)], legs, 0.05m);

        Assert.Null(noOffer);
        Assert.Contains("no offer", offerFailure);

        var (noBid, bidFailure) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(legs[0].Contract, 0m, 0.20m), Quote(legs[1].Contract, 0.55m, 0.58m)], legs, 0.05m);

        Assert.Null(noBid);
        Assert.Contains("no bid", bidFailure);

        var (noQuote, quoteFailure) = SpyExitPlanner.ComputeMarketableClose(
            [Quote(legs[1].Contract, 0.55m, 0.58m)], legs, 0.05m);

        Assert.Null(noQuote);
        Assert.Contains("No quote came back", quoteFailure);
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private static PositionSnapshot Position(
        string underlying, DateOnly expiration, decimal strike, int quantity, OptionRight right = OptionRight.Put) =>
        new(
            new OptionContract(
                $"{underlying}{expiration:yyyyMMdd}{(right == OptionRight.Put ? 'P' : 'C')}{strike:F0}",
                underlying, expiration, strike, right, TradingClass: underlying),
            quantity,
            1.00m,
            GreeksVector.Zero);

    private static ManagedStructure Structure(params PositionSnapshot[] legs) =>
        new("SPY", legs[0].Contract.Expiration, legs, "test-key");

    /// <summary>The legs of a closing order, in the strike order <see cref="SpyExitPlanner"/> produces.</summary>
    private static OrderLegRequest[] ClosingLegs(
        decimal shortStrike, decimal longStrike, int longQuantity = 1, int shortQuantity = -1)
    {
        var structure = SpyExitPlanner.ManagedStructures(
            [
                Position("SPY", new DateOnly(2026, 8, 12), longStrike, longQuantity),
                Position("SPY", new DateOnly(2026, 8, 12), shortStrike, shortQuantity),
            ],
            "SPY")[0];

        return [.. structure.Legs.Select(leg => new OrderLegRequest(
            leg.Contract,
            leg.Quantity < 0 ? OrderSide.Buy : OrderSide.Sell,
            Math.Abs(leg.Quantity),
            PositionEffect.Close))];
    }

    private static QuoteSnapshot Quote(OptionContract contract, decimal bid, decimal ask) =>
        new(Guid.NewGuid(), contract, bid, ask, (bid + ask) / 2m,
            new OptionGreeks(0.5m, 0.01m, -0.2m, 0.3m), DateTimeOffset.UnixEpoch, "test");
}
