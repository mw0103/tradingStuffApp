using System.Net;
using System.Text.Json;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Edgar;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class EventsStepTests
{
    private const string SampleCoSubmissionsUrl = "https://data.sec.gov/submissions/CIK0001000001.json";
    private const string SampleCoContinuation1Url = "https://data.sec.gov/submissions/CIK0001000001-submissions-001.json"; // does not intersect the window -- must never be requested
    private const string SampleCoContinuation2Url = "https://data.sec.gov/submissions/CIK0001000001-submissions-002.json";
    private const string SampleCoCompanyFactsUrl = "https://data.sec.gov/api/xbrl/companyfacts/CIK0001000001.json";

    private const string EmptySubmissions = """
        {"filings":{"recent":{"accessionNumber":[],"filingDate":[],"reportDate":[],"acceptanceDateTime":[],"form":[],"items":[]},"files":[]}}
        """;

    private static UniverseRow EligibleRow(string symbol, long cik) =>
        new(symbol, symbol + " Co", cik, symbol, symbol + " Co", "Nasdaq", true, null, null);

    private static FakeHttpHandler SampleCoHandler() => new FakeHttpHandler()
        .On(SampleCoSubmissionsUrl, HttpStatusCode.OK, Fixture.Read("Edgar/submissions-sampleco.json"))
        .On(SampleCoContinuation2Url, HttpStatusCode.OK, Fixture.Read("Edgar/submissions-sampleco-continuation-002.json"))
        .On(SampleCoCompanyFactsUrl, HttpStatusCode.OK, Fixture.Read("Edgar/companyfacts-sampleco.json"));

    private static async Task<(StudyContext Context, List<EventRow> Events, List<GateCountRow> Gates, List<SharesFactRow> SharesFacts)> RunAsync(
        TempStudyDirectory dir, IEnumerable<UniverseRow> universe, HttpMessageHandler handler)
    {
        var context = dir.NewContext();
        CsvFile.Write(context.Paths.Universe, universe);
        var step = new EventsStep(handler, "Research Bot contact@example.com", TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        var code = await step.RunAsync(context, [], CancellationToken.None);
        Assert.Equal(0, code);

        return (context,
            CsvFile.Read<EventRow>(context.Paths.Events),
            CsvFile.Read<GateCountRow>(context.Paths.GateCounts),
            CsvFile.Read<SharesFactRow>(context.Paths.SharesFacts));
    }

    // ---- Refusal without a declared User-Agent ------------------------------------------------

    [Fact]
    public async Task Refuses_to_run_without_a_declared_user_agent()
    {
        using var dir = new TempStudyDirectory();
        var output = new StringWriter();
        var context = dir.NewContext(output);
        var step = new EventsStep(userAgentOverride: ""); // explicit non-null empty string: immune to any ambient env var

        var code = await step.RunAsync(context, [], CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Contains("EDGAR_USER_AGENT", output.ToString());
    }

    [Fact]
    public async Task Reads_the_real_environment_variable_when_no_override_is_supplied()
    {
        var original = Environment.GetEnvironmentVariable("EDGAR_USER_AGENT");
        try
        {
            Environment.SetEnvironmentVariable("EDGAR_USER_AGENT", null);
            using var dir = new TempStudyDirectory();
            var context = dir.NewContext();
            var step = new EventsStep(); // no override -- must fall through to the real process environment

            var code = await step.RunAsync(context, [], CancellationToken.None);

            Assert.Equal(2, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGAR_USER_AGENT", original);
        }
    }

    // ---- Full pipeline over one name: selection, window, amendment, dedup, continuation files --

    [Fact]
    public async Task Full_pipeline_selects_windows_dedups_and_never_fetches_a_non_intersecting_continuation_file()
    {
        using var dir = new TempStudyDirectory();
        var handler = SampleCoHandler();

        var (_, events, gates, sharesFacts) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], handler);

        Assert.DoesNotContain(SampleCoContinuation1Url, handler.Requests.Select(r => r.Url));
        Assert.Contains(SampleCoContinuation2Url, handler.Requests.Select(r => r.Url));

        // 5 selected in "recent" (2 excluded there: one wrong form, one item 9.01 only) + 1 in the
        // intersecting continuation file.
        Assert.Equal(6, events.Count);
        Assert.Equal(2, sharesFacts.Count);

        var gate4 = gates.Single(g => g.Gate == Gates.HasItem202);
        Assert.Equal((1, 0, 1), (gate4.Considered, gate4.Removed, gate4.Remaining));

        var gate5 = gates.Single(g => g.Gate == Gates.NotAmendment);
        Assert.Equal((5, 1, 4), (gate5.Considered, gate5.Removed, gate5.Remaining)); // 5 in-window rows, 1 amendment

        var gate6 = gates.Single(g => g.Gate == Gates.Dedup);
        Assert.Equal((4, 1, 3), (gate6.Considered, gate6.Removed, gate6.Remaining)); // 4 dedup candidates, 1 loses to an earlier same-quarter filing

        foreach (var gate in gates) Assert.Equal(gate.Remaining, gate.Considered - gate.Removed);
    }

    [Fact]
    public async Task Items_token_match_excludes_a_filing_with_only_an_unrelated_item()
    {
        using var dir = new TempStudyDirectory();
        var (_, events, _, _) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        Assert.DoesNotContain(events, e => e.AccessionNumber == "0001000001-23-000003"); // items "9.01" only
        Assert.DoesNotContain(events, e => e.AccessionNumber == "0001000001-23-000004"); // form 10-Q
        Assert.Contains(events, e => e.AccessionNumber == "0001000001-23-000001"); // items "2.02,9.01"
    }

    [Fact]
    public async Task An_8KA_is_emitted_but_excluded_from_dedup_and_counted()
    {
        using var dir = new TempStudyDirectory();
        var (_, events, gates, _) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        var amendment = events.Single(e => e.AccessionNumber == "0001000001-23-000005");
        Assert.Equal("8-K/A", amendment.Form);
        Assert.True(amendment.InWindow);
        Assert.False(amendment.KeptAfterDedup);
        Assert.Equal("8-K/A ignored", amendment.DedupNote);

        var gate5 = gates.Single(g => g.Gate == Gates.NotAmendment);
        Assert.Equal(1, gate5.Removed);
    }

    [Fact]
    public async Task Acceptance_time_is_read_as_Eastern_wall_clock()
    {
        using var dir = new TempStudyDirectory();
        var (_, events, _, _) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        var filing = events.Single(e => e.AccessionNumber == "0001000001-23-000001");
        Assert.Equal(new DateTime(2023, 1, 30, 17, 45, 0, DateTimeKind.Unspecified), filing.AcceptanceEt);
    }

    [Fact]
    public async Task InWindow_is_decided_on_the_acceptance_date_not_the_filing_date()
    {
        using var dir = new TempStudyDirectory();
        var (_, events, _, _) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        // Both share filingDate 2026-01-02 -- only the acceptance date tells them apart.
        var inWindowAtTheBoundary = events.Single(e => e.AccessionNumber == "0001000001-25-000010");
        Assert.Equal(new DateOnly(2026, 1, 2), inWindowAtTheBoundary.FilingDate);
        Assert.True(inWindowAtTheBoundary.InWindow, "accepted 2025-12-31T17:45 ET == WindowTo, inclusive");

        var outOfWindow = events.Single(e => e.AccessionNumber == "0001000001-26-000001");
        Assert.Equal(new DateOnly(2026, 1, 2), outOfWindow.FilingDate);
        Assert.False(outOfWindow.InWindow, "accepted 2026-01-02, one day past WindowTo");
        Assert.Equal("out of window", outOfWindow.DedupNote);
    }

    [Fact]
    public async Task Dedup_keeps_the_earliest_acceptance_instant_even_when_two_filings_share_a_filing_date()
    {
        using var dir = new TempStudyDirectory();
        var (_, events, _, _) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        // A: accepted Jan 30 17:45, filingDate Jan 31. B: accepted Jan 31 08:00, filingDate Jan 31.
        var a = events.Single(e => e.AccessionNumber == "0001000001-23-000001");
        var b = events.Single(e => e.AccessionNumber == "0001000001-23-000002");

        Assert.Equal(a.FilingDate, b.FilingDate); // same filing date, so only the instant can decide
        Assert.True(a.KeptAfterDedup);
        Assert.Null(a.DedupNote);
        Assert.False(b.KeptAfterDedup);
        Assert.Equal($"dedup: kept {a.AccessionNumber}", b.DedupNote);
    }

    [Fact]
    public async Task Every_seed_filing_appears_in_events_csv_amendments_and_out_of_window_included()
    {
        using var dir = new TempStudyDirectory();
        var (_, events, _, _) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        string[] expectedAccessions =
        [
            "0001000001-23-000001", "0001000001-23-000002", "0001000001-23-000005",
            "0001000001-25-000010", "0001000001-26-000001", "0001000001-22-000003"
        ];
        Assert.Equal(expectedAccessions.OrderBy(x => x), events.Select(e => e.AccessionNumber).OrderBy(x => x));
    }

    [Fact]
    public async Task Shares_outstanding_facts_are_dumped_verbatim_from_the_fixture()
    {
        using var dir = new TempStudyDirectory();
        var (_, _, _, sharesFacts) = await RunAsync(dir, [EligibleRow("SAMP", 1000001)], SampleCoHandler());

        Assert.Equal(2, sharesFacts.Count);
        var first = sharesFacts.Single(r => r.AccessionNumber == "0001000001-23-000001");
        Assert.Equal(1000001, first.Cik);
        Assert.Equal(new DateOnly(2022, 12, 31), first.PeriodEnd);
        Assert.Equal(100_000_000m, first.Value);
        Assert.Equal(new DateOnly(2023, 2, 15), first.Filed);
        Assert.Equal("10-K", first.Form);
        Assert.Equal("CY2022Q4I", first.Frame);

        var second = sharesFacts.Single(r => r.AccessionNumber == "0001000001-23-000006");
        Assert.Null(second.Frame); // fixture omits the optional "frame" key for this entry
    }

    // ---- Non-crashing 404s ----------------------------------------------------------------------

    [Fact]
    public async Task A_submissions_404_for_an_eligible_name_is_recorded_at_gate_04_not_a_crash()
    {
        using var dir = new TempStudyDirectory();
        var handler = SampleCoHandler()
            .On("https://data.sec.gov/submissions/CIK0001000099.json", HttpStatusCode.NotFound, "nope")
            // A submissions 404 says nothing about companyfacts availability for the same CIK -- the
            // two fetches are independent, so this must be wired up too or the run legitimately has
            // more work left to do.
            .On("https://data.sec.gov/api/xbrl/companyfacts/CIK0001000099.json", HttpStatusCode.NotFound, "nope");

        var (_, events, gates, _) = await RunAsync(
            dir, [EligibleRow("SAMP", 1000001), EligibleRow("NOPE", 1000099)], handler);

        Assert.DoesNotContain(events, e => e.Cik == 1000099);
        var gate4 = gates.Single(g => g.Gate == Gates.HasItem202);
        Assert.Equal((2, 1, 1), (gate4.Considered, gate4.Removed, gate4.Remaining));
        Assert.Contains("submissions_unavailable: 1", gate4.Note);
    }

    [Fact]
    public async Task Companyfacts_404_and_a_structurally_missing_fact_are_both_counted_never_a_crash()
    {
        using var dir = new TempStudyDirectory();
        var handler = SampleCoHandler()
            .On("https://data.sec.gov/submissions/CIK0001000002.json", HttpStatusCode.OK, EmptySubmissions)
            .On("https://data.sec.gov/api/xbrl/companyfacts/CIK0001000002.json", HttpStatusCode.OK, Fixture.Read("Edgar/companyfacts-missing-fact.json"))
            .On("https://data.sec.gov/submissions/CIK0001000003.json", HttpStatusCode.OK, EmptySubmissions)
            .On("https://data.sec.gov/api/xbrl/companyfacts/CIK0001000003.json", HttpStatusCode.NotFound, "nope");

        var (_, _, gates, sharesFacts) = await RunAsync(
            dir,
            [EligibleRow("SAMP", 1000001), EligibleRow("NOFACT", 1000002), EligibleRow("NOFACT404", 1000003)],
            handler);

        Assert.Equal(2, sharesFacts.Count); // only SAMP's two facts; the other two CIKs contribute none
        var gate4 = gates.Single(g => g.Gate == Gates.HasItem202);
        Assert.Contains("shares_outstanding_fact_missing: 2 of 3 CIKs", gate4.Note);
    }

    // ---- A CIK shared by two eligible symbols (dual-class shares) -------------------------------

    [Fact]
    public async Task A_cik_shared_by_two_eligible_symbols_dedups_across_both_but_shares_facts_are_emitted_once()
    {
        using var dir = new TempStudyDirectory();
        var handler = SampleCoHandler();

        var (_, events, _, sharesFacts) = await RunAsync(
            dir, [EligibleRow("SAMP", 1000001), EligibleRow("SAMP2", 1000001)], handler);

        // Each symbol independently gets its own row per selected filing (each has its own option
        // chain to measure downstream) -- 6 selected filings x 2 symbols.
        Assert.Equal(12, events.Count);

        // But the pre-registration's dedup rule is CIK-scoped, not security-scoped: exactly one row
        // survives per (CIK, quarter) across BOTH symbols combined.
        var q1_2023 = events.Where(e => e.Cik == 1000001 && e.AcceptanceEt is { Year: 2023, Month: <= 3 }).ToList();
        Assert.Equal(4, q1_2023.Count); // SAMP's F-A/F-B + SAMP2's F-A/F-B
        Assert.Equal(1, q1_2023.Count(e => e.KeptAfterDedup));

        // companyfacts is a property of the filer, not of the ticker: fetched/emitted once per CIK.
        Assert.Equal(2, sharesFacts.Count(r => r.Cik == 1000001));
    }

    // ---- RequiresEdgar: pins the Eastern reading against the real SEC data ----------------------

    [Fact]
    [Trait("Category", "RequiresEdgar")]
    public async Task Live_apple_8K_filed_2024_02_01_has_item_202_and_accepts_at_hour_16_Eastern()
    {
        var userAgent = Environment.GetEnvironmentVariable("EDGAR_USER_AGENT");
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            // Per the WP1 brief: skip by returning early with a message rather than failing. EDGAR
            // is unreachable from this sandbox, and this tool refuses to invent a declared
            // User-Agent on the operator's behalf, so this test only runs where a real one is set.
            Console.WriteLine(
                "SKIPPED Live_apple_8K_filed_2024_02_01_has_item_202_and_accepts_at_hour_16_Eastern: " +
                "EDGAR_USER_AGENT is not set in this environment.");
            return;
        }

        using var edgar = new EdgarHttpClient(userAgent);
        var body = await edgar.GetAsync("https://data.sec.gov/submissions/CIK0000320193.json", CancellationToken.None);
        using var doc = JsonDocument.Parse(body);
        var (recent, _) = EdgarParsing.ParseSubmissions(doc.RootElement);

        var filing = recent.SingleOrDefault(f => f.FilingDate == new DateOnly(2024, 2, 1) && f.Form == "8-K");
        Assert.True(filing is not null,
            $"expected exactly one 8-K filed 2024-02-01 among {recent.Count} item-2.02 filings fetched live for CIK 320193; " +
            $"filing dates actually returned: {string.Join(", ", recent.Select(f => $"{f.FilingDate}/{f.Form}"))}");

        Assert.Contains("2.02", filing!.Items.Split(',').Select(s => s.Trim()));
        // Pins the Eastern-wall-clock reading against reality: a UTC misreading of this same
        // timestamp would land close to midday ET, not after the 2024-02-01 market close. If EDGAR
        // ever changes how it stamps this field, this assertion -- not a unit test against a
        // fixture -- is what will tell us.
        Assert.Equal(16, filing.AcceptanceEt.Hour);
        Assert.Equal(30, filing.AcceptanceEt.Minute);
    }
}
