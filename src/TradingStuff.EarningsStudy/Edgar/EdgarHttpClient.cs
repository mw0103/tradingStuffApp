using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;

namespace TradingStuff.EarningsStudy.Edgar;

/// <summary>Thrown when the SEC returns a non-success status this client does not retry. Carries the
/// status so callers can special-case 404 (expected for some CIKs) without treating it as fatal.</summary>
public sealed class EdgarHttpException(HttpStatusCode statusCode, string url)
    : Exception($"{(int)statusCode} {statusCode} fetching {url}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Url { get; } = url;
}

/// <summary>
/// Talks to sec.gov / data.sec.gov under the SEC's fair-access policy: a declared User-Agent
/// (never invented — null means "not declared", and the caller decides whether that is fatal),
/// gzip acceptance, at most ~10 requests/second (sequential, ~110ms spacing), and 429/503 retried
/// with backoff honouring <c>Retry-After</c>. Every successful (and every 404) response is cached
/// verbatim under a caller-supplied path, so a rerun that already has the file never touches the
/// network for it — this is what keeps both the fixtures-only test path and a resumed real run
/// offline for everything already fetched.
/// </summary>
public sealed class EdgarHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly TimeSpan _requestSpacing;
    private readonly TimeSpan _retryBaseDelay;
    private readonly int _maxRetries;
    private DateTimeOffset? _lastRequestUtc;

    /// <param name="userAgent">
    /// The declared User-Agent (operator + contact) per SEC policy. Null/empty means none is sent —
    /// this class never fabricates one; a real, uncached fetch without it will be refused by the SEC
    /// with 403 "Undeclared Automated Tool".
    /// </param>
    /// <param name="handler">Injected for tests so no suite ever touches the network. Null in
    /// production, which builds a real <see cref="HttpClientHandler"/>.</param>
    /// <param name="requestSpacing">Minimum gap between the START of successive network requests.
    /// Defaults to 110ms (~9/sec, under the 10 req/s ceiling). Tests pass a smaller value.</param>
    /// <param name="retryBaseDelay">Base of the exponential backoff for 429/503 without a
    /// <c>Retry-After</c> header. Defaults to 1s. Tests pass a smaller value.</param>
    public EdgarHttpClient(
        string? userAgent,
        HttpMessageHandler? handler = null,
        TimeSpan? requestSpacing = null,
        TimeSpan? retryBaseDelay = null,
        int maxRetries = 5)
    {
        _ownedHandler = handler is null ? new HttpClientHandler() : null;
        _http = new HttpClient(handler ?? _ownedHandler!, disposeHandler: false);
        if (!string.IsNullOrEmpty(userAgent))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
        _requestSpacing = requestSpacing ?? TimeSpan.FromMilliseconds(110);
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        _maxRetries = maxRetries;
    }

    /// <summary>
    /// The body at <paramref name="cachePath"/> (verbatim, from a previous fetch) if it exists — no
    /// network call at all. Otherwise fetches <paramref name="url"/>, writes the body to
    /// <paramref name="cachePath"/> and returns it. A 404 is itself cached (as a sibling marker
    /// file) so a rerun does not re-request a URL already known not to exist, and re-throws
    /// <see cref="EdgarHttpException"/> both the first time and on every cached replay.
    /// </summary>
    public async Task<string> GetCachedAsync(string url, string cachePath, CancellationToken cancellationToken)
    {
        if (File.Exists(cachePath))
        {
            return await File.ReadAllTextAsync(cachePath, cancellationToken);
        }

        var notFoundMarker = cachePath + ".404";
        if (File.Exists(notFoundMarker))
        {
            throw new EdgarHttpException(HttpStatusCode.NotFound, url);
        }

        try
        {
            var body = await GetAsync(url, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            // Write-then-move so a killed process never leaves a half-written file that a later
            // run's File.Exists check would trust as a complete, verbatim cache entry.
            var tmp = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(tmp, body, cancellationToken);
            File.Move(tmp, cachePath, overwrite: true);
            return body;
        }
        catch (EdgarHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(notFoundMarker, $"404 at {DateTimeOffset.UtcNow:O}: {url}\n", cancellationToken);
            throw;
        }
    }

    /// <summary>Fetches <paramref name="url"/> with pacing and 429/503 retry. Does not consult or
    /// write any cache — most callers want <see cref="GetCachedAsync"/> instead.</summary>
    public async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            await PaceAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Requested explicitly rather than left to automatic decompression: an injected fake
            // handler in tests never runs the real decompression pipeline, so the header would
            // otherwise silently vanish from what tests observe. Decoding is symmetric below.
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt >= _maxRetries)
                {
                    throw new EdgarHttpException(response.StatusCode, url);
                }
                await Task.Delay(RetryDelay(response.Headers, attempt), cancellationToken);
                attempt++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EdgarHttpException(response.StatusCode, url);
            }

            return await ReadBodyAsync(response, cancellationToken);
        }
    }

    private TimeSpan RetryDelay(HttpResponseHeaders headers, int attempt)
    {
        if (headers.RetryAfter?.Delta is { } delta) return delta;
        if (headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return TimeSpan.FromTicks(_retryBaseDelay.Ticks * (1L << attempt));
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (_lastRequestUtc is { } last)
        {
            var wait = _requestSpacing - (DateTimeOffset.UtcNow - last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        }
        _lastRequestUtc = DateTimeOffset.UtcNow;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.Content.Headers.ContentEncoding.Any(e => string.Equals(e, "gzip", StringComparison.OrdinalIgnoreCase)))
        {
            using var compressed = new MemoryStream(bytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await gzip.CopyToAsync(output, cancellationToken);
            bytes = output.ToArray();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
    }
}
