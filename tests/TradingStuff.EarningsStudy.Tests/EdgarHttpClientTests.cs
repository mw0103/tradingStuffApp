using System.Diagnostics;
using System.Net;
using TradingStuff.EarningsStudy.Edgar;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class EdgarHttpClientTests
{
    private const string Url = "https://data.sec.gov/fake/one.json";

    [Fact]
    public async Task Sends_accept_encoding_gzip_and_the_declared_user_agent()
    {
        var handler = new FakeHttpHandler().On(Url, HttpStatusCode.OK, "{}");
        using var client = new EdgarHttpClient("Research Bot contact@example.com", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        await client.GetAsync(Url, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("gzip", request.AcceptEncoding);
        Assert.Equal("Research Bot contact@example.com", request.UserAgent);
    }

    [Fact]
    public async Task No_user_agent_is_sent_when_none_is_declared()
    {
        var handler = new FakeHttpHandler().On(Url, HttpStatusCode.OK, "{}");
        using var client = new EdgarHttpClient(null, handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        await client.GetAsync(Url, CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).UserAgent);
    }

    [Fact]
    public async Task Decompresses_a_gzip_encoded_body()
    {
        const string body = "{\"hello\":\"world\"}";
        var handler = new FakeHttpHandler().OnGzipped(Url, HttpStatusCode.OK, body);
        using var client = new EdgarHttpClient("t", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        var result = await client.GetAsync(Url, CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task Retries_503_then_succeeds()
    {
        var handler = new FakeHttpHandler()
            .On(Url, HttpStatusCode.ServiceUnavailable, "busy")
            .On(Url, HttpStatusCode.ServiceUnavailable, "busy")
            .On(Url, HttpStatusCode.OK, "finally ok");
        using var client = new EdgarHttpClient("t", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(2));

        var result = await client.GetAsync(Url, CancellationToken.None);

        Assert.Equal("finally ok", result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_429_honoring_retry_after()
    {
        var retryAfter = TimeSpan.FromMilliseconds(60);
        var handler = new FakeHttpHandler()
            .On(Url, HttpStatusCode.TooManyRequests, "slow down", retryAfter)
            .On(Url, HttpStatusCode.OK, "ok now");
        using var client = new EdgarHttpClient("t", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        var stopwatch = Stopwatch.StartNew();
        var result = await client.GetAsync(Url, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal("ok now", result);
        // Loose lower bound only (docs/LESSONS.md #11: duration is not evidence on its own upper
        // bound under CI load) -- but it must have waited roughly the declared Retry-After, not
        // retried instantly or used the (much larger) exponential base instead.
        Assert.True(stopwatch.Elapsed >= retryAfter * 0.5, $"expected a retry-after-shaped wait, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Gives_up_after_the_retry_budget_and_throws()
    {
        var handler = new FakeHttpHandler()
            .On(Url, HttpStatusCode.ServiceUnavailable, "1")
            .On(Url, HttpStatusCode.ServiceUnavailable, "2")
            .On(Url, HttpStatusCode.ServiceUnavailable, "3");
        using var client = new EdgarHttpClient("t", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1), maxRetries: 2);

        var ex = await Assert.ThrowsAsync<EdgarHttpException>(() => client.GetAsync(Url, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count); // the original attempt plus 2 retries, then give up
    }

    [Fact]
    public async Task A_404_throws_EdgarHttpException_carrying_NotFound()
    {
        var handler = new FakeHttpHandler().On(Url, HttpStatusCode.NotFound, "nope");
        using var client = new EdgarHttpClient("t", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<EdgarHttpException>(() => client.GetAsync(Url, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetCachedAsync_writes_the_body_verbatim_and_returns_it()
    {
        using var dir = new TempStudyDirectory();
        var cachePath = Path.Combine(dir.Path, "raw", "edgar", "thing.json");
        var handler = new FakeHttpHandler().On(Url, HttpStatusCode.OK, "  { \"a\" : 1 }  ");
        using var client = new EdgarHttpClient("t", handler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        var result = await client.GetCachedAsync(Url, cachePath, CancellationToken.None);

        Assert.Equal("  { \"a\" : 1 }  ", result);
        Assert.Equal("  { \"a\" : 1 }  ", await File.ReadAllTextAsync(cachePath));
    }

    [Fact]
    public async Task GetCachedAsync_second_call_never_touches_the_network()
    {
        using var dir = new TempStudyDirectory();
        var cachePath = Path.Combine(dir.Path, "raw", "edgar", "thing.json");

        var firstHandler = new FakeHttpHandler().On(Url, HttpStatusCode.OK, "cached body");
        using (var firstClient = new EdgarHttpClient("t", firstHandler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1)))
        {
            await firstClient.GetCachedAsync(Url, cachePath, CancellationToken.None);
        }

        using var secondClient = new EdgarHttpClient("t", new ThrowingHandler(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        var result = await secondClient.GetCachedAsync(Url, cachePath, CancellationToken.None);

        Assert.Equal("cached body", result);
    }

    [Fact]
    public async Task GetCachedAsync_caches_a_404_so_a_rerun_does_not_re_request_it()
    {
        using var dir = new TempStudyDirectory();
        var cachePath = Path.Combine(dir.Path, "raw", "edgar", "missing.json");

        var firstHandler = new FakeHttpHandler().On(Url, HttpStatusCode.NotFound, "nope");
        using (var firstClient = new EdgarHttpClient("t", firstHandler, TimeSpan.Zero, TimeSpan.FromMilliseconds(1)))
        {
            await Assert.ThrowsAsync<EdgarHttpException>(() => firstClient.GetCachedAsync(Url, cachePath, CancellationToken.None));
        }

        // If the marker were not written (or not checked), this would throw the ThrowingHandler's
        // "network touched" InvalidOperationException instead of the expected EdgarHttpException.
        using var secondClient = new EdgarHttpClient("t", new ThrowingHandler(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<EdgarHttpException>(() => secondClient.GetCachedAsync(Url, cachePath, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Paces_successive_requests_at_least_the_configured_spacing_apart()
    {
        var spacing = TimeSpan.FromMilliseconds(60);
        var handler = new FakeHttpHandler()
            .On("https://data.sec.gov/fake/a.json", HttpStatusCode.OK, "a")
            .On("https://data.sec.gov/fake/b.json", HttpStatusCode.OK, "b")
            .On("https://data.sec.gov/fake/c.json", HttpStatusCode.OK, "c");
        using var client = new EdgarHttpClient("t", handler, spacing, TimeSpan.FromMilliseconds(1));

        var stopwatch = Stopwatch.StartNew();
        await client.GetAsync("https://data.sec.gov/fake/a.json", CancellationToken.None);
        await client.GetAsync("https://data.sec.gov/fake/b.json", CancellationToken.None);
        await client.GetAsync("https://data.sec.gov/fake/c.json", CancellationToken.None);
        stopwatch.Stop();

        // Two gaps of `spacing`; a generous lower-bound tolerance only, per docs/LESSONS.md #11.
        Assert.True(stopwatch.Elapsed >= spacing * 2 * 0.8, $"expected >= ~{spacing.TotalMilliseconds * 2}ms, took {stopwatch.Elapsed}");
    }
}
