using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Closes;

/// <summary>
/// Joins each <see cref="EventTimingRow"/> to its symbol's daily-close series on disk, by exact
/// trading date, and computes the trailing 20-day median absolute daily return ending at pre-entry.
/// </summary>
/// <remarks>
/// Every timing row gets exactly one output row (LESSONS.md #3 — absence renders as health, so the
/// join must never skip a row for lack of data; it must say why in <c>Status</c> instead). Dates on
/// the series are already trading dates (IBKR only returns session bars), so a row's neighbours in
/// the file ARE its calendar neighbours — no calendar arithmetic is needed here, holiday gaps
/// included.
/// </remarks>
internal static class ClosesJoiner
{
    /// <summary>Trading days ending at (and including) pre-entry that the median needs: 21 closes, 20 returns.</summary>
    private const int MedianWindowCloses = 21;

    public static IReadOnlyList<ClosesRow> Join(
        StudyPaths paths, IReadOnlyList<EventTimingRow> timing, IReadOnlyDictionary<string, string> symbolByEventId)
    {
        var seriesCache = new Dictionary<string, SymbolSeries?>(StringComparer.Ordinal);

        SymbolSeries? SeriesFor(string symbol)
        {
            if (seriesCache.TryGetValue(symbol, out var cached))
            {
                return cached;
            }

            var loaded = LoadSeries(paths, symbol);
            seriesCache[symbol] = loaded;
            return loaded;
        }

        var rows = new List<ClosesRow>(timing.Count);
        foreach (var t in timing)
        {
            if (!symbolByEventId.TryGetValue(t.EventId, out var symbol))
            {
                // A timing row whose event is not in events.csv is a pipeline integrity failure, not
                // a data-availability gap — the two must not be conflated by defaulting this to
                // "no_bars" and moving on (LESSONS.md #3 again: that would render a real break as a
                // normal, countable outcome).
                throw new InvalidDataException(
                    $"closes: event_timing.csv references event '{t.EventId}', which is not in events.csv.");
            }

            rows.Add(BuildRow(t, symbol, SeriesFor(symbol)));
        }

        return rows;
    }

    private static ClosesRow BuildRow(EventTimingRow t, string symbol, SymbolSeries? series)
    {
        if (series is null || series.Bars.Count == 0)
        {
            return new ClosesRow(t.EventId, symbol, null, null, null, null, "no_bars", "ibkr", "no bars series available for this symbol");
        }

        var closePre = CloseAt(series, t.PreEntryDate);
        var closeEntry = CloseAt(series, t.EntryDate);
        var closeExit = CloseAt(series, t.ExitDate);

        var status =
            closePre is null ? "missing_pre_entry" :
            closeEntry is null ? "missing_entry" :
            closeExit is null ? "missing_exit" :
            "ok";

        var (median, medianNote) = MedianAbsReturn20(series, t.PreEntryDate);

        return new ClosesRow(t.EventId, symbol, closePre, closeEntry, closeExit, median, status, "ibkr", medianNote);
    }

    private static decimal? CloseAt(SymbolSeries series, DateOnly? date) =>
        date is { } d && series.IndexByDate.TryGetValue(d, out var idx) ? series.Bars[idx].Close : null;

    private static (decimal? Median, string? Note) MedianAbsReturn20(SymbolSeries series, DateOnly? preEntryDate)
    {
        if (preEntryDate is not { } date || !series.IndexByDate.TryGetValue(date, out var idx))
        {
            // Pre-entry itself is unresolved or absent from the series; Status already says so via
            // missing_pre_entry (or no_bars) — no separate note needed for the median.
            return (null, null);
        }

        if (idx < MedianWindowCloses - 1)
        {
            return (null,
                $"insufficient trailing history for the 20-day median (need {MedianWindowCloses} consecutive " +
                $"closes ending at pre-entry, have {idx + 1}).");
        }

        var returns = new List<decimal>(MedianWindowCloses - 1);
        for (var i = idx - MedianWindowCloses + 2; i <= idx; i++)
        {
            var previous = series.Bars[i - 1].Close;
            var current = series.Bars[i].Close;
            returns.Add(Math.Abs(current / previous - 1m));
        }

        return (Median(returns), null);
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2m;
    }

    private static SymbolSeries? LoadSeries(StudyPaths paths, string symbol)
    {
        var markerPath = Path.Combine(paths.RawBarsDirectory, $"{symbol}.done");
        if (!File.Exists(markerPath))
        {
            // No marker: either the fetch phase never ran for this symbol, or it left this symbol
            // unmarked after exhausting retries. Either way, trusting a same-named .csv left on disk
            // would be exactly the defect LESSONS.md #2 exists to catch — treat it as no series.
            return null;
        }

        if (!BarsMarker.IsOk(File.ReadAllText(markerPath)))
        {
            return null; // "empty" or "rejected": confirmed, no series to join.
        }

        var csvPath = Path.Combine(paths.RawBarsDirectory, $"{symbol}.csv");
        if (!File.Exists(csvPath))
        {
            return null; // Defensive: an "ok" marker with no bars file would itself be a defect.
        }

        var bars = CsvFile.Read<BarRow>(csvPath).OrderBy(b => b.TradingDate).ToList();
        var indexByDate = new Dictionary<DateOnly, int>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            indexByDate[bars[i].TradingDate] = i;
        }

        return new SymbolSeries(bars, indexByDate);
    }

    private sealed record SymbolSeries(IReadOnlyList<BarRow> Bars, IReadOnlyDictionary<DateOnly, int> IndexByDate);
}
