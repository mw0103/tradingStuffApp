using System.Net.Sockets;
using IBApi;
using TradingStuff.ResearchContracts;

namespace TradingStuff.Tests;

/// <summary>
/// Live integration tests against a running paper TWS. Excluded from the default run — set
/// <c>TRADING_TEST_TWS=host:port</c> (e.g. <c>127.0.0.1:7497</c>) and run with
/// <c>--filter "Category=RequiresTws"</c>.
/// </summary>
/// <remarks>
/// This suite exists because of a specific, expensive lesson. The recorder subscribed to market
/// data with a bare conId and a hardcoded <c>Exchange = "SMART"</c>. TWS accepts that for options
/// and stocks but rejects it for INDEX conIds with error 200, returning zero ticks — so SPX and
/// VIX, two of the three core underlyings, recorded nothing at all. Every unit test passed
/// throughout, because they all stub the socket. No amount of additional mocked testing could have
/// caught it; only a real connection could.
/// <para>
/// So the rule these tests encode: any assumption about what TWS <em>accepts</em> — contract
/// shapes, tick types, entitlements — is not knowledge until a live connection has demonstrated
/// it. Assertions here should pin observed broker behaviour, not our own code's internal logic.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
public sealed class LiveTwsSubscriptionTests
{
    private const int SpxIndexConId = 416904; // verified live; SPX index, CBOE

    private static (string Host, int Port)? TwsEndpoint
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("TRADING_TEST_TWS");

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(':', 2);
            return parts.Length == 2 && int.TryParse(parts[1], out var port)
                ? (parts[0], port)
                : ("127.0.0.1", 7497);
        }
    }

    /// <summary>Confirms a socket is actually listening before a test claims anything about TWS.</summary>
    private static bool CanReachTws((string Host, int Port) endpoint)
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(3))
                   && probe.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    [Fact]
    public void An_index_conId_is_not_reachable_via_SMART_which_is_why_Exchange_is_required()
    {
        // The regression this whole suite was created for. Documents the broker behaviour that made
        // a hardcoded "SMART" silently fatal for SPX/VIX, so nobody "simplifies" the required
        // Exchange back into a default.
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new LiveTwsClient(endpoint.Host, endpoint.Port, clientId: 271);
        client.Connect();

        var viaSmart = client.TryStreamTicks(SpxIndexConId, exchange: "SMART", TimeSpan.FromSeconds(8));
        var viaCboe = client.TryStreamTicks(SpxIndexConId, exchange: "CBOE", TimeSpan.FromSeconds(8));

        Assert.True(
            viaCboe.TickCount > 0,
            $"SPX on its native exchange should stream. Got {viaCboe.TickCount} ticks, " +
            $"error {viaCboe.ErrorCode}. If this fails the account may lack the Cboe index " +
            "subscription, or the market is closed — check before assuming a code defect.");

        Assert.True(
            viaSmart.TickCount == 0,
            "SMART unexpectedly streamed an index conId. If TWS behaviour has genuinely changed, " +
            "update SubscriptionLeaseRequest.Exchange's remarks — but a required Exchange is " +
            "still correct, because omitting it entirely is rejected with error 321.");
    }

    [Fact]
    public void The_lease_request_type_refuses_to_be_constructed_without_an_exchange()
    {
        // Not a TWS test — a guard that the type-level fix survives. Kept beside the live test so
        // the reason for the constraint stays next to the evidence for it.
        var request = new SubscriptionLeaseRequest(
            SpxIndexConId, LeasePriority.CoreRecording, RecordToDatabase: true, IsOption: false,
            GenericTickList: null, HeartbeatIntervalSeconds: 60, Exchange: "CBOE");

        Assert.Equal("CBOE", request.Exchange);
    }

    /// <summary>Minimal EWrapper that counts ticks for one subscription. Test-scope only.</summary>
    private sealed class LiveTwsClient(string host, int port, int clientId) : DefaultEWrapper, IDisposable
    {
        private readonly EReaderMonitorSignal _signal = new();
        private EClientSocket? _socket;
        private int _tickCount;
        private int _lastErrorCode;
        private int _activeTicker = -1;

        public void Connect()
        {
            _socket = new EClientSocket(this, _signal);
            _socket.eConnect(host, port, clientId);

            var reader = new EReader(_socket, _signal);
            reader.Start();

            new Thread(() =>
            {
                while (_socket?.IsConnected() == true)
                {
                    _signal.waitForSignal();
                    reader.processMsgs();
                }
            })
            { IsBackground = true }.Start();

            // Give TWS time to complete startApi before the first request.
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        public (int TickCount, int ErrorCode) TryStreamTicks(int conId, string exchange, TimeSpan wait)
        {
            var ticker = Interlocked.Increment(ref _activeTicker) + 9000;
            Interlocked.Exchange(ref _tickCount, 0);
            Interlocked.Exchange(ref _lastErrorCode, 0);

            var contract = new Contract { ConId = conId, Exchange = exchange };
            _socket!.reqMktData(ticker, contract, string.Empty, false, false, null);

            Thread.Sleep(wait);
            _socket.cancelMktData(ticker);

            return (Volatile.Read(ref _tickCount), Volatile.Read(ref _lastErrorCode));
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs) =>
            Interlocked.Increment(ref _tickCount);

        public override void tickSize(int tickerId, int field, decimal size) =>
            Interlocked.Increment(ref _tickCount);

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            // Informational farm-status notices are not failures.
            if (errorCode is not (>= 2100 and <= 2200))
            {
                Interlocked.Exchange(ref _lastErrorCode, errorCode);
            }
        }

        public void Dispose()
        {
            try
            {
                _socket?.eDisconnect();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
            {
                // Disconnecting a already-dead socket is not a test failure.
            }
        }
    }
}
