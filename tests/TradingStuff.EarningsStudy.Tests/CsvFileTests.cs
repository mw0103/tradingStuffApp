using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class CsvFileTests
{
    [Fact]
    public void Round_trips_every_record_type_including_nulls_and_quoted_text()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c1-csv-{Guid.NewGuid():N}.csv");
        try
        {
            var rows = new List<UniverseRow>
            {
                new("FLWS", "1 800 FLOWERS COM INC CL A", 1084869, "FLWS", "1-800-FLOWERS.COM, INC.", "Nasdaq", true, null, null),
                new("BRK.B", "BERKSHIRE HATHAWAY INC CL B", null, null, null, null, false, Gates.CikMapped, "name has \"quotes\", commas,\nand a newline")
            };

            CsvFile.Write(path, rows);
            var back = CsvFile.Read<UniverseRow>(path);

            Assert.Equal(rows, back);
            Assert.StartsWith("symbol,cboe_name,cik,sec_ticker,sec_name,exchange,eligible,exclusion_gate,note", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dates_times_and_decimals_are_invariant_and_exact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c1-csv-{Guid.NewGuid():N}.csv");
        try
        {
            var row = new EventRow("320193:0000320193-24-000006", 320193, "AAPL", "0000320193-24-000006", "8-K", "2.02,9.01",
                new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 1), new DateTime(2024, 2, 1, 16, 30, 38), true, true, null);
            var measure = new OptionMeasuresRow("e", "AAPL", new DateOnly(2024, 2, 2), 1, "eod", "16:00:00",
                185.1234567m, null, 185m, 2.05m, 2.10m, 1.95m, 2.00m, 4.05m, 0.10m, null, null, 0.5m, 190.25m, "ok", null);

            CsvFile.Write(path, [row]);
            Assert.Equal(row, CsvFile.Read<EventRow>(path).Single());
            Assert.Contains("2024-02-01T16:30:38", File.ReadAllText(path));

            CsvFile.Write(path, [measure]);
            var back = CsvFile.Read<OptionMeasuresRow>(path).Single();
            Assert.Equal(measure, back);
            Assert.Equal(185.1234567m, back.SpotParityEntry);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_writes_the_header_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c1-csv-{Guid.NewGuid():N}.csv");
        try
        {
            CsvFile.Append(path, [new GateCountRow("universe", 1, Gates.SeedOptionable, 5333, 0, 5333, null)]);
            CsvFile.Append(path, [new GateCountRow("universe", 2, Gates.CikMapped, 5333, 900, 4433, null)]);

            var back = CsvFile.Read<GateCountRow>(path);
            Assert.Equal(2, back.Count);
            Assert.Equal(1, File.ReadAllLines(path).Count(l => l.StartsWith("step,")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_required_column_fails_loudly_rather_than_defaulting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"c1-csv-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "event_id,symbol\ne1,AAPL\n");
            var ex = Assert.Throws<InvalidDataException>(() => CsvFile.Read<ClosesRow>(path));
            Assert.Contains("close_pre_entry", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Column_names_are_snake_case()
    {
        Assert.Equal("close_pre_entry", CsvFile.ColumnName("ClosePreEntry"));
        Assert.Equal("median_abs_return20", CsvFile.ColumnName("MedianAbsReturn20"));
        Assert.Equal("cik", CsvFile.ColumnName("Cik"));
    }
}
