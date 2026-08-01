using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the exact edges of the window the estimator samples, and how bars outside any session
/// are handled.
/// </summary>
/// <remarks>
/// One bar either side of a boundary changes realized variance without changing anything that
/// looks wrong, so the inclusivity of each edge is asserted directly rather than inferred from
/// a session's return count.
/// </remarks>
public class SessionWindowTests
{
    private static readonly DateOnly Day1 = new(2024, 3, 4);

    private static RealizedVolatilityOptions Options() => new()
    {
        SourceBarMinutes = 1,
        SamplingMinutes = 5,
        UseSubsampling = false,
        OvernightPolicy = OvernightPolicy.Exclude,
    };

    /// <summary>A policy with no skip, so the window starts exactly at the session open.</summary>
    private static SessionQualityPolicy NoSkip() => new()
    {
        SkipMinutesAfterOpen = 0,
        MinimumReturnsPerDay = 1,
        MaximumStaleSampleFraction = 1.0,
    };

    private static IntradayBar Bar(DateTimeOffset at, double price) =>
        new(at.UtcDateTime, price, price, price, price, 1);

    [Fact]
    public void TheWindowOpensExactlyAtTheSessionOpenPlusTheSkip()
    {
        var session = SessionBars.Regular(Day1)!;

        // Three bars: one a minute before the open, one exactly on it, one after.
        List<IntradayBar> bars =
        [
            Bar(session.OpenUtc.AddMinutes(-1), 90.0),
            Bar(session.OpenUtc, 100.0),
            Bar(session.OpenUtc.AddMinutes(30), 101.0),
        ];

        var days = SessionBars.Build(bars, Options(), NoSkip());

        // The open is inclusive, so the session's first sampled price is the bar on it, not
        // the one before it and not the one after.
        Assert.Single(days);
        Assert.Equal(100.0, days[0].SessionOpen, 12);
    }

    [Fact]
    public void ABarBeforeTheOpenIsExcluded()
    {
        var session = SessionBars.Regular(Day1)!;

        List<IntradayBar> bars =
        [
            Bar(session.OpenUtc.AddMinutes(-1), 1.0),
            Bar(session.OpenUtc, 100.0),
            Bar(session.OpenUtc.AddMinutes(30), 101.0),
        ];

        // A price of 1.0 against 100.0 would dominate realized variance if it were admitted.
        var withEarly = SessionBars.Build(bars, Options(), NoSkip());
        var without = SessionBars.Build(bars.Skip(1), Options(), NoSkip());

        Assert.Equal(without[0].IntradayVariance, withEarly[0].IntradayVariance, 15);
    }

    [Fact]
    public void TheSkipMovesTheWindowStartForward()
    {
        var session = SessionBars.Regular(Day1)!;

        List<IntradayBar> bars =
        [
            Bar(session.OpenUtc, 100.0),
            Bar(session.OpenUtc.AddMinutes(5), 101.0),
            Bar(session.OpenUtc.AddMinutes(30), 102.0),
        ];

        var skipped = SessionBars.Build(bars, Options(), new SessionQualityPolicy
        {
            SkipMinutesAfterOpen = 5, MinimumReturnsPerDay = 1, MaximumStaleSampleFraction = 1.0,
        });

        // The opening auction print is discarded, so the session opens at the 5-minute bar.
        Assert.Equal(101.0, skipped[0].SessionOpen, 12);
    }

    [Fact]
    public void TheWindowIncludesTheClosingPrint()
    {
        var session = SessionBars.Regular(Day1)!;

        // Bars are stamped bar-start, so the last bar *of* the session is stamped one minute
        // before the close — that is the one whose interval ends on it.
        List<IntradayBar> bars =
        [
            Bar(session.OpenUtc, 100.0),
            Bar(session.CloseUtc.AddMinutes(-30), 101.0),
            Bar(session.CloseUtc.AddMinutes(-1), 105.0),
        ];

        var days = SessionBars.Build(bars, Options(), NoSkip());

        // The closing move is a material share of daily variance; dropping it would bias
        // every session low.
        Assert.Equal(105.0, days[0].SessionClose, 12);
    }

    [Fact]
    public void ABarAfterTheCloseIsExcluded()
    {
        var session = SessionBars.Regular(Day1)!;

        List<IntradayBar> bars =
        [
            Bar(session.OpenUtc, 100.0),
            Bar(session.CloseUtc.AddMinutes(-30), 101.0),
            Bar(session.CloseUtc.AddMinutes(-1), 105.0),
            Bar(session.CloseUtc.AddMinutes(5), 500.0),
        ];

        var days = SessionBars.Build(bars, Options(), NoSkip());

        // A post-close print must not become the session close.
        Assert.Equal(105.0, days[0].SessionClose, 12);
    }

    [Fact]
    public void UnderBarEndStampingTheCloseItselfIsSampled()
    {
        var session = SessionBars.Regular(Day1)!;
        var options = Options();
        options.TimestampConvention = BarTimestampConvention.BarEnd;

        // With bar-end stamping a bar carrying the close time closes exactly on the boundary,
        // so the inclusive edge is what admits it.
        List<IntradayBar> bars =
        [
            Bar(session.OpenUtc.AddMinutes(1), 100.0),
            Bar(session.CloseUtc.AddMinutes(-30), 101.0),
            Bar(session.CloseUtc, 105.0),
        ];

        var days = SessionBars.Build(bars, options, NoSkip());

        Assert.Equal(105.0, days[0].SessionClose, 12);
        Assert.Equal(session.CloseUtc.UtcDateTime, days[0].LastBarTime);
    }

    [Fact]
    public void BarsOutsideAnySessionProduceNothing()
    {
        var session = SessionBars.Regular(Day1)!;

        // A full run of bars in the hours before the open: attributed to the trading date, but
        // outside its window.
        var overnight = Enumerable.Range(0, 120)
            .Select(i => Bar(session.OpenUtc.AddHours(-4).AddMinutes(i), 100.0 + (i % 7) * 0.25))
            .ToList();

        Assert.Empty(SessionBars.Build(overnight, Options(), NoSkip()));
    }

    [Fact]
    public void ASingleBarCannotFormASession()
    {
        var session = SessionBars.Regular(Day1)!;

        // One bar yields no return, so there is nothing to estimate from.
        Assert.Empty(SessionBars.Build([Bar(session.OpenUtc, 100.0)], Options(), NoSkip()));

        // Two do.
        Assert.Single(SessionBars.Build(
            [Bar(session.OpenUtc, 100.0), Bar(session.OpenUtc.AddMinutes(30), 101.0)], Options(), NoSkip()));
    }

    [Fact]
    public void SessionsAreEmittedInTradingDateOrder()
    {
        var dates = SessionBars.TradingDates(3, Day1);
        var bars = dates.SelectMany(d => SessionBars.Wiggly(d)).Reverse().ToList();

        var days = SessionBars.Build(bars, Options());

        Assert.Equal(3, days.Count);
        Assert.Equal(dates, days.Select(d => DateOnly.FromDateTime(d.Date)));
    }

    [Fact]
    public void TheCalendarKeyIsReportedWhenMissing()
    {
        Assert.Contains("calendar key is required",
            Assert.Throws<ArgumentException>(() => new RealizedVolatilitySeriesBuilder(
                SessionBars.Clock, " ", SessionQualityPolicy.UsEquity(), new RealizedVolatilityOptions())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCalendarIsRejectedByTheClock()
    {
        // The builder does not validate the key itself; the clock is the authority on which
        // calendars exist, and asking it for a fictional one must fail rather than silently
        // return no sessions.
        var builder = new RealizedVolatilitySeriesBuilder(
            SessionBars.Clock, "NOT_A_CALENDAR", SessionQualityPolicy.UsEquity(), new RealizedVolatilityOptions());

        Assert.ThrowsAny<Exception>(() => builder.Build("SPY", SessionBars.Wiggly(Day1)));
    }

    [Fact]
    public void TheIndexCalendarProducesItsOwnSessions()
    {
        // Same bars, two calendars: both resolve, and the trading dates agree, which is the
        // property the RTH-key rule guarantees.
        var bars = SessionBars.Wiggly(Day1);

        var nyse = SessionBars.Build(bars, Options(), calendar: SessionBars.Nyse);
        var cboe = SessionBars.Build(bars, Options(), calendar: SessionBars.CboeIndex);

        Assert.Single(nyse);
        Assert.Single(cboe);
        Assert.Equal(nyse[0].Date, cboe[0].Date);
    }
}
