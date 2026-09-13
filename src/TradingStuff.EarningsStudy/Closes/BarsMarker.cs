using System.Globalization;

namespace TradingStuff.EarningsStudy.Closes;

/// <summary>
/// The <c>raw/bars/{symbol}.done</c> completion marker's text format, in one place so the writer
/// (<see cref="BarsFetcher"/>) and the reader (<see cref="ClosesJoiner"/>) cannot drift apart on what
/// the first line means.
/// </summary>
/// <remarks>
/// The marker's mere existence — not the bars CSV's — is the resume signal: a bars file can be left
/// behind mid-write by a killed process, but the marker is written only after that file has been
/// moved into place atomically, and only once. See <see cref="BarsFetcher"/> for the write order.
/// </remarks>
internal static class BarsMarker
{
    private const string OkStatus = "ok";
    private const string EmptyStatus = "empty";
    private const string RejectedPrefix = "rejected: ";

    public static string BuildOk(int barCount, DateOnly? lastTradingDate, string ibkrSymbol, string? exchange) =>
        string.Join(
            Environment.NewLine,
            OkStatus,
            $"bar_count={barCount.ToString(CultureInfo.InvariantCulture)}",
            $"last_trading_date={(lastTradingDate is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")}",
            $"symbol={ibkrSymbol}",
            $"exchange={exchange}",
            "");

    public static string BuildEmpty(string ibkrSymbol, string? exchange) =>
        string.Join(Environment.NewLine, EmptyStatus, $"symbol={ibkrSymbol}", $"exchange={exchange}", "");

    public static string BuildRejected(string detail, string ibkrSymbol, string? exchange) =>
        string.Join(
            Environment.NewLine, $"{RejectedPrefix}{detail}", $"symbol={ibkrSymbol}", $"exchange={exchange}", "");

    /// <summary>The marker's status word: "ok", "empty", or "rejected" — never the detail text.</summary>
    public static string ReadStatus(string markerContent)
    {
        var firstLine = FirstLine(markerContent);
        return firstLine.StartsWith(RejectedPrefix, StringComparison.Ordinal) ? "rejected" : firstLine;
    }

    public static bool IsOk(string markerContent) => FirstLine(markerContent) == OkStatus;

    private static string FirstLine(string content)
    {
        var newlineIndex = content.IndexOf('\n');
        var line = newlineIndex < 0 ? content : content[..newlineIndex];
        return line.TrimEnd('\r').Trim();
    }
}
