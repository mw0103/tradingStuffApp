using System.Net;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// A canned-response <see cref="HttpMessageHandler"/> for Theta Terminal tests, keyed by request
/// PATH rather than the full URL: <c>ThetaDataClient.GetAsync</c> builds its query string from a
/// plain <c>Dictionary&lt;string,string&gt;</c>, and matching on the exact query would make every
/// test depend on that dictionary's enumeration order rather than on what the client actually sent.
/// Each registered response is consumed once, in registration order, mirroring
/// <c>TestSupport.FakeHttpHandler</c>'s "unexpected or exhausted request throws" discipline so an
/// accidental extra network touch (a caching regression, a fallback that fires when it should not)
/// fails the test loudly instead of coincidentally passing.
/// </summary>
internal sealed class FakeThetaHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _routes = new(StringComparer.Ordinal);

    public int RequestCount { get; private set; }
    public List<string> RequestedUrls { get; } = [];

    public FakeThetaHandler On(string path, string body) => OnStatus(path, HttpStatusCode.OK, body);

    public FakeThetaHandler OnStatus(string path, HttpStatusCode status, string body)
    {
        if (!_routes.TryGetValue(path, out var queue))
        {
            _routes[path] = queue = new Queue<(HttpStatusCode, string)>();
        }
        queue.Enqueue((status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        RequestedUrls.Add(request.RequestUri!.ToString());

        var path = request.RequestUri!.AbsolutePath;
        if (_routes.TryGetValue(path, out var queue) && queue.Count > 0)
        {
            var (status, body) = queue.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }

        throw new InvalidOperationException(
            $"FakeThetaHandler: unexpected or exhausted request to '{request.RequestUri}'. " +
            $"Registered paths: {string.Join(", ", _routes.Keys)}");
    }
}
