using System.Globalization;
using IbContract = IBApi.Contract;

namespace TradingStuff.IbkrGateway.History;

/// <summary>
/// A generic IBKR contract descriptor for historical data requests.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="TradingStuff.Contracts.OptionContract"/>: historical bars and head
/// timestamps are requested against underlyings, indices, and futures (including <c>CONTFUT</c>)
/// at least as often as options, and forcing every caller through an option-shaped record would
/// make those cases unrepresentable. This mirrors <see cref="IBApi.Contract"/> field-for-field
/// rather than adding an abstraction over it, including <see cref="IncludeExpired"/> — required for
/// the expired-contract walk a later work package performs, since a live <c>CONTFUT</c> rejects a
/// past <c>endDateTime</c> with error 10339.
/// </remarks>
public sealed record HistoricalContractSpec(
    string Symbol,
    string SecType,
    string Exchange = "SMART",
    string Currency = "USD",
    string? LastTradeDateOrContractMonth = null,
    decimal? Strike = null,
    string? Right = null,
    string? Multiplier = null,
    string? TradingClass = null,
    string? PrimaryExchange = null,
    int? ConId = null,
    bool IncludeExpired = false)
{
    internal IbContract ToIbContract() => new()
    {
        ConId = ConId ?? 0,
        Symbol = Symbol.ToUpperInvariant(),
        SecType = SecType.ToUpperInvariant(),
        Exchange = Exchange,
        PrimaryExch = PrimaryExchange,
        Currency = Currency,
        LastTradeDateOrContractMonth = LastTradeDateOrContractMonth,
        Strike = Strike.HasValue ? (double)Strike.Value : 0d,
        Right = Right,
        Multiplier = Multiplier,
        TradingClass = TradingClass,
        IncludeExpired = IncludeExpired,
    };
}

/// <summary>Request body for <c>POST /ibkr/history/bars</c>.</summary>
/// <param name="Contract">The instrument to request bars for.</param>
/// <param name="EndDateTime">
/// UTC instant the bars end at; null requests "now". Sent to TWS in the unambiguous
/// <c>yyyyMMdd-HH:mm:ss</c> UTC wire format rather than an exchange-local string, for the same
/// reason bars themselves are requested with <c>formatDate=2</c> — exchange-local strings are a
/// documented source of silent timezone bugs.
/// </param>
/// <param name="Duration">TWS duration string, e.g. <c>"1 D"</c>, <c>"5 D"</c>, <c>"6 M"</c>, <c>"1 Y"</c>.</param>
/// <param name="BarSize">TWS bar size, e.g. <c>"1 min"</c>, <c>"5 secs"</c>, <c>"1 day"</c>.</param>
/// <param name="WhatToShow">
/// <c>TRADES</c>, <c>MIDPOINT</c>, <c>BID</c>, <c>ASK</c>, <c>BID_ASK</c>, etc. <c>BID_ASK</c> costs
/// double against the historical pacing window.
/// </param>
/// <param name="UseRth">Regular trading hours only when true; TWS's <c>useRTH</c> flag.</param>
public sealed record HistoricalBarsRequest(
    HistoricalContractSpec Contract,
    DateTimeOffset? EndDateTime,
    string Duration,
    string BarSize,
    string WhatToShow,
    bool UseRth = true);

/// <summary>
/// One bar. Exactly one of <see cref="Timestamp"/> (intraday) or <see cref="TradingDate"/> (daily
/// and coarser) is populated on a value produced by <see cref="HistoricalBarTime"/> — see there for
/// why TWS makes that distinction load-bearing rather than cosmetic.
/// </summary>
public sealed record HistoricalBar(
    DateTimeOffset? Timestamp,
    DateOnly? TradingDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    int Count,
    decimal Wap)
{
    /// <summary>True for a daily-or-coarser bar, where only <see cref="TradingDate"/> is meaningful.</summary>
    public bool IsDaily => TradingDate is not null;
}

/// <summary>Response body for <c>POST /ibkr/history/bars</c>.</summary>
/// <param name="Bars">Empty when <paramref name="HasData"/> is false.</param>
/// <param name="HasData">
/// False when TWS answered with error 162 ("HMDS query returned no data") — a confirmed-empty
/// slice, not a request failure. An automated backfill coordinator should mark the slice
/// permanently empty rather than retry it. This is a 200 OK either way: only the pacing budget, a
/// connection outage, or a rejected contract are actual request failures (see the endpoint's error
/// mapping in Program.cs). Kept as an explicit flag rather than relying on an empty
/// <paramref name="Bars"/> list alone, so "no data for this slice" cannot be confused with "the
/// request timed out and this happens to be an empty partial result".
/// </param>
public sealed record HistoricalBarsResponse(IReadOnlyList<HistoricalBar> Bars, bool HasData);

/// <summary>Request body for <c>POST /ibkr/history/head-timestamp</c>.</summary>
public sealed record HeadTimestampQuery(HistoricalContractSpec Contract, string WhatToShow, bool UseRth = true);

/// <summary>Response body for <c>POST /ibkr/history/head-timestamp</c>.</summary>
public sealed record HeadTimestampResponse(DateTimeOffset HeadTimestamp);

/// <summary>
/// A pending <c>reqHeadTimeStamp</c> request. One value, one callback — no accumulate/complete
/// split like <see cref="ListRequest{T}"/>, because TWS answers with exactly one
/// <c>headTimestamp</c> message per request rather than a stream terminated by an ...End callback.
/// </summary>
internal sealed class HeadTimestampSink : IPendingRequest
{
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Task => _completion.Task;

    public void Apply(string headTimestamp) => _completion.TrySetResult(headTimestamp);

    public void Fail(Exception error) => _completion.TrySetException(error);
}

/// <summary>
/// Parses a TWS bar/head-timestamp <c>Time</c> string produced under <c>formatDate=2</c> (epoch
/// seconds for intraday values). Daily bars are the one exception: TWS returns a bare
/// <c>yyyyMMdd</c> date for them even under formatDate=2, so a caller cannot assume every value is
/// an intraday instant — treating a daily bar's date as an epoch-seconds instant (or vice versa)
/// silently produces a bar with the wrong time.
/// </summary>
internal static class HistoricalBarTime
{
    private const string DateFormat = "yyyyMMdd";

    /// <returns>
    /// True if <paramref name="raw"/> was recognised. Exactly one of <paramref name="timestamp"/>
    /// (an intraday instant) or <paramref name="tradingDate"/> (a daily bar's session date) is set
    /// on success, never both — that is what stops a daily bar's date from being read as an
    /// intraday instant downstream.
    /// </returns>
    public static bool TryParse(string? raw, out DateTimeOffset? timestamp, out DateOnly? tradingDate)
    {
        timestamp = null;
        tradingDate = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var value = raw.Trim();

        // Daily (and coarser) bars come back as a bare 8-digit yyyyMMdd date even under
        // formatDate=2. An 8-digit epoch-seconds value falls in 1970-04-26..1973-03-03 — long
        // before any date this platform trades — so length alone disambiguates safely without
        // needing to know the requested bar size at parse time.
        if (value.Length == 8 &&
            DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            tradingDate = date;
            return true;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Converts a TWS historical bar's OHLC <c>double</c> fields to <c>decimal</c>, reusing
/// <see cref="QuoteRequest.TryConvertSigned"/>'s sentinel guard rather than writing a new one.
/// </summary>
/// <remarks>
/// OHLC can, in principle, be legitimately negative — a handful of commodity futures (crude oil,
/// April 2020) have printed negative prices — so this uses the sign-preserving guard rather than
/// <see cref="QuoteRequest.TryConvertPrice"/>, which is correct for a bid/ask quote but would
/// silently discard a real negative print as if it were TWS's "no quote" sentinel.
/// </remarks>
internal static class HistoricalBarPrice
{
    public static decimal Convert(double value) => QuoteRequest.TryConvertSigned(value, out var result) ? result : 0m;
}
