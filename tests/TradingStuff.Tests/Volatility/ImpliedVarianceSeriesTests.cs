using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins constant-maturity interpolation, the rate sources, and the day/series builder.
/// </summary>
/// <remarks>
/// The interpolation is in total variance, not volatility — variance is additive in time and
/// volatility is not, so blending volatilities is not arbitrage-consistent. That is asserted
/// against a hand-computed blend rather than against the implementation's own output. The
/// builder's contract is that an uncomputable date is emitted as unusable with a reason, never
/// dropped, so a gap in the series stays visible.
/// </remarks>
public class ImpliedVarianceSeriesTests
{
    private static readonly DateTime Observed = new(2024, 3, 4, 15, 45, 0);

    private static ModelFreeVarianceResult Expiration(
        double days, double annualizedVariance, int strikes = 100, double spacing = 5.0) =>
        new()
        {
            Variance = annualizedVariance,
            TimeToExpiryYears = days / 365.0,
            SettlesAt = Observed.AddDays(days),
            StrikesUsed = strikes,
            MedianStrikeSpacing = spacing,
            Forward = 5000.0,
            AtTheMoneyStrike = 5000.0,
        };

    // ---------- rate sources ----------

    [Fact]
    public void AFlatRateIgnoresDateAndHorizon()
    {
        var rate = new FlatRiskFreeRate(0.035);

        Assert.Equal(0.035, rate.RateFor(Observed, 0.1));
        Assert.Equal(0.035, rate.RateFor(Observed.AddYears(5), 2.0));
    }

    [Fact]
    public void AHistoricalRateRequiresAtLeastOneObservation()
    {
        Assert.Throws<ArgumentNullException>(() => new HistoricalRiskFreeRate(null!));
        Assert.Throws<ArgumentException>(() => new HistoricalRiskFreeRate([]));
    }

    [Fact]
    public void AHistoricalRateCarriesTheLastObservationForward()
    {
        var rates = new HistoricalRiskFreeRate(
        [
            new(new DateTime(2020, 1, 1), 0.01),
            new(new DateTime(2023, 1, 1), 0.045),
        ]);

        Assert.Equal(0.01, rates.RateFor(new DateTime(2021, 6, 1), 0.1));
        Assert.Equal(0.045, rates.RateFor(new DateTime(2023, 1, 1), 0.1));
        Assert.Equal(0.045, rates.RateFor(new DateTime(2026, 1, 1), 0.1));
    }

    [Fact]
    public void ADateBeforeTheFirstObservationUsesTheEarliestRate() =>
        Assert.Equal(0.01, new HistoricalRiskFreeRate([new(new DateTime(2020, 1, 1), 0.01)])
            .RateFor(new DateTime(2015, 1, 1), 0.1));

    [Fact]
    public void HistoricalRatesAreSortedOnConstruction()
    {
        var rates = new HistoricalRiskFreeRate(
        [
            new(new DateTime(2023, 1, 1), 0.045),
            new(new DateTime(2020, 1, 1), 0.01),
        ]);

        Assert.Equal(0.01, rates.RateFor(new DateTime(2021, 6, 1), 0.1));
    }

    // ---------- constant maturity: options ----------

    [Fact]
    public void MaturityDefaultsFollowTheVixConvention()
    {
        var o = new ConstantMaturityOptions();

        Assert.Equal(30, o.TargetDays);
        // VIX rolls out of very short-dated options, whose quotes are erratic and whose
        // settlement convention dominates the time calculation.
        Assert.Equal(23.0, o.MinimumNearTermDays);
        Assert.Equal(37.0, o.MaximumNextTermDays);
        Assert.False(o.AllowExtrapolation);
    }

    // ---------- constant maturity: interpolation ----------

    [Fact]
    public void InterpolationRequiresTwoUsableExpirations()
    {
        Assert.Throws<ArgumentNullException>(() => ConstantMaturityVariance.Interpolate(null!));
        Assert.Throws<InvalidOperationException>(() => ConstantMaturityVariance.Interpolate([Expiration(25, 0.04)]));

        // A non-positive variance is not usable, so this is still only one expiration.
        Assert.Throws<InvalidOperationException>(() => ConstantMaturityVariance.Interpolate(
            [Expiration(25, 0.04), Expiration(35, 0.0)]));
    }

    [Fact]
    public void AgreeingTermsInterpolateToTheSameVariance()
    {
        var result = ConstantMaturityVariance.Interpolate([Expiration(25, 0.04), Expiration(35, 0.04)]);

        Assert.Equal(0.04, result.Variance, 12);
        Assert.Equal(0.2, result.ImpliedVolatility, 12);
        Assert.False(result.IsExtrapolated);
    }

    [Fact]
    public void TheBlendIsInTotalVarianceNotVolatility()
    {
        var near = Expiration(25, 0.04);
        var next = Expiration(35, 0.09);

        var result = ConstantMaturityVariance.Interpolate([near, next]);

        // Recompute the VIX blend by hand in minutes.
        const double minutesPerDay = 1440.0, minutesPerYear = 365.0 * 1440.0;
        var nearMin = near.TimeToExpiryYears * minutesPerYear;
        var nextMin = next.TimeToExpiryYears * minutesPerYear;
        var targetMin = 30 * minutesPerDay;
        var expected = (near.TotalVariance * ((nextMin - targetMin) / (nextMin - nearMin))
                        + next.TotalVariance * ((targetMin - nearMin) / (nextMin - nearMin)))
                       * (minutesPerYear / targetMin);

        Assert.Equal(expected, result.Variance, 12);

        // Blending the volatilities instead would give a different, arbitrage-inconsistent
        // answer; confirm the two really do differ so this assertion has teeth.
        var volatilityBlend = Math.Pow((Math.Sqrt(0.04) + Math.Sqrt(0.09)) / 2.0, 2);
        Assert.NotEqual(volatilityBlend, result.Variance, 4);
    }

    [Fact]
    public void TheResultCarriesBothTermsAndTheirProvenance()
    {
        var result = ConstantMaturityVariance.Interpolate(
            [Expiration(25, 0.04, strikes: 80, spacing: 5.0), Expiration(35, 0.09, strikes: 120, spacing: 25.0)]);

        Assert.Equal(30, result.TargetDays);
        Assert.Equal(25.0, result.NearTermDays, 9);
        Assert.Equal(35.0, result.NextTermDays, 9);
        Assert.Equal(0.04, result.NearTermVariance, 12);
        Assert.Equal(0.09, result.NextTermVariance, 12);
        Assert.Equal(200, result.TotalStrikesUsed);
        // The widest spacing governs, because that term carries the larger discretization bias.
        Assert.Equal(25.0, result.WidestStrikeSpacing, 12);
    }

    [Fact]
    public void ExpirationsOutsideTheEligibilityWindowAreRefused()
    {
        // 20 days is below MinimumNearTermDays, 40 is above MaximumNextTermDays.
        Assert.Throws<InvalidOperationException>(() =>
            ConstantMaturityVariance.Interpolate([Expiration(20, 0.04), Expiration(40, 0.09)]));
    }

    [Fact]
    public void ExtrapolationIsOptInAndFlagged()
    {
        List<ModelFreeVarianceResult> expirations = [Expiration(20, 0.04), Expiration(40, 0.09)];

        Assert.Throws<InvalidOperationException>(() => ConstantMaturityVariance.Interpolate(expirations));

        var result = ConstantMaturityVariance.Interpolate(
            expirations, new ConstantMaturityOptions { AllowExtrapolation = true });

        Assert.True(result.IsExtrapolated);
        Assert.True(result.Variance > 0.0);
    }

    [Fact]
    public void TwoExpirationsAtTheSameInstantAreRefused()
    {
        var options = new ConstantMaturityOptions { AllowExtrapolation = true };

        Assert.Throws<InvalidOperationException>(() => ConstantMaturityVariance.Interpolate(
            [Expiration(28, 0.04), Expiration(28, 0.09)], options));
    }

    [Fact]
    public void TheNearestPairIsChosenWhenExtrapolating()
    {
        var options = new ConstantMaturityOptions { AllowExtrapolation = true };

        var result = ConstantMaturityVariance.Interpolate(
            [Expiration(5, 0.01), Expiration(18, 0.04), Expiration(21, 0.09)], options);

        // 18 and 21 are closest to 30, and are reported in ascending order.
        Assert.Equal(18.0, result.NearTermDays, 9);
        Assert.Equal(21.0, result.NextTermDays, 9);
    }

    [Fact]
    public void UnorderedExpirationsAreSortedBeforeSelection()
    {
        var ordered = ConstantMaturityVariance.Interpolate([Expiration(25, 0.04), Expiration(35, 0.09)]);
        var reversed = ConstantMaturityVariance.Interpolate([Expiration(35, 0.09), Expiration(25, 0.04)]);

        Assert.Equal(ordered.Variance, reversed.Variance, 15);
        Assert.Equal(ordered.NearTermDays, reversed.NearTermDays, 12);
    }

    [Fact]
    public void ANegativeVarianceFloorsTheReportedVolatility() =>
        Assert.Equal(0.0, new ConstantMaturityResult { Variance = -1.0 }.ImpliedVolatility);

    // ---------- series builder ----------

    private sealed class ThrowingRate : IRiskFreeRateSource
    {
        public double RateFor(DateTime date, double yearsToExpiry) =>
            throw new InvalidOperationException("no rate for this date");
    }

    private static OptionChainSlice Slice(double days) => new()
    {
        Root = "SPXW",
        ObservedAt = Observed,
        SettlesAt = Observed.AddDays(days),
    };

    private static ImpliedVarianceSeriesBuilder Builder() =>
        new(new FlatRiskFreeRate(0.03));

    [Fact]
    public void TheBuilderRequiresARateSource() =>
        Assert.Throws<ArgumentNullException>(() => new ImpliedVarianceSeriesBuilder(null!));

    [Fact]
    public void TheBuilderDefaultsItsOptionalOptions()
    {
        var day = Builder().BuildDay("SPX", Observed, []);

        // TargetDays comes from the defaulted maturity options.
        Assert.Equal(30, day.TargetDays);
    }

    [Fact]
    public void BuildDayRejectsMissingSlices() =>
        Assert.Throws<ArgumentNullException>(() => Builder().BuildDay("SPX", Observed, null!));

    [Fact]
    public void ADayWithTooFewExpirationsIsUnusableWithAReason()
    {
        var day = Builder().BuildDay("SPX", Observed, []);

        Assert.False(day.IsUsable);
        Assert.Equal("SPX", day.Symbol);
        Assert.Equal(Observed.Date, day.Date);
        Assert.Contains("0 expiration(s)", day.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyExpiredSlicesAreSkipped()
    {
        // Settling in the past yields a non-positive time to expiry.
        var day = Builder().BuildDay("SPX", Observed, [Slice(-5), Slice(-1)]);

        Assert.False(day.IsUsable);
        Assert.Contains("0 expiration(s)", day.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingExpirationIsRecordedRatherThanThrown()
    {
        var builder = new ImpliedVarianceSeriesBuilder(new ThrowingRate());

        var day = builder.BuildDay("SPX", Observed, [Slice(25), Slice(35)]);

        Assert.False(day.IsUsable);
        // The reason names the settlement date so a bad expiration can be located.
        Assert.Contains("2024-03-29", day.Note, StringComparison.Ordinal);
        Assert.Contains("no rate for this date", day.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstFewFailuresAreReported()
    {
        var builder = new ImpliedVarianceSeriesBuilder(new ThrowingRate());
        var slices = Enumerable.Range(1, 10).Select(i => Slice(20 + i)).ToList();

        var day = builder.BuildDay("SPX", Observed, slices);

        // Three, so a systematically broken day does not produce an unreadable note.
        Assert.Equal(3, day.Note.Split(';').Length);
    }

    [Fact]
    public void AnUncomputableDayIsEmittedRatherThanDropped()
    {
        var builder = Builder();
        var chains = new List<KeyValuePair<DateTime, List<OptionChainSlice>>>
        {
            new(Observed, []),
            new(Observed.AddDays(1), []),
        };

        var series = builder.Build("SPX", chains);

        // Gaps must stay visible in the series rather than silently shortening it.
        Assert.Equal(2, series.Count);
        Assert.All(series, d => Assert.False(d.IsUsable));
        Assert.All(series, d => Assert.NotNull(d.Note));
    }

    [Fact]
    public void BuildRejectsMissingChains() =>
        Assert.Throws<ArgumentNullException>(() => Builder().Build("SPX", null!));

    [Fact]
    public void TheSeriesIsOrderedByObservationDate()
    {
        var chains = new List<KeyValuePair<DateTime, List<OptionChainSlice>>>
        {
            new(Observed.AddDays(2), []),
            new(Observed, []),
            new(Observed.AddDays(1), []),
        };

        var series = Builder().Build("SPX", chains);

        Assert.Equal(
            [Observed.Date, Observed.Date.AddDays(1), Observed.Date.AddDays(2)],
            series.Select(d => d.Date));
    }

    [Fact]
    public void ADayCarriesItsSymbolAndNormalizedDate()
    {
        var series = Builder().Build("SPXW",
            [new KeyValuePair<DateTime, List<OptionChainSlice>>(Observed, [])]);

        Assert.Equal("SPXW", series[0].Symbol);
        // The time component is dropped so the series keys on the trading date.
        Assert.Equal(Observed.Date, series[0].Date);
    }

    [Fact]
    public void ANegativeVarianceFloorsTheDaysReportedVolatility() =>
        Assert.Equal(0.0, new ImpliedVarianceDay { ImpliedVariance = -1.0 }.ImpliedVolatility);
}
