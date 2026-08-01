using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the realized-volatility engine: configuration, session filtering, and the overnight
/// policies.
/// </summary>
/// <remarks>
/// Fixtures are built from the real session calendar rather than an assumed 09:30-16:00, so
/// these tests can tell a correct implementation from one that ignores the calendar. The
/// overnight treatment gets the most attention: implied volatility covers calendar time and
/// intraday realized variance does not, so dropping or double-counting the close-to-open move
/// biases every variance risk premium downstream, smoothly and without ever producing a number
/// that looks wrong on its own.
/// </remarks>
public class RealizedVolatilityEngineTests
{
    private static readonly DateOnly Day1 = new(2024, 3, 4);

    private static RealizedVolatilityOptions Options(OvernightPolicy policy) => new()
    {
        SourceBarMinutes = 1,
        SamplingMinutes = 5,
        UseSubsampling = true,
        OvernightPolicy = policy,
        OvernightScalingWindow = 252,
    };

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

    // ---------- session quality policy ----------

    [Fact]
    public void ThePolicyDefaultsAreTheUsEquityConvention()
    {
        var p = SessionQualityPolicy.UsEquity();

        Assert.Equal(1, p.SkipMinutesAfterOpen);
        Assert.Equal(20, p.MinimumReturnsPerDay);
        Assert.Equal(0.20, p.MaximumStaleSampleFraction);
    }

    [Fact]
    public void TheIndexPolicySkipsFurtherIntoTheSession()
    {
        // The printed SPX open is stitched from staggered constituent prints and is not a
        // tradeable simultaneous price, so more of the open is discarded than for SPY.
        Assert.Equal(5, SessionQualityPolicy.SpxIndex().SkipMinutesAfterOpen);
        Assert.True(SessionQualityPolicy.SpxIndex().SkipMinutesAfterOpen
            > SessionQualityPolicy.UsEquity().SkipMinutesAfterOpen);
    }

    [Fact]
    public void ThePolicyKnowsNothingAboutTheCalendar()
    {
        // Regression guard on the whole point of this design: session boundaries, holidays and
        // half days belong to ISessionClock, and a second answer living here is the drift the
        // doctrine exists to prevent.
        var members = typeof(SessionQualityPolicy).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("RegularOpen", members);
        Assert.DoesNotContain("RegularClose", members);
        Assert.DoesNotContain("KnownShortSessions", members);
        Assert.DoesNotContain("ShortSessionToleranceMinutes", members);
    }

    [Theory]
    [InlineData(-1, 20, 0.2)]
    [InlineData(1, -1, 0.2)]
    [InlineData(1, 20, -0.1)]
    [InlineData(1, 20, 1.1)]
    public void ThePolicyRejectsIncoherentThresholds(int skip, int minimum, double stale) =>
        Assert.Throws<InvalidOperationException>(() => new SessionQualityPolicy
        {
            SkipMinutesAfterOpen = skip, MinimumReturnsPerDay = minimum, MaximumStaleSampleFraction = stale,
        }.Validate());

    // ---------- bars ----------

    [Fact]
    public void ABarCarriesItsPricesAndVolume()
    {
        var at = new DateTime(2024, 3, 4, 14, 30, 0, DateTimeKind.Utc);
        var bar = new IntradayBar(at, 1.0, 2.0, 0.5, 1.5, 42);

        Assert.Equal(at, bar.Timestamp);
        Assert.Equal(1.0, bar.Open);
        Assert.Equal(2.0, bar.High);
        Assert.Equal(0.5, bar.Low);
        Assert.Equal(1.5, bar.Close);
        Assert.Equal(42, bar.Volume);
    }

    [Fact]
    public void VolumeDefaultsToZero() =>
        Assert.Equal(0L, new IntradayBar(new DateTime(2024, 3, 4, 14, 30, 0, DateTimeKind.Utc), 1, 1, 1, 1).Volume);

    [Fact]
    public void ALocalTimestampIsRejected()
    {
        // The one mistake that yields a plausible series from the wrong hours of the wrong day.
        var local = new DateTime(2024, 3, 4, 9, 30, 0, DateTimeKind.Local);

        Assert.Contains("UTC instants",
            Assert.Throws<ArgumentException>(() => new IntradayBar(local, 1, 1, 1, 1)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnspecifiedTimestampIsReadAsUtc()
    {
        // Accepted, because the overwhelmingly common construction has no Kind and means UTC.
        var unspecified = new DateTime(2024, 3, 4, 14, 30, 0);

        Assert.Equal(unspecified, new IntradayBar(unspecified, 1, 1, 1, 1).Timestamp);
    }

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
        Assert.Equal(usable, new IntradayBar(
            new DateTime(2024, 3, 4, 14, 30, 0, DateTimeKind.Utc), o, h, l, c).HasUsablePrices);

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
        var policy = SessionQualityPolicy.UsEquity();
        var options = new RealizedVolatilityOptions();

        Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(null!, SessionBars.Nyse, policy, options)).ParamName);
        Assert.Equal("policy", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionBars.Clock, SessionBars.Nyse, null!, options)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionBars.Clock, SessionBars.Nyse, policy, null!)).ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheBuilderRequiresACalendarKey(string? calendar) =>
        Assert.Equal("calendar", Assert.Throws<ArgumentException>(() => new RealizedVolatilitySeriesBuilder(
            SessionBars.Clock, calendar!, SessionQualityPolicy.UsEquity(), new RealizedVolatilityOptions())).ParamName);

    [Fact]
    public void TheBuilderValidatesItsConfigurationOnConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new RealizedVolatilitySeriesBuilder(
            SessionBars.Clock, SessionBars.Nyse, SessionQualityPolicy.UsEquity(),
            new RealizedVolatilityOptions { SourceBarMinutes = 0 }));

        Assert.Throws<InvalidOperationException>(() => new RealizedVolatilitySeriesBuilder(
            SessionBars.Clock, SessionBars.Nyse,
            new SessionQualityPolicy { SkipMinutesAfterOpen = -1 }, new RealizedVolatilityOptions()));
    }

    [Fact]
    public void BuildRejectsNullBars() =>
        Assert.Throws<ArgumentNullException>(() => new RealizedVolatilitySeriesBuilder(
                SessionBars.Clock, SessionBars.Nyse, SessionQualityPolicy.UsEquity(), new RealizedVolatilityOptions())
            .Build("SPY", null!));

    [Fact]
    public void ExtendedHoursPrintsAreExcluded()
    {
        var bars = SessionBars.Wiggly(Day1);
        var session = SessionBars.Regular(Day1)!;

        // A wild ramp five hours before the open must not move the session's variance at all.
        var preMarket = Enumerable.Range(0, 60)
            .Select(i => new IntradayBar(
                session.OpenUtc.AddHours(-5).AddMinutes(i).UtcDateTime,
                500.0 + i, 500.0 + i, 500.0 + i, 500.0 + i))
            .ToList();

        var clean = SessionBars.Build(bars, Options(OvernightPolicy.Exclude));
        var polluted = SessionBars.Build(bars.Concat(preMarket), Options(OvernightPolicy.Exclude));

        Assert.Equal(clean[0].IntradayVariance, polluted[0].IntradayVariance, 15);
    }

    [Fact]
    public void SeparateSessionsBecomeSeparateDays()
    {
        var dates = SessionBars.TradingDates(2, Day1);
        var bars = SessionBars.Wiggly(dates[0]).Concat(SessionBars.Wiggly(dates[1])).ToList();

        var days = SessionBars.Build(bars, Options(OvernightPolicy.Exclude));

        Assert.Equal(2, days.Count);
        Assert.Equal(dates[0], DateOnly.FromDateTime(days[0].Date));
        Assert.Equal(dates[1], DateOnly.FromDateTime(days[1].Date));
        Assert.Equal("SPY", days[0].Symbol);
    }

    [Fact]
    public void ASessionWithTooFewBarsIsDropped() =>
        Assert.Empty(SessionBars.Build(
            SessionBars.Wiggly(Day1, minutes: 0), Options(OvernightPolicy.Exclude)));

    [Fact]
    public void AThinSessionIsFlaggedRatherThanDropped()
    {
        // Ten minutes of data is far below MinimumReturnsPerDay but is still emitted, so a
        // gap stays visible to whatever consumes the series instead of silently vanishing.
        var days = SessionBars.Build(SessionBars.Wiggly(Day1, minutes: 10), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.False(days[0].IsComplete);
    }

    [Fact]
    public void AFullSessionIsComplete()
    {
        var days = SessionBars.Build(SessionBars.Wiggly(Day1), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.True(days[0].IsComplete);
        Assert.False(days[0].IsShortSession);
    }

    [Fact]
    public void SessionOpenAndCloseAreTheFirstAndLastSampledPrices()
    {
        var days = SessionBars.Build(
            SessionBars.Session(Day1, i => 100.0 + i * 0.01), Options(OvernightPolicy.Exclude));

        Assert.True(days[0].SessionOpen < days[0].SessionClose);
        Assert.True(days[0].FirstBarTime < days[0].LastBarTime);
    }

    // ---------- the calendar is the authority ----------

    [Fact]
    public void AHalfDayIsFlaggedFromTheCalendarNotInferredFromTheData()
    {
        // 2024-11-29, the day after Thanksgiving: a genuine early close.
        var halfDay = new DateOnly(2024, 11, 29);
        var session = SessionBars.Regular(halfDay)!;

        Assert.True(session.IsHalfDay);

        var days = SessionBars.Build(SessionBars.Wiggly(halfDay), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.True(days[0].IsShortSession);

        // And it is a complete half day, not a truncated full one - a distinction the old
        // shortfall-in-minutes heuristic could not make.
        Assert.True(days[0].IsComplete);
    }

    [Fact]
    public void AFeedThatStopsEarlyIsIncompleteNotAHalfDay()
    {
        // The same early last bar, a different cause, and the opposite handling.
        var days = SessionBars.Build(SessionBars.Wiggly(Day1, minutes: 60), Options(OvernightPolicy.Exclude));

        Assert.Single(days);
        Assert.False(days[0].IsShortSession);
        Assert.False(days[0].IsComplete);
    }

    [Fact]
    public void AHolidayProducesNoSession()
    {
        // Bars stamped on a holiday are attributed forward to the next trading date and then
        // fall outside that session's window, so they are dropped rather than folded into a
        // neighbouring day - which is what calendar-date bucketing would have done.
        var independenceDay = new DateOnly(2024, 7, 4);
        Assert.Null(SessionBars.Regular(independenceDay));

        var open = SessionBars.Regular(new DateOnly(2024, 7, 3))!.OpenUtc;
        var holidayBars = Enumerable.Range(0, 300)
            .Select(i =>
            {
                var p = 100.0 + (i % 7) * 0.25;
                return new IntradayBar(open.AddDays(1).AddMinutes(i).UtcDateTime, p, p, p, p);
            })
            .ToList();

        Assert.Empty(SessionBars.Build(holidayBars, Options(OvernightPolicy.Exclude)));
    }

    [Fact]
    public void SessionsSurviveTheDaylightSavingTransition()
    {
        // 2024-03-10 is the spring-forward. The sessions either side are the same length in
        // wall-clock terms but sit at different UTC instants, which a fixed offset gets wrong.
        var before = SessionBars.Regular(new DateOnly(2024, 3, 8))!;
        var after = SessionBars.Regular(new DateOnly(2024, 3, 11))!;

        Assert.Equal(before.CloseUtc - before.OpenUtc, after.CloseUtc - after.OpenUtc);
        Assert.NotEqual(before.OpenUtc.TimeOfDay, after.OpenUtc.TimeOfDay);

        var days = SessionBars.Build(
            SessionBars.Wiggly(new DateOnly(2024, 3, 8)).Concat(SessionBars.Wiggly(new DateOnly(2024, 3, 11))),
            Options(OvernightPolicy.Exclude));

        Assert.Equal(2, days.Count);
        Assert.All(days, d => Assert.True(d.IsComplete));
    }

    [Fact]
    public void TheCalendarGovernsTheSamplingWindow()
    {
        // The grid runs to the calendar's close, so a half day yields materially fewer
        // returns than a full one from the same generator.
        var full = SessionBars.Build(SessionBars.Wiggly(Day1), Options(OvernightPolicy.Exclude));
        var half = SessionBars.Build(SessionBars.Wiggly(new DateOnly(2024, 11, 29)), Options(OvernightPolicy.Exclude));

        Assert.True(half[0].ReturnCount < full[0].ReturnCount);
    }

    // ---------- overnight policies ----------

    [Fact]
    public void ExcludeLeavesTotalVarianceAtTheIntradayFigure()
    {
        var days = SessionBars.Build(SessionBars.Series(2, i => 100.0 + i * 10.0), Options(OvernightPolicy.Exclude));

        Assert.All(days, d => Assert.Equal(d.IntradayVariance, d.TotalVariance, 15));
    }

    [Fact]
    public void AddSquaredReturnAddsExactlyTheOvernightMove()
    {
        var days = SessionBars.Build(
            SessionBars.Series(2, i => 100.0 + i * 10.0), Options(OvernightPolicy.AddSquaredReturn));

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
        var days = SessionBars.Build(
            SessionBars.Series(2, i => 100.0 + i * 10.0), Options(OvernightPolicy.AddSquaredReturn));

        Assert.Equal(Math.Log(days[1].SessionOpen / days[0].SessionClose), days[1].OvernightReturn, 15);
        Assert.Equal(
            days[1].OvernightReturn + Math.Log(days[1].SessionClose / days[1].SessionOpen),
            days[1].CloseToCloseReturn, 15);
    }

    [Fact]
    public void AnExDividendGapIsAddedBackBeforeMeasuringTheOvernightReturn()
    {
        var dates = SessionBars.TradingDates(2, Day1);
        // Price gaps down by exactly the distribution overnight.
        var bars = SessionBars.Session(dates[0], i => 100.0 + (i % 7) * 0.25)
            .Concat(SessionBars.Session(dates[1], i => 99.0 + (i % 7) * 0.25)).ToList();

        var plain = SessionBars.Build(bars, Options(OvernightPolicy.AddSquaredReturn));

        var adjusted = Options(OvernightPolicy.AddSquaredReturn);
        adjusted.ExDividends[dates[1].ToDateTime(TimeOnly.MinValue)] = 1.0;
        var withAdjustment = SessionBars.Build(bars, adjusted);

        Assert.Equal(1.0, withAdjustment[1].DividendAdjustment);
        Assert.Equal(0.0, plain[1].DividendAdjustment);

        // The mechanical gap is a cash transfer, not volatility: adding it back shrinks the
        // overnight move toward zero.
        Assert.True(Math.Abs(withAdjustment[1].OvernightReturn) < Math.Abs(plain[1].OvernightReturn));
    }

    [Fact]
    public void HansenLundeFallsBackToTheSquaredReturnDuringWarmUp()
    {
        // Fewer than the 20 sessions the ratio needs, so the noisier estimate is used.
        var bars = SessionBars.Series(5, i => 100.0 + i);

        var scaled = SessionBars.Build(bars, Options(OvernightPolicy.HansenLundeScaling));
        var added = SessionBars.Build(bars, Options(OvernightPolicy.AddSquaredReturn));

        Assert.Equal(added.Select(d => d.TotalVariance), scaled.Select(d => d.TotalVariance));
    }

    [Fact]
    public void HansenLundeNeverScalesBelowTheIntradayFigure()
    {
        var days = SessionBars.Build(
            SessionBars.Series(60, i => 100.0 * Math.Pow(1.01, i)), Options(OvernightPolicy.HansenLundeScaling));

        // The overnight session adds variance, so the factor is floored at one.
        Assert.All(days, d => Assert.True(d.TotalVariance >= d.IntradayVariance));
    }

    // ---------- presets ----------

    [Fact]
    public void ThePresetsDifferOnlyInTheirPolicyAndCalendar()
    {
        VolatilityPresets.Spy(out var spyPolicy, out var spyOptions);
        VolatilityPresets.Spx(out var spxPolicy, out var spxOptions);

        Assert.Equal(1, spyPolicy.SkipMinutesAfterOpen);
        Assert.Equal(5, spxPolicy.SkipMinutesAfterOpen);
        Assert.Equal("NYSE", VolatilityPresets.SpyCalendar);

        // NOT CBOE_INDEX_RTH, which is the index OPTION window (08:30-15:15 CT, 405 min). The SPX
        // index level stops at the cash close — TWS reports liquidHours 0830-1500 and returns exactly
        // 390 one-minute bars a session. Pinning the option calendar here made this assertion agree
        // with the constant while both disagreed with the data the estimator is fed; see
        // VolatilityPresets.SpxCalendar's remarks.
        Assert.Equal("CBOE_SPX_RTH", VolatilityPresets.SpxCalendar);

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
        var spyBars = SessionBars.Wiggly(Day1);
        var spxBars = SessionBars.Wiggly(Day1, calendar: SessionBars.CboeIndex);

        Assert.Equal("SPY", VolatilityPresets.BuildSpy(SessionBars.Clock, spyBars)[0].Symbol);
        Assert.Equal("SPX", VolatilityPresets.BuildSpx(SessionBars.Clock, spxBars)[0].Symbol);
    }

    [Fact]
    public void TheStudyTargetExcludesTheOvernightGap()
    {
        VolatilityPresets.Spx(out var premiumPolicy, out var premiumOptions);
        VolatilityPresets.SpxStudyTarget(out var studyPolicy, out var studyOptions);

        // The premium pipeline prices calendar time, so it folds the close-to-open move in.
        Assert.Equal(OvernightPolicy.HansenLundeScaling, premiumOptions.OvernightPolicy);
        // The study's v1 label is session RV only.
        Assert.Equal(OvernightPolicy.Exclude, studyOptions.OvernightPolicy);

        // Everything else is the same estimator, including the index session policy.
        Assert.Equal(premiumPolicy.SkipMinutesAfterOpen, studyPolicy.SkipMinutesAfterOpen);
        Assert.Equal(premiumOptions.SourceBarMinutes, studyOptions.SourceBarMinutes);
        Assert.Equal(premiumOptions.SamplingMinutes, studyOptions.SamplingMinutes);
        Assert.Equal(premiumOptions.UseSubsampling, studyOptions.UseSubsampling);
        Assert.Equal(premiumOptions.TimestampConvention, studyOptions.TimestampConvention);
    }

    [Fact]
    public void TheStudyTargetAndThePremiumSeriesDisagreeOnOvernightVariance()
    {
        // Not a theoretical difference: the two presets produce different labels from the
        // same bars, which is exactly why they are separate rather than a changed default.
        var bars = SessionBars.Series(30, i => 100.0 * Math.Pow(1.01, i), calendar: SessionBars.CboeIndex);

        var premium = VolatilityPresets.BuildSpx(SessionBars.Clock, bars);
        var study = VolatilityPresets.BuildSpxStudyTarget(SessionBars.Clock, bars);

        Assert.Equal(premium.Count, study.Count);
        Assert.All(study, d => Assert.Equal(d.IntradayVariance, d.TotalVariance, 15));
        Assert.Contains(premium.Zip(study), p => p.First.TotalVariance > p.Second.TotalVariance);
        Assert.Equal("SPX", study[0].Symbol);
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
        var dates = SessionBars.TradingDates(2, Day1);
        var bars = SessionBars.Session(dates[0], i => 100.0 + (i % 7) * 0.25)
            .Concat(SessionBars.Session(dates[1], i => 99.0 + (i % 7) * 0.25)).ToList();

        var plain = VolatilityPresets.BuildSpy(SessionBars.Clock, bars);
        // Keyed on the date component, so a timestamped key still matches the session.
        var adjusted = VolatilityPresets.BuildSpy(SessionBars.Clock, bars, new Dictionary<DateTime, double>
        {
            [dates[1].ToDateTime(new TimeOnly(11, 0))] = 1.0,
        });

        Assert.Equal(0.0, plain[1].DividendAdjustment);
        Assert.Equal(1.0, adjusted[1].DividendAdjustment);
    }

    [Fact]
    public void TheIndexPresetSkipsTheStaleOpeningPrints()
    {
        // Cboe index RTH and NYSE open at the same instant, so with identical bars the only
        // difference is the extra skip the index policy applies.
        var spy = SessionBars.Regular(Day1)!;
        var spx = SessionBars.Regular(Day1, SessionBars.CboeIndex)!;
        Assert.Equal(spy.OpenUtc, spx.OpenUtc);

        var bars = SessionBars.Wiggly(Day1);

        Assert.True(VolatilityPresets.BuildSpx(SessionBars.Clock, bars)[0].FirstBarTime
            > VolatilityPresets.BuildSpy(SessionBars.Clock, bars)[0].FirstBarTime);
    }
}
