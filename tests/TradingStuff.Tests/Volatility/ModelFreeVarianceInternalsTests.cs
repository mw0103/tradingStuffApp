using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the mechanics of the model-free integral: forward resolution, strike selection,
/// truncation flags, and strike-spacing arithmetic.
/// </summary>
/// <remarks>
/// Built from hand-specified quotes rather than a Black-Scholes chain. The pricing tests
/// elsewhere prove the integral recovers a known volatility; these prove the surrounding
/// selection rules, where a boundary that is off by one strike changes the answer without
/// making it look wrong.
/// </remarks>
public class ModelFreeVarianceInternalsTests
{
    private static readonly DateTime Observed = new(2024, 3, 4, 15, 45, 0);
    private const double Rate = 0.03;

    private static OptionChainSlice Slice(int days, params (double Strike, OptionRight Right, double Bid, double Ask)[] quotes)
    {
        var slice = new OptionChainSlice
        {
            Root = "SPXW",
            ObservedAt = Observed,
            SettlesAt = Observed.AddDays(days),
        };
        foreach (var (strike, right, bid, ask) in quotes)
        {
            slice.Quotes.Add(new OptionQuote(strike, right, bid, ask));
        }
        return slice;
    }

    /// <summary>
    /// A symmetric chain around 100 with both rights quoted at every strike. Prices decay to
    /// a cent at the wings, so the tails die out naturally and the chain is not truncated.
    /// The call/put gap is smallest at 100, which makes that the parity strike.
    /// </summary>
    private static OptionChainSlice SymmetricChain(int days = 30, double step = 5.0, int wings = 6)
    {
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -wings; i <= wings; i++)
        {
            var strike = 100.0 + i * step;
            var call = Math.Max(0.01, 5.0 - i * 0.9);
            var put = Math.Max(0.01, 5.0 + i * 0.9);
            // Proportional spread keeps every bid strictly positive, so a cheap wing is a
            // dying tail rather than a zero-bid stop.
            quotes.Add((strike, OptionRight.Call, call * 0.9, call * 1.1));
            quotes.Add((strike, OptionRight.Put, put * 0.9, put * 1.1));
        }
        return Slice(days, [.. quotes]);
    }

    // ---------- usability ----------

    [Fact]
    public void EveryUsabilityConditionIsIndividuallyNecessary()
    {
        static ModelFreeVarianceResult Base() => new()
        {
            Variance = 0.04, StrikesUsed = 5, TruncatedLowSide = false, TruncatedHighSide = false,
        };

        Assert.True(Base().IsUsable);

        var noVariance = Base(); noVariance.Variance = 0.0;
        Assert.False(noVariance.IsUsable);

        var tooFew = Base(); tooFew.StrikesUsed = 4;
        Assert.False(tooFew.IsUsable);

        var low = Base(); low.TruncatedLowSide = true;
        Assert.False(low.IsUsable);

        var high = Base(); high.TruncatedHighSide = true;
        Assert.False(high.IsUsable);
    }

    [Fact]
    public void ANegativeVarianceFloorsTheReportedVolatility() =>
        Assert.Equal(0.0, new ModelFreeVarianceResult { Variance = -1.0 }.ImpliedVolatility);

    [Fact]
    public void TotalVarianceIsAnnualizedVarianceOverItsOwnLife()
    {
        var result = new ModelFreeVarianceResult { Variance = 0.04, TimeToExpiryYears = 0.25 };

        Assert.Equal(0.01, result.TotalVariance, 12);
        Assert.Equal(0.2, result.ImpliedVolatility, 12);
    }

    // ---------- input validation ----------

    [Fact]
    public void ComputeRejectsAMissingSlice() =>
        Assert.Equal("slice", Assert.Throws<ArgumentNullException>(() => ModelFreeVariance.Compute(null!, Rate)).ParamName);

    [Fact]
    public void AnExpiredOrInstantaneousSliceIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ModelFreeVariance.Compute(SymmetricChain(days: -1), Rate));

        // Settling exactly at the observation instant is also degenerate: the guard is `<= 0`.
        var atTheInstant = SymmetricChain();
        atTheInstant.SettlesAt = atTheInstant.ObservedAt;
        Assert.Throws<ArgumentException>(() => ModelFreeVariance.Compute(atTheInstant, Rate));
    }

    [Fact]
    public void TooFewUsableStrikesIsReportedWithTheCount()
    {
        // Three strikes, below the default minimum of five.
        var slice = Slice(30,
            (95.0, OptionRight.Put, 1.0, 1.1), (95.0, OptionRight.Call, 6.0, 6.1),
            (100.0, OptionRight.Put, 3.0, 3.1), (100.0, OptionRight.Call, 3.0, 3.1),
            (105.0, OptionRight.Put, 6.0, 6.1), (105.0, OptionRight.Call, 1.0, 1.1));

        var ex = Assert.Throws<InvalidOperationException>(() => ModelFreeVariance.Compute(slice, Rate));

        Assert.Contains("usable strikes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SPXW", ex.Message, StringComparison.Ordinal);
        Assert.Contains("need at least 5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMinimumStrikeCountIsInclusive()
    {
        var slice = SymmetricChain(wings: 6);

        // Exactly at the configured minimum is accepted; one more is not required.
        var result = ModelFreeVariance.Compute(slice, Rate, new ModelFreeVarianceOptions { MinimumStrikes = 13 });

        Assert.Equal(13, result.StrikesUsed);
    }

    [Fact]
    public void OptionsDefaultWhenNotSupplied()
    {
        var slice = SymmetricChain();

        var defaulted = ModelFreeVariance.Compute(slice, Rate);
        var explicitDefaults = ModelFreeVariance.Compute(slice, Rate, new ModelFreeVarianceOptions());

        Assert.Equal(explicitDefaults.Variance, defaulted.Variance, 15);
        Assert.Equal(explicitDefaults.StrikesUsed, defaulted.StrikesUsed);
    }

    // ---------- forward resolution ----------

    [Fact]
    public void TheForwardComesFromPutCallParityAtTheClosestPair()
    {
        // Call and put agree exactly at 100, so that is the parity strike and the forward
        // sits at 100 plus the (zero) discounted difference.
        var slice = SymmetricChain();

        var result = ModelFreeVariance.Compute(slice, Rate);

        Assert.Equal(100.0, result.AtTheMoneyStrike, 9);
        Assert.Equal(100.0, result.Forward, 6);
    }

    [Fact]
    public void TheForwardReflectsTheCallPutDifferenceNotTheirSum()
    {
        // Skew the at-the-money pair so the call is dearer by 0.2. That keeps 100 the unique
        // smallest gap (its neighbours sit at 1.8), so parity still resolves there.
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -6; i <= 6; i++)
        {
            var strike = 100.0 + i * 5.0;
            var call = Math.Max(0.01, 5.0 - i * 0.9) + (i == 0 ? 0.2 : 0.0);
            var put = Math.Max(0.01, 5.0 + i * 0.9);
            quotes.Add((strike, OptionRight.Call, call * 0.9, call * 1.1));
            quotes.Add((strike, OptionRight.Put, put * 0.9, put * 1.1));
        }

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate);

        // F = K + e^{rT}(C - P), so a dearer call pushes the forward above the strike.
        Assert.True(result.Forward > 100.0);
        Assert.Equal(100.0 + Math.Exp(Rate * (30.0 / 365.0)) * 0.2, result.Forward, 6);
    }

    [Fact]
    public void AStrikeWithNoTwoSidedMarketOnEitherLegIsSkippedForParity()
    {
        // 100 has the tightest call/put gap but a zero-bid call, so parity must fall back to
        // the next-best strike rather than trusting a one-sided quote.
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -6; i <= 6; i++)
        {
            var strike = 100.0 + i * 5.0;
            var call = Math.Max(0.05, 5.0 - i * 0.5);
            var put = Math.Max(0.05, 5.0 + i * 0.5);
            var callBid = i == 0 ? 0.0 : call - 0.02;
            quotes.Add((strike, OptionRight.Call, callBid, call + 0.02));
            quotes.Add((strike, OptionRight.Put, put - 0.02, put + 0.02));
        }

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate);

        Assert.NotEqual(100.0, result.Forward, 3);
    }

    [Fact]
    public void AChainWithNoPairedStrikesIsRejected()
    {
        var slice = Slice(30,
            (90.0, OptionRight.Put, 1.0, 1.1), (95.0, OptionRight.Put, 2.0, 2.1),
            (105.0, OptionRight.Call, 1.0, 1.1), (110.0, OptionRight.Call, 0.5, 0.6));

        Assert.Contains("both a call and a put",
            Assert.Throws<InvalidOperationException>(() => ModelFreeVariance.Compute(slice, Rate)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AChainWithNoTwoSidedPairIsRejected()
    {
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -6; i <= 6; i++)
        {
            var strike = 100.0 + i * 5.0;
            // Every call is zero-bid, so no strike has a two-sided market on both legs.
            quotes.Add((strike, OptionRight.Call, 0.0, 1.0));
            quotes.Add((strike, OptionRight.Put, 1.0, 1.1));
        }

        Assert.Contains("two-sided market",
            Assert.Throws<InvalidOperationException>(() => ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheAtTheMoneyStrikeIsTheHighestAtOrBelowTheForward()
    {
        // Forward lands exactly on 100, and the boundary is inclusive, so K0 is 100 and not 95.
        var result = ModelFreeVariance.Compute(SymmetricChain(), Rate);

        Assert.Equal(100.0, result.AtTheMoneyStrike, 9);
    }

    // ---------- strike selection ----------

    [Fact]
    public void TwoConsecutiveZeroBidsTerminateAWing()
    {
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -6; i <= 6; i++)
        {
            var strike = 100.0 + i * 5.0;
            var call = Math.Max(0.05, 5.0 - i * 0.5);
            var put = Math.Max(0.05, 5.0 + i * 0.5);
            // Calls at 115 and 120 are zero-bid: the run of two stops the upper wing there,
            // so 125 and 130 are discarded even though they quote.
            var callBid = strike is 115.0 or 120.0 ? 0.0 : call - 0.02;
            quotes.Add((strike, OptionRight.Call, callBid, call + 0.02));
            quotes.Add((strike, OptionRight.Put, put - 0.02, put + 0.02));
        }

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate);

        Assert.Equal(110.0, result.HighestStrike, 9);
    }

    [Fact]
    public void ASingleZeroBidDoesNotTerminateAWing()
    {
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -6; i <= 6; i++)
        {
            var strike = 100.0 + i * 5.0;
            var call = Math.Max(0.05, 5.0 - i * 0.5);
            var put = Math.Max(0.05, 5.0 + i * 0.5);
            // One gap only: usually a quoting artifact, so the walk continues past it.
            var callBid = strike == 115.0 ? 0.0 : call - 0.02;
            quotes.Add((strike, OptionRight.Call, callBid, call + 0.02));
            quotes.Add((strike, OptionRight.Put, put - 0.02, put + 0.02));
        }

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate);

        Assert.Equal(130.0, result.HighestStrike, 9);
    }

    [Fact]
    public void TheZeroBidRunLengthIsConfigurable()
    {
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -6; i <= 6; i++)
        {
            var strike = 100.0 + i * 5.0;
            var call = Math.Max(0.05, 5.0 - i * 0.5);
            var put = Math.Max(0.05, 5.0 + i * 0.5);
            var callBid = strike == 115.0 ? 0.0 : call - 0.02;
            quotes.Add((strike, OptionRight.Call, callBid, call + 0.02));
            quotes.Add((strike, OptionRight.Put, put - 0.02, put + 0.02));
        }

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate,
            new ModelFreeVarianceOptions { ConsecutiveZeroBidsToStop = 1 });

        // With a run length of one, the same single gap now ends the wing.
        Assert.Equal(110.0, result.HighestStrike, 9);
    }

    [Fact]
    public void OnlyOutOfTheMoneyOptionsEnterTheIntegral()
    {
        var result = ModelFreeVariance.Compute(SymmetricChain(), Rate);

        // Puts below the money, calls above, and one blended price at the money: thirteen
        // strikes from a chain that quotes twenty-six options.
        Assert.Equal(13, result.StrikesUsed);
        Assert.Equal(70.0, result.LowestStrike, 9);
        Assert.Equal(130.0, result.HighestStrike, 9);
    }

    // ---------- truncation ----------

    [Fact]
    public void AWingStillCarryingValueIsFlaggedAsTruncated()
    {
        // Wings floored well above the threshold, so both tails are still worth something
        // where the chain stops. The call/put gap is still smallest at 100, so the money is
        // in the middle and both wings genuinely exist.
        var quotes = new List<(double, OptionRight, double, double)>();
        for (int i = -3; i <= 3; i++)
        {
            var strike = 100.0 + i * 5.0;
            var call = Math.Max(2.0, 5.0 - i * 0.5);
            var put = Math.Max(2.0, 5.0 + i * 0.5);
            quotes.Add((strike, OptionRight.Call, call * 0.9, call * 1.1));
            quotes.Add((strike, OptionRight.Put, put * 0.9, put * 1.1));
        }

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate);

        Assert.True(result.TruncatedLowSide);
        Assert.True(result.TruncatedHighSide);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void AWingThatDiesOutIsNotFlagged()
    {
        var result = ModelFreeVariance.Compute(SymmetricChain(), Rate);

        // The outermost options are worth 0.05 against a threshold of 0.0005 * 100 = 0.05,
        // and the comparison is strict, so exactly at the threshold is not truncation.
        Assert.False(result.TruncatedLowSide);
        Assert.False(result.TruncatedHighSide);
    }

    [Fact]
    public void TheTruncationThresholdScalesWithTheMoneyness()
    {
        var slice = SymmetricChain();

        var strict = ModelFreeVariance.Compute(slice, Rate,
            new ModelFreeVarianceOptions { TruncationPriceThreshold = 1e-6 });

        Assert.True(strict.TruncatedLowSide);
        Assert.True(strict.TruncatedHighSide);
    }

    // ---------- strike spacing ----------

    [Fact]
    public void MedianSpacingIsReportedForAnEvenGrid() =>
        Assert.Equal(5.0, ModelFreeVariance.Compute(SymmetricChain(step: 5.0), Rate).MedianStrikeSpacing, 9);

    [Fact]
    public void MedianSpacingTakesTheMiddleGapOnAnUnevenGrid()
    {
        // Five strikes at 85, 95, 100, 105, 115 give gaps of 10, 5, 5, 10. Sorted that is
        // 5, 5, 10, 10 — an even count whose middle pair straddles two different values, so
        // the result can only be right if the median averages them.
        var quotes = new List<(double, OptionRight, double, double)>
        {
            (85.0, OptionRight.Put, 0.9, 1.1), (85.0, OptionRight.Call, 9.0, 11.0),
            (95.0, OptionRight.Put, 3.6, 4.4), (95.0, OptionRight.Call, 5.4, 6.6),
            (100.0, OptionRight.Put, 4.5, 5.5), (100.0, OptionRight.Call, 4.5, 5.5),
            (105.0, OptionRight.Put, 5.4, 6.6), (105.0, OptionRight.Call, 3.6, 4.4),
            (115.0, OptionRight.Put, 9.0, 11.0), (115.0, OptionRight.Call, 0.9, 1.1),
        };

        var result = ModelFreeVariance.Compute(Slice(30, [.. quotes]), Rate);

        Assert.Equal(5, result.StrikesUsed);
        Assert.Equal(7.5, result.MedianStrikeSpacing, 9);
    }

    [Fact]
    public void ACoarseGridIsReportedAsSuch()
    {
        var fine = ModelFreeVariance.Compute(SymmetricChain(step: 1.0), Rate);
        var coarse = ModelFreeVariance.Compute(SymmetricChain(step: 25.0), Rate);

        // The spacing is recorded because a coarse grid inflates the discretized sum, so a
        // series built from chains of varying spacing carries a bias that moves around.
        Assert.Equal(1.0, fine.MedianStrikeSpacing, 9);
        Assert.Equal(25.0, coarse.MedianStrikeSpacing, 9);
    }
}
