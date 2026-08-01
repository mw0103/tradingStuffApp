using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Recording;
using TradingStuff.IbkrGateway.Subscriptions;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;

namespace TradingStuff.Tests;

/// <summary>
/// The real <see cref="SubscriptionManager"/> driven through the real socket against paper TWS —
/// grant, replay, release — with a real <see cref="ObservationRecorder"/> behind it. Needs
/// <c>TRADING_TEST_TWS=host:port</c> and <c>TRADING_TEST_POSTGRES</c>; run with
/// <c>--filter "Category=RequiresTws"</c>.
/// </summary>
/// <remarks>
/// The lease lifetime's unit tests replace the socket with a fake, which by construction cannot say
/// anything about what TWS accepts — the lesson <see cref="LiveTwsSubscriptionTests"/> was written
/// for. This suite covers what those cannot: that the subscribe/cancel calls the lifetime makes are
/// still shaped in a way TWS accepts after the transport was put behind an interface, and that a
/// replay's ticker swap does not leave a rejected or duplicated subscription behind.
/// <para>
/// Deliberately does NOT assert that ticks arrived. That depends on the session being open, and a
/// test whose pass/fail flips with the clock is a test people learn to ignore. What it does assert
/// is market-hours independent and still a real broker fact: TWS raised no error for this
/// subscription. A rejected contract (error 200 for an index on SMART, 321 for a missing exchange)
/// faults the sink, which opens a <c>recorder_gaps</c> row — so "no gap row exists for this lease"
/// IS the acceptance check, and it is the same signal that would be silent in a unit test.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
[Collection(PostgresCollection.Name)]
public sealed class LiveTwsSubscriptionManagerTests
{
    private const int SpxIndexConId = 416904; // verified live; SPX index, CBOE
    private const int TestClientId = 273;     // distinct from the gateway's 11 and LiveTwsSubscriptionTests' 271

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

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
            return parts.Length == 2 && int.TryParse(parts[1], out var port) ? (parts[0], port) : ("127.0.0.1", 7497);
        }
    }

    private static bool CanReachTws((string Host, int Port) endpoint)
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(3)) && probe.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_lease_survives_grant_replay_and_release_against_paper_TWS()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint) || ServerConnectionString is not { } server)
        {
            return;
        }

        // A paper port, always. 7496/4001 are live money and nothing here may reach them.
        Assert.True(endpoint.Port is 7497 or 4002, $"refusing to run against non-paper port {endpoint.Port}");

        var connectionString = PostgresCollection.FreshDatabase(server);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        await new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance)
            .ApplyOnceAsync(connectionString, CancellationToken.None);

        // AllowLiveTrading stays false, and nothing here goes near placeOrder.
        var options = Options.Create(new IbkrOptions
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            ClientId = TestClientId,
        });

        var registry = new IbkrRequestRegistry();
        var governor = new IbkrPacingGovernor(
            options, TimeProvider.System, new TestMeterFactory(), NullLogger<IbkrPacingGovernor>.Instance);
        var wrapper = new IbkrClientWrapper(
            registry, new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance),
            NullLogger<IbkrClientWrapper>.Instance);

        using var connection = new IbkrConnection(
            options, registry, wrapper, governor, NullLogger<IbkrConnection>.Instance);
        var socket = new PacedSocket(connection, governor, NullLogger<PacedSocket>.Instance);

        await using var recorder = new ObservationRecorder(
            configuration, new TestMeterFactory(), NullLogger<ObservationRecorder>.Instance);

        var manager = new SubscriptionManager(connection, socket, recorder, NullLogger<SubscriptionManager>.Instance);

        await connection.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => Task.FromResult(connection.IsConnected), TimeSpan.FromSeconds(30));
            Assert.True(connection.IsConnected, "TWS did not accept the API connection.");

            var lease = await manager.GrantAsync(
                new SubscriptionLeaseRequest(
                    SpxIndexConId, LeasePriority.AdHoc, RecordToDatabase: true, IsOption: false,
                    GenericTickList: null, HeartbeatIntervalSeconds: 30, Exchange: "CBOE"),
                CancellationToken.None);

            var scope = ObservationRecorder.LeaseScope(lease.LeaseId);

            Assert.Equal(1, governor.GetLineBudget().ResearchInUse);
            Assert.Equal(1, registry.InFlightCount);

            // Long enough for TWS to have rejected the contract if it were going to. Verified to be
            // a live check rather than a vacuous one: re-running this with Exchange = "SMART" (which
            // TWS rejects for an index conId with error 200) does produce the row and fail here.
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.Equal(0, await GapCountAsync(connectionString, scope));

            // A replay with NO ledger reset in front of it, which is a genuinely different broker
            // interaction and has to come FIRST: after a reset the displaced lease is stale-epoch
            // and releasing it is a no-op, so running these passes afterwards would prove nothing.
            // Here the reqMktData being displaced is one TWS is still streaming, and the manager
            // now sends a real cancelMktData for it — a call site that did not exist before, since
            // the displaced subscription used to be abandoned outright, leaking a TWS line and a
            // ledger line per re-issue.
            //
            // Three passes rather than one because a leak of one line per pass sits inside the
            // 90-line cap for a long time: "the count stayed at 1" only means something once it has
            // had several chances not to. Verified to be a live check rather than a vacuous one:
            // with the displaced lease dropped instead of released, this fails on the first pass
            // with ResearchInUse = 2.
            //
            // Two broker facts here that no fake socket can answer — that TWS accepts the cancel of
            // a live ticker interleaved with a fresh reqMktData for the same conId, and that doing
            // so does not disturb the replacement (checked by the gap count below, since an error
            // against the live ticker would fault its sink and write a row).
            for (var pass = 0; pass < 3; pass++)
            {
                await manager.ReplayAsync(CancellationToken.None);

                Assert.Equal(1, governor.GetLineBudget().ResearchInUse);
                Assert.Equal(1, registry.InFlightCount);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await GapCountAsync(connectionString, scope));

            // And now the reconnect path, on a socket that never dropped — the same thing TWS's
            // 1101 notice makes the manager do. The old ticker's registration must go with it:
            // exactly one in-flight sink before and after, never two.
            governor.ResetLineLedgerForReconnect();
            await manager.ReplayAsync(CancellationToken.None);

            Assert.Equal(1, registry.InFlightCount);
            Assert.Equal(1, governor.GetLineBudget().ResearchInUse);

            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await GapCountAsync(connectionString, scope));

            Assert.True(await manager.ReleaseAsync(lease.LeaseId, CancellationToken.None));

            // The whole point of the release path: the line comes back, the sink is gone, and no
            // unbounded gap row is left to poison every future coverage window.
            Assert.Equal(0, governor.GetLineBudget().ResearchInUse);
            Assert.Equal(0, registry.InFlightCount);
            Assert.Equal(0, await OpenGapCountAsync(connectionString, scope));
        }
        finally
        {
            await connection.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Captures what a <see cref="RecordingTickSink"/> emits, with no Postgres behind it.</summary>
    private sealed class CapturingSink : IObservationSink
    {
        public List<OptionQuoteObservation> Options { get; } = [];

        public List<UnderlyingTickObservation> Underlyings { get; } = [];

        public List<int> NonLiveReports { get; } = [];

        public List<Guid> LiveReports { get; } = [];

        public void EnqueueOption(OptionQuoteObservation observation)
        {
            lock (Options)
            {
                Options.Add(observation);
            }
        }

        public void EnqueueUnderlying(UnderlyingTickObservation observation)
        {
            lock (Underlyings)
            {
                Underlyings.Add(observation);
            }
        }

        public void NotifyGapClosed(Guid leaseId, short? effectiveMarketDataType)
        {
        }

        public void NotifyNonLiveMarketData(Guid leaseId, int marketDataType)
        {
            lock (NonLiveReports)
            {
                NonLiveReports.Add(marketDataType);
            }
        }

        public void NotifyLiveMarketData(Guid leaseId)
        {
            lock (LiveReports)
            {
                LiveReports.Add(leaseId);
            }
        }
    }

    [Fact]
    public async Task Paper_TWS_reports_an_effective_market_data_type_and_it_is_what_gets_stamped()
    {
        // The broker fact a stubbed socket cannot supply (docs/LESSONS.md #5): that TWS answers
        // reqMarketDataType with a marketDataType callback per ticker at all, that the answer is in
        // the documented 1|2|3|4 domain the schema's CHECK holds, and — the point of this whole
        // change — that the answer for THIS account may not be the value that was requested.
        //
        // Deliberately market-hours independent in its required half. The callback arrives on the
        // subscription's acceptance, not on trading activity, so it is asserted unconditionally.
        // Whether any TICK follows depends on the session being open; that half is asserted only if
        // ticks actually arrive, and the test says out loud when they did not rather than passing
        // quietly as though it had checked (docs/LESSONS.md #12).
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        Assert.True(endpoint.Port is 7497 or 4002, $"refusing to run against non-paper port {endpoint.Port}");

        const int requested = 1; // ask for LIVE, so requested and served can differ visibly
        var options = Options.Create(new IbkrOptions
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            ClientId = TestClientId + 1,
            MarketDataType = requested,
        });

        var registry = new IbkrRequestRegistry();
        var governor = new IbkrPacingGovernor(
            options, TimeProvider.System, new TestMeterFactory(), NullLogger<IbkrPacingGovernor>.Instance);
        var wrapper = new IbkrClientWrapper(
            registry, new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance),
            NullLogger<IbkrClientWrapper>.Instance);

        using var connection = new IbkrConnection(
            options, registry, wrapper, governor, NullLogger<IbkrConnection>.Instance);
        var socket = new PacedSocket(connection, governor, NullLogger<PacedSocket>.Instance);

        await connection.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => Task.FromResult(connection.IsConnected), TimeSpan.FromSeconds(30));
            Assert.True(connection.IsConnected, "TWS did not accept the API connection.");

            var capture = new CapturingSink();
            var leaseId = Guid.NewGuid();
            var ticker = registry.NextRequestId();

            registry.Register(
                ticker,
                new RecordingTickSink(
                    SpxIndexConId, leaseId, isOption: false, markFirstTickAsReplay: false, capture, _ => { }));

            var line = await socket.ReqMktDataAsync(
                ticker,
                new IBApi.Contract { ConId = SpxIndexConId, Exchange = "CBOE" },
                genericTickList: string.Empty,
                snapshot: false,
                regulatorySnapshot: false,
                mktDataOptions: null,
                LineClass.Research,
                CancellationToken.None);

            try
            {
                await WaitUntilAsync(
                    () => Task.FromResult(capture.NonLiveReports.Count + capture.LiveReports.Count > 0),
                    TimeSpan.FromSeconds(20));

                var reported = capture.LiveReports.Count > 0 ? 1 : capture.NonLiveReports.FirstOrDefault();

                Assert.True(
                    capture.NonLiveReports.Count + capture.LiveReports.Count > 0,
                    "Paper TWS never sent a marketDataType callback for this subscription, so nothing " +
                    "here has been demonstrated about what regime the account is actually served.");

                // Measured on paper TWS, 2026-08-01: this account IS entitled to live Cboe index
                // data, so a request for 1 came back as 1 and four SPX ticks arrived stamped 1.
                // Recorded because it fixes what the assertion below is worth: on THIS account the
                // requested and served values happen to agree, so this test demonstrates that the
                // callback arrives and that what it says is what gets stamped — it does NOT
                // demonstrate a downgrade. Only an unentitled account or an explicit request for 3
                // can do that, and the unit tests cover the divergence directly.
                Assert.InRange(reported, 1, 4);

                // What the recorder stamps is what arrived on the callback — every emitted row, not
                // the requested value, and not a null once the answer is in.
                var stamped = capture.Underlyings.Select(o => o.Envelope.MarketDataType).ToArray();

                Assert.All(
                    stamped.Where(value => value is not null),
                    value => Assert.Equal((short)reported, value));

                Assert.True(
                    stamped.Length > 0,
                    $"TWS reported market data type {reported} for the SPX index, but no tick arrived " +
                    "within the window, so the stamping half of this test proved nothing. Expected " +
                    "outside RTH/GTH — re-run while the Cboe session is open.");
            }
            finally
            {
                await socket.CancelMktDataAsync(ticker, line);
                registry.Remove(ticker);
            }
        }
        finally
        {
            await connection.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<int> GapCountAsync(string connectionString, string scope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = $1", connection);
        command.Parameters.AddWithValue(scope);

        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> OpenGapCountAsync(string connectionString, string scope)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM gateway.recorder_gaps WHERE scope = $1 AND ended_at IS NULL", connection);
        command.Parameters.AddWithValue(scope);

        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }
}
