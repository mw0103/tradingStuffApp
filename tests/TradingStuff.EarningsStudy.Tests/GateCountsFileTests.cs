using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class GateCountsFileTests
{
    [Fact]
    public void A_rerun_of_one_verb_replaces_only_that_verbs_tallies()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c1-gates-{Guid.NewGuid():N}.csv");
        try
        {
            CsvFile.Append(path,
            [
                new GateCountRow("universe", 1, Gates.SeedOptionable, 10, 0, 10, null),
                new GateCountRow("events", 4, Gates.HasItem202, 10, 2, 8, "first run"),
                new GateCountRow("events", 5, Gates.NotAmendment, 8, 1, 7, "first run")
            ]);

            var dropped = GateCountsFile.DropStep(path, "events");
            CsvFile.Append(path, [new GateCountRow("events", 4, Gates.HasItem202, 10, 3, 7, "second run")]);

            var rows = CsvFile.Read<GateCountRow>(path);
            Assert.Equal(2, dropped);
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, r => r.Step == "universe");
            Assert.Single(rows, r => r.Step == "events" && r.Note == "second run");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_or_empty_file_is_not_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c1-gates-{Guid.NewGuid():N}.csv");
        Assert.Equal(0, GateCountsFile.DropStep(path, "events"));
        File.WriteAllText(path, "");
        try
        {
            Assert.Equal(0, GateCountsFile.DropStep(path, "events"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
