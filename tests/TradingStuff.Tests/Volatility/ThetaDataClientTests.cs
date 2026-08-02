using System.Net;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Exercises the Theta Terminal request path without a Terminal.
/// </summary>
/// <remarks>
/// The client takes an <see cref="HttpClient"/>, so everything up to the socket — endpoint
/// selection, parameter names, encoding, status handling, and the Terminal's habit of
/// reporting failures with a 200 — is reachable from a fake handler.
/// <para>
/// The parameter names are the contract and are asserted literally. That is not
/// over-specification: this client was written against API v2, and when it first met a live
/// v3 Terminal every request was rejected because <c>root</c> had become <c>symbol</c> and
/// <c>use_csv</c> had become <c>format</c>. The expectations below are what a live Terminal
/// was observed to accept.
/// </para>
/// </remarks>
public class ThetaDataClientTests
{
    private const string CsvBody = "symbol,bid,ask\n\"SPXW\",1.25,1.35\n";

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
        HttpStatusCode status = HttpStatusCode.OK, string body = CsvBody)
    {
        var handler = new FakeHandler(status, body);
        return (new ThetaDataClient(new ThetaDataOptions(), new HttpClient(handler)), handler);
    }

    private static Dictionary<string, string> QueryOf(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));

    // ---------- defaults ----------

    [Fact]
    public void DefaultsTargetTheLocalV3Terminal()
    {
        var options = new ThetaDataOptions();

        // 25503 is the v3 REST port; the v2 Terminal served 25510.
        Assert.Equal("http://127.0.0.1:25503", options.BaseAddress);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Timeout);

        // v3 quotes strikes in dollars. The v2 feed used tenths of a cent and needed 1000,
        // which against v3 would put every strike three orders of magnitude out.
        Assert.Equal(1.0, options.StrikeDivisor);

        // 15:45, late enough to be representative but before the closing auction widens quotes.
        Assert.Equal(new TimeSpan(15, 45, 0), options.SnapshotTimeOfDay);
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

        Assert.Equal("http://127.0.0.1:25503", client.Options.BaseAddress);
    }

    // ---------- endpoints ----------

    [Fact]
    public void EndpointPathsAreTheV3Routes()
    {
        Assert.Equal("/v3/option/list/expirations", ThetaDataEndpoints.Expirations);
        Assert.Equal("/v3/option/list/strikes", ThetaDataEndpoints.Strikes);
        Assert.Equal("/v3/option/history/quote", ThetaDataEndpoints.OptionQuotes);
        Assert.Equal("/v3/option/history/eod", ThetaDataEndpoints.OptionEndOfDay);
        Assert.Equal("/v3/index/history/price", ThetaDataEndpoints.IndexPrice);
        Assert.Equal("/v3/stock/history/ohlc", ThetaDataEndpoints.StockOhlc);
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

        // v3 spells this `format`; v2 spelled it `use_csv`.
        Assert.Equal("csv", QueryOf(handler.LastUri!)["format"]);
        Assert.False(QueryOf(handler.LastUri!).ContainsKey("use_csv"));
    }

    [Fact]
    public async Task ListExpirationsSendsTheSymbol()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.ListExpirationsAsync("SPXW");
        }

        Assert.Equal("/v3/option/list/expirations", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPXW", QueryOf(handler.LastUri!)["symbol"]);
        // `root` is the v2 spelling and is rejected outright by v3.
        Assert.False(QueryOf(handler.LastUri!).ContainsKey("root"));
    }

    [Fact]
    public async Task ListStrikesSendsSymbolAndExpiration()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.ListStrikesAsync("SPXW", new DateTime(2024, 3, 15));
        }

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("/v3/option/list/strikes", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPXW", q["symbol"]);
        Assert.Equal("2024-03-15", q["expiration"]);
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
        Assert.Equal("/v3/option/history/quote", handler.LastUri!.AbsolutePath);
        Assert.Equal("SPXW", q["symbol"]);
        Assert.Equal("2024-03-15", q["expiration"]);
        Assert.Equal("2024-03-01", q["start_date"]);
        Assert.Equal("2024-03-04", q["end_date"]);

        // One-minute bars bounded to a single minute: exactly one row per contract per day
        // rather than a full session of ticks that would be discarded.
        Assert.Equal("1m", q["interval"]);
        Assert.Equal("15:45:00", q["start_time"]);
        Assert.Equal(q["start_time"], q["end_time"]);
    }

    [Fact]
    public async Task TheBulkFormOmitsStrikeAndRightRatherThanWildcardingThem()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetDailyChainQuotesAsync(
                "SPXW", new DateTime(2024, 3, 15), new DateTime(2024, 3, 1), new DateTime(2024, 3, 4));
        }

        // A live v3 Terminal answers `right=*` with `400 Invalid right: *`. Absence is how
        // the whole chain is requested.
        var q = QueryOf(handler.LastUri!);
        Assert.False(q.ContainsKey("strike"));
        Assert.False(q.ContainsKey("right"));
    }

    [Theory]
    [InlineData(OptionRightCode.Call, "C")]
    [InlineData(OptionRightCode.Put, "P")]
    public async Task ASingleContractRequestNamesItsStrikeAndRight(OptionRightCode right, string expected)
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetContractQuotesAsync(
                "SPXW", new DateTime(2024, 3, 15), 5000.0, right,
                new DateTime(2024, 3, 4), new DateTime(2024, 3, 4), TimeSpan.FromHours(1));
        }

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("5000", q["strike"]);
        Assert.Equal(expected, q["right"]);
        Assert.Equal("1h", q["interval"]);
    }

    [Fact]
    public async Task AFractionalStrikeIsSentUnrounded()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetContractQuotesAsync(
                "SPXW", new DateTime(2024, 3, 15), 5002.5, OptionRightCode.Call,
                new DateTime(2024, 3, 4), new DateTime(2024, 3, 4), TimeSpan.FromMinutes(1));
        }

        Assert.Equal("5002.5", QueryOf(handler.LastUri!)["strike"]);
    }

    [Fact]
    public async Task IndexAndStockHistorySendSymbolDatesAndInterval()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetIndexPriceAsync("SPX", new DateTime(2024, 1, 2), new DateTime(2024, 1, 3), TimeSpan.FromMinutes(1));

            var q = QueryOf(handler.LastUri!);
            Assert.Equal("/v3/index/history/price", handler.LastUri!.AbsolutePath);
            Assert.Equal("SPX", q["symbol"]);
            Assert.Equal("2024-01-02", q["start_date"]);
            Assert.Equal("2024-01-03", q["end_date"]);
            Assert.Equal("1m", q["interval"]);

            await client.GetStockOhlcAsync("SPY", new DateTime(2024, 1, 2), new DateTime(2024, 1, 3), TimeSpan.FromMinutes(5));

            Assert.Equal("/v3/stock/history/ohlc", handler.LastUri!.AbsolutePath);
            Assert.Equal("5m", QueryOf(handler.LastUri!)["interval"]);
        }
    }

    [Fact]
    public async Task ParametersAreUrlEscaped()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetAsync("/v3/option/list/expirations", new Dictionary<string, string> { { "a b", "c&d=e" } });
        }

        Assert.Contains("a%20b=c%26d%3De", handler.LastUri!.Query, StringComparison.Ordinal);
        Assert.Equal("c&d=e", QueryOf(handler.LastUri!)["a b"]);
    }

    [Fact]
    public async Task NullParametersStillProduceACsvRequest()
    {
        var (client, handler) = Build();
        using (client)
        {
            await client.GetAsync("/v3/option/list/expirations", null);
        }

        Assert.Equal("?format=csv", handler.LastUri!.Query);
    }

    [Fact]
    public async Task ANullPathIsRejected()
    {
        var (client, handler) = Build();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetAsync(null!, null));
        }

        Assert.Equal(0, handler.Calls);
    }

    // ---------- response handling ----------

    [Fact]
    public async Task ASuccessfulResponseIsParsedAsCsv()
    {
        var (client, _) = Build();
        using (client)
        {
            var table = await client.ListExpirationsAsync("SPXW");

            Assert.Equal(1, table.Count);
            Assert.Equal("1.25", CsvTable.GetString(table.Rows[0], table.RequireColumn("bid")));
            // v3 quotes its string fields; the quotes must not survive into a value.
            Assert.Equal("SPXW", CsvTable.GetString(table.Rows[0], table.RequireColumn("symbol")));
        }
    }

    [Fact]
    public async Task AnOutdatedApiVersionIsReportedAsSuch()
    {
        // A v2 route against a v3 Terminal returns 410 with the renamed parameters. Nothing
        // about the arguments will fix it, so it is not an ordinary request failure.
        var (client, _) = Build(
            status: HttpStatusCode.Gone,
            body: "We have upgraded to API v3. Deprecated query parameters: root -> symbol");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<ThetaDataVersionException>(() => client.ListExpirationsAsync("SPXW"));

            Assert.Contains("outdated API version", ex.Message, StringComparison.Ordinal);
            Assert.Contains("root -> symbol", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnUncoveredSubscriptionIsReportedAsSuch()
    {
        // A property of the account, not the request: the caller should skip the endpoint
        // rather than retry or adjust it.
        var (client, _) = Build(
            status: HttpStatusCode.Forbidden,
            body: "Requesting an index endpoint requiring a value subscription, but you only have a FREE subscription.");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<ThetaDataSubscriptionException>(() =>
                client.GetIndexPriceAsync("SPX", new DateTime(2024, 1, 2), new DateTime(2024, 1, 3), TimeSpan.FromMinutes(1)));

            Assert.Contains("subscription this account does not have", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/v3/index/history/price", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AFailureStatusReportsCodeAndUrl()
    {
        var (client, _) = Build(status: HttpStatusCode.InternalServerError, body: "boom");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPXW"));

            Assert.Contains("500", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/v3/option/list/expirations", ex.Message, StringComparison.Ordinal);
            Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABadRequestIsAnOrdinaryFailure()
    {
        // 400 covers malformed arguments — an unsupported interval, a wildcard right — which
        // are the caller's to fix, unlike 410 and 403.
        var (client, _) = Build(status: HttpStatusCode.BadRequest, body: "Invalid interval: 3600000");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPXW"));

            Assert.IsNotType<ThetaDataVersionException>(ex);
            Assert.IsNotType<ThetaDataSubscriptionException>(ex);
            Assert.Contains("Invalid interval", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ALongFailureBodyIsTruncated()
    {
        var (client, _) = Build(status: HttpStatusCode.BadRequest, body: new string('x', 900));
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPXW"));

            Assert.Contains(new string('x', 500) + "...", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 501), ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnEmptyFailureBodyIsReportedWithoutCrashing()
    {
        var (client, _) = Build(status: HttpStatusCode.BadGateway, body: "");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPXW"));

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
            var ex = await Assert.ThrowsAsync<ThetaDataNoDataException>(() => client.ListExpirationsAsync("SPXW"));

            Assert.Contains("/v3/option/list/expirations", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheNoDataProbeIsCaseInsensitive()
    {
        var (client, _) = Build(body: "NO DATA FOR THE SPECIFIED REQUEST");
        using (client)
        {
            await Assert.ThrowsAsync<ThetaDataNoDataException>(() => client.ListExpirationsAsync("SPXW"));
        }
    }

    [Fact]
    public async Task AHttp472IsReportedAsNoDataNotAsAGenericFailure()
    {
        // Measured live against the Terminal 2026-08-02: /v3/option/history/quote answers a date
        // range with no trading (e.g. a weekend) with HTTP 472 and the plain-text body "No data
        // found for your request" — a third spelling this client did not originally recognize.
        // Before this test's own fix, a caller (OptionChainCoordinator included) would have seen a
        // generic InvalidOperationException and retried a legitimately-empty result as a transient
        // failure until it exhausted its attempt budget, rather than settling it once as empty.
        var (client, _) = Build(status: (HttpStatusCode)472, body: "No data found for your request");
        using (client)
        {
            var ex = await Assert.ThrowsAsync<ThetaDataNoDataException>(() =>
                client.GetContractQuotesAsync(
                    "SPXW", new DateTime(2012, 6, 8), 1050.0, OptionRightCode.Call,
                    new DateTime(2012, 6, 3), new DateTime(2012, 6, 3), TimeSpan.FromMinutes(1)));

            Assert.Contains("/v3/option/history/quote", ex.Message, StringComparison.Ordinal);
            Assert.Contains("472", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheAlternateNoDataWordingIsAlsoRecognizedOnA200()
    {
        var (client, _) = Build(body: "No data found for your request");
        using (client)
        {
            await Assert.ThrowsAsync<ThetaDataNoDataException>(() => client.ListExpirationsAsync("SPXW"));
        }
    }

    [Fact]
    public async Task AnUnreachableTerminalSaysSo()
    {
        var handler = new FakeHandler { ThrowOnSend = new HttpRequestException("connection refused") };
        var options = new ThetaDataOptions();
        using var client = new ThetaDataClient(options, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPXW"));

        Assert.Contains(options.BaseAddress, ex.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // ---------- formatting ----------

    [Theory]
    [InlineData(2024, 3, 4, "2024-03-04")]
    [InlineData(2024, 12, 31, "2024-12-31")]
    [InlineData(1999, 1, 1, "1999-01-01")]
    public void DatesAreFormattedIso(int y, int m, int d, string expected) =>
        Assert.Equal(expected, ThetaDataClient.IsoDate(new DateTime(y, m, d)));

    [Fact]
    public void DateFormattingIgnoresTheTimeComponent() =>
        Assert.Equal("2024-03-04", ThetaDataClient.IsoDate(new DateTime(2024, 3, 4, 23, 59, 59)));

    [Theory]
    [InlineData(0, 0, 0, "00:00:00")]
    [InlineData(15, 45, 0, "15:45:00")]
    [InlineData(9, 30, 5, "09:30:05")]
    [InlineData(23, 59, 59, "23:59:59")]
    public void TimesOfDayAreZeroPadded(int h, int m, int s, string expected) =>
        Assert.Equal(expected, ThetaDataClient.TimeOfDay(new TimeSpan(h, m, s)));

    [Fact]
    public void ATimeOfDayOutsideOneDayIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.TimeOfDay(TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.TimeOfDay(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(1, "1s")]
    [InlineData(30, "30s")]
    [InlineData(60, "1m")]
    [InlineData(300, "5m")]
    [InlineData(900, "15m")]
    [InlineData(3600, "1h")]
    [InlineData(7200, "2h")]
    [InlineData(5400, "90m")]   // not a whole hour, so minutes
    [InlineData(90, "90s")]     // not a whole minute, so seconds
    public void IntervalsUseTheLargestWholeUnit(int seconds, string expected) =>
        Assert.Equal(expected, ThetaDataClient.FormatInterval(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ADayLongIntervalIsStillExpressedInHours() =>
        // v3 rejects `1d`, so the formatter must never produce it.
        Assert.Equal("24h", ThetaDataClient.FormatInterval(TimeSpan.FromDays(1)));

    [Fact]
    public void ANonPositiveOrSubSecondIntervalIsRejected()
    {
        // v3 rejects `0`, and there is no sub-second form to fall back to.
        Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.FormatInterval(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.FormatInterval(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.FormatInterval(TimeSpan.FromMilliseconds(500)));
    }
}
