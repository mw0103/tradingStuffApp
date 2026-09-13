using System.Net;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Edgar;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class UniverseStepTests
{
    private const string SecUrl = "https://www.sec.gov/files/company_tickers_exchange.json";

    private static async Task<(StudyContext Context, List<UniverseRow> Rows)> RunAsync(TempStudyDirectory dir, HttpMessageHandler? handler = null)
    {
        var context = dir.NewContext();
        File.Copy(Fixture.Path("Edgar/cboe-seed-sample.csv"), context.Paths.UniverseSeed);
        var step = new UniverseStep(handler ?? new FakeHttpHandler().On(SecUrl, HttpStatusCode.OK, Fixture.Read("Edgar/company-tickers-exchange-sample.json")));
        var code = await step.RunAsync(context, [], CancellationToken.None);
        Assert.Equal(0, code);
        return (context, CsvFile.Read<UniverseRow>(context.Paths.Universe));
    }

    [Fact]
    public async Task Parses_the_cboe_header_and_resolves_class_share_symbols_via_dash_substitution()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        var brkB = rows.Single(r => r.Symbol == "BRK.B");
        Assert.Equal(1067983, brkB.Cik);
        Assert.Equal("BRK-B", brkB.SecTicker);
        Assert.True(brkB.Eligible);

        var bfB = rows.Single(r => r.Symbol == "BF.B");
        Assert.Equal(14693, bfB.Cik);
        Assert.Equal("BF-B", bfB.SecTicker);
        Assert.True(bfB.Eligible);
    }

    [Fact]
    public async Task Exact_ticker_match_is_tried_first()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        var aapl = rows.Single(r => r.Symbol == "AAPL");
        Assert.Equal(320193, aapl.Cik);
        Assert.Equal("AAPL", aapl.SecTicker);
        Assert.True(aapl.Eligible);
    }

    [Fact]
    public async Task An_unmatched_ticker_is_excluded_at_the_cik_gate()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        var row = rows.Single(r => r.Symbol == "ZNOMATCH");
        Assert.False(row.Eligible);
        Assert.Equal(Gates.CikMapped, row.ExclusionGate);
        Assert.Null(row.Cik);
    }

    [Fact]
    public async Task A_ticker_mapping_to_multiple_ciks_is_excluded_and_reported_not_silently_picked()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        var row = rows.Single(r => r.Symbol == "ZAMBIG");
        Assert.False(row.Eligible);
        Assert.Equal(Gates.CikMapped, row.ExclusionGate);
        Assert.Null(row.Cik);
        Assert.Contains("9999903", row.Note);
        Assert.Contains("9999904", row.Note);
    }

    [Fact]
    public async Task A_cik_shared_by_two_symbols_keeps_both_and_reports_it_on_each()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        var goog = rows.Single(r => r.Symbol == "GOOG");
        var googl = rows.Single(r => r.Symbol == "GOOGL");

        Assert.True(goog.Eligible);
        Assert.True(googl.Eligible);
        Assert.Equal(1652044, goog.Cik);
        Assert.Equal(1652044, googl.Cik);
        Assert.Contains("GOOGL", goog.Note);
        Assert.Contains("GOOG", googl.Note);
    }

    [Fact]
    public async Task Exchange_gate_admits_only_nyse_nasdaq_and_nyse_american()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        var otc = rows.Single(r => r.Symbol == "ZOTC");
        Assert.False(otc.Eligible);
        Assert.Equal(Gates.PrimaryListing, otc.ExclusionGate);

        var empty = rows.Single(r => r.Symbol == "ZEMPTY");
        Assert.False(empty.Eligible);
        Assert.Equal(Gates.PrimaryListing, empty.ExclusionGate);

        Assert.All(new[] { "AAPL", "BRK.B", "BF.B", "GOOG", "GOOGL" },
            symbol => Assert.True(rows.Single(r => r.Symbol == symbol).Eligible));
    }

    [Fact]
    public async Task Gate_counts_match_hand_computed_values_and_are_internally_consistent()
    {
        using var dir = new TempStudyDirectory();
        var (context, _) = await RunAsync(dir);

        var gates = CsvFile.Read<GateCountRow>(context.Paths.GateCounts);
        Assert.Equal(3, gates.Count);

        var gate1 = gates.Single(g => g.Gate == Gates.SeedOptionable);
        Assert.Equal((9, 0, 9), (gate1.Considered, gate1.Removed, gate1.Remaining));

        var gate2 = gates.Single(g => g.Gate == Gates.CikMapped);
        Assert.Equal((9, 2, 7), (gate2.Considered, gate2.Removed, gate2.Remaining));
        Assert.Contains("ambiguous_multiple_ciks: 1", gate2.Note);
        Assert.Contains("no_match: 1", gate2.Note);

        var gate3 = gates.Single(g => g.Gate == Gates.PrimaryListing);
        Assert.Equal((7, 2, 5), (gate3.Considered, gate3.Removed, gate3.Remaining));
        Assert.Contains("OTC: 1", gate3.Note);
        Assert.Contains("(empty): 1", gate3.Note);

        foreach (var gate in gates)
        {
            Assert.Equal(gate.Remaining, gate.Considered - gate.Removed);
        }
    }

    [Fact]
    public async Task Every_seed_row_appears_in_universe_csv_whether_eligible_or_not()
    {
        using var dir = new TempStudyDirectory();
        var (_, rows) = await RunAsync(dir);

        Assert.Equal(9, rows.Count);
        Assert.Equal(5, rows.Count(r => r.Eligible));
    }

    [Fact]
    public async Task A_rerun_never_touches_the_network_once_the_sec_file_is_cached()
    {
        using var dir = new TempStudyDirectory();
        var (_, firstRun) = await RunAsync(dir);

        var secondContext = dir.NewContext();
        var secondStep = new UniverseStep(new ThrowingHandler());
        var code = await secondStep.RunAsync(secondContext, [], CancellationToken.None);

        Assert.Equal(0, code);
        var secondRun = CsvFile.Read<UniverseRow>(secondContext.Paths.Universe);
        Assert.Equal(firstRun, secondRun);
    }

    [Fact]
    public async Task The_seed_option_overrides_the_default_seed_path()
    {
        using var dir = new TempStudyDirectory();
        // Deliberately leave context.Paths.UniverseSeed absent so the run can only succeed by
        // actually reading the --seed override.
        var context = dir.NewContext();
        var handler = new FakeHttpHandler().On(SecUrl, HttpStatusCode.OK, Fixture.Read("Edgar/company-tickers-exchange-sample.json"));
        var step = new UniverseStep(handler);

        var code = await step.RunAsync(context, ["--seed", Fixture.Path("Edgar/cboe-seed-sample.csv")], CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(9, CsvFile.Read<UniverseRow>(context.Paths.Universe).Count);
    }
}
