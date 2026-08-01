using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the realized-volatility engine: configuration, session filtering, and the overnight
/// policies.
/// </summary>
/// <remarks>
/// The overnight treatment gets the most attention here. Implied volatility covers calendar
/// time and intraday realized variance does not, so dropping or double-counting the
/// close-to-open move biases every variance risk premium computed downstream — and it does so
/// smoothly, without ever producing a number that looks wrong on its own.
/// </remarks>
public class RealizedVolatilityEngineTests
{
    private static readonly DateTime Day1 = new(2024, 3, 4);

    /// <summary>A full 09:30-16:00 session of one-minute bars following <paramref name="priceAt"/>.</summary>
    private static List<IntradayBar> Session(DateTime day, Func<int, double> priceAt, int minutes = 390)
    {
        var open = day.Date.AddHours(9).AddMinutes(30);
        return Enumerable.Range(0, minutes + 1)
            .Select(i =>
            {
                var p = priceAt(i);
                return new IntradayBar(open.AddMinutes(i), p, p, p, p, 100);
            })
            .ToList();
    }

    /// <summary>A session that oscillates, so realized variance is strictly positive.</summary>
    private static List<IntradayBar> WigglySession(DateTime day, double baseline = 100.0, double amplitude = 0.25) =>
        Session(day, i => baseline + (i % 7) * amplitude);

    private static RealizedVolatilityOptions Options(OvernightPolicy policy) => new()
    {
        SourceBarMinutes = 1,
        SamplingMinutes = 5,
        UseSubsampling = true,
        OvernightPolicy = policy,
        OvernightScalingWindow = 252,
    };

    private static List<RealizedVolatilityDay> Build(
        IEnumerable<IntradayBar> bars, RealizedVolatilityOptions options, SessionProfile? session = null) =>
        new RealizedVolatilitySeriesBuilder(session ?? SessionProfile.UsEquity(), options).Build("SPY", bars);

    // ---------- options ----------

    [Fact]
    public void OptionDefaultsAreTheLiquidUsEquityConvention()
    {
        var o = new RealizedVolatilityOptions();

        Assert.Equal(1, o.SourceBarMinutes);
        Assert.Equal(5, o.SamplingMinutes);
        Assert.True(o.UseSubsampling);
        Assert.Equal(BarTimestampConvention.BarStart, o.TimestampConvention);
        Assert.Equal(OvernightPolicy.HansenLundeScaling, o.OvernightPolicy);
        Assert.Equal(252, o.OvernightScalingWindow);
        Assert.Empty(o.ExDividends);
    }

    [Fact]
    public void ValidateAcceptsTheDefaults() => new RealizedVolatilityOptions().Validate();

    [Theory]
    [InlineData(0, 5, 252)]
    [InlineData(-1, 5, 252)]
    [InlineData(5, 4, 252)]   // sampling finer than the source bars
    [InlineData(1, 5, 0)]
    [InlineData(1, 5, -1)]
    public void ValidateRejectsIncoherentSettings(int source, int sampling, int window) =>
        Assert.Throws<InvalidOperationException>(() => new RealizedVolatilityOptions
        {
            SourceBarMinutes = source, SamplingMinutes = sampling, OvernightScalingWindow = window,
        }.Validate());

    [Fact]
    public void SamplingEqualToTheSourceBarIsAccepted() =>
        // The guard is `<`: sampling at the source grain is one grid, not an error.
        new RealizedVolatilityOptions { SourceBarMinutes = 5, SamplingMinutes = 5 }.Validate();

    [Theory]
    [InlineData(1, 5, true, 5)]
    [InlineData(1, 15, true, 15)]
    [InlineData(5, 5, true, 1)]
    [InlineData(1, 5, false, 1)]   // subsampling off collapses to a single grid
    [InlineData(2, 5, true, 2)]    // integer division, not rounding
    public void SubsampleGridCountFollowsTheGrainRatio(int source, int sampling, bool subsample, int expected) =>
        Assert.Equal(expected, new RealizedVolatilityOptions
        {
            SourceBarMinutes = source, SamplingMinutes = sampling, UseSubsampling = subsample,
        }.SubsampleGridCount);

    // ---------- session profile ----------

    [Fact]
    public void UsEquitySessionIsNineThirtyToFour()
    {
        var s = SessionProfile.UsEquity();

        Assert.Equal(new TimeSpan(9, 30, 0), s.RegularOpen);
        Assert.Equal(new TimeSpan(16, 0, 0), s.RegularClose);
        Assert.Equal(1, s.SkipMinutesAfterOpen);
        Assert.Equal(60, s.ShortSessionToleranceMinutes);
        Assert.Equal(20, s.MinimumReturnsPerDay);
        Assert.Equal(0.20, s.MaximumStaleSampleFraction);
        Assert.Empty(s.KnownShortSessions);
    }

    [Fact]
    public void TheIndexProfileSkipsFurtherIntoTheSession()
    {
        // The printed SPX open is stitched from staggered constituent prints and is not a
        // tradeable simultaneous price, so more of the open is discarded than for SPY.
        Assert.Equal(5, SessionProfile.SpxIndex().SkipMinutesAfterOpen);
        Assert.True(SessionProfile.SpxIndex().SkipMinutesAfterOpen > SessionProfile.UsEquity().SkipMinutesAfterOpen);
    }

    [Fact]
    public void EffectiveOpenAddsTheSkip() =>
        Assert.Equal(new TimeSpan(9, 35, 0), SessionProfile.SpxIndex().EffectiveOpen);

    [Theory]
    [InlineData(9, 30, false)]  // before the effective open
    [InlineData(9, 31, true)]   // inclusive lower bound
    [InlineData(12, 0, true)]
    [InlineData(16, 0, true)]   // inclusive upper bound
    [InlineData(16, 1, false)]
    [InlineData(4, 0, false)]   // pre-market
    [InlineData(20, 0, false)]  // post-market
    public void RegularSessionMembershipIsBoundedInclusively(int hour, int minute, bool expected) =>
        Assert.Equal(expected, SessionProfile.UsEquity()
            .IsInRegularSession(Day1.AddHours(hour).AddMinutes(minute)));

    // ---------- bars ----------

    [Fact]
    public void ABarCarriesItsPricesAndVolume()
    {
        var bar = new IntradayBar(Day1, 1.0, 2.0, 0.5, 1.5, 42);

        Assert.Equal(Day1, bar.Timestamp);
        Assert.Equal(1.0, bar.Open);
        Assert.Equal(2.0, bar.High);
        Assert.Equal(0.5, bar.Low);
        Assert.Equal(1.5, bar.Close);
        Assert.Equal(42, bar.Volume);
    }

    [Fact]
    public void VolumeDefaultsToZero() => Assert.Equal(0L, new IntradayBar(Day1, 1, 1, 1, 1).Volume);

    [Theory]
    [InlineData(1, 1, 1, 1, true)]
    [InlineData(0, 1, 1, 1, false)]
    [InlineData(1, 0, 1, 1, false)]
    [InlineData(1, 1, 0, 1, false)]
    [InlineData(1, 1, 1, 0, false)]
    [InlineData(-1, 1, 1, 1, false)]
    public void OnlyStrictlyPositivePricesAreUsable(double o, double h, double l, double c, bool usable) =>
        // Any non-positive leg makes a log return impossible, so the bar is dropped
        // rather than allowed to produce a NaN downstream.
        Assert.Equal(usable, new IntradayBar(Day1, o, h, l, c).HasUsablePrices);

    // ---------- scaling ----------

    [Fact]
    public void ScalingConstantsAreTheStandardConventions()
    {
        Assert.Equal(252.0, VolatilityScaling.TradingDaysPerYear);
        Assert.Equal(252.0 / 365.0, VolatilityScaling.TradingDaysPerCalendarDay, 15);
    }

    [Fact]
    public void AnnualizationRoundTrips()
    {
        Assert.Equal(0.0252, VolatilityScaling.AnnualizeVariance(1e-4), 15);
        Assert.Equal(Math.Sqrt(0.0252), VolatilityScaling.AnnualizeVolatility(1e-4), 15);
        Assert.Equal(1e-4, VolatilityScaling.ToMeanDailyVariance(VolatilityScaling.AnnualizeVolatility(1e-4)), 15);
        Assert.Equal(0.0, VolatilityScaling.AnnualizeVolatility(0.0));
    }

    [Fact]
    public void ANegativeVarianceCannotBeAnnualized() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VolatilityScaling.AnnualizeVolatility(-1e-9));

    [Theory]
    [InlineData(30, 21)]   // a 30-calendar-day option spans ~21 trading days
    [InlineData(365, 252)]
    [InlineData(1, 1)]     // never rounds down to zero
    [InlineData(2, 1)]
    public void CalendarDaysConvertToTradingDays(int calendar, int expected) =>
        Assert.Equal(expected, VolatilityScaling.CalendarDaysToTradingDays(calendar));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveCalendarSpanIsRejected(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VolatilityScaling.CalendarDaysToTradingDays(days));

    // ---------- builder wiring ----------

    [Fact]
    public void TheBuilderRequiresItsCollaborators()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(null!, new RealizedVolatilityOptions()));
        Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionProfile.UsEquity(), null!));
    }

    [Fact]
    public void TheBuilderValidatesOptionsOnConstruction() =>
        Assert.Throws<InvalidOperationException>(() => new RealizedVolatilitySeriesBuilder(
            SessionProfile.UsEquity(), new RealizedVolatilityOptions { SourceBarMinutes = 0 }));

    [Fact]
    public void BuildRejectsNullBars() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionProfile.UsEquity(), new RealizedVolatilityOptions())
                .Build("SPY", null!));

    [Fact]
    public void ExtendedHoursPrintsAreExcluded()
    {
        var bars = WigglySession(Day1);
        var withPreMarket = bars.Concat(Session(Day1, i => 500.0 + i, minutes: 60)
            .Select(b => new IntradayBar(b.Timestamp.AddHours(-5), b.Open, b.High, b.Low, b.Close, b.Volume)))
            .ToList();

        var clean = Build(bars, Options(OvernightPolicy.Exclude));
        var polluted = Build(withPreMarket, Options(OvernightPolicy.Exclude));

        // A wild pre-market ramp must not move the session's variance at all.
        Assert.Equal(clean[0].IntradayVariance, polluted[0].IntradayVariance, 15);
    }

    [Fact]
    public void SeparateDaysBecomeSeparateSessions()
    {
        var bars = WigglySession(Day1).Concat(WigglySession(Day1.AddDays(1))).ToList();

        var days = Build(bars, Options(OvernightPolicy.Exclude));

        Assert.Equal(2, days.Count);
        Assert.Equal(Day1.Date, days[0].Date.Date);
        Assert.Equal(Day1.AddDays(1).Date, days[1].Date.Date);
        Assert.Equal("SPY", days[0].Symbol);
    }

    [Fact]
    public void ASessionWithTooFewBarsIsDropped() =>
        Assert.Empty(Build(Session(Day1, _ => 100.0, minutes: 0), Options(OvernightPolicy.Exclude)));

    [Fact]
    public void AThinSessionIsFlaggedRatherThanDropped()
    {
        // Ten minutes of data is far below MinimumReturnsPerDay but is still emitted, so a
        // gap stays visible to whatever consumes the series instead of silently vanishing.
        var days = Build(WigglySession(Day1).Take(11).ToList(), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.False(days[0].IsComplete);
    }

    [Fact]
    public void AFullSessionIsComplete()
    {
        var days = Build(WigglySession(Day1), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.True(days[0].IsComplete);
        Assert.False(days[0].IsShortSession);
    }

    [Fact]
    public void AnEarlyCloseIsFlaggedAsAShortSession()
    {
        // Ends at 13:00, more than the 60-minute tolerance before the scheduled close.
        var days = Build(WigglySession(Day1).Take(211).ToList(), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.True(days[0].IsShortSession);
    }

    [Fact]
    public void AKnownShortSessionIsFlaggedEvenWhenItRunsToTheClose()
    {
        var session = SessionProfile.UsEquity();
        session.KnownShortSessions.Add(Day1.Date);

        var days = Build(WigglySession(Day1), Options(OvernightPolicy.Exclude), session);

        Assert.True(days[0].IsShortSession);
    }

    [Fact]
    public void SessionOpenAndCloseAreTheFirstAndLastSampledPrices()
    {
        var days = Build(Session(Day1, i => 100.0 + i * 0.01), Options(OvernightPolicy.Exclude));

        Assert.True(days[0].SessionOpen < days[0].SessionClose);
        Assert.True(days[0].FirstBarTime < days[0].LastBarTime);
    }

    // ---------- overnight policies ----------

    [Fact]
    public void ExcludeLeavesTotalVarianceAtTheIntradayFigure()
    {
        var bars = WigglySession(Day1).Concat(WigglySession(Day1.AddDays(1), baseline: 110.0)).ToList();

        var days = Build(bars, Options(OvernightPolicy.Exclude));

        Assert.All(days, d => Assert.Equal(d.IntradayVariance, d.TotalVariance, 15));
    }

    [Fact]
    public void AddSquaredReturnAddsExactlyTheOvernightMove()
    {
        var bars = WigglySession(Day1).Concat(WigglySession(Day1.AddDays(1), baseline: 110.0)).ToList();

        var days = Build(bars, Options(OvernightPolicy.AddSquaredReturn));

        // The first session has no prior close, so it has no overnight return to add.
        Assert.False(days[0].HasOvernightReturn);
        Assert.Equal(days[0].IntradayVariance, days[0].TotalVariance, 15);

        Assert.True(days[1].HasOvernightReturn);
        Assert.Equal(
            days[1].IntradayVariance + days[1].OvernightReturn * days[1].OvernightReturn,
            days[1].TotalVariance, 15);
        Assert.True(days[1].TotalVariance > days[1].IntradayVariance);
    }

    [Fact]
    public void TheOvernightReturnIsMeasuredFromThePriorSessionClose()
    {
        var bars = WigglySession(Day1).Concat(WigglySession(Day1.AddDays(1), baseline: 110.0)).ToList();

        var days = Build(bars, Options(OvernightPolicy.AddSquaredReturn));

        Assert.Equal(Math.Log(days[1].SessionOpen / days[0].SessionClose), days[1].OvernightReturn, 15);
        Assert.Equal(
            days[1].OvernightReturn + Math.Log(days[1].SessionClose / days[1].SessionOpen),
            days[1].CloseToCloseReturn, 15);
    }

    [Fact]
    public void AnExDividendGapIsAddedBackBeforeMeasuringTheOvernightReturn()
    {
        var day2 = Day1.AddDays(1);
        // Price gaps down by exactly the distribution overnight.
        var bars = Session(Day1, i => 100.0 + (i % 7) * 0.25)
            .Concat(Session(day2, i => 99.0 + (i % 7) * 0.25)).ToList();

        var options = Options(OvernightPolicy.AddSquaredReturn);
        var withoutAdjustment = Build(bars, options);

        var adjusted = Options(OvernightPolicy.AddSquaredReturn);
        adjusted.ExDividends[day2.Date] = 1.0;
        var withAdjustment = Build(bars, adjusted);

        Assert.Equal(1.0, withAdjustment[1].DividendAdjustment);
        Assert.Equal(0.0, withoutAdjustment[1].DividendAdjustment);

        // The mechanical gap is a cash transfer, not volatility: adding it back shrinks the
        // overnight move toward zero.
        Assert.True(Math.Abs(withAdjustment[1].OvernightReturn) < Math.Abs(withoutAdjustment[1].OvernightReturn));
    }

    [Fact]
    public void HansenLundeFallsBackToTheSquaredReturnDuringWarmUp()
    {
        // Fewer than the 20 sessions the ratio needs, so the noisier estimate is used.
        var bars = Enumerable.Range(0, 5)
            .SelectMany(i => WigglySession(Day1.AddDays(i), baseline: 100.0 + i))
            .ToList();

        var scaled = Build(bars, Options(OvernightPolicy.HansenLundeScaling));
        var added = Build(bars, Options(OvernightPolicy.AddSquaredReturn));

        Assert.Equal(added.Select(d => d.TotalVariance), scaled.Select(d => d.TotalVariance));
    }

    [Fact]
    public void HansenLundeScalesUpOnceTheTrailingWindowIsLongEnough()
    {
        var bars = Enumerable.Range(0, 60)
            .SelectMany(i => WigglySession(Day1.AddDays(i), baseline: 100.0 + i))
            .ToList();

        var days = Build(bars, Options(OvernightPolicy.HansenLundeScaling));
        var late = days[^1];

        // The overnight session adds variance, so the factor is floored at one and total
        // variance can never fall below the intraday figure.
        Assert.True(late.TotalVariance >= late.IntradayVariance);
        Assert.All(days, d => Assert.True(d.TotalVariance >= d.IntradayVariance));
    }

    [Fact]
    public void TheScalingFactorUsesOnlyTrailingInformation()
    {
        var bars = Enumerable.Range(0, 40)
            .SelectMany(i => WigglySession(Day1.AddDays(i), baseline: 100.0 + i))
            .ToList();

        var baseline = Build(bars, Options(OvernightPolicy.HansenLundeScaling));

        // Perturbing the final session cannot change any earlier session's scaling.
        var perturbed = bars.Where(b => b.Timestamp.Date != Day1.AddDays(39).Date)
            .Concat(WigglySession(Day1.AddDays(39), baseline: 500.0, amplitude: 5.0))
            .ToList();
        var after = Build(perturbed, Options(OvernightPolicy.HansenLundeScaling));

        for (int i = 0; i < baseline.Count - 1; i++)
        {
            Assert.Equal(baseline[i].TotalVariance, after[i].TotalVariance, 15);
        }
    }

    // ---------- presets ----------

    [Fact]
    public void ThePresetsDifferOnlyInTheirSessionProfile()
    {
        VolatilityPresets.Spy(out var spySession, out var spyOptions);
        VolatilityPresets.Spx(out var spxSession, out var spxOptions);

        Assert.Equal(1, spySession.SkipMinutesAfterOpen);
        Assert.Equal(5, spxSession.SkipMinutesAfterOpen);

        foreach (var o in new[] { spyOptions, spxOptions })
        {
            Assert.Equal(1, o.SourceBarMinutes);
            Assert.Equal(5, o.SamplingMinutes);
            Assert.True(o.UseSubsampling);
            Assert.Equal(BarTimestampConvention.BarStart, o.TimestampConvention);
            Assert.Equal(OvernightPolicy.HansenLundeScaling, o.OvernightPolicy);
            Assert.Equal(252, o.OvernightScalingWindow);
        }
    }

    [Fact]
    public void ThePresetBuildersLabelTheirSeries()
    {
        var bars = WigglySession(Day1);

        Assert.Equal("SPY", VolatilityPresets.BuildSpy(bars)[0].Symbol);
        Assert.Equal("SPX", VolatilityPresets.BuildSpx(bars)[0].Symbol);
    }

    [Fact]
    public void TheSpyPresetLeavesDistributionsEmptyUnlessSupplied()
    {
        VolatilityPresets.Spy(out _, out var options);

        Assert.Empty(options.ExDividends);
    }

    [Fact]
    public void SuppliedDistributionsReachTheSpySeries()
    {
        var day2 = Day1.AddDays(1);
        var bars = Session(Day1, i => 100.0 + (i % 7) * 0.25)
            .Concat(Session(day2, i => 99.0 + (i % 7) * 0.25)).ToList();

        var plain = VolatilityPresets.BuildSpy(bars);
        // Keyed on the date component, so a timestamped key still matches the session.
        var adjusted = VolatilityPresets.BuildSpy(bars, new Dictionary<DateTime, double>
        {
            [day2.AddHours(11)] = 1.0,
        });

        Assert.Equal(0.0, plain[1].DividendAdjustment);
        Assert.Equal(1.0, adjusted[1].DividendAdjustment);
    }

    [Fact]
    public void TheIndexPresetSkipsTheStaleOpeningPrints()
    {
        // 09:31-09:34 are inside SPY's session and outside SPX's, so the index series must
        // start later even given identical bars.
        var bars = WigglySession(Day1);

        Assert.True(VolatilityPresets.BuildSpx(bars)[0].FirstBarTime
            > VolatilityPresets.BuildSpy(bars)[0].FirstBarTime);
    }
}
