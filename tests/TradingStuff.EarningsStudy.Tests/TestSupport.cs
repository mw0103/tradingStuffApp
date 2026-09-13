using System.IO.Compression;
using System.Net;
using System.Text;
using TradingStuff.ResearchContracts;

namespace TradingStuff.EarningsStudy.Tests;

/// <summary>Absolute paths to files under Fixtures/, which the csproj copies beside the test DLL.</summary>
internal static class Fixture
{
    public static string Path(string relativePath) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);
    public static string Read(string relativePath) => File.ReadAllText(Path(relativePath));
}

/// <summary>
/// A canned-response <see cref="HttpMessageHandler"/> for EDGAR tests. Every test registers exactly
/// the URLs it expects; anything else — including a URL requested more times than it was
/// registered — throws immediately rather than returning a default response, so an accidental extra
/// network touch (e.g. a caching bug that re-fetches something already on disk) fails loudly instead
/// of silently passing with coincidentally-right data.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = new();

    /// <summary>Every request this handler has seen, in order, with the two headers WP1's client is
    /// required to send.</summary>
    public List<(string Url, string? AcceptEncoding, string? UserAgent)> Requests { get; } = [];

    public FakeHttpHandler On(string url, HttpStatusCode status, string body)
    {
        Enqueue(url, () => Build(status, body, gzip: false));
        return this;
    }

    public FakeHttpHandler OnGzipped(string url, HttpStatusCode status, string body)
    {
        Enqueue(url, () => Build(status, body, gzip: true));
        return this;
    }

    public FakeHttpHandler On(string url, HttpStatusCode status, string body, TimeSpan retryAfter)
    {
        Enqueue(url, () =>
        {
            var response = Build(status, body, gzip: false);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            return response;
        });
        return this;
    }

    private void Enqueue(string url, Func<HttpResponseMessage> factory)
    {
        if (!_responses.TryGetValue(url, out var queue))
        {
            _responses[url] = queue = new Queue<Func<HttpResponseMessage>>();
        }
        queue.Enqueue(factory);
    }

    private static HttpResponseMessage Build(HttpStatusCode status, string body, bool gzip)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (gzip)
        {
            using var compressed = new MemoryStream();
            using (var gzipStream = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            {
                gzipStream.Write(bytes);
            }
            bytes = compressed.ToArray();
        }
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        if (gzip) response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var acceptEncoding = request.Headers.TryGetValues("Accept-Encoding", out var ae) ? string.Join(",", ae) : null;
        var userAgent = request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(",", ua) : null;
        Requests.Add((url, acceptEncoding, userAgent));

        if (_responses.TryGetValue(url, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue()());
        }
        throw new InvalidOperationException(
            $"FakeHttpHandler: unexpected or exhausted request to '{url}'. Registered URLs: {string.Join(", ", _responses.Keys)}");
    }
}

/// <summary>Throws on every request. Used to prove a second run of a step touches no network at
/// all — a caching bug shows up as this exception rather than as a silent pass.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"network touched: {request.RequestUri} (this call should have been served from cache)");
}

/// <summary>WP1 never resolves a trading session or calendar date; every member throws so a future
/// change that starts depending on it fails loudly in tests rather than silently getting a fake
/// answer.</summary>
internal sealed class NullSessionClock : ISessionClock
{
    public TradingSession? SessionAt(string calendar, DateTimeOffset instantUtc) =>
        throw new NotSupportedException("UniverseStep/EventsStep (WP1) do not use ISessionClock.");

    public DateOnly TradingDateOf(string calendar, DateTimeOffset instantUtc) =>
        throw new NotSupportedException("UniverseStep/EventsStep (WP1) do not use ISessionClock.");

    public IReadOnlyList<TradingSession> SessionsBetween(string calendar, DateOnly from, DateOnly to) =>
        throw new NotSupportedException("UniverseStep/EventsStep (WP1) do not use ISessionClock.");
}

/// <summary>A fresh, isolated data directory under the OS temp path, deleted on disposal.</summary>
internal sealed class TempStudyDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"c1-study-{Guid.NewGuid():N}");

    public TempStudyDirectory() => Directory.CreateDirectory(Path);

    public StudyContext NewContext(TextWriter? output = null) =>
        new(new StudyPaths(Path), new NullSessionClock(), output ?? TextWriter.Null, TimeProvider.System);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
