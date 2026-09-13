using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.EarningsStudy.Closes;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.EarningsStudy.Tests.Closes;

/// <summary>
/// <see cref="BarsFetcher"/> against a canned <see cref="HttpMessageHandler"/> — no gateway, no TWS,
/// per the outcome mapping <c>IbkrGatewayClient</c> already implements and this class consumes.
/// </summary>
public sealed class BarsFetcherTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"c1-closes-{Guid.NewGuid():N}");
    private readonly List<string> _logLines = [];

    private StudyPaths Paths => new(_dataDirectory);

    private static readonly UniverseRow BrkB = new(
        "BRK.B", "BERKSHIRE HATHAWAY INC CL B", 1067983, "BRK.B", "Berkshire Hathaway Inc", "NYSE American", true, null, null);

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private BarsFetcher FetcherFor(
        HttpMessageHandler handler, string duration = "5 Y", Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var gateway = new IbkrGatewayClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") },
            NullLogger<IbkrGatewayClient>.Instance);
        return new BarsFetcher(gateway, Paths, duration, line => _logLines.Add(line), delay);
    }

    private string CsvPath(string symbol) => Path.Combine(Paths.RawBarsDirectory, $"{symbol}.csv");
    private string MarkerPath(string symbol) => Path.Combine(Paths.RawBarsDirectory, $"{symbol}.done");

    private static (Func<TimeSpan, CancellationToken, Task> Delay, List<TimeSpan> Requested) NoOpDelay()
    {
        var requested = new List<TimeSpan>();
        return ((ts, _) => { lock (requested) requested.Add(ts); return Task.CompletedTask; }, requested);
    }

    private static string BarsJson(bool hasData, params (string Date, decimal Close)[] bars)
    {
        var items = bars.Select(b =>
            $$"""{"timestamp":null,"tradingDate":"{{b.Date}}","open":{{b.Close}},"high":{{b.Close}},"low":{{b.Close}},"close":{{b.Close}},"volume":100,"count":1,"wap":{{b.Close}}}""");
        return $$"""{"bars":[{{string.Join(",", items)}}],"hasData":{{(hasData ? "true" : "false")}}}""";
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is { } ra)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        }
        return response;
    }

    /// <summary>Answers every call the same way, and counts how many calls it received.</summary>
    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _bodies = [];
        private int _callCount;

        public int CallCount { get { lock (_gate) return _callCount; } }
        public IReadOnlyList<string> RequestBodies { get { lock (_gate) return [.. _bodies]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            int call;
            lock (_gate)
            {
                _bodies.Add(body);
                call = ++_callCount;
            }

            return respond(call, request);
        }
    }

    // ---- outcome mapping -------------------------------------------------------------------------

    [Fact]
    public async Task Ok_response_writes_bars_csv_and_ok_marker_atomically()
    {
        var handler = new ScriptedHandler((_, _) =>
            JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-29", 10m), ("2025-12-30", 11m), ("2025-12-31", 12m))));

        var outcome = await FetcherFor(handler).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Fetched, outcome);
        Assert.Equal(1, handler.CallCount);

        var bars = CsvFile.Read<BarRow>(CsvPath("BRK.B"));
        Assert.Equal(3, bars.Count);
        Assert.Equal(new DateOnly(2025, 12, 31), bars[^1].TradingDate);
        Assert.Equal(12m, bars[^1].Close);

        var marker = File.ReadAllText(MarkerPath("BRK.B"));
        Assert.True(BarsMarker.IsOk(marker));
        Assert.Contains("bar_count=3", marker, StringComparison.Ordinal);
        Assert.Contains("last_trading_date=2025-12-31", marker, StringComparison.Ordinal);

        // No leftover temp files from the atomic write.
        Assert.DoesNotContain(Directory.GetFiles(Paths.RawBarsDirectory), f => f.Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirmed_empty_writes_empty_marker_and_no_csv()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, BarsJson(false)));

        var outcome = await FetcherFor(handler).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Empty, outcome);
        Assert.False(File.Exists(CsvPath("BRK.B")));
        Assert.Equal("empty", BarsMarker.ReadStatus(File.ReadAllText(MarkerPath("BRK.B"))));
    }

    [Fact]
    public async Task Rejected_400_writes_rejected_marker_with_detail_and_does_not_retry()
    {
        var handler = new ScriptedHandler((_, _) =>
            JsonResponse(HttpStatusCode.BadRequest, """{"detail":"No security definition has been found for the request"}"""));

        var outcome = await FetcherFor(handler).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Rejected, outcome);
        Assert.Equal(1, handler.CallCount); // permanent: retrying cannot help, so it must not retry.
        Assert.False(File.Exists(CsvPath("BRK.B")));

        var marker = File.ReadAllText(MarkerPath("BRK.B"));
        Assert.StartsWith("rejected: No security definition has been found for the request", marker, StringComparison.Ordinal);
        Assert.Equal("rejected", BarsMarker.ReadStatus(marker));
    }

    [Fact]
    public async Task Paced_429_retries_the_same_request_and_does_not_count_as_a_failure()
    {
        var (delay, requested) = NoOpDelay();
        var handler = new ScriptedHandler((call, _) => call switch
        {
            1 => JsonResponse(HttpStatusCode.TooManyRequests, """{"detail":"pacing budget exhausted"}""", TimeSpan.FromSeconds(2)),
            _ => JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-31", 12m))),
        });

        var outcome = await FetcherFor(handler, delay: delay).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Fetched, outcome);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(2)], requested); // slept exactly the Retry-After it was given, once.
        Assert.True(File.Exists(MarkerPath("BRK.B")));
    }

    /// <summary>
    /// The claim "a paced answer is never counted as a failure" is only meaningfully tested by
    /// pacing MORE times than the transient budget allows and still succeeding — a single 429 before
    /// success would pass even if pacing quietly shared the transient counter, since one attempt is
    /// always inside that budget too.
    /// </summary>
    [Fact]
    public async Task Sustained_pacing_beyond_the_transient_retry_bound_still_eventually_succeeds()
    {
        var (delay, _) = NoOpDelay();
        var pacedCalls = BarsFetcher.MaxTransientAttempts + 3;
        var handler = new ScriptedHandler((call, _) => call <= pacedCalls
            ? JsonResponse(HttpStatusCode.TooManyRequests, """{"detail":"pacing budget exhausted"}""", TimeSpan.FromSeconds(1))
            : JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-31", 1m))));

        var outcome = await FetcherFor(handler, delay: delay).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Fetched, outcome);
        Assert.Equal(pacedCalls + 1, handler.CallCount);
    }

    [Fact]
    public async Task NotConnected_503_exhausts_bounded_retries_and_leaves_no_marker()
    {
        var (delay, _) = NoOpDelay();
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.ServiceUnavailable, """{"detail":"not connected to TWS"}"""));

        var outcome = await FetcherFor(handler, delay: delay).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Failed, outcome);
        Assert.Equal(BarsFetcher.MaxTransientAttempts + 1, handler.CallCount); // the initial attempt plus every retry.
        Assert.False(File.Exists(MarkerPath("BRK.B")));
        Assert.False(File.Exists(CsvPath("BRK.B")));
    }

    [Fact]
    public async Task Gateway_timeout_504_is_treated_the_same_as_other_transient_failures()
    {
        var (delay, _) = NoOpDelay();
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.GatewayTimeout, """{"detail":"TWS did not answer in time"}"""));

        var outcome = await FetcherFor(handler, delay: delay).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Failed, outcome);
        Assert.Equal(BarsFetcher.MaxTransientAttempts + 1, handler.CallCount);
        Assert.False(File.Exists(MarkerPath("BRK.B")));
    }

    [Fact]
    public async Task Network_level_failure_is_bounded_and_leaves_no_marker()
    {
        var (delay, _) = NoOpDelay();
        var handler = new ThrowingHandler(() => new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        var outcome = await FetcherFor(handler, delay: delay).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Failed, outcome);
        Assert.False(File.Exists(MarkerPath("BRK.B")));
    }

    private sealed class ThrowingHandler(Func<Exception> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(factory());
    }

    // ---- resume / atomicity ----------------------------------------------------------------------

    [Fact]
    public async Task Existing_marker_skips_without_a_network_call()
    {
        Directory.CreateDirectory(Paths.RawBarsDirectory);
        File.WriteAllText(MarkerPath("BRK.B"), BarsMarker.BuildOk(3, new DateOnly(2025, 12, 31), "BRK B", "AMEX"));
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("must not be called when a marker already exists"));

        var outcome = await FetcherFor(handler).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Skipped, outcome);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// The negative claim WP4 makes: "a rerun adds nothing" only holds if a bars file with no
    /// marker is never trusted. Pre-seed a stale, partial-looking CSV with no marker and prove the
    /// fetch still runs and overwrites it — this is the scenario LESSONS.md #2 calls for reintroducing
    /// the defect against: checking <c>File.Exists(csvPath)</c> instead of the marker made this fail.
    /// </summary>
    [Fact]
    public async Task A_bars_file_without_a_marker_is_treated_as_partial_and_refetched()
    {
        Directory.CreateDirectory(Paths.RawBarsDirectory);
        CsvFile.Write(CsvPath("BRK.B"), new[] { new BarRow(new DateOnly(1999, 1, 1), 1m, 1m, 1m, 1m, 1m) }); // stale/partial, no marker
        var handler = new ScriptedHandler((_, _) =>
            JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-31", 42m))));

        var outcome = await FetcherFor(handler).FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Fetched, outcome);
        Assert.Equal(1, handler.CallCount); // it WAS re-fetched, not skipped.

        var bars = CsvFile.Read<BarRow>(CsvPath("BRK.B"));
        var bar = Assert.Single(bars);
        Assert.Equal(new DateOnly(2025, 12, 31), bar.TradingDate); // the stale 1999 row is gone, not merged.
        Assert.Equal(42m, bar.Close);
    }

    // ---- symbol / exchange mapping on the wire ----------------------------------------------------

    [Fact]
    public async Task Sends_the_mapped_symbol_and_primary_exchange_and_records_them_in_the_marker()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-31", 500m))));

        var outcome = await FetcherFor(handler, duration: "3 Y").FetchOneAsync(BrkB, CancellationToken.None);

        Assert.Equal(FetchOutcome.Fetched, outcome);
        var sent = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"symbol\":\"BRK B\"", sent, StringComparison.Ordinal); // dot -> space
        Assert.Contains("\"primaryExchange\":\"AMEX\"", sent, StringComparison.Ordinal); // NYSE American -> AMEX
        Assert.Contains("\"secType\":\"STK\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"exchange\":\"SMART\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"currency\":\"USD\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"duration\":\"3 Y\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"barSize\":\"1 day\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"whatToShow\":\"TRADES\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"useRth\":true", sent, StringComparison.Ordinal);
        Assert.Contains("\"endDateTime\":null", sent, StringComparison.Ordinal);

        var marker = File.ReadAllText(MarkerPath("BRK.B"));
        Assert.Contains("symbol=BRK B", marker, StringComparison.Ordinal);
        Assert.Contains("exchange=AMEX", marker, StringComparison.Ordinal);
    }

    // ---- concurrency and aggregation ---------------------------------------------------------------

    [Fact]
    public async Task FetchAllAsync_runs_symbols_concurrently_up_to_the_requested_degree()
    {
        var inFlight = 0;
        var maxObserved = 0;
        var gate = new object();

        // Every request parks until three are in flight together, so overlap is forced rather than
        // hoped for: a fetcher that ran one symbol at a time could never fill the barrier, and the
        // timed-out wait answers a permanent 400 — which the fetcher records without retrying — so
        // the assertions below fail within seconds on a loaded machine and an idle one alike. The
        // earlier version slept 50 ms and asserted that overlap had happened to occur, which failed
        // once under full-suite load; an exception here instead would be retried as transient.
        using var rendezvous = new Barrier(3);

        var handler = new ScriptedHandler((_, _) =>
        {
            lock (gate)
            {
                inFlight++;
                maxObserved = Math.Max(maxObserved, inFlight);
            }

            if (!rendezvous.SignalAndWait(TimeSpan.FromSeconds(3)))
            {
                lock (gate) inFlight--;
                return JsonResponse(HttpStatusCode.BadRequest, """{"detail":"fewer than three requests were ever in flight together"}""");
            }

            lock (gate)
            {
                inFlight--;
            }

            return JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-31", 1m)));
        });

        var universe = Enumerable.Range(0, 6)
            .Select(i => new UniverseRow($"SYM{i}", $"SYM{i} INC", i, $"SYM{i}", $"SYM{i} INC", "NYSE", true, null, null))
            .ToList();

        var summary = await FetcherFor(handler).FetchAllAsync(universe, concurrency: 3, CancellationToken.None);

        Assert.Equal(6, summary.Fetched);
        Assert.Equal(6, summary.Total);
        Assert.Equal(3, maxObserved);
    }

    [Fact]
    public async Task FetchAllAsync_logs_progress_at_the_configured_interval()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, BarsJson(true, ("2025-12-31", 1m))));
        var universe = Enumerable.Range(0, BarsFetcher.ProgressLogInterval + 1)
            .Select(i => new UniverseRow($"SYM{i}", $"SYM{i} INC", i, $"SYM{i}", $"SYM{i} INC", "NYSE", true, null, null))
            .ToList();

        await FetcherFor(handler).FetchAllAsync(universe, concurrency: 1, CancellationToken.None);

        Assert.Contains(_logLines, line => line.Contains($"{BarsFetcher.ProgressLogInterval}/{universe.Count}", StringComparison.Ordinal));
    }
}
