using System.Net;
using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// The parts of the ThetaData wire format that only a live Terminal revealed: the v3 response
/// shape, quoted fields, and the boundaries of the formatters that build a request.
/// </summary>
/// <remarks>
/// Everything asserted here was wrong on the first contact with a real Terminal, so the
/// assertions are literal rather than structural. A mock cannot discover a renamed parameter,
/// but it can stop a known one from drifting back.
/// </remarks>
public class ThetaDataWireFormatTests
{
    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static Dictionary<string, string> QueryOf(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));

    // ---------- request construction ----------

    [Fact]
    public async Task ASingleContractRequestNamesEveryParameter()
    {
        var handler = new CapturingHandler("symbol,bid\n\"SPXW\",1.0\n");
        using var client = new ThetaDataClient(new ThetaDataOptions(), new HttpClient(handler));

        await client.GetContractQuotesAsync(
            "SPXW", new DateTime(2024, 3, 15), 5000.0, OptionRightCode.Call,
            new DateTime(2024, 3, 1), new DateTime(2024, 3, 4), TimeSpan.FromMinutes(5));

        var q = QueryOf(handler.LastUri!);
        Assert.Equal("SPXW", q["symbol"]);
        Assert.Equal("2024-03-15", q["expiration"]);
        Assert.Equal("2024-03-01", q["start_date"]);
        Assert.Equal("2024-03-04", q["end_date"]);
        Assert.Equal("5m", q["interval"]);
    }

    // ---------- formatter boundaries ----------

    [Fact]
    public void FormatterFailuresNameTheParameterAndTheReason()
    {
        var interval = Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.FormatInterval(TimeSpan.Zero));
        Assert.Equal("interval", interval.ParamName);
        Assert.Contains("must be positive", interval.Message, StringComparison.Ordinal);

        var subSecond = Assert.Throws<ArgumentOutOfRangeException>(
            () => ThetaDataClient.FormatInterval(TimeSpan.FromMilliseconds(1500)));
        Assert.Contains("Sub-second", subSecond.Message, StringComparison.Ordinal);

        var time = Assert.Throws<ArgumentOutOfRangeException>(() => ThetaDataClient.TimeOfDay(TimeSpan.FromDays(2)));
        Assert.Equal("timeOfDay", time.ParamName);
        Assert.Contains("within one day", time.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIntervalOfExactlyOneDayIsAccepted() =>
        // The guard is `>= one day` on times of day, not on intervals: a 24-hour sampling
        // interval is legitimate and renders as hours.
        Assert.Equal("24h", ThetaDataClient.FormatInterval(TimeSpan.FromDays(1)));

    [Fact]
    public void TheLastInstantOfADayIsStillATimeOfDay() =>
        Assert.Equal("23:59:59", ThetaDataClient.TimeOfDay(new TimeSpan(23, 59, 59)));

    // ---------- error body truncation ----------

    [Fact]
    public async Task AFailureBodyExactlyAtTheLimitIsNotTruncated()
    {
        var handler = new FixedStatusHandler(HttpStatusCode.BadRequest, new string('y', 500));
        using var client = new ThetaDataClient(new ThetaDataOptions(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListExpirationsAsync("SPXW"));

        Assert.DoesNotContain("...", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionRejectionBodyIsAlsoTruncated()
    {
        var handler = new FixedStatusHandler(HttpStatusCode.Gone, new string('z', 900));
        using var client = new ThetaDataClient(new ThetaDataOptions(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ThetaDataVersionException>(() => client.ListExpirationsAsync("SPXW"));

        Assert.Contains(new string('z', 300) + "...", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 301), ex.Message, StringComparison.Ordinal);
    }

    private sealed class FixedStatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    [Fact]
    public void DisposingClosesTheUnderlyingClient()
    {
        var http = new HttpClient(new CapturingHandler("a\n1\n"));
        var client = new ThetaDataClient(new ThetaDataOptions(), http);

        client.Dispose();

        // A disposed HttpClient refuses further work, which is how the disposal is observable.
        Assert.ThrowsAny<Exception>(() => http.GetAsync("/x").GetAwaiter().GetResult());
    }

    // ---------- quoted csv ----------

    [Fact]
    public void QuotesAreStrippedFromValuesAndHeaders()
    {
        // v3 quotes its string fields and leaves numerics bare, in the same response.
        var table = CsvTable.Parse("\"symbol\",strike,\"right\"\n\"SPXW\",5000.000,\"CALL\"\n");

        Assert.True(table.HasColumn("symbol"));
        Assert.True(table.HasColumn("right"));
        Assert.Equal("SPXW", CsvTable.GetString(table.Rows[0], table.RequireColumn("symbol")));
        Assert.Equal("CALL", CsvTable.GetString(table.Rows[0], table.RequireColumn("right")));
        Assert.Equal(5000.0, CsvTable.GetDouble(table.Rows[0], table.RequireColumn("strike")), 9);
    }

    [Fact]
    public void AQuotedNumberStillParses() =>
        Assert.Equal(1.25, CsvTable.GetDouble(["\"1.25\""], 0), 12);

    [Fact]
    public void ALoneQuoteCharacterIsLeftAlone()
    {
        // Two characters are needed to be a quoted field; a single quote is content, and
        // stripping it would silently produce an empty value.
        Assert.Equal("\"", CsvTable.GetString(["\""], 0));
        Assert.Equal("", CsvTable.GetString(["\"\""], 0));
    }

    [Fact]
    public void UnquotedValuesAreUnaffected() =>
        Assert.Equal("SPXW", CsvTable.GetString([" SPXW "], 0));

    [Fact]
    public void AMissingColumnListsBothWhatWasWantedAndWhatArrived()
    {
        var table = CsvTable.Parse("\"symbol\",bid\n\"SPXW\",1.0\n");

        var ex = Assert.Throws<InvalidOperationException>(() => table.RequireColumn("strike", "strike_price"));

        Assert.Contains("strike_price", ex.Message, StringComparison.Ordinal);
        Assert.Contains("symbol", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bid", ex.Message, StringComparison.Ordinal);
    }

    // ---------- the v3 response shape ----------

    private static CsvTable V3Chain(params string[] rows) =>
        CsvTable.Parse("symbol,expiration,strike,right,timestamp,bid,ask\n" + string.Join("\n", rows) + "\n");

    private static string V3Row(string timestamp, double strike, string right, double bid, double ask) =>
        $"\"SPXW\",\"2024-03-15\",{strike:F3},\"{right}\",{timestamp},{bid},{ask}";

    [Fact]
    public void TheV3TimestampCarriesBothTheDateAndTheTimeOfDay()
    {
        var slices = new ThetaDataChainLoader().Parse(
            V3Chain(V3Row("2024-03-04T15:45:00.000", 5000.0, "CALL", 1.0, 1.2)), "SPXW", new DateTime(2024, 3, 15));

        Assert.Single(slices);
        // One column, not a date plus a separate milliseconds-of-day.
        Assert.Equal(new DateTime(2024, 3, 4, 15, 45, 0), slices[0].ObservedAt);
    }

    [Fact]
    public void TheV3TimestampTakesPrecedenceOverASnapshotDefault()
    {
        var loader = new ThetaDataChainLoader(new ThetaDataOptions { SnapshotTimeOfDay = new TimeSpan(9, 30, 0) });

        var slices = loader.Parse(
            V3Chain(V3Row("2024-03-04T15:45:00.000", 5000.0, "CALL", 1.0, 1.2)), "SPXW", new DateTime(2024, 3, 15));

        // The row says when it was observed; the configured snapshot is only a fallback.
        Assert.Equal(new DateTime(2024, 3, 4, 15, 45, 0), slices[0].ObservedAt);
    }

    [Fact]
    public void AQuotedRightParsesAsItsSide()
    {
        var slices = new ThetaDataChainLoader().Parse(
            V3Chain(
                V3Row("2024-03-04T15:45:00.000", 5000.0, "CALL", 1.0, 1.2),
                V3Row("2024-03-04T15:45:00.000", 5000.0, "PUT", 2.0, 2.2)),
            "SPXW", new DateTime(2024, 3, 15));

        // Without quote stripping the first character is a quote, which is neither side.
        Assert.Equal(OptionRight.Call, slices[0].Quotes[0].Right);
        Assert.Equal(OptionRight.Put, slices[0].Quotes[1].Right);
    }

    [Fact]
    public void V3RowsAreGroupedByTheirTimestampDate()
    {
        var slices = new ThetaDataChainLoader().Parse(
            V3Chain(
                V3Row("2024-03-04T15:45:00.000", 5000.0, "CALL", 1.0, 1.2),
                V3Row("2024-03-05T15:45:00.000", 5000.0, "CALL", 1.1, 1.3)),
            "SPXW", new DateTime(2024, 3, 15));

        Assert.Equal(2, slices.Count);
        Assert.Equal(new DateTime(2024, 3, 4), slices[0].ObservedAt.Date);
        Assert.Equal(new DateTime(2024, 3, 5), slices[1].ObservedAt.Date);
    }

    [Fact]
    public void AnUnparseableTimestampNamesTheOffendingValue()
    {
        var table = V3Chain(V3Row("not-a-timestamp", 5000.0, "CALL", 1.0, 1.2));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ThetaDataChainLoader().Parse(table, "SPXW", new DateTime(2024, 3, 15)));

        Assert.Contains("not-a-timestamp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timestamp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettlementSourceDefaultsWhenNotSupplied()
    {
        var slices = new ThetaDataChainLoader(new ThetaDataOptions())
            .Parse(V3Chain(V3Row("2024-03-04T15:45:00.000", 5000.0, "CALL", 1.0, 1.2)), "SPX",
                new DateTime(2024, 3, 15));

        // The default settlement table knows SPX settles in the morning.
        Assert.Equal(new DateTime(2024, 3, 15, 9, 30, 0), slices[0].SettlesAt);
    }

    // ---------- strike-scale guard bounds ----------

    [Theory]
    [InlineData(5000.0, true)]    // squarely inside
    [InlineData(2400.0, true)]    // just above half the lowest strike (4800 * 0.5)
    [InlineData(10_000.0, true)]  // just below twice the highest (5200 * 2)
    [InlineData(2000.0, false)]
    [InlineData(20_000.0, false)]
    public void TheStrikeScaleGuardIsBoundedByTheChainItself(double expectedLevel, bool passes)
    {
        var table = V3Chain(
            V3Row("2024-03-04T15:45:00.000", 4800.0, "PUT", 1.0, 1.2),
            V3Row("2024-03-04T15:45:00.000", 5200.0, "CALL", 1.0, 1.2));

        var parse = () => new ThetaDataChainLoader().Parse(table, "SPXW", new DateTime(2024, 3, 15), expectedLevel);

        if (passes) Assert.Single(parse());
        else Assert.Throws<InvalidOperationException>(parse);
    }

    [Fact]
    public void TheStrikeScaleGuardReportsTheRangeItSaw()
    {
        var table = V3Chain(
            V3Row("2024-03-04T15:45:00.000", 4800.0, "PUT", 1.0, 1.2),
            V3Row("2024-03-04T15:45:00.000", 5200.0, "CALL", 1.0, 1.2));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ThetaDataChainLoader().Parse(table, "SPXW", new DateTime(2024, 3, 15), 100.0));

        // The lowest and highest strike actually seen, so the mismatch is diagnosable.
        Assert.Contains("4800.00", ex.Message, StringComparison.Ordinal);
        Assert.Contains("5200.00", ex.Message, StringComparison.Ordinal);
        Assert.Contains("100.00", ex.Message, StringComparison.Ordinal);
    }

    // ---------- expiration selection ----------

    [Fact]
    public void SelectionDeduplicatesAndOrdersBeforeChoosing()
    {
        var asOf = new DateTime(2024, 3, 4);
        DateTime[] available =
        [
            asOf.AddDays(35), asOf.AddDays(35), asOf.AddDays(25).AddHours(9), asOf.AddDays(25),
        ];

        var selected = ThetaDataChainLoader.SelectBracketingExpirations(available, asOf);

        Assert.Equal([asOf.AddDays(25), asOf.AddDays(35)], selected);
    }

    [Fact]
    public void ParseRightFailuresNameTheValue()
    {
        var table = V3Chain(V3Row("2024-03-04T15:45:00.000", 5000.0, "XYZ", 1.0, 1.2));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ThetaDataChainLoader().Parse(table, "SPXW", new DateTime(2024, 3, 15)));

        Assert.Contains("XYZ", ex.Message, StringComparison.Ordinal);
    }
}
