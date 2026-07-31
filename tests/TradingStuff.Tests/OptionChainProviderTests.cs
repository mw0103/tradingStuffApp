using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.MarketDataService;

namespace TradingStuff.Tests;

/// <summary>
/// The chain selectors must survive the MarketDataService hop. Dropping <c>tradingClass</c> there
/// silently reduces SPX to the AM-settled monthly series and makes SPXW unreachable — the exact
/// regression these tests pin.
/// </summary>
public sealed class OptionChainProviderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task The_gateway_request_carries_expiration_window_and_trading_class()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://ibkrgateway.test") };
        var provider = new IbkrOptionMarketDataProvider(
            httpClient, NullLogger<IbkrOptionMarketDataProvider>.Instance);

        await provider.GetOptionChainAsync(
            "SPX",
            new DateOnly(2026, 8, 26),
            strikeWindow: 4,
            tradingClass: "SPXW",
            CancellationToken.None);

        var uri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Equal("/ibkr/options/chains/SPX", uri.AbsolutePath);
        Assert.Contains("expiration=2026-08-26", uri.Query);
        Assert.Contains("window=4", uri.Query);
        Assert.Contains("tradingClass=SPXW", uri.Query);
    }

    [Fact]
    public async Task Selectors_are_omitted_from_the_request_when_not_specified()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://ibkrgateway.test") };
        var provider = new IbkrOptionMarketDataProvider(
            httpClient, NullLogger<IbkrOptionMarketDataProvider>.Instance);

        await provider.GetOptionChainAsync("SPY", null, null, null, CancellationToken.None);

        var uri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Equal("/ibkr/options/chains/SPY", uri.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(uri.Query));
    }

    [Fact]
    public async Task The_deterministic_provider_accepts_and_ignores_the_selectors()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var provider = new DeterministicOptionMarketDataProvider(configuration);

        var withSelectors = await provider.GetOptionChainAsync(
            "XYZ", new DateOnly(2026, 9, 18), 3, "XYZW", CancellationToken.None);
        var without = await provider.GetOptionChainAsync(
            "XYZ", new DateOnly(2026, 9, 18), null, null, CancellationToken.None);

        Assert.Equal(22, withSelectors.Count);
        Assert.Equal(without, withSelectors);
    }
}
