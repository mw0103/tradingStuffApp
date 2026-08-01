using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.Contracts;
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

            // The gateway answers with a window AND how it was cut, not a bare contract array —
            // see its OptionChainResult. A caller that cannot tell a spot-centred window from a
            // degraded one is the shape that cost a node grid.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"contracts":[],"spotCentred":true,"referencePrice":100.0,"expiration":"2026-08-26","windowLow":90.0,"windowHigh":110.0,"unavailable":null}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
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
        // strikeHalfCount, not window. The old name was the ambiguity itself: the gateway reads the
        // number as a half-COUNT of strikes while a caller selecting by moneyness read it as a
        // fraction of spot, so `window: 20` meant ±1.3% of SPX to one end and ±20% to the other and
        // neither end complained. The gateway now takes strikeHalfCount or moneynessHalfWidth, and
        // this assertion pins that this hop forwards the unambiguous name.
        Assert.Contains("strikeHalfCount=4", uri.Query);
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
        var provider = new DeterministicOptionMarketDataProvider();

        var withSelectors = await provider.GetOptionChainAsync(
            "XYZ", new DateOnly(2026, 9, 18), 3, "XYZW", CancellationToken.None);
        var without = await provider.GetOptionChainAsync(
            "XYZ", new DateOnly(2026, 9, 18), null, null, CancellationToken.None);

        Assert.Equal(22, withSelectors.Count);
        Assert.Equal(without, withSelectors);
    }

    // Negative control for the vega sign fix: a prior version gave puts negative vega, so a
    // straddle's two legs nearly cancelled instead of adding — the exact structure MAX_VEGA exists
    // to catch. If the sign regresses, this fails; it does not merely pin a numeric snapshot.
    [Fact]
    public void A_straddles_net_vega_is_materially_positive()
    {
        var provider = new DeterministicOptionMarketDataProvider();
        var expiration = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30));
        var call = new OptionContract("XYZC", "XYZ", expiration, 100m, OptionRight.Call);
        var put = new OptionContract("XYZP", "XYZ", expiration, 100m, OptionRight.Put);

        var response = provider.GetQuotes(new MarketDataQuoteRequest(
        [
            new OrderLegRequest(call, OrderSide.Buy, 1, PositionEffect.Open),
            new OrderLegRequest(put, OrderSide.Buy, 1, PositionEffect.Open),
        ]));

        Assert.All(response.Quotes, quote => Assert.True(
            quote.Greeks.Vega > 0m,
            $"{quote.Contract.Right} vega must be positive, got {quote.Greeks.Vega}"));

        var netVega = response.Quotes.Sum(quote => quote.Greeks.Vega);
        Assert.True(netVega > 0.1m, $"expected a straddle's net vega to be materially positive, got {netVega}");
    }
}
