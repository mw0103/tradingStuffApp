using Microsoft.Extensions.Time.Testing;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Timing;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class TimingStepTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"c1-timing-{Guid.NewGuid():N}");

    private readonly StringWriter _output = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private StudyContext Context() => new(
        new StudyPaths(_dataDirectory),
        new SessionClock(),
        _output,
        new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 14, 0, 0, TimeSpan.Zero)));

    private static EventRow Event(
        string id, DateTime acceptanceEt, bool inWindow = true, bool keptAfterDedup = true) =>
        new(id, 320193, "AAPL", $"acc-{id}", "8-K", "2.02,9.01",
            DateOnly.FromDateTime(acceptanceEt), DateOnly.FromDateTime(acceptanceEt),
            acceptanceEt, inWindow, keptAfterDedup, null);

    [Fact]
    public async Task Every_carried_event_gets_a_row_and_the_gate_reconciles()
    {
        var context = Context();
        Directory.CreateDirectory(_dataDirectory);

        CsvFile.Write(context.Paths.Events,
        [
            Event("bmo", new DateTime(2024, 2, 1, 8, 0, 0)),
            Event("amc", new DateTime(2024, 2, 1, 16, 5, 0)),
            Event("bell", new DateTime(2024, 2, 1, 16, 0, 0)),
            Event("intraday", new DateTime(2024, 2, 1, 12, 0, 0)),
            Event("good-friday", new DateTime(2024, 3, 29, 8, 0, 0)),
            Event("never-existed", new DateTime(2024, 3, 10, 2, 30, 0)),
            Event("out-of-window", new DateTime(2024, 2, 1, 8, 0, 0), inWindow: false),
            Event("deduped-away", new DateTime(2024, 2, 1, 8, 0, 0), keptAfterDedup: false)
        ]);

        var exitCode = await new TimingStep().RunAsync(context, [], CancellationToken.None);

        Assert.Equal(0, exitCode);

        var rows = CsvFile.Read<EventTimingRow>(context.Paths.EventTiming);

        // One row per carried event, and only the carried ones: the rows removed at gates 04-06 are
        // already recorded in events.csv and counted there.
        Assert.Equal(6, rows.Count);
        Assert.DoesNotContain(rows, row => row.EventId is "out-of-window" or "deduped-away");

        Assert.Equal(TimingResolver.Bmo, rows.Single(row => row.EventId == "bmo").TimingClass);
        Assert.Equal(TimingResolver.Amc, rows.Single(row => row.EventId == "amc").TimingClass);
        Assert.Equal(TimingResolver.Amc, rows.Single(row => row.EventId == "bell").TimingClass);
        Assert.Equal(TimingResolver.Intraday, rows.Single(row => row.EventId == "intraday").TimingClass);
        Assert.Equal(TimingResolver.Bmo, rows.Single(row => row.EventId == "good-friday").TimingClass);
        Assert.Equal(TimingResolver.Unresolved, rows.Single(row => row.EventId == "never-existed").TimingClass);

        var gate = Assert.Single(CsvFile.Read<GateCountRow>(context.Paths.GateCounts));

        Assert.Equal("timing", gate.Step);
        Assert.Equal(Gates.TimingClassified, gate.Gate);
        Assert.Equal(7, gate.Order);
        Assert.Equal(6, gate.Considered);
        Assert.Equal(2, gate.Removed);
        Assert.Equal(4, gate.Remaining);
        Assert.Equal(gate.Considered - gate.Removed, gate.Remaining);

        // The counts the memo cannot reconstruct from the table: which readouts, and how many
        // acceptances landed on a day the exchange never opened.
        Assert.Contains("bmo=2", gate.Note);
        Assert.Contains("amc=2", gate.Note);
        Assert.Contains("intraday=1", gate.Note);
        Assert.Contains("unresolved=1", gate.Note);
        Assert.Contains("acceptance_on_non_trading_day=2", gate.Note);
    }

    [Fact]
    public async Task A_missing_events_table_is_refused_rather_than_treated_as_no_events()
    {
        var context = Context();

        var exitCode = await new TimingStep().RunAsync(context, [], CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(context.Paths.EventTiming));
        Assert.False(File.Exists(context.Paths.GateCounts));
        Assert.Contains("does not exist", _output.ToString());
    }

    [Fact]
    public async Task An_empty_carried_set_still_writes_a_table_and_a_gate_row()
    {
        // Zero rows is a number, not a reason to write nothing: an absent table would render as
        // health downstream, an empty one with considered=0 cannot.
        var context = Context();
        Directory.CreateDirectory(_dataDirectory);

        CsvFile.Write(context.Paths.Events,
            [Event("out-of-window", new DateTime(2024, 2, 1, 8, 0, 0), inWindow: false)]);

        Assert.Equal(0, await new TimingStep().RunAsync(context, [], CancellationToken.None));

        Assert.Empty(CsvFile.Read<EventTimingRow>(context.Paths.EventTiming));

        var gate = Assert.Single(CsvFile.Read<GateCountRow>(context.Paths.GateCounts));

        Assert.Equal(0, gate.Considered);
        Assert.Equal(0, gate.Removed);
        Assert.Equal(0, gate.Remaining);
    }

    [Fact]
    public async Task The_written_table_round_trips_through_the_csv_mapper()
    {
        var context = Context();
        Directory.CreateDirectory(_dataDirectory);

        CsvFile.Write(context.Paths.Events, [Event("amc", new DateTime(2024, 11, 29, 13, 30, 0))]);

        await new TimingStep().RunAsync(context, [], CancellationToken.None);

        var row = Assert.Single(CsvFile.Read<EventTimingRow>(context.Paths.EventTiming));

        Assert.Equal(TimingResolver.Amc, row.TimingClass);
        Assert.Equal(new DateOnly(2024, 11, 29), row.EntryDate);
        Assert.Equal(new DateOnly(2024, 12, 2), row.ExitDate);
        Assert.Equal(new DateOnly(2024, 11, 27), row.PreEntryDate);
        Assert.Equal("2024-W48", row.EarningsWeek);

        Assert.StartsWith(
            "event_id,timing_class,print_date,pre_entry_date,entry_date,exit_date,earnings_week,quarantined,quarantine_reason",
            File.ReadAllText(context.Paths.EventTiming));
    }
}
