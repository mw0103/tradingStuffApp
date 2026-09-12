using System.Text.Json;
using TradingStuff.EarningsStudy.Edgar;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class EdgarParsingTests
{
    [Theory]
    [InlineData("2.02,9.01", true)]
    [InlineData("9.01,2.02", true)]
    [InlineData(" 2.02 ", true)]
    [InlineData("9.01", false)]
    [InlineData("", false)]
    // A naive Contains("2.02") substring check would wrongly match either of these; the exact,
    // comma-split, trimmed token must not.
    [InlineData("9.01,12.02", false)]
    [InlineData("2.021", false)]
    public void HasItem202_matches_the_exact_comma_separated_token_only(string items, bool expected) =>
        Assert.Equal(expected, EdgarParsing.HasItem202(items));

    [Fact]
    public void ParseAcceptanceEt_reads_Eastern_wall_clock_not_UTC()
    {
        // Apple's real FY24 Q1 8-K: filed after the 2024-02-01 close (~16:30 ET). A UTC reading of
        // this exact string would misplace it at 16:30 UTC == 11:30 ET, mid-session, not after close.
        var parsed = EdgarParsing.ParseAcceptanceEt("2024-02-01T16:30:38.000Z");

        Assert.Equal(new DateTime(2024, 2, 1, 16, 30, 38, DateTimeKind.Unspecified), parsed);
        Assert.Equal(DateTimeKind.Unspecified, parsed.Kind);
    }

    [Fact]
    public void ParseAcceptanceEt_tolerates_no_fractional_seconds()
    {
        var parsed = EdgarParsing.ParseAcceptanceEt("2024-02-01T16:30:38Z");
        Assert.Equal(new DateTime(2024, 2, 1, 16, 30, 38, DateTimeKind.Unspecified), parsed);
    }

    [Theory]
    [InlineData("2022-01-01", "2025-12-31", "2022-01-01", "2025-12-31", true)] // identical ranges
    [InlineData("2012-06-01", "2015-12-31", "2022-01-01", "2025-12-31", false)] // entirely before
    [InlineData("2026-01-01", "2026-12-31", "2022-01-01", "2025-12-31", false)] // entirely after
    [InlineData("2021-06-01", "2022-03-31", "2022-01-01", "2025-12-31", true)] // straddles the start
    [InlineData("2025-06-01", "2026-06-01", "2022-01-01", "2025-12-31", true)] // straddles the end
    [InlineData("2015-01-01", "2022-01-01", "2022-01-01", "2025-12-31", true)] // touches at a single day
    public void Intersects_is_inclusive_on_both_ends(string rangeFrom, string rangeTo, string windowFrom, string windowTo, bool expected) =>
        Assert.Equal(expected, EdgarParsing.Intersects(DateOnly.Parse(rangeFrom), DateOnly.Parse(rangeTo), DateOnly.Parse(windowFrom), DateOnly.Parse(windowTo)));

    [Fact]
    public void ParseFilingsBlock_selects_only_8K_forms_with_item_202()
    {
        using var doc = JsonDocument.Parse("""
        {
          "accessionNumber": ["A1", "A2", "A3", "A4"],
          "filingDate": ["2023-01-01", "2023-01-02", "2023-01-03", "2023-01-04"],
          "reportDate": ["", "", "", ""],
          "acceptanceDateTime": ["2023-01-01T09:00:00.000Z", "2023-01-02T09:00:00.000Z", "2023-01-03T09:00:00.000Z", "2023-01-04T09:00:00.000Z"],
          "form": ["8-K", "8-K", "10-Q", "8-K/A"],
          "items": ["2.02", "9.01", "2.02", "2.02"]
        }
        """);

        var result = EdgarParsing.ParseFilingsBlock(doc.RootElement);

        var accessions = result.Select(r => r.AccessionNumber).ToList();
        Assert.Equal(["A1", "A4"], accessions); // A2 wrong item, A3 wrong form
        Assert.Equal("8-K/A", result.Single(r => r.AccessionNumber == "A4").Form);
    }

    [Fact]
    public void ParseFilingsBlock_reads_report_date_as_null_when_empty()
    {
        using var doc = JsonDocument.Parse("""
        {
          "accessionNumber": ["A1"],
          "filingDate": ["2023-01-01"],
          "reportDate": [""],
          "acceptanceDateTime": ["2023-01-01T09:00:00.000Z"],
          "form": ["8-K"],
          "items": ["2.02"]
        }
        """);

        var result = EdgarParsing.ParseFilingsBlock(doc.RootElement);

        Assert.Null(Assert.Single(result).ReportDate);
    }

    [Fact]
    public void ParseFilingsBlock_throws_rather_than_guessing_when_a_selected_row_has_no_acceptance_time()
    {
        using var doc = JsonDocument.Parse("""
        {
          "accessionNumber": ["A1"],
          "filingDate": ["2023-01-01"],
          "reportDate": [""],
          "acceptanceDateTime": [""],
          "form": ["8-K"],
          "items": ["2.02"]
        }
        """);

        Assert.Throws<InvalidDataException>(() => EdgarParsing.ParseFilingsBlock(doc.RootElement));
    }

    [Fact]
    public void ParseSubmissions_reads_recent_filings_and_the_continuation_file_index()
    {
        using var doc = JsonDocument.Parse(Fixture.Read("Edgar/submissions-sampleco.json"));

        var (recent, files) = EdgarParsing.ParseSubmissions(doc.RootElement);

        // 7 entries in the fixture's "recent" block; 2 are excluded (one wrong form, one missing
        // item 2.02), leaving 5 selected 8-K/8-K-A rows.
        Assert.Equal(5, recent.Count);
        Assert.Equal(2, files.Count);
        Assert.Equal("CIK0001000001-submissions-002.json", files[1].Name);
        Assert.Equal(new DateOnly(2021, 6, 1), files[1].FilingFrom);
        Assert.Equal(new DateOnly(2022, 3, 31), files[1].FilingTo);
    }
}
