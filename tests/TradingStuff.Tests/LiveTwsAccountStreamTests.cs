using System.Collections.Concurrent;
using System.Net.Sockets;
using IBApi;

namespace TradingStuff.Tests;

/// <summary>
/// Live integration tests pinning what TWS actually does with repeated account-summary
/// subscriptions. Excluded from the default run — set <c>TRADING_TEST_TWS=host:port</c> (e.g.
/// <c>127.0.0.1:7497</c>) and run with <c>--filter "Category=RequiresTws"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The account feed is rebuilt whenever its connection changes, its account changes, or one of its
/// streams dies. Each rebuild used to issue a fresh <c>reqAccountSummary</c> id and never cancel the
/// old one, and the consequence is not a slow leak: TWS allows exactly two account-summary requests
/// per API client, so the SECOND rebuild of a session gets error 322 and every portfolio read after
/// it fails for the rest of the connection — taking buying power, positions and daily P&amp;L away
/// from pre-trade risk.
/// </para>
/// <para>
/// The obvious remedy is wrong, which is why these tests exist rather than a comment. Cancelling the
/// old subscription first does NOT free the slot. What works is re-issuing on a request id TWS has
/// already seen, because the cap counts distinct ids rather than live subscriptions. That is
/// broker behaviour, unknowable from any amount of mocked testing, and the only thing standing
/// between the gateway and a dead portfolio feed — so it is recorded here, re-runnable, next to the
/// code that depends on it.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
public sealed class LiveTwsAccountStreamTests
{
    private const string SummaryTags =
        "NetLiquidation,BuyingPower,AvailableFunds,ExcessLiquidity,GrossPositionValue";

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
    public void A_third_account_summary_id_is_refused_even_after_cancelling_the_previous_one()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new LiveAccountClient(endpoint.Host, endpoint.Port, clientId: 245);

        if (!client.ConnectToPaperAccount())
        {
            return;
        }

        var previous = -1;
        var outcomes = new List<(int RequestId, int Rows, int ErrorCode)>();

        for (var index = 0; index < 3; index++)
        {
            var requestId = 30_100 + index;

            if (previous >= 0)
            {
                client.CancelAccountSummary(previous);
            }

            outcomes.Add(client.RequestAccountSummary(requestId, SummaryTags));
            previous = requestId;
        }

        Assert.True(outcomes[0].Rows > 0, $"The first subscription should deliver rows. Got {outcomes[0]}.");
        Assert.True(outcomes[1].Rows > 0, $"The second subscription should deliver rows. Got {outcomes[1]}.");

        Assert.True(
            outcomes[2].Rows == 0 && outcomes[2].ErrorCode == 322,
            "TWS is expected to refuse a THIRD distinct account-summary id with error 322 even though " +
            "the previous two were cancelled — that is the whole reason IbkrAccountClient reuses one " +
            $"id per stream. Got {outcomes[2]}. If TWS has genuinely changed, reusing the id is still " +
            "correct, but say so in IbkrAccountClient.RebuildFeedAsync rather than reverting it.");
    }

    [Fact]
    public void Re_issuing_the_same_account_summary_id_works_indefinitely()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        // A separate client id: the cap is per API client session, so the test above would poison
        // this one if they shared a connection.
        using var client = new LiveAccountClient(endpoint.Host, endpoint.Port, clientId: 246);

        if (!client.ConnectToPaperAccount())
        {
            return;
        }

        const int requestId = 30_200;

        for (var rebuild = 0; rebuild < 4; rebuild++)
        {
            if (rebuild > 0)
            {
                client.CancelAccountSummary(requestId);
            }

            var outcome = client.RequestAccountSummary(requestId, SummaryTags);

            Assert.True(
                outcome.Rows > 0 && outcome.ErrorCode == 0,
                $"Rebuild {rebuild} on the reused id should deliver rows. Got {outcome}.");
        }
    }

    /// <summary>Minimal EWrapper counting account-summary rows for one request. Test-scope only.</summary>
    private sealed class LiveAccountClient(string host, int port, int clientId) : DefaultEWrapper, IDisposable
    {
        private readonly EReaderMonitorSignal _signal = new();
        private readonly ConcurrentDictionary<int, int> _rows = new();
        private EClientSocket? _socket;
        private string[] _accounts = [];
        private int _lastErrorCode;

        /// <summary>Connects, and refuses to proceed on anything but a paper (DU) login.</summary>
        public bool ConnectToPaperAccount()
        {
            _socket = new EClientSocket(this, _signal);
            _socket.eConnect(host, port, clientId);

            if (!_socket.IsConnected())
            {
                return false;
            }

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

            Thread.Sleep(TimeSpan.FromSeconds(2));

            var accounts = Volatile.Read(ref _accounts);

            return accounts.Length > 0
                   && accounts.All(account => account.StartsWith("DU", StringComparison.OrdinalIgnoreCase));
        }

        public (int RequestId, int Rows, int ErrorCode) RequestAccountSummary(int requestId, string tags)
        {
            _rows[requestId] = 0;
            Interlocked.Exchange(ref _lastErrorCode, 0);

            _socket!.reqAccountSummary(requestId, "All", tags);
            Thread.Sleep(TimeSpan.FromSeconds(3));

            return (requestId, _rows[requestId], Volatile.Read(ref _lastErrorCode));
        }

        public void CancelAccountSummary(int requestId)
        {
            _socket!.cancelAccountSummary(requestId);
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
        }

        public override void managedAccounts(string accountsList) =>
            Volatile.Write(
                ref _accounts,
                accountsList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        public override void accountSummary(int reqId, string account, string tag, string value, string currency) =>
            _rows.AddOrUpdate(reqId, 1, (_, count) => count + 1);

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            // Farm-status notices are not failures.
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
                // Disconnecting an already-dead socket is not a test failure.
            }
        }
    }
}
