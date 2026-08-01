using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.Tests;

/// <summary>
/// How <see cref="IbkrGatewayClient"/> classifies a send that never produced a response.
/// </summary>
/// <remarks>
/// This is the one place in the backfill drain where "retryable" and "free to retry" are decided,
/// and they are not the same question. The coordinator refunds a slice's attempt for
/// <see cref="GatewayOutcome.Unreachable"/> and spends it for <see cref="GatewayOutcome.Transient"/>,
/// and <c>attempts</c> has no reset path anywhere — so a misclassification here is not a slower
/// retry, it is a slice permanently retired for a reason that had nothing to do with it, in a
/// pipeline whose whole premise is that unrecorded data is unrecoverable.
/// </remarks>
public sealed class BackfillGatewayClientTests
{
    private static readonly HistoricalBarsRequestDto Request = new(
        new HistoricalContractSpecDto("SPX", "IND", "CBOE", "USD", ConId: 416904),
        new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero),
        "1 D",
        "1 min",
        "TRADES");

    private sealed class ThrowingHandler(Func<Exception> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(factory());
    }

    private static IbkrGatewayClient ClientThrowing(Func<Exception> factory) =>
        new(
            new HttpClient(new ThrowingHandler(factory)) { BaseAddress = new Uri("http://localhost:5100") },
            NullLogger<IbkrGatewayClient>.Instance);

    [Fact]
    public async Task A_refused_connection_is_unreachable_so_the_slice_keeps_its_attempt()
    {
        // The reported scenario: the gateway is redeployed and is down for twenty seconds. Every
        // request in that window fails at the transport, provably without reaching TWS — yet it was
        // classified Transient, which burns an attempt. Because MarkOutcomeAsync backs off only the
        // slice that just failed, the loop walked down the job spending one attempt per slice at
        // HTTP-failure speed; five routine redeploys retired ~100 of the newest slices for good.
        //
        // Deliberately a REAL refused connection rather than a hand-built exception. The whole fix
        // rests on .NET populating HttpRequestException.HttpRequestError, and a fixture that sets
        // that property itself would be asserting the test's own assumption rather than the
        // runtime's behaviour — the shape of mistake this repo's Phase 1 review kept finding.
        using var closed = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)closed.LocalEndPoint!).Port;
        closed.Close(); // bound, never listening: connecting to it is refused immediately.

        var client = new IbkrGatewayClient(
            new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) },
            NullLogger<IbkrGatewayClient>.Instance);

        var result = await client.GetHistoricalBarsAsync(Request, CancellationToken.None);

        Assert.Equal(GatewayOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task An_unresolvable_gateway_host_is_unreachable()
    {
        var client = ClientThrowing(() => new HttpRequestException(
            HttpRequestError.NameResolutionError, "No such host is known."));

        Assert.Equal(
            GatewayOutcome.Unreachable,
            (await client.GetHistoricalBarsAsync(Request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_open_circuit_is_classified_rather_than_escaping_as_an_unhandled_exception()
    {
        // BrokenCircuitException matched neither arm of the original `HttpRequestException or
        // TaskCanceledException` filter, so it escaped this class entirely and surfaced at the
        // coordinator's outermost catch — the one path that writes no outcome at all, leaving the
        // claimed row 'inflight' until a reaper turned it into 'failed' with its attempt already
        // spent. Losing the slice AND its retry budget, from a failure that never left the process.
        var client = ClientThrowing(() => new BrokenCircuitException("The circuit is now open."));

        var result = await client.GetHistoricalBarsAsync(Request, CancellationToken.None);

        Assert.Equal(GatewayOutcome.Unreachable, result.Outcome);
        Assert.Contains("circuit", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_response_that_ended_early_stays_transient_because_TWS_may_have_answered()
    {
        // The conservative direction, and the reason the split is on HttpRequestError rather than on
        // the exception type: the request WAS accepted, so it may have consumed a paced TWS request
        // slot. Refunding the attempt on that basis would let a request that genuinely reaches TWS
        // and genuinely fails retry without limit.
        var client = ClientThrowing(() => new HttpRequestException(
            HttpRequestError.ResponseEnded, "The response ended prematurely."));

        Assert.Equal(
            GatewayOutcome.Transient,
            (await client.GetHistoricalBarsAsync(Request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_client_side_timeout_stays_transient()
    {
        var client = ClientThrowing(() => new TaskCanceledException("The request timed out."));

        Assert.Equal(
            GatewayOutcome.Transient,
            (await client.GetHistoricalBarsAsync(Request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_head_timestamp_probe_classifies_the_same_way()
    {
        // Same classifier, deliberately: the ES walker skips a contract whose head will not resolve,
        // and it must be able to tell a gateway that is down from a contract TWS has no data for.
        var client = ClientThrowing(() => new BrokenCircuitException("The circuit is now open."));

        var result = await client.GetHeadTimestampAsync(
            Request.Contract, "TRADES", useRth: false, CancellationToken.None);

        Assert.Equal(GatewayOutcome.Unreachable, result.Outcome);
    }

    private sealed class RespondingHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory());
    }

    private static IbkrGatewayClient ClientResponding(System.Net.HttpStatusCode status, string body, string mediaType) =>
        new(
            new HttpClient(new RespondingHandler(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
            }))
            { BaseAddress = new Uri("http://localhost:5100") },
            NullLogger<IbkrGatewayClient>.Instance);

    // ---- the second region: reading the body of a response that DID arrive -------------------------
    //
    // The send's catch cannot see any of this — it has already completed successfully. A failure here
    // escaped the client entirely and surfaced at the coordinator's outermost catch, which writes no
    // outcome: the same stranded-claim shape the BrokenCircuitException fix was written for, and which
    // that fix's own remark wrongly claimed to have closed as a class.
    //
    // Worth recording what these tests ruled OUT while being written: a connection that dies
    // mid-stream is NOT one of them. HttpClient buffers the response content during the send (the
    // default completion option is ResponseContentRead), so a reset mid-body surfaces from the send
    // itself and the existing transport classifier already catches it. What genuinely reaches the
    // body read is a response that arrived intact and cannot be deserialised.

    [Fact]
    public async Task A_200_carrying_the_wrong_content_type_is_classified_rather_than_thrown()
    {
        // A proxy or a misrouted request answering 200 with an HTML page. ReadFromJsonAsync raises
        // NotSupportedException for an unsupported media type — not a JsonException, so even a
        // JsonException-only guard would not have caught it.
        var client = ClientResponding(System.Net.HttpStatusCode.OK, "<html>Service Unavailable</html>", "text/html");

        var result = await client.GetHistoricalBarsAsync(Request, CancellationToken.None);

        // Transient, not Unreachable: a 200 means the request reached TWS and spent a paced slot, so
        // refunding the slice's attempt would let a genuinely-failing request retry without limit.
        Assert.Equal(GatewayOutcome.Transient, result.Outcome);
        Assert.Contains("body could not be read", result.Detail);
    }

    [Fact]
    public async Task A_200_carrying_malformed_json_is_classified_rather_than_thrown()
    {
        var client = ClientResponding(System.Net.HttpStatusCode.OK, """{"bars":[{"open":1""", "application/json");

        var result = await client.GetHistoricalBarsAsync(Request, CancellationToken.None);

        Assert.Equal(GatewayOutcome.Transient, result.Outcome);
        Assert.Contains("body could not be read", result.Detail);
    }

    [Fact]
    public async Task The_head_timestamp_probe_survives_an_unreadable_body_too()
    {
        // Same region, same guard. An escape here strands nothing (the walker holds no claim), but it
        // does abort the whole ES scan mid-family, which is why it is classified rather than thrown.
        var client = ClientResponding(System.Net.HttpStatusCode.OK, "<html>nope</html>", "text/html");

        Assert.Equal(
            GatewayOutcome.Transient,
            (await client.GetHeadTimestampAsync(Request.Contract, "TRADES", useRth: false, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_error_response_with_an_unparseable_body_still_classifies_on_its_status_code()
    {
        // The detail text is a nicety; the status code is the classification. Losing the first must
        // never lose the second — 400 is Permanent whatever the body turns out to be.
        var client = ClientResponding(System.Net.HttpStatusCode.BadRequest, "<html>Bad Request</html>", "text/html");

        Assert.Equal(
            GatewayOutcome.Permanent,
            (await client.GetHistoricalBarsAsync(Request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_laundered_into_a_retryable_outcome()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = ClientThrowing(() => new TaskCanceledException("Cancelled."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetHistoricalBarsAsync(Request, cts.Token));
    }
}
