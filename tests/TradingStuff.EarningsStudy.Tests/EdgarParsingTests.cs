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

    // EDGAR's acceptanceDateTime is genuine UTC and its "Z" means what it says. Apple's quarterly
    // 8-K is accepted at 16:30 Eastern every quarter, and the stamp tracks the DST offset: 21:30Z in
    // winter (EST, UTC-5), 20:30Z in summer (EDT, UTC-4). Fetched live 2026-09-13 from
    // data.sec.gov/submissions/CIK0000320193.json -- see docs/STATE.md.
    [Theory]
    [InlineData("2024-02-01T21:30:30.000Z", 2024, 2, 1, 16, 30, 30)]  // accession 0000320193-24-000005, EST
    [InlineData("2026-07-30T20:30:28.000Z", 2026, 7, 30, 16, 30, 28)] // the same 16:30 ET acceptance, EDT
    // An evening acceptance whose UTC stamp has already rolled into the next day. The Eastern
    // calendar date is what InWindow, the print date and the earnings week all bucket on, so this is
    // the case where reading the stamp as anything but UTC-converted-to-Eastern moves an event.
    [InlineData("2026-01-01T01:00:00.000Z", 2025, 12, 31, 20, 0, 0)]
    public void ParseAcceptanceEt_converts_the_UTC_stamp_to_an_Eastern_wall_clock(
        string raw, int year, int month, int day, int hour, int minute, int second)
    {
        var parsed = EdgarParsing.ParseAcceptanceEt(raw);

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified), parsed);
        // DateTime.Equals ignores Kind, so the assertion above would pass on a Utc-kinded value that
        // EdgarAcceptance.Resolve refuses outright. Assert the kind separately.
        Assert.Equal(DateTimeKind.Unspecified, parsed.Kind);
    }

    [Fact]
    public void ParseAcceptanceEt_tolerates_no_fractional_seconds()
    {
        var parsed = EdgarParsing.ParseAcceptanceEt("2024-02-01T21:30:30Z");
        Assert.Equal(new DateTime(2024, 2, 1, 16, 30, 30, DateTimeKind.Unspecified), parsed);
    }

    // The offset marker is now load-bearing: reading these digits under the wrong zone rule is the
    // exact defect the live pin caught on 2026-09-13. A stamp EDGAR has never sent is a data-shape
    // break, refused in the same voice as a missing acceptanceDateTime, not read under a guess.
    [Theory]
    [InlineData("2024-02-01T16:30:38")]       // no zone marker at all
    [InlineData("2024-02-01T16:30:38-05:00")] // an explicit offset EDGAR does not emit
    [InlineData("2024-02-01 16:30:38Z")]      // not ISO-8601 at all
    public void ParseAcceptanceEt_refuses_a_stamp_that_is_not_a_Z_marked_UTC_instant(string raw) =>
        Assert.Throws<InvalidDataException>(() => EdgarParsing.ParseAcceptanceEt(raw));

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
          "acceptanceDateTime": ["2023-01-01T14:00:00.000Z", "2023-01-02T14:00:00.000Z", "2023-01-03T14:00:00.000Z", "2023-01-04T14:00:00.000Z"],
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
          "acceptanceDateTime": ["2023-01-01T14:00:00.000Z"],
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
