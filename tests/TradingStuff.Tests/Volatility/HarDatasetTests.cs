using TradingStuff.Volatility;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins HAR dataset construction: forward alignment, warm-up, window arithmetic and the
/// embargoed split.
/// </summary>
/// <remarks>
/// These are the assertions that catch look-ahead leakage. A dataset that quietly labels a
/// sample with variance it could already see still trains, still scores well, and is worthless
/// — so the alignment and embargo are asserted on exact dates rather than on counts.
/// </remarks>
public class HarDatasetTests
{
    private static RealizedVolatilityDay Day(
        DateTime date, double variance, bool complete = true,
        double jump = 0.0, double upside = 0.0, double downside = 0.0) =>
        new()
        {
            Symbol = "SPX",
            Date = date,
            TotalVariance = variance,
            IntradayVariance = variance,
            JumpVariation = jump,
            UpsideVariance = upside,
            DownsideVariance = downside,
            IsComplete = complete,
            ReturnCount = 78,
        };

    /// <summary>A series whose variance is i+1 on day i, so window means are exact integers.</summary>
    private static List<RealizedVolatilityDay> Ramp(int count, double scale = 1e-5)
    {
        var start = new DateTime(2024, 1, 1);
        return Enumerable.Range(0, count)
            .Select(i => Day(start.AddDays(i), (i + 1) * scale))
            .ToList();
    }

    // ---------- options ----------

    [Fact]
    public void OptionDefaultsAreTheStandardHarConfiguration()
    {
        var o = new HarDatasetOptions();

        Assert.Equal(21, o.HorizonDays);
        Assert.Equal(5, o.WeeklyWindow);
        Assert.Equal(22, o.MonthlyWindow);
        Assert.False(o.IncludeJumpComponent);
        Assert.False(o.IncludeSemivariance);
        Assert.Equal(1e-12, o.VarianceFloor);
        Assert.False(o.NonOverlappingOnly);
    }

    [Fact]
    public void FeatureNamesAreTheHarTripletByDefault() =>
        Assert.Equal(["log_rv_daily", "log_rv_weekly", "log_rv_monthly"], new HarDatasetOptions().FeatureNames());

    [Fact]
    public void FeatureNamesGrowWithTheOptionalRegressors()
    {
        Assert.Equal(
            ["log_rv_daily", "log_rv_weekly", "log_rv_monthly", "jump_share"],
            new HarDatasetOptions { IncludeJumpComponent = true }.FeatureNames());

        Assert.Equal(
            ["log_rv_daily", "log_rv_weekly", "log_rv_monthly", "downside_share"],
            new HarDatasetOptions { IncludeSemivariance = true }.FeatureNames());

        // Jump precedes semivariance, and the feature vector must agree with this order.
        Assert.Equal(
            ["log_rv_daily", "log_rv_weekly", "log_rv_monthly", "jump_share", "downside_share"],
            new HarDatasetOptions { IncludeJumpComponent = true, IncludeSemivariance = true }.FeatureNames());
    }

    [Fact]
    public void FeatureNamesMatchTheFeatureVectorLength()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 3,
            IncludeJumpComponent = true, IncludeSemivariance = true,
        };

        var samples = HarDatasetBuilder.Build(Ramp(20), options);

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(options.FeatureNames().Length, s.Features.Length));
    }

    // ---------- validation ----------

    [Fact]
    public void BuildRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => HarDatasetBuilder.Build(null!, new HarDatasetOptions()));
        Assert.Throws<ArgumentNullException>(() => HarDatasetBuilder.Build(Ramp(5), null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildRejectsANonPositiveHorizon(int horizon) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HarDatasetBuilder.Build(Ramp(30), new HarDatasetOptions { HorizonDays = horizon }));

    [Fact]
    public void BuildRejectsAMonthlyWindowShorterThanTheWeeklyOne() =>
        Assert.Throws<ArgumentException>(() => HarDatasetBuilder.Build(
            Ramp(30), new HarDatasetOptions { WeeklyWindow = 10, MonthlyWindow = 9 }));

    [Fact]
    public void EqualWindowsAreAccepted()
    {
        // The guard is `<`, not `<=`: a monthly window equal to the weekly one is degenerate
        // but not invalid, and rejecting it would be a different contract.
        var samples = HarDatasetBuilder.Build(
            Ramp(40), new HarDatasetOptions { HorizonDays = 2, WeeklyWindow = 5, MonthlyWindow = 5 });

        Assert.NotEmpty(samples);
    }

    // ---------- alignment ----------

    [Fact]
    public void FeaturesUseOnlyInformationUpToTheSampleDate()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var days = Ramp(20, scale: 1.0);

        var samples = HarDatasetBuilder.Build(days, options);
        var first = samples[0];

        // warmup = max(4, 3) - 1 = 3, so the first sample sits on index 3 (2024-01-04).
        Assert.Equal(new DateTime(2024, 1, 4), first.Date);

        // Daily feature is log of that day's variance: index 3 -> 4.0.
        Assert.Equal(Math.Log(4.0), first.Features[0], 12);
        // Weekly is the mean of indices 2..3 -> (3+4)/2.
        Assert.Equal(Math.Log(3.5), first.Features[1], 12);
        // Monthly is the mean of indices 0..3 -> (1+2+3+4)/4.
        Assert.Equal(Math.Log(2.5), first.Features[2], 12);
    }

    [Fact]
    public void TheTargetIsRealizedStrictlyAfterTheSampleDate()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var days = Ramp(20, scale: 1.0);

        var first = HarDatasetBuilder.Build(days, options)[0];

        // Sample sits at index 3; the label averages indices 4,5,6 -> (5+6+7)/3 = 6.
        Assert.Equal(6.0, first.ForwardVariance, 12);
        Assert.Equal(Math.Log(6.0), first.Target, 12);
    }

    [Fact]
    public void TheRandomWalkForecastUsesTheTrailingWindowOfTheSameLength()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var first = HarDatasetBuilder.Build(Ramp(20, scale: 1.0), options)[0];

        // Trailing window for t=3, h=3 is indices 1,2,3 -> (2+3+4)/3 = 3.
        Assert.Equal(3.0, first.RandomWalkForecast, 12);
    }

    [Fact]
    public void TheFinalHorizonDaysAreUnlabelledAndDropped()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var days = Ramp(20);

        var samples = HarDatasetBuilder.Build(days, options);

        // Loop runs while t + horizon < count, so the last labelled index is count-horizon-1 = 16.
        Assert.Equal(days[16].Date, samples[^1].Date);
        Assert.DoesNotContain(samples, s => s.Date > days[16].Date);
    }

    [Fact]
    public void UnorderedInputIsSortedBeforeAlignment()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var days = Ramp(20, scale: 1.0);

        var shuffled = days.OrderByDescending(d => d.Date).ToList();

        var fromOrdered = HarDatasetBuilder.Build(days, options);
        var fromShuffled = HarDatasetBuilder.Build(shuffled, options);

        Assert.Equal(fromOrdered.Count, fromShuffled.Count);
        Assert.Equal(fromOrdered.Select(s => s.Date), fromShuffled.Select(s => s.Date));
        Assert.Equal(fromOrdered.Select(s => s.Target), fromShuffled.Select(s => s.Target));
    }

    [Fact]
    public void ASeriesShorterThanTheWarmupYieldsNothing()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };

        Assert.Empty(HarDatasetBuilder.Build(Ramp(4), options));
    }

    // ---------- exclusions ----------

    [Fact]
    public void AnIncompleteSampleDayIsSkipped()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var days = Ramp(20);
        days[5].IsComplete = false;

        Assert.DoesNotContain(HarDatasetBuilder.Build(days, options), s => s.Date == days[5].Date);
    }

    [Fact]
    public void AnIncompleteDayInsideTheLabelWindowSkipsTheSample()
    {
        var options = new HarDatasetOptions { HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4 };
        var days = Ramp(20);

        // Index 6 is inside the label window of the sample at index 3 (4,5,6) but is itself
        // a perfectly good feature day, so only the label-window rule can exclude it.
        days[6].IsComplete = false;

        var samples = HarDatasetBuilder.Build(days, options);

        Assert.DoesNotContain(samples, s => s.Date == days[3].Date);
        Assert.Contains(samples, s => s.Date == days[7].Date);
    }

    [Fact]
    public void ANonPositiveForwardVarianceSkipsTheSample()
    {
        var options = new HarDatasetOptions { HorizonDays = 1, WeeklyWindow = 2, MonthlyWindow = 3 };
        var days = Ramp(12);
        days[5].TotalVariance = 0.0;

        // Index 4's label window is exactly index 5.
        Assert.DoesNotContain(HarDatasetBuilder.Build(days, options), s => s.Date == days[4].Date);
    }

    [Fact]
    public void ANonPositiveFeatureVarianceSkipsTheSample()
    {
        var options = new HarDatasetOptions { HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 3 };
        var days = Ramp(15);
        days[6].TotalVariance = -1.0;

        // A negative daily variance cannot be logged, so the sample on that day is dropped
        // rather than floored into a plausible-looking number.
        Assert.DoesNotContain(HarDatasetBuilder.Build(days, options), s => s.Date == days[6].Date);
    }

    [Fact]
    public void NonOverlappingModeEmitsEveryHorizonthSample()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4, NonOverlappingOnly = true,
        };
        var days = Ramp(30);

        var overlapping = HarDatasetBuilder.Build(days, new HarDatasetOptions
        {
            HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4,
        });
        var spaced = HarDatasetBuilder.Build(days, options);

        Assert.True(spaced.Count < overlapping.Count);

        // Consecutive kept samples are exactly a horizon apart, so label windows never overlap.
        for (int i = 1; i < spaced.Count; i++)
        {
            Assert.Equal(3, (spaced[i].Date - spaced[i - 1].Date).Days);
        }
    }

    // ---------- optional regressors ----------

    [Fact]
    public void TheJumpShareIsCappedAtOne()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 3, IncludeJumpComponent = true,
        };
        var days = Ramp(15, scale: 1.0);

        // Sampling noise can put the jump estimate above total variance; the share is a share.
        foreach (var d in days) d.JumpVariation = d.TotalVariance * 5.0;

        var samples = HarDatasetBuilder.Build(days, options);

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(1.0, s.Features[3], 12));
    }

    [Fact]
    public void TheJumpShareIsTheRatioWhenBelowOne()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 3, IncludeJumpComponent = true,
        };
        var days = Ramp(15, scale: 1.0);
        foreach (var d in days) d.JumpVariation = d.TotalVariance * 0.25;

        Assert.All(HarDatasetBuilder.Build(days, options), s => Assert.Equal(0.25, s.Features[3], 12));
    }

    [Fact]
    public void TheDownsideShareIsTheSignedSplit()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 3, IncludeSemivariance = true,
        };
        var days = Ramp(15, scale: 1.0);
        foreach (var d in days)
        {
            d.UpsideVariance = d.TotalVariance * 0.25;
            d.DownsideVariance = d.TotalVariance * 0.75;
        }

        Assert.All(HarDatasetBuilder.Build(days, options), s => Assert.Equal(0.75, s.Features[3], 12));
    }

    [Fact]
    public void TheDownsideShareFallsBackToAHalfWithNoSignedVariance()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 3, IncludeSemivariance = true,
        };
        var days = Ramp(15, scale: 1.0);

        // Upside and downside both zero: uninformative rather than a division by zero.
        Assert.All(HarDatasetBuilder.Build(days, options), s => Assert.Equal(0.5, s.Features[3], 12));
    }

    // ---------- sample projection ----------

    [Fact]
    public void ForwardAnnualizedVolatilityMatchesTheScalingConvention()
    {
        var sample = new HarSample { ForwardVariance = 1e-4 };

        Assert.Equal(VolatilityScaling.AnnualizeVolatility(1e-4), sample.ForwardAnnualizedVolatility, 12);
        Assert.Equal(Math.Sqrt(1e-4 * 252.0), sample.ForwardAnnualizedVolatility, 12);
    }

    // ---------- split ----------

    [Fact]
    public void SplitRejectsBadArguments()
    {
        var samples = HarDatasetBuilder.Build(Ramp(60), new HarDatasetOptions { HorizonDays = 2, MonthlyWindow = 4, WeeklyWindow = 2 });

        Assert.Throws<ArgumentNullException>(() => HarDatasetBuilder.Split(null!, 0.5, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarDatasetBuilder.Split(samples, 0.0, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarDatasetBuilder.Split(samples, 1.0, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarDatasetBuilder.Split(samples, -0.1, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HarDatasetBuilder.Split(samples, 0.5, -1, out _, out _));
    }

    [Fact]
    public void SplitCutsChronologicallyAtTheRatio()
    {
        var samples = HarDatasetBuilder.Build(Ramp(60), new HarDatasetOptions { HorizonDays = 2, MonthlyWindow = 4, WeeklyWindow = 2 });

        HarDatasetBuilder.Split(samples, 0.5, 0, out var train, out var test);

        Assert.Equal(samples.Count / 2, train.Count);
        Assert.Equal(samples.Count - train.Count, test.Count);

        // Chronological, not random: every training date precedes every test date.
        Assert.True(train[^1].Date < test[0].Date);
    }

    [Fact]
    public void TheEmbargoRemovesSamplesWhoseLabelWindowReachesIntoTest()
    {
        var samples = HarDatasetBuilder.Build(Ramp(60), new HarDatasetOptions { HorizonDays = 2, MonthlyWindow = 4, WeeklyWindow = 2 });

        HarDatasetBuilder.Split(samples, 0.5, 0, out var trainNoEmbargo, out var testNoEmbargo);
        HarDatasetBuilder.Split(samples, 0.5, 5, out var train, out var test);

        // The embargo comes out of the test side; training is untouched by it.
        Assert.Equal(trainNoEmbargo.Count, train.Count);
        Assert.Equal(testNoEmbargo.Count - 5, test.Count);
        Assert.True((test[0].Date - train[^1].Date).Days > 1);
    }

    [Fact]
    public void AnEmbargoLongerThanTheSeriesEmptiesTestRatherThanThrowing()
    {
        var samples = HarDatasetBuilder.Build(Ramp(60), new HarDatasetOptions { HorizonDays = 2, MonthlyWindow = 4, WeeklyWindow = 2 });

        HarDatasetBuilder.Split(samples, 0.5, 10_000, out var train, out var test);

        Assert.NotEmpty(train);
        Assert.Empty(test);
    }

    [Fact]
    public void SplitOrdersSamplesBeforeCutting()
    {
        var samples = HarDatasetBuilder.Build(Ramp(60), new HarDatasetOptions { HorizonDays = 2, MonthlyWindow = 4, WeeklyWindow = 2 });
        var shuffled = samples.OrderByDescending(s => s.Date).ToList();

        HarDatasetBuilder.Split(shuffled, 0.5, 0, out var train, out var test);

        Assert.True(train.Zip(train.Skip(1)).All(p => p.First.Date < p.Second.Date));
        Assert.True(train[^1].Date < test[0].Date);
    }
}
