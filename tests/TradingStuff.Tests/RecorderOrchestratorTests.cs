using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Recording;
using TradingStuff.ResearchService.Universe;

namespace TradingStuff.Tests;

/// <summary>
/// The orchestrator against a gateway that goes away underneath it — the shape that actually
/// happens, since the gateway owns the TWS socket and gets restarted while this service keeps
/// running. Everything here is driven through a real <see cref="IbkrGatewayClient"/> over a stub
/// transport, so the exceptions under test are the ones <c>HttpClient</c> genuinely raises rather
/// than something a mock was told to throw.
/// </summary>
public sealed class RecorderOrchestratorTests
{
    /// <summary>
    /// Stands in for the gateway's HTTP surface. <see cref="Reachable"/> false reproduces a gateway
    /// that has been stopped: the connect fails, exactly as <c>HttpClient</c> reports it.
    /// </summary>
    private sealed class StubGateway : HttpMessageHandler
    {
        private int _nextConId = 1000;

        public volatile bool Reachable = true;

        /// <summary>When true, the gateway answers but does not recognise the lease (a 404).</summary>
        public volatile bool KnowsLeases = true;

        public int Grants;

        public int Heartbeats;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Reachable)
            {
                throw new HttpRequestException(
                    HttpRequestError.ConnectionError, "Connection refused (127.0.0.1:5000)");
            }

            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/resolve", StringComparison.Ordinal))
            {
                return Json(new UnderlyingResolution(Interlocked.Increment(ref _nextConId), "IND", "CBOE"));
            }

            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Heartbeats);

                return KnowsLeases
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == "/ibkr/subscriptions" && request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref Grants);

                var body = await request.Content!.ReadFromJsonAsync<SubscriptionLeaseRequest>(cancellationToken);

                return Json(new SubscriptionLease(
                    Guid.NewGuid(), body!.ConId, body.Priority, body.RecordToDatabase,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3)));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static (RecorderOrchestrator Orchestrator, StubGateway Gateway) Create(
        TimeSpan? assumeEvictedAfter = null, TimeSpan? heartbeatEvery = null)
    {
        var stub = new StubGateway();

        var client = new IbkrGatewayClient(
            new HttpClient(stub) { BaseAddress = new Uri("http://gateway.test") },
            NullLogger<IbkrGatewayClient>.Instance);

        // No connection string: NodeSelector short-circuits to "no nodes", so these tests exercise
        // the three core-underlying leases without needing Postgres.
        var configuration = new ConfigurationBuilder().Build();
        var selector = new NodeSelector(configuration, client, chains: null!, NullLogger<NodeSelector>.Instance);

        var orchestrator = new RecorderOrchestrator(client, selector, NullLogger<RecorderOrchestrator>.Instance)
        {
            AssumeEvictedAfter = assumeEvictedAfter ?? TimeSpan.FromSeconds(195),
            HeartbeatEvery = heartbeatEvery ?? TimeSpan.FromMilliseconds(10),
            RetryEvery = TimeSpan.FromMilliseconds(50),
        };

        return (orchestrator, stub);
    }

    // The defect, at the level where it bites: an unreachable gateway at heartbeat time threw
    // straight out of ExecuteAsync, and BackgroundServiceExceptionBehavior.StopHost took the whole
    // ResearchService host down with it — PartitionMaintainer included, whose sweeps must not stop.
    [Fact]
    public async Task A_gateway_that_disappears_does_not_fault_the_orchestrator()
    {
        var (orchestrator, gateway) = Create();

        await orchestrator.StartAsync(CancellationToken.None);

        try
        {
            // Leases granted while the gateway is up, so the maps are populated — the precondition
            // for the failure: heartbeats only happen for leases that exist.
            await WaitUntilAsync(() => gateway.Grants >= 3, "the core underlyings to be leased");

            gateway.Reachable = false; // the gateway restarts

            await WaitUntilAsync(
                () => orchestrator.ExecuteTask is { IsCompleted: true } || gateway.Heartbeats > 0,
                "a heartbeat tick against the stopped gateway");

            // Several more ticks, so this is not merely a race that has not resolved yet.
            await Task.Delay(100);

            Assert.NotNull(orchestrator.ExecuteTask);
            Assert.False(
                orchestrator.ExecuteTask!.IsFaulted,
                $"the orchestrator faulted and would have stopped the host: {orchestrator.ExecuteTask.Exception}");
            Assert.False(orchestrator.ExecuteTask.IsCompleted, "the orchestrator stopped recording entirely");
        }
        finally
        {
            await orchestrator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_undeliverable_heartbeat_does_not_throw_and_keeps_the_lease_for_now()
    {
        var (orchestrator, gateway) = Create();

        await orchestrator.RunLeasePassAsync(CancellationToken.None);
        Assert.Equal(3, orchestrator.TrackedLeaseCount);

        gateway.Reachable = false;

        var dropped = await orchestrator.HeartbeatAllAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Kept, deliberately: the gateway may still hold these leases, and re-granting now would
        // double-book a market-data line against a budget that has no room for two copies.
        Assert.False(dropped);
        Assert.Equal(3, orchestrator.TrackedLeaseCount);
    }

    [Fact]
    public async Task A_lease_the_gateway_must_have_evicted_by_now_is_dropped_and_re_acquired()
    {
        var (orchestrator, gateway) = Create(assumeEvictedAfter: TimeSpan.FromSeconds(195));

        await orchestrator.RunLeasePassAsync(CancellationToken.None);
        var granted = gateway.Grants;

        gateway.Reachable = false;

        // Past the point where the gateway's own three-missed-heartbeat eviction has certainly run:
        // the map entries are now fiction, and keeping them means never recording these again.
        var dropped = await orchestrator.HeartbeatAllAsync(
            DateTimeOffset.UtcNow.AddSeconds(200), CancellationToken.None);

        Assert.True(dropped);
        Assert.Equal(0, orchestrator.TrackedLeaseCount);

        // And the next pass re-leases them, which is the whole point of dropping.
        gateway.Reachable = true;
        await orchestrator.RunLeasePassAsync(CancellationToken.None);

        Assert.Equal(3, orchestrator.TrackedLeaseCount);
        Assert.Equal(granted + 3, gateway.Grants);
    }

    [Fact]
    public async Task A_refused_heartbeat_drops_the_lease_immediately()
    {
        // A 404 is the gateway ANSWERING that it does not hold this lease — nothing to double-book,
        // so unlike an undeliverable heartbeat this needs no waiting period.
        var (orchestrator, gateway) = Create();

        await orchestrator.RunLeasePassAsync(CancellationToken.None);
        gateway.KnowsLeases = false;

        Assert.True(await orchestrator.HeartbeatAllAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(0, orchestrator.TrackedLeaseCount);
    }

    [Fact]
    public async Task A_reachable_gateway_keeps_every_lease_indefinitely()
    {
        // The negative control for the drop rules above: acknowledged heartbeats must keep moving
        // the deadline, or a healthy recording session would tear itself down every three minutes.
        var (orchestrator, _) = Create(assumeEvictedAfter: TimeSpan.FromSeconds(195));

        await orchestrator.RunLeasePassAsync(CancellationToken.None);

        var now = DateTimeOffset.UtcNow;

        for (var tick = 0; tick < 40; tick++)
        {
            now = now.AddSeconds(20);
            Assert.False(await orchestrator.HeartbeatAllAsync(now, CancellationToken.None));
        }

        Assert.Equal(3, orchestrator.TrackedLeaseCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
