using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the frozen A4 construction rules (docs/research/a4-slope-construction.md) that
/// <see cref="TermStructureBuilder"/> owns: tightest-bracket leg selection around the target
/// moment with no width cap, the one-calendar-day eligibility floor, no silent substitution when
/// a selected leg is unusable, and the primary slope definition ln(sigma9/sigma30).
/// The per-expiration variance math itself is pinned by ImpliedVolatilityTests.
/// </summary>
public sealed class TermStructureBuilderTests
{
    private const double Spot = 100.0;
    private const double Rate = 0.02;

    private static readonly DateTime Snapshot = new(2013, 3, 5, 20, 30, 0, DateTimeKind.Utc);

    private static TermStructureBuilder Builder() => new(new FlatRiskFreeRate(Rate));

    [Fact]
    public void Selects_the_tightest_settlements_around_each_target_moment()
    {
        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            Chain(0.20, days: 4), Chain(0.20, days: 7), Chain(0.21, days: 11),
            Chain(0.22, days: 25), Chain(0.23, days: 32), Chain(0.24, days: 46),
        });

        Assert.True(day.NineDay.IsUsable, day.NineDay.Note);
        Assert.Equal(7.0, day.NineDay.NearTermDays, 6);
        Assert.Equal(11.0, day.NineDay.NextTermDays, 6);

        Assert.True(day.ThirtyDay.IsUsable, day.ThirtyDay.Note);
        Assert.Equal(25.0, day.ThirtyDay.NearTermDays, 6);
        Assert.Equal(32.0, day.ThirtyDay.NextTermDays, 6);
    }

    [Fact]
    public void A_wide_bracket_is_used_rather_than_refused_because_there_is_no_width_cap()
    {
        // Sparse early-era ladder: nothing between 2 and 60 days. The frozen construction
        // brackets 9d and 30d with that pair rather than fabricating a hole.
        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            Chain(0.20, days: 2), Chain(0.22, days: 60),
        });

        Assert.True(day.NineDay.IsUsable, day.NineDay.Note);
        Assert.Equal(2.0, day.NineDay.NearTermDays, 6);
        Assert.Equal(60.0, day.NineDay.NextTermDays, 6);
        Assert.True(day.ThirtyDay.IsUsable, day.ThirtyDay.Note);
    }

    [Fact]
    public void A_slice_settling_under_one_calendar_day_out_is_ineligible()
    {
        // The 0.5-day slice would be the tightest 9d near leg; the floor excludes it.
        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            Chain(0.30, hours: 12), Chain(0.20, days: 7), Chain(0.21, days: 11),
            Chain(0.22, days: 25), Chain(0.23, days: 32),
        });

        Assert.True(day.NineDay.IsUsable, day.NineDay.Note);
        Assert.Equal(7.0, day.NineDay.NearTermDays, 6);
    }

    [Fact]
    public void An_unbracketed_point_is_unusable_with_a_reason_never_extrapolated()
    {
        // Everything settles beyond 9 days: no near leg for the 9d point.
        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            Chain(0.21, days: 11), Chain(0.22, days: 25), Chain(0.23, days: 32),
        });

        Assert.False(day.NineDay.IsUsable);
        Assert.Contains("near leg missing", day.NineDay.Note);
        Assert.True(day.ThirtyDay.IsUsable, day.ThirtyDay.Note);
        Assert.False(day.IsUsable);
        Assert.Null(day.Slope);
    }

    [Fact]
    public void An_unusable_selected_leg_fails_the_point_instead_of_substituting_the_next_expiration()
    {
        // The 7-day slice is the selected 9d near leg but carries too few strikes to be usable.
        // A 4-day slice exists and COULD bracket — the frozen construction § 6 forbids the swap.
        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            Chain(0.20, days: 4),
            Chain(0.20, days: 7, lowStrike: 99, highStrike: 101),
            Chain(0.21, days: 11),
            Chain(0.22, days: 25), Chain(0.23, days: 32),
        });

        Assert.False(day.NineDay.IsUsable);
        // The note must blame the SELECTED 7-day leg (2013-03-12), proving the 4-day
        // alternative was not silently swapped in.
        Assert.Contains("2013-03-12", day.NineDay.Note);
        Assert.False(day.IsUsable);
    }

    [Fact]
    public void The_slope_is_the_log_ratio_of_the_two_constant_maturity_vols()
    {
        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            Chain(0.25, days: 7), Chain(0.25, days: 11),
            Chain(0.20, days: 25), Chain(0.20, days: 32),
        });

        Assert.True(day.IsUsable);

        var expected = 0.5 * Math.Log(day.NineDay.Variance / day.ThirtyDay.Variance);
        Assert.Equal(expected, day.Slope!.Value, 12);

        // Short-dated implied above 30-day implied must read as inversion: positive slope.
        Assert.True(day.Slope.Value > 0.0,
            $"slope {day.Slope.Value} should be positive when the 9d vol sits above the 30d vol");
    }

    [Fact]
    public void Identical_settlement_moments_keep_the_slice_with_more_two_sided_strikes()
    {
        var sparse = Chain(0.20, days: 7, lowStrike: 96, highStrike: 104);
        var dense = Chain(0.20, days: 7);

        var day = Builder().BuildDay(Snapshot.Date, new List<OptionChainSlice>
        {
            sparse, dense, Chain(0.21, days: 11), Chain(0.22, days: 25), Chain(0.23, days: 32),
        });

        Assert.True(day.NineDay.IsUsable, day.NineDay.Note);
        Assert.True(day.NineDay.StrikesUsed > sparse.Quotes.Count / 2,
            "the dedupe kept the sparse duplicate instead of the dense one");
    }

    // ---------- helpers ----------

    private static OptionChainSlice Chain(
        double volatility, int days = 0, int hours = 0,
        double lowStrike = 60, double highStrike = 140, double step = 1.0)
    {
        const double tick = 0.005;

        var slice = new OptionChainSlice
        {
            Root = "SPXW",
            ObservedAt = Snapshot,
            SettlesAt = Snapshot.AddDays(days).AddHours(hours),
        };

        var timeToExpiry = (days + hours / 24.0) / 365.0;

        for (var strike = lowStrike; strike <= highStrike + 1e-9; strike += step)
        {
            var call = BlackScholes(Spot, strike, timeToExpiry, Rate, volatility, isCall: true);
            var put = BlackScholes(Spot, strike, timeToExpiry, Rate, volatility, isCall: false);

            slice.Quotes.Add(new OptionQuote(strike, OptionRight.Call, Math.Max(0.0, call - tick), call + tick));
            slice.Quotes.Add(new OptionQuote(strike, OptionRight.Put, Math.Max(0.0, put - tick), put + tick));
        }

        return slice;
    }

    private static double BlackScholes(
        double spot, double strike, double timeToExpiry, double rate, double volatility, bool isCall)
    {
        if (timeToExpiry <= 0.0) return Math.Max(0.0, isCall ? spot - strike : strike - spot);

        var sqrtT = Math.Sqrt(timeToExpiry);
        var d1 = (Math.Log(spot / strike) + (rate + 0.5 * volatility * volatility) * timeToExpiry)
                 / (volatility * sqrtT);
        var d2 = d1 - volatility * sqrtT;
        var discount = Math.Exp(-rate * timeToExpiry);

        return isCall
            ? spot * NormalCdf(d1) - strike * discount * NormalCdf(d2)
            : strike * discount * NormalCdf(-d2) - spot * NormalCdf(-d1);
    }

    private static double NormalCdf(double x)
    {
        // Abramowitz & Stegun 7.1.26 via erf; plenty for test tolerances.
        var t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2.0));
        var erf = 1.0 - t * (0.254829592 + t * (-0.284496736 + t * (1.421413741
                  + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-x * x / 2.0);
        return x >= 0 ? 0.5 * (1.0 + erf) : 0.5 * (1.0 - erf);
    }
}
