using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the Hansen-Lunde overnight scaling and the session-quality thresholds.
/// </summary>
/// <remarks>
/// The scaling factor is the one piece of this pipeline that reads other days, so it is where
/// lookahead would enter if the window were ever taken as inclusive. These tests recompute the
/// expected factor from the emitted series using the trailing window's own definition, which
/// makes the assertions fail if the window bounds, the usability filter, or the ratio change —
/// rather than merely checking that the number moved in a plausible direction.
/// </remarks>
public class HansenLundeScalingTests
{
    private static readonly DateOnly Day1 = new(2024, 3, 4);
    private const int MinimumCalibrationDays = 20;

    private static RealizedVolatilityOptions Options(int window = 252) => new()
    {
        SourceBarMinutes = 1,
        SamplingMinutes = 5,
        UseSubsampling = true,
        OvernightPolicy = OvernightPolicy.HansenLundeScaling,
        OvernightScalingWindow = window,
    };

    /// <summary>Recomputes the factor the implementation should have applied to day <paramref name="i"/>.</summary>
    private static double? ExpectedScale(List<RealizedVolatilityDay> days, int i, int window)
    {
        var windowStart = Math.Max(0, i - window);
        double cc = 0.0, intraday = 0.0;
        var usable = 0;

        // Strictly trailing: j < i, never j <= i.
        for (int j = windowStart; j < i; j++)
        {
            var prior = days[j];
            if (!prior.HasOvernightReturn || !prior.IsComplete || prior.IntradayVariance <= 0.0) continue;

            cc += prior.CloseToCloseReturn * prior.CloseToCloseReturn;
            intraday += prior.IntradayVariance;
            usable++;
        }

        if (usable < MinimumCalibrationDays || intraday <= 0.0) return null;

        var scale = cc / intraday;
        return scale < 1.0 ? 1.0 : scale;
    }

    /// <summary>A series with a persistent overnight gap, so close-to-close exceeds intraday.</summary>
    private static List<IntradayBar> GappySeries(int sessions) =>
        SessionBars.Series(sessions, i => 100.0 * Math.Pow(1.01, i), amplitude: 0.05);

    // ---------- the factor itself ----------

    [Fact]
    public void TheAppliedFactorMatchesTheTrailingWindowDefinition()
    {
        const int window = 25;
        var days = SessionBars.Build(GappySeries(60), Options(window));

        var scaled = 0;
        for (int i = 0; i < days.Count; i++)
        {
            var expected = ExpectedScale(days, i, window);
            if (expected is null) continue;

            Assert.Equal(days[i].IntradayVariance * expected.Value, days[i].TotalVariance, 15);
            scaled++;
        }

        // The test is worthless if nothing actually reached the calibrated branch.
        Assert.True(scaled > 10, $"only {scaled} sessions were scaled");
    }

    [Fact]
    public void TheWindowIsBoundedSoDistantHistoryIsForgotten()
    {
        // A short window and a long one must disagree once enough history exists for the
        // short one to have dropped sessions the long one still sees.
        var bars = GappySeries(80);

        var shortWindow = SessionBars.Build(bars, Options(25));
        var longWindow = SessionBars.Build(bars, Options(252));

        Assert.Equal(shortWindow.Count, longWindow.Count);
        Assert.NotEqual(shortWindow[^1].TotalVariance, longWindow[^1].TotalVariance, 12);
    }

    [Fact]
    public void TheFactorUsesOnlyPriorSessionsNotTheCurrentOne()
    {
        const int window = 30;
        var days = SessionBars.Build(GappySeries(60), Options(window));

        // Recomputing with an inclusive window (j <= i) must disagree, which is what proves
        // the implementation is not quietly including the day it is scaling.
        var i = days.Count - 1;
        var inclusiveCc = 0.0;
        var inclusiveIntraday = 0.0;
        for (int j = Math.Max(0, i - window); j <= i; j++)
        {
            if (!days[j].HasOvernightReturn || !days[j].IsComplete || days[j].IntradayVariance <= 0.0) continue;
            inclusiveCc += days[j].CloseToCloseReturn * days[j].CloseToCloseReturn;
            inclusiveIntraday += days[j].IntradayVariance;
        }

        var inclusiveScale = inclusiveCc / inclusiveIntraday;
        var applied = days[i].TotalVariance / days[i].IntradayVariance;

        Assert.NotEqual(inclusiveScale, applied, 12);
        Assert.Equal(ExpectedScale(days, i, window)!.Value, applied, 12);
    }

    // ---------- the calibration threshold ----------

    [Fact]
    public void ScalingBeginsOnlyAfterTwentyUsableSessions()
    {
        var days = SessionBars.Build(GappySeries(40), Options());

        for (int i = 0; i < days.Count; i++)
        {
            var fallback = days[i].IntradayVariance
                + (days[i].HasOvernightReturn ? days[i].OvernightReturn * days[i].OvernightReturn : 0.0);

            if (ExpectedScale(days, i, 252) is null)
            {
                Assert.Equal(fallback, days[i].TotalVariance, 15);
            }
        }

        // And confirm both branches were actually exercised.
        Assert.NotNull(ExpectedScale(days, days.Count - 1, 252));
        Assert.Null(ExpectedScale(days, 5, 252));
    }

    [Fact]
    public void TheWarmUpFallbackIsIntradayPlusTheSquaredOvernightMove()
    {
        var days = SessionBars.Build(GappySeries(10), Options());

        // Ten sessions is short of the calibration minimum, so every day takes the fallback.
        Assert.All(days, d => Assert.Equal(
            d.IntradayVariance + (d.HasOvernightReturn ? d.OvernightReturn * d.OvernightReturn : 0.0),
            d.TotalVariance, 15));

        // The first session has no prior close, so it gets intraday variance alone.
        Assert.False(days[0].HasOvernightReturn);
        Assert.Equal(days[0].IntradayVariance, days[0].TotalVariance, 15);
    }

    [Fact]
    public void IncompleteSessionsAreExcludedFromTheCalibration()
    {
        const int window = 252;
        // Every fifth session is thin, so it never reaches MinimumReturnsPerDay.
        var dates = SessionBars.TradingDates(60);
        var bars = dates
            .SelectMany((d, i) => SessionBars.Wiggly(
                d, 100.0 * Math.Pow(1.01, i), 0.05, minutes: i % 5 == 0 ? 10 : null))
            .ToList();

        var days = SessionBars.Build(bars, Options(window));

        Assert.Contains(days, d => !d.IsComplete);

        // ExpectedScale skips incomplete priors; if the implementation did not, these would
        // disagree.
        for (int i = 0; i < days.Count; i++)
        {
            var expected = ExpectedScale(days, i, window);
            if (expected is null) continue;
            Assert.Equal(days[i].IntradayVariance * expected.Value, days[i].TotalVariance, 15);
        }
    }

    // ---------- the floor ----------

    [Fact]
    public void TheFactorIsFlooredAtOneWhenThereIsNoOvernightMove()
    {
        // Identical baseline every session, so the close-to-close move is tiny while intraday
        // variance is substantial: the raw ratio is far below one.
        var days = SessionBars.Build(SessionBars.Series(60, _ => 100.0, amplitude: 0.25), Options());
        var late = days[^1];

        // The overnight session adds variance; a factor below one would mean the trailing
        // window is degenerate, not that nights are calming.
        Assert.Equal(1.0, late.TotalVariance / late.IntradayVariance, 12);
        Assert.All(days, d => Assert.True(d.TotalVariance >= d.IntradayVariance));
    }

    [Fact]
    public void APersistentOvernightGapPushesTheFactorAboveOne()
    {
        // Large close-to-open moves against a quiet intraday session, so the trailing ratio
        // of close-to-close to intraday variance is unambiguously above one.
        var days = SessionBars.Build(
            SessionBars.Series(60, i => 100.0 * Math.Pow(1.05, i), amplitude: 0.01), Options());
        var late = days[^1];

        Assert.True(late.TotalVariance > late.IntradayVariance,
            $"scale was {late.TotalVariance / late.IntradayVariance:G6}");
    }

    // ---------- session quality thresholds ----------

    [Fact]
    public void CompletenessTurnsOnTheMinimumReturnCount()
    {
        var policy = SessionQualityPolicy.UsEquity();
        policy.MinimumReturnsPerDay = 20;
        var options = Options();
        options.OvernightPolicy = OvernightPolicy.Exclude;

        // Sampling every five minutes: ~105 minutes gives 20+ returns, 60 gives about 12.
        var enough = SessionBars.Build(SessionBars.Wiggly(Day1, minutes: 105), options, policy);
        var tooFew = SessionBars.Build(SessionBars.Wiggly(Day1, minutes: 60), options, policy);

        Assert.True(enough[0].ReturnCount >= 20);
        Assert.True(enough[0].IsComplete);
        Assert.True(tooFew[0].ReturnCount < 20);
        Assert.False(tooFew[0].IsComplete);
    }

    [Fact]
    public void TooManyStaleSamplesMarkASessionIncomplete()
    {
        var options = Options();
        options.OvernightPolicy = OvernightPolicy.Exclude;
        var policy = SessionQualityPolicy.UsEquity();

        // A dense first hour then a long hole: plenty of returns, but most grid points reuse
        // the previous bar. The return count alone cannot catch this, which is the point.
        var session = SessionBars.Regular(Day1)!;
        var bars = Enumerable.Range(0, 60)
            .Select(i =>
            {
                var p = 100.0 + (i % 7) * 0.25;
                return new IntradayBar(session.OpenUtc.AddMinutes(i).UtcDateTime, p, p, p, p, 1);
            })
            .ToList();
        bars.Add(new IntradayBar(session.CloseUtc.AddMinutes(-1).UtcDateTime, 101.0, 101.0, 101.0, 101.0, 1));

        var days = SessionBars.Build(bars, options, policy);

        Assert.True(days[0].StaleSamples > 0);
        Assert.True((double)days[0].StaleSamples / days[0].ReturnCount > policy.MaximumStaleSampleFraction);
        Assert.False(days[0].IsComplete);
    }

    [Fact]
    public void TheStaleFractionThresholdIsInclusive()
    {
        var options = Options();
        options.OvernightPolicy = OvernightPolicy.Exclude;

        // A permissive threshold accepts a session a strict one rejects, so the comparison is
        // load-bearing rather than decorative.
        var session = SessionBars.Regular(Day1)!;
        var bars = Enumerable.Range(0, 60)
            .Select(i =>
            {
                var p = 100.0 + (i % 7) * 0.25;
                return new IntradayBar(session.OpenUtc.AddMinutes(i).UtcDateTime, p, p, p, p, 1);
            })
            .ToList();
        bars.Add(new IntradayBar(session.CloseUtc.AddMinutes(-1).UtcDateTime, 101.0, 101.0, 101.0, 101.0, 1));

        var strict = SessionBars.Build(bars, options, new SessionQualityPolicy
        {
            SkipMinutesAfterOpen = 1, MinimumReturnsPerDay = 1, MaximumStaleSampleFraction = 0.01,
        });
        var permissive = SessionBars.Build(bars, options, new SessionQualityPolicy
        {
            SkipMinutesAfterOpen = 1, MinimumReturnsPerDay = 1, MaximumStaleSampleFraction = 1.0,
        });

        Assert.False(strict[0].IsComplete);
        Assert.True(permissive[0].IsComplete);
    }

    // ---------- subsampling ----------

    [Fact]
    public void EveryGridOffsetIsUsedWhenSubsamplingIsOn()
    {
        var single = Options();
        single.UseSubsampling = false;
        single.OvernightPolicy = OvernightPolicy.Exclude;

        var subsampled = Options();
        subsampled.OvernightPolicy = OvernightPolicy.Exclude;

        var bars = SessionBars.Wiggly(Day1);

        var one = SessionBars.Build(bars, single);
        var many = SessionBars.Build(bars, subsampled);

        Assert.Equal(1, single.SubsampleGridCount);
        Assert.Equal(5, subsampled.SubsampleGridCount);
        // Averaging over five offsets is a different estimate from any single grid.
        Assert.NotEqual(one[0].IntradayVariance, many[0].IntradayVariance, 12);
    }

    // ---------- argument names ----------

    [Fact]
    public void ArgumentFailuresNameTheParameterAtFault()
    {
        var options = new RealizedVolatilityOptions();
        var policy = SessionQualityPolicy.UsEquity();

        Assert.Equal("clock", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(null!, SessionBars.Nyse, policy, options)).ParamName);
        Assert.Equal("policy", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionBars.Clock, SessionBars.Nyse, null!, options)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionBars.Clock, SessionBars.Nyse, policy, null!)).ParamName);
        Assert.Equal("bars", Assert.Throws<ArgumentNullException>(() =>
            new RealizedVolatilitySeriesBuilder(SessionBars.Clock, SessionBars.Nyse, policy, options)
                .Build("SPY", null!)).ParamName);
    }
}
