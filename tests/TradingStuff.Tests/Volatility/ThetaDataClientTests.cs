using System.Net;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Exercises the Theta Terminal request path without a Terminal.
/// </summary>
/// <remarks>
/// The client takes an <see cref="HttpClient"/>, so everything up to the socket — endpoint
/// selection, parameter names, encoding, status handling, and the Terminal's habit of
/// reporting failures with a 200 — is reachable from a fake handler. That matters more here
/// than in most HTTP clients: the endpoint paths and parameter names are the contract, a
/// wrong one returns a plausible CSV rather than an error, and the whole reason this client
/// parses by column name is that silently-wrong data is the failure mode being defended
/// against. These assertions pin the request; only the socket itself is left unverified.
/// </remarks>
public class ThetaDataClientTests
{
    private const string CsvBody = "ms_of_day,bid,ask\n56700000,1.25,1.35\n";

    /// <summary>Captures the outgoing request and replays a canned response.</summary>
    private sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK, string body = CsvBody)
        : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public int Calls { get; private set; }
        public Exception? ThrowOnSend { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            if (ThrowOnSend is not null) throw ThrowOnSend;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static (ThetaDataClient Client, FakeHandler Handler) Build(
        string apiVersion = "v2", HttpStatusCode status = HttpStatusCode.OK, string body = CsvBody)
    {
        var handler = new FakeHandler(status, body);
        var options = new ThetaDataOptions { ApiVersion = apiVersion };
        return (new ThetaDataClient(options, new HttpClient(handler)), handler);
    }

    private static Dictionary<string, string> QueryOf(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));

    // ---------- defaults ----------

    [Fact]
    public void DefaultsTargetTheLocalTerminal()
    {
        var options = new ThetaDataOptions();

        Assert.Equal("http://127.0.0.1:25510", options.BaseAddress);
        Assert.Equal("v2", options.ApiVersion);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Timeout);

        // Strikes arrive in tenths of a cent. A wrong divisor parses cleanly and is off by
        // three orders of magnitude, which is why the loader cross-checks it.
        Assert.Equal(1000.0, options.StrikeDivisor);

        // 15:45 ET, late enough to be representative but before the closing auction widens quotes.
        Assert.Equal((15 * 3600 + 45 * 60) * 1000, options.SnapshotMillisecondsOfDay);
        Assert.Equal(56_700_000, options.SnapshotMillisecondsOfDay);
    }

    [Fact]
    public void ConstructorAppliesBaseAddressAndTimeout()
    {
        var options = new ThetaDataOptions { BaseAddress = "http://localhost:9999", Timeout = TimeSpan.FromSeconds(42) };
        using var http = new HttpClient(new FakeHandler());

        using var client = new ThetaDataClient(options, http);

        Assert.Equal(new Uri("http://localhost:9999"), http.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(42), http.Timeout);
        Assert.Same(options, client.Options);
    }

    [Fact]
    public void ConstructorFallsBackToDefaultOptions()
    {
        using var client = new ThetaDataClient(null, new HttpClient(new FakeHandler()));

        Assert.Equal("v2", client.Options.ApiVersion);
        Assert.Equal("http://127.0.0.1:25510", client.Options.BaseAddress);
    }

    // ---------- endpoint selection ----------

    [Theory]
    [InlineData("v2", "/v2/list/expirations")]
    [InlineData("v3", "/v3/option/list/expirations")]
    public void ExpirationsEndpointIsVersioned(string version, string expected) =>
        Assert.Equal(expected, ThetaDataEndpoints.Expirations(version));

    [Theory]
    [InlineData("v2", "/v2/list/strikes")]
    [InlineData("v3", "/v3/option/list/strikes")]
    public void StrikesEndpointIsVersioned(string version, string expected) =>
        Assert.Equal(expected, ThetaDataEndpoints.Strikes(version));

    [Theory]
    [InlineData("v2", "/v2/bulk_hist/option/quote")]
    [InlineData("v3", "/v3/option/history/quote")]
    public void BulkQuotesEndpointIsVersioned(string version, string expected) =>
        Assert.Equal(expected, ThetaDataEndpoints.BulkOptionQuotes(version));

    [Theory]
    [InlineData("v2", "/v2/hist/index/price")]
    [InlineData("v3", "/v3/index/history/price")]
    public void IndexPriceEndpointIsVersioned(string version, string expected) =>
        Assert.Equal(expected, ThetaDataEndpoints.IndexPrice(version));

    [Theory]
    [InlineData("v2", "/v2/hist/stock/ohlc")]
    [InlineData("v3", "/v3/stock/history/ohlc")]
    public void StockOhlcEndpointIsVersioned(string version, string expected) =>
        Assert.Equal(expected, ThetaDataEndpoints.StockOhlc(version));

    [Fact]
    public void AnUnknownVersionFallsBackToV2Paths()
    {
        // The check is `== "v3"`, so anything else — including a future "v4" — takes the v2
        // path rather than silently constructing a URL for a version that does not exist.
        Assert.Equal("/v2/list/expirations", ThetaDataEndpoints.Expirations("v4"));
        Assert.Equal("/v2/bulk_hist/option/quote", ThetaDataEndpoints.BulkOptionQuotes(""));
    }

    // ---------- query construction ----------

    [Fact]
    public async Task EveryRequestAsksForCsv()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.ListExpirationsAsync("SPXW");
        }

        Assert.Equal("true", QueryOf(handler.LastUri!)["use_csv"]);
    }

    [Fact]
    public async Task ListExpirationsSendsRootOnTheVersionedPath()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.ListExpirationsAsync("SPXW");
        }

        Assert.Equal("/v2/list/expirations", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPXW", QueryOf(handler.LastUri!)["root"]);
    }

    [Fact]
    public async Task DailyChainQuotesPinTheSnapshotToASingleMinute()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetDailyChainQuotesAsync(
                "SPXW", new DateTime(2024, 3, 15), new DateTime(2024, 3, 1), new DateTime(2024, 3, 4));
        }

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("/v2/bulk_hist/option/quote", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPXW", q["root"]);
        Assert.Equal("20240301", q["start_date"]);
        Assert.Equal("20240304", q["end_date"]);

        // One-minute bars bounded to a single minute: exactly one row per contract per day
        // rather than a full day of ticks that would be discarded.
        Assert.Equal("60000", q["ivl"]);
        Assert.Equal("56700000", q["start_time"]);
        Assert.Equal(q["start_time"], q["end_time"]);

        // v2 names the expiration "exp" and has no strike/right wildcards.
        Assert.Equal("20240315", q["exp"]);
        Assert.False(q.ContainsKey("expiration"));
        Assert.False(q.ContainsKey("strike"));
        Assert.False(q.ContainsKey("right"));
    }

    [Fact]
    public async Task DailyChainQuotesUseWildcardsOnV3()
    {
        var (client, handler) = Build("v3");
        using (client)
        {
            await client.GetDailyChainQuotesAsync(
                "SPXW", new DateTime(2024, 3, 15), new DateTime(2024, 3, 1), new DateTime(2024, 3, 4));
        }

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("/v3/option/history/quote", handler.LastUri!.AbsolutePath);
        Assert.Equal("20240315", q["expiration"]);
        Assert.Equal("*", q["strike"]);
        Assert.Equal("*", q["right"]);
        Assert.False(q.ContainsKey("exp"));
    }

    [Fact]
    public async Task IndexPriceSendsTheRequestedInterval()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetIndexPriceAsync("SPX", new DateTime(2024, 1, 2), new DateTime(2024, 1, 3), 60000);
        }

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("/v2/hist/index/price", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPX", q["root"]);
        Assert.Equal("20240102", q["start_date"]);
        Assert.Equal("20240103", q["end_date"]);
        Assert.Equal("60000", q["ivl"]);
    }

    [Fact]
    public async Task StockOhlcSendsTheRequestedInterval()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetStockOhlcAsync("SPY", new DateTime(2024, 1, 2), new DateTime(2024, 1, 3), 300000);
        }

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("/v2/hist/stock/ohlc", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPY", q["root"]);
        Assert.Equal("300000", q["ivl"]);
    }

    [Fact]
    public async Task ParametersAreUrlEscaped()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetAsync("/v2/list/expirations", new Dictionary<string, string> { { "a b", "c&d=e" } });
        }

        // Escaped on the wire...
        Assert.Contains("a%20b=c%26d%3De", handler.LastUri!.Query, StringComparison.Ordinal);
        // ...and round-trips back to the original value.
        Assert.Equal("c&d=e", QueryOf(handler.LastUri!)["a b"]);
    }

    [Fact]
    public async Task NullParametersStillProduceACsvRequest()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetAsync("/v2/list/expirations", null);
        }

        Assert.Equal("?use_csv=true", handler.LastUri!.Query);
    }

    [Fact]
    public async Task ANullPathIsRejected()
    {
        var (client, handler) = Build();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetAsync(null!, null));
        }

        // Rejected before anything was sent.
        Assert.Equal(0, handler.Calls);
    }

    // ---------- response handling ----------

    [Fact]
    public async Task ASuccessfulResponseIsParsedAsCsv()
    {
        var (client, _) = Build();
        using (client)
        {
            var table = await client.ListExpirationsAsync("SPX");

            Assert.Equal(1, table.Count);
            Assert.Equal("1.25", CsvTable.GetString(table.Rows[0], table.RequireColumn("bid")));
        }
    }

    [Fact]
    public async Task AFailureStatusReportsCodeAndUrl()
    {
        var (client, _) = Build(status: HttpStatusCode.InternalServerError, body: "boom");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPX"));

            Assert.Contains("500", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/v2/list/expirations", ex.Message, StringComparison.Ordinal);
            Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ALongFailureBodyIsTruncated()
    {
        var (client, _) = Build(status: HttpStatusCode.BadRequest, body: new string('x', 900));
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPX"));

            Assert.Contains(new string('x', 500) + "...", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 501), ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AFailureBodyExactlyAtTheLimitIsNotTruncated()
    {
        var (client, _) = Build(status: HttpStatusCode.BadRequest, body: new string('y', 500));
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPX"));

            Assert.DoesNotContain("...", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnEmptyFailureBodyIsReportedWithoutCrashing()
    {
        var (client, _) = Build(status: HttpStatusCode.BadGateway, body: "");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPX"));

            Assert.Contains("502", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoDataIsReportedWithATwoHundredAndItsOwnException()
    {
        // The Terminal reports several conditions with a 200 and an error body. An expiration
        // with no quotes on a date is normal and must be skippable, not retried as a failure.
        var (client, _) = Build(body: "No data for the specified request");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<ThetaDataNoDataException>(() => client.ListExpirationsAsync("SPX"));

            Assert.Contains("/v2/list/expirations", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheNoDataProbeIsCaseInsensitive()
    {
        var (client, _) = Build(body: "NO DATA FOR THE SPECIFIED REQUEST");
        using (client)
        {
            await Assert.ThrowsAsync<ThetaDataNoDataException>(() => client.ListExpirationsAsync("SPX"));
        }
    }

    [Fact]
    public async Task ANoDataMarkerLaterInTheBodyIsStillDetected()
    {
        var (client, _) = Build(body: "header\nNo data for the specified request\n");
        using (client)
        {
            await Assert.ThrowsAsync<ThetaDataNoDataException>(() => client.ListExpirationsAsync("SPX"));
        }
    }

    [Fact]
    public async Task AnUnreachableTerminalSaysSo()
    {
        var handler = new FakeHandler { ThrowOnSend = new HttpRequestException("connection refused") };
        var options = new ThetaDataOptions();
        using var client = new ThetaDataClient(options, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPX"));

        // The actionable part is that the Terminal is not running, and where it was expected.
        Assert.Contains(options.BaseAddress, ex.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // ---------- date formatting ----------

    [Theory]
    [InlineData(2024, 3, 4, "20240304")]
    [InlineData(2024, 12, 31, "20241231")]
    [InlineData(1999, 1, 1, "19990101")]
    public void DatesAreFormattedAsYyyymmdd(int y, int m, int d, string expected) =>
        Assert.Equal(expected, ThetaDataClient.Yyyymmdd(new DateTime(y, m, d)));

    [Fact]
    public void DateFormattingIgnoresTheTimeComponent() =>
        Assert.Equal("20240304", ThetaDataClient.Yyyymmdd(new DateTime(2024, 3, 4, 23, 59, 59)));
}
