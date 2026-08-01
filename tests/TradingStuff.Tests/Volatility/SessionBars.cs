using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Builds bars against the platform's real session calendar.
/// </summary>
/// <remarks>
/// Deliberately asks <see cref="SessionClock"/> for each session's boundaries instead of
/// hardcoding 09:30-16:00. Hardcoding is what the estimator used to do, and it is what these
/// tests exist to prove has stopped: a fixture that assumed the boundaries could not tell a
/// correct implementation from one that ignored the calendar entirely. As a side effect the
/// fixtures inherit real holidays and half days for free.
/// </remarks>
internal static class SessionBars
{
    /// <summary>The real clock. Generation is memoised, so a shared instance is fine.</summary>
    public static readonly ISessionClock Clock = new SessionClock();

    public const string Nyse = VolatilityPresets.SpyCalendar;
    public const string CboeIndex = VolatilityPresets.SpxCalendar;

    /// <summary>The regular session for a trading date, or null if the calendar has none.</summary>
    public static TradingSession? Regular(DateOnly date, string calendar = Nyse) =>
        Clock.SessionsBetween(calendar, date, date).FirstOrDefault(s => s.Label == "RTH");

    /// <summary>Consecutive trading dates on a calendar, starting at or after <paramref name="from"/>.</summary>
    public static List<DateOnly> TradingDates(int count, DateOnly? from = null, string calendar = Nyse)
    {
        var start = from ?? new DateOnly(2024, 1, 2);
        var dates = new List<DateOnly>();

        for (var d = start; dates.Count < count; d = d.AddDays(1))
        {
            if (Regular(d, calendar) is not null) dates.Add(d);
        }

        return dates;
    }

    /// <summary>
    /// One-minute bars spanning a session, priced by <paramref name="priceAt"/> on the minute
    /// index from the open.
    /// </summary>
    /// <param name="minutes">
    /// How many minutes of the session to emit. Null fills it; a smaller number produces a
    /// session that stops early, which is an incomplete feed rather than a half day.
    /// </param>
    public static List<IntradayBar> Session(
        DateOnly date, Func<int, double> priceAt, int? minutes = null, string calendar = Nyse)
    {
        var session = Regular(date, calendar)
            ?? throw new InvalidOperationException($"{calendar} has no regular session on {date:yyyy-MM-dd}.");

        var span = (int)(session.CloseUtc - session.OpenUtc).TotalMinutes;
        var count = Math.Min(minutes ?? span, span);

        return Enumerable.Range(0, count + 1)
            .Select(i =>
            {
                var price = priceAt(i);
                return new IntradayBar(session.OpenUtc.AddMinutes(i).UtcDateTime, price, price, price, price, 100);
            })
            .ToList();
    }

    /// <summary>A session that oscillates, so realized variance is strictly positive.</summary>
    public static List<IntradayBar> Wiggly(
        DateOnly date, double baseline = 100.0, double amplitude = 0.25, int? minutes = null, string calendar = Nyse) =>
        Session(date, i => baseline + (i % 7) * amplitude, minutes, calendar);

    /// <summary>Consecutive wiggly sessions, each scaled by <paramref name="baselineAt"/>.</summary>
    public static List<IntradayBar> Series(
        int sessions, Func<int, double>? baselineAt = null, double amplitude = 0.25, string calendar = Nyse)
    {
        baselineAt ??= _ => 100.0;
        return TradingDates(sessions, calendar: calendar)
            .SelectMany((d, i) => Wiggly(d, baselineAt(i), amplitude, calendar: calendar))
            .ToList();
    }

    /// <summary>Builds a series with the given policy and options against the real clock.</summary>
    public static List<RealizedVolatilityDay> Build(
        IEnumerable<IntradayBar> bars,
        RealizedVolatilityOptions options,
        SessionQualityPolicy? policy = null,
        string calendar = Nyse,
        string symbol = "SPY") =>
        new RealizedVolatilitySeriesBuilder(Clock, calendar, policy ?? SessionQualityPolicy.UsEquity(), options)
            .Build(symbol, bars);
}
