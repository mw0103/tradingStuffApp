using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// The order the <c>chains</c> verb works through its events, observed at the wire: the stub
/// Terminal records the symbol of every request it receives, in the order it receives them.
///
/// Why the order matters. The v2 deadline fallback can only deliver WHOLE calendar quarters, most
/// recent first (<c>docs/research/c1-preregistration-v2.md</c> section 3), so a chains run that is
/// cut short — a limit, a deadline, a Terminal that falls over — has to have left the recent
/// quarters complete. Working in file order would leave every quarter partially fetched and every
/// one of them outside the deliverable subset, which is the difference between a PROVISIONAL memo
/// and no memo at all. Print date DESCENDING, then event id, is therefore a property of the verb and
/// not an incidental of how the timing table happens to be sorted.
/// </summary>
public sealed class ChainsStepOrderTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"c1-chains-order-{Guid.NewGuid():N}");
    private readonly List<string> _requestedSymbols = [];
    private WebApplication? _stub;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = builder.Build();

        // One request per event, recorded in arrival order. An empty expirations list ends each
        // event's work there, so nothing else reaches the wire and the sequence is unambiguous.
        _stub.MapGet("/v3/option/list/expirations", async (HttpContext ctx) =>
        {
            var symbol = ctx.Request.Query["symbol"].ToString();
            lock (_requestedSymbols) _requestedSymbols.Add(symbol);
            await ctx.Response.WriteAsync("expiration\n");
        });

        await _stub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_stub is not null) { await _stub.StopAsync(); await _stub.DisposeAsync(); }
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    private string ThetaUrl =>
        _stub!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    [Fact]
    public async Task Events_are_fetched_most_recent_print_date_first_with_the_event_id_breaking_ties()
    {
        SeedFixture();
        var output = new StringWriter();

        var code = await StudyCli.RunAsync(
            ["chains", "--data-dir", _dataDirectory, "--theta-url", ThetaUrl], output, CancellationToken.None);

        Assert.Equal(0, code);

        // The timing table is written in the order e-old, e-tie-b, e-tie-a, e-mid, e-new — neither
        // print-date order nor event-id order — so neither could be reproduced by accident.
        Assert.Equal(["NEW", "TIEA", "TIEB", "MID", "OLD"], _requestedSymbols);
    }

    [Fact]
    public async Task A_run_cut_short_by_a_limit_covers_the_most_recent_events_and_not_the_first_rows_of_the_file()
    {
        SeedFixture();
        var output = new StringWriter();

        var code = await StudyCli.RunAsync(
            ["chains", "--data-dir", _dataDirectory, "--theta-url", ThetaUrl, "--limit", "2"],
            output, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(["NEW", "TIEA"], _requestedSymbols);

        // And the table it wrote holds exactly those two events, so the quarter they belong to is
        // the one a coverage check can find complete.
        var rows = CsvFile.Read<OptionMeasuresRow>(new StudyPaths(_dataDirectory).OptionMeasures);
        Assert.Equal(["e-new", "e-tie-a"], rows.Select(r => r.EventId).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Five events whose file order, print-date order and event-id order all differ:
    /// <list type="bullet">
    /// <item>e-new 2025-12-31, the most recent;</item>
    /// <item>e-tie-a and e-tie-b share 2025-11-20, and e-tie-a wins on the ordinal tie-break even
    /// though e-tie-b is written first;</item>
    /// <item>e-mid 2024-02-15 and e-old 2023-05-01 follow.</item>
    /// </list>
    /// </summary>
    private void SeedFixture()
    {
        Directory.CreateDirectory(_dataDirectory);
        var paths = new StudyPaths(_dataDirectory);

        (string Id, string Symbol, DateOnly Print)[] events =
        [
            ("e-old", "OLD", new DateOnly(2023, 5, 1)),
            ("e-tie-b", "TIEB", new DateOnly(2025, 11, 20)),
            ("e-tie-a", "TIEA", new DateOnly(2025, 11, 20)),
            ("e-mid", "MID", new DateOnly(2024, 2, 15)),
            ("e-new", "NEW", new DateOnly(2025, 12, 31))
        ];

        CsvFile.Write(paths.Universe,
            [.. events.Select((e, i) => new UniverseRow(e.Symbol, $"{e.Symbol} INC", i + 1, e.Symbol, $"{e.Symbol} Inc.", "Nasdaq", true, null, null))]);

        CsvFile.Write(paths.Events,
            [.. events.Select((e, i) => new EventRow(e.Id, i + 1, e.Symbol, $"000000000{i + 1}-24-000001", "8-K", "2.02",
                e.Print, e.Print, e.Print.ToDateTime(new TimeOnly(21, 0)), true, true, null))]);

        CsvFile.Write(paths.EventTiming,
            [.. events.Select(e => new EventTimingRow(e.Id, "amc", e.Print,
                e.Print.AddDays(-2), e.Print.AddDays(-1), e.Print.AddDays(1), null, false, null))]);
    }
}
