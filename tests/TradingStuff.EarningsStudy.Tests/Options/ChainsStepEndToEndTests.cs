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
/// Runs the real <c>chains</c> verb - real HTTP client, real CLI arg parsing, real CSV read/write -
/// against an in-process Kestrel stub standing in for the Theta Terminal, per
/// <c>ClosesStepEndToEndTests</c>'s pattern. Three events in one run: one with unresolved dates
/// (never touches the network), one whose EOD request comes back "no data" (an error that must not
/// stop the other two), and one that succeeds - proving the verb produces one row per event and
/// keeps going past a failure in the same pass that proves the real code path end to end.
/// </summary>
public sealed class ChainsStepEndToEndTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"c1-chains-e2e-{Guid.NewGuid():N}");
    private WebApplication? _stub;
    private int _expirationsRequests;
    private int _eodRequests;
    private bool _forceVersionError;

    private const string EodChain =
        "date,strike,right,bid,ask\n" +
        "2024-02-01,90,C,10.00,10.10\n2024-02-01,90,P,0.05,0.10\n" +
        "2024-02-01,100,C,2.00,2.10\n2024-02-01,100,P,1.95,2.05\n" +
        "2024-02-01,110,C,0.05,0.10\n2024-02-01,110,P,10.00,10.10\n";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = builder.Build();

        _stub.MapGet("/v3/option/list/expirations", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _expirationsRequests);
            if (_forceVersionError)
            {
                ctx.Response.StatusCode = 410;
                await ctx.Response.WriteAsync("this route requires an API version this client does not speak");
                return;
            }

            var symbol = ctx.Request.Query["symbol"].ToString();
            if (symbol is "AOK" or "AERR")
            {
                await ctx.Response.WriteAsync("expiration\n2024-02-02\n");
                return;
            }
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"unexpected symbol requested: {symbol}"); // AUNRES must never reach here
        });

        _stub.MapGet("/v3/option/history/eod", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _eodRequests);
            var symbol = ctx.Request.Query["symbol"].ToString();
            if (symbol == "AERR")
            {
                await ctx.Response.WriteAsync("No data for the specified request.");
                return;
            }
            if (symbol == "AOK")
            {
                await ctx.Response.WriteAsync(EodChain);
                return;
            }
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"unexpected symbol requested: {symbol}");
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
    public async Task ChainsVerbProducesOneRowPerEventAndKeepsGoingPastAFailure()
    {
        SeedFixture();
        var output = new StringWriter();

        var code = await StudyCli.RunAsync(
            ["chains", "--data-dir", _dataDirectory, "--theta-url", ThetaUrl], output, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(2, _expirationsRequests); // AOK and AERR each list expirations once; AUNRES never calls out
        var paths = new StudyPaths(_dataDirectory);
        var rows = CsvFile.Read<OptionMeasuresRow>(paths.OptionMeasures);
        Assert.Equal(3, rows.Count);

        var unresolved = rows.Single(r => r.EventId == "e-unres");
        Assert.Equal("dates_unresolved", unresolved.FetchStatus);

        var errored = rows.Single(r => r.EventId == "e-err");
        Assert.Equal("error", errored.FetchStatus);
        Assert.Contains("No data available", errored.Note);

        var ok = rows.Single(r => r.EventId == "e-ok");
        Assert.Equal("ok", ok.FetchStatus);
        Assert.Equal(100m, ok.AtmStrike);
        Assert.Equal(4.05m, ok.StraddleMidEntry);

        Assert.True(File.Exists(Path.Combine(paths.RawChainsDirectory, "AOK", "expirations.csv")));
        Assert.Contains("chains_summary", output.ToString());

        // Rerun: AOK's expirations+EOD are cached and must not be requested again. AERR's
        // expirations succeeded (and IS cached) but its EOD call threw and was never cached, so a
        // rerun legitimately retries EOD for AERR specifically - a failed fetch is not cached.
        var expirationsBefore = _expirationsRequests;
        var eodBefore = _eodRequests;
        var secondOutput = new StringWriter();
        var secondCode = await StudyCli.RunAsync(
            ["chains", "--data-dir", _dataDirectory, "--theta-url", ThetaUrl], secondOutput, CancellationToken.None);

        Assert.Equal(0, secondCode);
        Assert.Equal(expirationsBefore, _expirationsRequests); // both roots' expirations came from disk
        Assert.Equal(eodBefore + 1, _eodRequests); // only AERR's uncached EOD call was repeated
    }

    [Fact]
    public async Task AVersionRejectionAbortsTheWholeVerbRatherThanRecordingAnEventFailure()
    {
        SeedFixture();
        _forceVersionError = true;
        var output = new StringWriter();

        var code = await StudyCli.RunAsync(
            ["chains", "--data-dir", _dataDirectory, "--theta-url", ThetaUrl], output, CancellationToken.None);

        Assert.NotEqual(0, code);
        Assert.Contains("aborting", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(new StudyPaths(_dataDirectory).OptionMeasures)); // no partial table written
    }

    private void SeedFixture()
    {
        Directory.CreateDirectory(_dataDirectory);
        var paths = new StudyPaths(_dataDirectory);

        CsvFile.Write(paths.Universe,
        [
            new UniverseRow("AOK", "A OK INC", 1, "AOK", "A OK Inc.", "Nasdaq", true, null, null),
            new UniverseRow("AERR", "A ERR INC", 2, "AERR", "A Err Inc.", "Nasdaq", true, null, null),
            new UniverseRow("AUNRES", "A UNRES INC", 3, "AUNRES", "A Unres Inc.", "Nasdaq", true, null, null),
        ]);

        CsvFile.Write(paths.Events,
        [
            new EventRow("e-ok", 1, "AOK", "0000000001-24-000001", "8-K", "2.02", new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 1), new DateTime(2024, 2, 1, 21, 0, 0), true, true, null),
            new EventRow("e-err", 2, "AERR", "0000000002-24-000001", "8-K", "2.02", new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 1), new DateTime(2024, 2, 1, 21, 0, 0), true, true, null),
            new EventRow("e-unres", 3, "AUNRES", "0000000003-24-000001", "8-K", "2.02", new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 1), new DateTime(2024, 2, 1, 21, 0, 0), true, true, null),
        ]);

        CsvFile.Write(paths.EventTiming,
        [
            new EventTimingRow("e-ok", "amc", new DateOnly(2024, 2, 1), new DateOnly(2024, 1, 31), new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 2), "2024-W05", false, null),
            new EventTimingRow("e-err", "amc", new DateOnly(2024, 2, 1), new DateOnly(2024, 1, 31), new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 2), "2024-W05", false, null),
            new EventTimingRow("e-unres", "amc", new DateOnly(2024, 2, 1), null, null, null, "2024-W05", false, "timing QA could not resolve dates"),
        ]);
    }
}
