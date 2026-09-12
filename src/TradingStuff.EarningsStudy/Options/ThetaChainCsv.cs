using System.Globalization;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.EarningsStudy.Options;

/// <summary>One chain response's quote rows, grouped by the date the Terminal reported them on, plus
/// whatever <c>underlying_price</c> the response carried for that date.</summary>
internal sealed record ParsedChainResponse(
    IReadOnlyDictionary<DateOnly, List<ChainQuote>> QuotesByDate,
    IReadOnlyDictionary<DateOnly, decimal> UnderlyingByDate,
    string HeaderLine);

/// <summary>
/// Tolerant CSV parsing for the two Theta shapes this verb reads: the expirations list, and a
/// whole-expiration chain response whose exact column set has not been observed by this repository
/// (see <see cref="ThetaChainFetcher"/>'s remarks). Every schema assumption is written down here,
/// once, so a real Terminal response that violates one fails loudly instead of silently misreading
/// a column.
/// </summary>
internal static class ThetaChainCsv
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The column is named <c>expiration</c> or <c>date</c>; values are <c>yyyy-MM-dd</c> or <c>yyyyMMdd</c>.</summary>
    public static List<DateOnly> ParseExpirations(CsvTable table)
    {
        var column = table.RequireColumn("expiration", "date");
        var expirations = new List<DateOnly>(table.Count);
        foreach (var row in table.Rows)
        {
            expirations.Add(ParseDateFlexible(CsvTable.GetString(row, column), "expiration"));
        }
        return expirations;
    }

    /// <summary>
    /// Parses a whole-expiration chain response into quotes grouped by date. Required: strike,
    /// right, bid, ask, and a per-row date (named <c>date</c>, <c>timestamp</c> or <c>created</c>; a
    /// bare date or a full ISO timestamp are both accepted — the bulk quote endpoint's
    /// <c>timestamp</c> carries a time of day, the EOD report's date column may not).
    /// <c>underlying_price</c> is read when present and is optional everywhere else.
    /// </summary>
    public static ParsedChainResponse ParseChainRows(CsvTable table, decimal strikeDivisor)
    {
        var strikeColumn = table.RequireColumn("strike");
        var rightColumn = table.RequireColumn("right");
        var bidColumn = table.RequireColumn("bid");
        var askColumn = table.RequireColumn("ask");
        var dateColumn = table.RequireColumn("date", "timestamp", "created");
        var hasUnderlying = table.HasColumn("underlying_price");
        var underlyingColumn = hasUnderlying ? table.RequireColumn("underlying_price") : -1;

        var quotesByDate = new Dictionary<DateOnly, List<ChainQuote>>();
        var underlyingByDate = new Dictionary<DateOnly, decimal>();

        foreach (var row in table.Rows)
        {
            var date = ParseDateFlexible(CsvTable.GetString(row, dateColumn), "date/timestamp/created");
            var strike = ParseDecimal(CsvTable.GetString(row, strikeColumn), "strike") / strikeDivisor;
            var isCall = ParseRight(CsvTable.GetString(row, rightColumn));
            var bid = ParseDecimal(CsvTable.GetString(row, bidColumn), "bid");
            var ask = ParseDecimal(CsvTable.GetString(row, askColumn), "ask");

            if (!quotesByDate.TryGetValue(date, out var quotes))
            {
                quotesByDate[date] = quotes = [];
            }
            quotes.Add(new ChainQuote(strike, isCall, bid, ask));

            if (hasUnderlying && !underlyingByDate.ContainsKey(date))
            {
                var text = CsvTable.GetString(row, underlyingColumn);
                if (text.Length > 0)
                {
                    underlyingByDate[date] = ParseDecimal(text, "underlying_price");
                }
            }
        }

        return new ParsedChainResponse(quotesByDate, underlyingByDate, string.Join(",", table.ColumnNames));
    }

    /// <summary>Accepts <c>yyyy-MM-dd</c>, a full ISO timestamp (date portion only), or compact <c>yyyyMMdd</c>.</summary>
    public static DateOnly ParseDateFlexible(string text, string columnName)
    {
        var raw = text.Trim();
        var datePart = raw.Length > 10 && (raw[10] == 'T' || raw[10] == ' ') ? raw[..10] : raw;

        if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var iso))
        {
            return iso;
        }
        if (datePart.Length == 8 && DateOnly.TryParseExact(datePart, "yyyyMMdd", Invariant, DateTimeStyles.None, out var compact))
        {
            return compact;
        }

        throw new InvalidOperationException(
            $"Could not parse '{text}' in column '{columnName}' as a date (expected yyyy-MM-dd, yyyyMMdd, or an ISO timestamp).");
    }

    /// <summary>"C"/"CALL" or "P"/"PUT", case-insensitive. Quotes are already stripped by <see cref="CsvTable.GetString"/>.</summary>
    public static bool ParseRight(string text) =>
        text.Trim().ToUpperInvariant() switch
        {
            "C" or "CALL" => true,
            "P" or "PUT" => false,
            _ => throw new InvalidOperationException($"'{text}' in column 'right' is neither a call nor a put."),
        };

    /// <summary>Decimal, parsed from feed text with invariant culture. Never <c>CsvTable.GetDouble</c> — CLAUDE.md.</summary>
    public static decimal ParseDecimal(string text, string columnName) =>
        decimal.TryParse(text, NumberStyles.Number, Invariant, out var value)
            ? value
            : throw new InvalidOperationException($"Could not parse '{text}' in column '{columnName}' as a decimal.");

    /// <summary>
    /// Reconstructs the response as CSV text for the on-disk cache: the column names in their
    /// original order, plus every row's original cell text. Not byte-identical to the HTTP body —
    /// <see cref="ThetaDataClient.GetAsync"/> only hands back the parsed table, not the raw bytes —
    /// but it round-trips through <see cref="CsvTable.Parse"/> and this module identically, which is
    /// the property the cache actually needs.
    /// </summary>
    public static string ToRawCsv(CsvTable table)
    {
        var writer = new StringWriter();
        writer.WriteLine(string.Join(",", table.ColumnNames));
        foreach (var row in table.Rows)
        {
            writer.WriteLine(string.Join(",", row));
        }
        return writer.ToString();
    }

    /// <summary>
    /// Writes via a temp file plus rename, so a killed run never leaves a partially-written cache
    /// file that a later run would trust as complete (docs/LESSONS.md 2's stale-restore lesson,
    /// applied to writes).
    /// </summary>
    public static void WriteCacheAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
