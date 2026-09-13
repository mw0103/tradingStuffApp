using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Tests.Closes;

/// <summary>
/// Runs the real <c>closes</c> verb — real HTTP client, real CLI arg parsing, real CSV read/write —
/// against an in-process Kestrel stub standing in for the gateway, per the brief's suggestion for
/// exercising the actual code path without a live TWS connection.
/// </summary>
public sealed class ClosesStepEndToEndTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"c1-closes-e2e-{Guid.NewGuid():N}");
    private WebApplication? _stub;
    private string? _capturedAuthHeader;
    private int _requestCount;

    // A single 40-day daily series, shared by every symbol the stub is asked about, starting well
    // before every date the fixture below resolves against.
    private static readonly DateOnly SeriesStart = new(2024, 1, 2);
    private const int SeriesLength = 40;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = builder.Build();

        _stub.MapPost("/ibkr/history/bars", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _requestCount);
            _capturedAuthHeader = ctx.Request.Headers.Authorization.ToString();

            using var document = await JsonDocument.ParseAsync(ctx.Request.Body);
            var symbol = document.RootElement.GetProperty("contract").GetProperty("symbol").GetString();

            if (symbol == "HALT") // the fixture's ineligible row must never reach here.
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { detail = "should never be requested" });
                return;
            }

            var bars = Enumerable.Range(0, SeriesLength)
                .Select(i => new
                {
                    timestamp = (string?)null,
                    tradingDate = SeriesStart.AddDays(i).ToString("yyyy-MM-dd"),
                    open = 100m + i,
                    high = 101m + i,
                    low = 99m + i,
                    close = 100m + i,
                    volume = 1000m,
                    count = 5,
                    wap = 100m + i,
                });

            await ctx.Response.WriteAsJsonAsync(new { bars, hasData = true });
        });

        await _stub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_stub is not null)
        {
            await _stub.StopAsync();
            await _stub.DisposeAsync();
        }

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private string GatewayUrl =>
        _stub!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    [Fact]
    public async Task Closes_verb_fetches_from_the_gateway_and_joins_real_files_end_to_end()
    {
        SeedFixture();

        var output = new StringWriter();
        var code = await StudyCli.RunAsync(
            [
                "closes", "--data-dir", _dataDirectory,
                "--gateway-url", GatewayUrl,
                "--token", "test-token",
                "--duration", "2 Y",
            ],
            output, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("Bearer test-token", _capturedAuthHeader); // the bearer-token convention actually travelled over HTTP.
        Assert.Equal(2, _requestCount); // exactly the two Eligible symbols, never the ineligible one.

        var paths = new StudyPaths(_dataDirectory);
        Assert.True(File.Exists(Path.Combine(paths.RawBarsDirectory, "AAPL.done")));
        Assert.True(File.Exists(Path.Combine(paths.RawBarsDirectory, "MSFT.done")));
        Assert.False(File.Exists(Path.Combine(paths.RawBarsDirectory, "HALT.csv")));
        Assert.False(File.Exists(Path.Combine(paths.RawBarsDirectory, "HALT.done")));

        var closes = CsvFile.Read<ClosesRow>(paths.Closes);
        Assert.Equal(2, closes.Count);

        var aapl = closes.Single(r => r.EventId == "e-aapl");
        Assert.Equal("ok", aapl.Status);
        Assert.Equal(100m + 21, aapl.ClosePreEntry); // 2024-01-23 is day index 21 of the series (2024-01-02 = index 0).
        Assert.NotNull(aapl.MedianAbsReturn20); // index 21 has the 21 trailing closes the median needs.

        var msft = closes.Single(r => r.EventId == "e-msft");
        Assert.Equal("missing_exit", msft.Status); // exit date deliberately outside the 40-day series.

        Assert.Contains("closes_summary", output.ToString());

        // The resume claim, exercised through the real CLI rather than just the unit-level fetcher:
        // a rerun against the same data directory must not touch the gateway again.
        var secondOutput = new StringWriter();
        var secondCode = await StudyCli.RunAsync(
            [
                "closes", "--data-dir", _dataDirectory,
                "--gateway-url", GatewayUrl,
                "--token", "test-token",
                "--duration", "2 Y",
            ],
            secondOutput, CancellationToken.None);

        Assert.Equal(0, secondCode);
        Assert.Equal(2, _requestCount); // unchanged: both symbols were skipped via their markers.
        Assert.Equal(closes, CsvFile.Read<ClosesRow>(paths.Closes)); // closes.csv is rewritten identically, not duplicated.
    }

    private void SeedFixture()
    {
        Directory.CreateDirectory(_dataDirectory);
        var paths = new StudyPaths(_dataDirectory);

        CsvFile.Write(paths.Universe,
        [
            new UniverseRow("AAPL", "APPLE INC", 320193, "AAPL", "Apple Inc.", "Nasdaq", true, null, null),
            new UniverseRow("MSFT", "MICROSOFT CORP", 789019, "MSFT", "Microsoft Corp", "Nasdaq", true, null, null),
            new UniverseRow("HALT", "HALTED INC", 999999, "HALT", "Halted Inc", "NYSE", false, "03_primary_listing_nyse_nasdaq_nyse_american", "not eligible"),
        ]);

        CsvFile.Write(paths.Events,
        [
            new EventRow("e-aapl", 320193, "AAPL", "0000320193-24-000001", "8-K", "2.02", new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10), new DateTime(2024, 1, 10, 21, 5, 0), true, true, null),
            new EventRow("e-msft", 789019, "MSFT", "0000789019-24-000001", "8-K", "2.02", new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10), new DateTime(2024, 1, 10, 21, 5, 0), true, true, null),
        ]);

        CsvFile.Write(paths.EventTiming,
        [
            // Index 21 of the series (2024-01-02 = index 0) has the 21 trailing closes the median needs.
            new EventTimingRow("e-aapl", "amc", new DateOnly(2024, 1, 24), new DateOnly(2024, 1, 23), new DateOnly(2024, 1, 24), new DateOnly(2024, 1, 25), "2024-W04", false, null),
            new EventTimingRow("e-msft", "amc", new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 9), new DateOnly(2024, 1, 10), new DateOnly(2099, 1, 1), "2024-W02", false, null),
        ]);
    }
}
