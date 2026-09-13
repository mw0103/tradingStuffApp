using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.EarningsStudy.Closes;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.ResearchService.Gateway;
using Xunit.Abstractions;

namespace TradingStuff.EarningsStudy.Tests.Closes;

/// <summary>
/// The one live check this work package calls for: a real gateway answering a real
/// <c>POST /ibkr/history/bars</c>. Needs <c>TRADING_TEST_GATEWAY_URL</c> (and, if the gateway
/// requires it, <c>Authentication__DevelopmentToken</c>); skips otherwise, per the convention in
/// <c>LiveIbkrUnderlyingBarsTests</c> — a fast pass here is a skip, not a demonstration.
/// </summary>
[Trait("Category", "RequiresGateway")]
public sealed class ClosesGatewayLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Pulls_a_real_daily_series_for_a_liquid_name()
    {
        var url = Environment.GetEnvironmentVariable("TRADING_TEST_GATEWAY_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            output.WriteLine("No TRADING_TEST_GATEWAY_URL set; skipping.");
            return;
        }

        var token = Environment.GetEnvironmentVariable("Authentication__DevelopmentToken") ?? "dev-internal-token";
        using var httpClient = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(90) };
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var gateway = new IbkrGatewayClient(httpClient, NullLogger<IbkrGatewayClient>.Instance);
        var row = new UniverseRow("SPY", "SPDR S&P 500", null, "SPY", "SPDR S&P 500 ETF Trust", "NYSE", true, null, null);

        var dataDirectory = Path.Combine(Path.GetTempPath(), $"c1-closes-live-{Guid.NewGuid():N}");
        try
        {
            var fetcher = new BarsFetcher(gateway, new StudyPaths(dataDirectory), "1 M", line => output.WriteLine(line));
            var outcome = await fetcher.FetchOneAsync(row, CancellationToken.None);

            output.WriteLine($"outcome: {outcome}");
            Assert.True(
                outcome is FetchOutcome.Fetched or FetchOutcome.Empty,
                $"expected a confirmed answer (Fetched or Empty) from a real gateway for SPY, got {outcome}");

            if (outcome == FetchOutcome.Fetched)
            {
                var marker = File.ReadAllText(Path.Combine(new StudyPaths(dataDirectory).RawBarsDirectory, "SPY.done"));
                output.WriteLine(marker);
                Assert.Contains("bar_count=", marker, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
