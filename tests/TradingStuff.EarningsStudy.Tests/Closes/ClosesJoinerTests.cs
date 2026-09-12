using TradingStuff.EarningsStudy.Closes;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests.Closes;

public sealed class ClosesJoinerTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"c1-closes-join-{Guid.NewGuid():N}");

    private StudyPaths Paths => new(_dataDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private void SeedOk(string symbol, IReadOnlyList<BarRow> bars)
    {
        Directory.CreateDirectory(Paths.RawBarsDirectory);
        CsvFile.Write(Path.Combine(Paths.RawBarsDirectory, $"{symbol}.csv"), bars);
        File.WriteAllText(
            Path.Combine(Paths.RawBarsDirectory, $"{symbol}.done"),
            BarsMarker.BuildOk(bars.Count, bars.Count > 0 ? bars[^1].TradingDate : null, symbol, "NYSE"));
    }

    private void SeedEmpty(string symbol)
    {
        Directory.CreateDirectory(Paths.RawBarsDirectory);
        File.WriteAllText(Path.Combine(Paths.RawBarsDirectory, $"{symbol}.done"), BarsMarker.BuildEmpty(symbol, "NYSE"));
    }

    private void SeedRejected(string symbol)
    {
        Directory.CreateDirectory(Paths.RawBarsDirectory);
        File.WriteAllText(Path.Combine(Paths.RawBarsDirectory, $"{symbol}.done"), BarsMarker.BuildRejected("no security definition", symbol, "NYSE"));
    }

    /// <summary>
    /// N consecutive bars, one calendar day apart by default, whose close exactly doubles every
    /// day. Every daily |return| is then exactly 1 (100%), so the 20-day median is trivially and
    /// exactly 1m regardless of how many bars precede the window — a value hand-verifiable without
    /// re-deriving the implementation's own arithmetic.
    /// </summary>
    private static List<BarRow> DoublingSeries(DateOnly firstDate, int count, Func<int, DateOnly>? dateAt = null)
    {
        dateAt ??= i => firstDate.AddDays(i);
        var close = 100m;
        var bars = new List<BarRow>(count);
        for (var i = 0; i < count; i++)
        {
            bars.Add(new BarRow(dateAt(i), close, close, close, close, 1000m));
            close *= 2m;
        }
        return bars;
    }

    private static EventTimingRow Timing(
        string eventId, DateOnly print, DateOnly? pre, DateOnly? entry, DateOnly? exit, bool quarantined = false) =>
        new(eventId, quarantined ? "intraday" : "bmo", print, pre, entry, exit, "2024-W05", quarantined, quarantined ? "unresolvable acceptance" : null);

    // ---- exact-date join, including a missing exit -------------------------------------------------

    [Fact]
    public void Join_matches_pre_entry_entry_and_exit_by_exact_date()
    {
        var bars = DoublingSeries(new DateOnly(2024, 2, 1), 25);
        SeedOk("AAPL", bars);
        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 4), new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 6)) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        var row = Assert.Single(rows);
        Assert.Equal("ok", row.Status);
        Assert.Equal(bars[3].Close, row.ClosePreEntry); // 2024-02-04 is bars[3] (day 0 = 02-01)
        Assert.Equal(bars[4].Close, row.CloseEntry);
        Assert.Equal(bars[5].Close, row.CloseExit);
    }

    [Fact]
    public void A_missing_exit_date_reports_missing_exit_but_keeps_pre_entry_and_entry()
    {
        var bars = DoublingSeries(new DateOnly(2024, 2, 1), 10); // does not reach the exit date below
        SeedOk("AAPL", bars);
        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 4), new DateOnly(2024, 2, 5), new DateOnly(2024, 3, 1)) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        var row = Assert.Single(rows);
        Assert.Equal("missing_exit", row.Status);
        Assert.NotNull(row.ClosePreEntry);
        Assert.NotNull(row.CloseEntry);
        Assert.Null(row.CloseExit);
    }

    // ---- no_bars: absence of a usable series, whatever the reason ----------------------------------

    [Theory]
    [InlineData("never fetched")]
    [InlineData("confirmed empty")]
    [InlineData("rejected")]
    public void No_series_available_reports_no_bars_regardless_of_why(string reason)
    {
        switch (reason)
        {
            case "confirmed empty": SeedEmpty("ZZZZ"); break;
            case "rejected": SeedRejected("ZZZZ"); break;
            // "never fetched": no marker, no csv — the ordinary case for a symbol the fetch phase never reached.
        }

        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 4), new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 6)) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "ZZZZ")));

        var row = Assert.Single(rows);
        Assert.Equal("no_bars", row.Status);
        Assert.Null(row.ClosePreEntry);
        Assert.Null(row.CloseEntry);
        Assert.Null(row.CloseExit);
        Assert.Null(row.MedianAbsReturn20);
    }

    [Fact]
    public void A_bars_file_with_no_marker_is_not_trusted_by_the_join_either()
    {
        // Mirrors BarsFetcherTests' resume negative control: the join must key off the marker, not
        // the CSV's mere presence, or a partial file from a killed fetch would silently join as data.
        Directory.CreateDirectory(Paths.RawBarsDirectory);
        CsvFile.Write(Path.Combine(Paths.RawBarsDirectory, "ZZZZ.csv"), DoublingSeries(new DateOnly(2024, 2, 1), 25));
        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 4), new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 6)) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "ZZZZ")));

        Assert.Equal("no_bars", Assert.Single(rows).Status);
    }

    // ---- status precedence -------------------------------------------------------------------------

    [Fact]
    public void Missing_pre_entry_takes_precedence_over_missing_entry_and_exit()
    {
        var bars = DoublingSeries(new DateOnly(2024, 2, 1), 5);
        SeedOk("AAPL", bars);
        // None of the three dates are in the series.
        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 2), new DateOnly(2099, 1, 3)) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        Assert.Equal("missing_pre_entry", Assert.Single(rows).Status);
    }

    [Fact]
    public void Missing_entry_takes_precedence_over_missing_exit_when_pre_entry_resolves()
    {
        var bars = DoublingSeries(new DateOnly(2024, 2, 1), 5);
        SeedOk("AAPL", bars);
        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 1), new DateOnly(2099, 1, 2), new DateOnly(2099, 1, 3)) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        Assert.Equal("missing_entry", Assert.Single(rows).Status);
    }

    [Fact]
    public void Quarantined_row_with_null_dates_still_emits_a_row_describing_why()
    {
        SeedOk("AAPL", DoublingSeries(new DateOnly(2024, 2, 1), 30));
        var timing = new[] { Timing("e1", new DateOnly(2024, 2, 5), null, null, null, quarantined: true) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        var row = Assert.Single(rows);
        Assert.Equal("missing_pre_entry", row.Status); // a null date can never resolve to a close.
        Assert.Equal("ibkr", row.Source);
    }

    // ---- 20-day trailing median --------------------------------------------------------------------

    [Fact]
    public void Exactly_21_closes_ending_at_pre_entry_yields_the_median()
    {
        var bars = DoublingSeries(new DateOnly(2024, 1, 1), 21); // indices 0..20; pre-entry at index 20.
        SeedOk("AAPL", bars);
        var preEntry = bars[^1].TradingDate;
        var timing = new[] { Timing("e1", preEntry.AddDays(1), preEntry, preEntry, preEntry) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        Assert.Equal(1m, Assert.Single(rows).MedianAbsReturn20); // every daily return is exactly 100%.
    }

    [Fact]
    public void Only_20_closes_ending_at_pre_entry_is_null_with_a_note()
    {
        var bars = DoublingSeries(new DateOnly(2024, 1, 1), 20); // one short of the 21 the median needs.
        SeedOk("AAPL", bars);
        var preEntry = bars[^1].TradingDate;
        var timing = new[] { Timing("e1", preEntry.AddDays(1), preEntry, preEntry, preEntry) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        var row = Assert.Single(rows);
        Assert.Null(row.MedianAbsReturn20);
        Assert.Contains("insufficient", row.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ok", row.Status); // pre/entry/exit all resolve; only the median is short of history.
    }

    [Fact]
    public void A_holiday_gap_in_calendar_dates_does_not_disturb_the_positional_20_day_window()
    {
        // 21 bars, but the run has one artificial multi-day jump in the middle (a holiday) — the
        // rows are still 21 CONSECUTIVE ROWS, which is all the median window looks at.
        var bars = DoublingSeries(new DateOnly(2024, 1, 1), 21, i => i < 10
            ? new DateOnly(2024, 1, 1).AddDays(i)
            : new DateOnly(2024, 1, 1).AddDays(i + 3)); // a 4-calendar-day gap between rows 9 and 10.
        SeedOk("AAPL", bars);
        var preEntry = bars[^1].TradingDate;
        var timing = new[] { Timing("e1", preEntry.AddDays(1), preEntry, preEntry, preEntry) };

        var rows = ClosesJoiner.Join(Paths, timing, Map(("e1", "AAPL")));

        Assert.Equal(1m, Assert.Single(rows).MedianAbsReturn20);
    }

    // ---- pipeline integrity ------------------------------------------------------------------------

    [Fact]
    public void An_event_id_absent_from_events_csv_is_a_hard_failure_not_a_silent_no_bars()
    {
        var timing = new[] { Timing("orphan", new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 4), new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 6)) };

        var ex = Assert.Throws<InvalidDataException>(() => ClosesJoiner.Join(Paths, timing, Map()));

        Assert.Contains("orphan", ex.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Map(params (string EventId, string Symbol)[] pairs) =>
        pairs.ToDictionary(p => p.EventId, p => p.Symbol, StringComparer.Ordinal);
}
