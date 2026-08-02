using System.Globalization;
using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// One row this platform is prepared to persist into <c>research.option_chain_quotes</c>.
/// </summary>
/// <remarks>
/// Every price and size field is <c>decimal</c>, converted from ThetaData's wire strings here and
/// nowhere upstream — this class IS the broker/vendor-adapter boundary CLAUDE.md's money convention
/// carves out (the same role <c>HistoricalBarAdapter</c> plays for IBKR bars): ThetaData's CSV is a
/// vendor wire format exactly like <c>IBApi</c>'s <c>double</c> fields, and nothing downstream of
/// this parser is allowed to see anything but <c>decimal</c>.
/// </remarks>
public sealed record OptionChainQuoteRow(
    string Underlying,
    string TradingClass,
    DateOnly Expiration,
    decimal Strike,
    char Right,
    DateTimeOffset ObservedAt,
    DateOnly TradingDate,
    decimal? Bid,
    decimal? Ask,
    decimal? BidSize,
    decimal? AskSize,
    short? BidExchange,
    short? AskExchange);

/// <summary>
/// Parses a raw ThetaData <c>/v3/option/history/quote</c> CSV response into landing rows.
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="ThetaDataChainLoader"/>. That loader's
/// <see cref="OptionQuote"/> shape keeps only strike/right/bid/ask — everything
/// <see cref="TradingStuff.Volatility.ImpliedVolatility.ModelFreeVariance"/> needs and nothing more —
/// and silently drops size and exchange columns the vendor actually returns. Canonical storage is
/// supposed to be the fuller record (scope item 3: "full NBBO both sides"), so this parser reads the
/// same raw <see cref="CsvTable"/> a second, independent way rather than lossily round-tripping
/// through the loader's narrower type.
/// <para>
/// <b>Timestamp interpretation is an unverified assumption, stated once so it cannot hide.</b>
/// ThetaData's <c>timestamp</c> column arrives with no UTC offset
/// (<c>2012-06-01T15:45:00.000</c>) — confirmed against the live Terminal 2026-08-02. It is
/// interpreted here as America/New_York local time, the OPRA/Cboe listing timezone for every root
/// this ingests (SPX, SPXW, VIX), and converted to UTC exactly once, at this boundary. Nothing in
/// this repository's own testing, nor ThetaData's documentation, was checked to confirm that
/// convention — it is the same convention <see cref="ThetaDataChainLoader"/> already assumes
/// implicitly by treating the value as already-local for <c>ExpirationSettlement</c> purposes, made
/// explicit and load-bearing here because a UTC-typed column is being written from it.
/// </para>
/// </remarks>
public static class OptionChainQuoteCsvParser
{
    private static readonly TimeZoneInfo ExchangeZone = ResolveExchangeZone();

    public const string Endpoint = "/v3/option/history/quote";

    public static IReadOnlyList<OptionChainQuoteRow> Parse(
        CsvTable table, string underlying, string tradingClass, DateOnly expiration)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        var timestampColumn = table.RequireColumn("timestamp");
        var strikeColumn = table.RequireColumn("strike");
        var rightColumn = table.RequireColumn("right");
        var bidColumn = table.RequireColumn("bid");
        var askColumn = table.RequireColumn("ask");
        var bidSizeColumn = table.HasColumn("bid_size") ? table.RequireColumn("bid_size") : -1;
        var askSizeColumn = table.HasColumn("ask_size") ? table.RequireColumn("ask_size") : -1;
        var bidExchangeColumn = table.HasColumn("bid_exchange") ? table.RequireColumn("bid_exchange") : -1;
        var askExchangeColumn = table.HasColumn("ask_exchange") ? table.RequireColumn("ask_exchange") : -1;

        var rows = new List<OptionChainQuoteRow>(table.Count);

        foreach (var row in table.Rows)
        {
            var strike = ParseDecimal(CsvTable.GetString(row, strikeColumn));
            if (strike <= 0m)
            {
                // Same guard ThetaDataChainLoader applies: a zero/negative strike is a malformed
                // row, never a real contract.
                continue;
            }

            var right = ParseRight(CsvTable.GetString(row, rightColumn));
            var localTimestamp = ParseLocalTimestamp(CsvTable.GetString(row, timestampColumn));
            var observedAtUtc = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified), ExchangeZone),
                TimeSpan.Zero);

            rows.Add(new OptionChainQuoteRow(
                underlying,
                tradingClass,
                expiration,
                strike,
                right,
                observedAtUtc,
                DateOnly.FromDateTime(localTimestamp),
                ParseNullableDecimal(row, bidColumn),
                ParseNullableDecimal(row, askColumn),
                bidSizeColumn >= 0 ? ParseNullableDecimal(row, bidSizeColumn) : null,
                askSizeColumn >= 0 ? ParseNullableDecimal(row, askSizeColumn) : null,
                bidExchangeColumn >= 0 ? ParseNullableShort(row, bidExchangeColumn) : null,
                askExchangeColumn >= 0 ? ParseNullableShort(row, askExchangeColumn) : null));
        }

        return rows;
    }

    private static char ParseRight(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("The 'right' column was empty.");
        }

        var first = char.ToUpperInvariant(value[0]);
        if (first is 'C' or 'P')
        {
            return first;
        }

        throw new InvalidOperationException($"Unrecognized option right '{value}'.");
    }

    private static DateTime ParseLocalTimestamp(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Could not parse '{value}' as a ThetaData quote timestamp.");
    }

    private static decimal ParseDecimal(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Could not parse '{value}' as a number.");
    }

    private static decimal? ParseNullableDecimal(string[] row, int column)
    {
        var text = CsvTable.GetString(row, column);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // ThetaData reports -1 or 0 for "no quote on this side" in some conditions; a genuinely
        // absent NBBO side is stored as NULL rather than a negative price, matching research.bars'
        // treatment of TWS's -1 volume sentinel (migration 004).
        var value = ParseDecimal(text);
        return value < 0m ? null : value;
    }

    private static short? ParseNullableShort(string[] row, int column)
    {
        var text = CsvTable.GetString(row, column);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static TimeZoneInfo ResolveExchangeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows spells it differently; this repo targets Linux containers, so this is a
            // last-resort fallback rather than the expected path.
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
