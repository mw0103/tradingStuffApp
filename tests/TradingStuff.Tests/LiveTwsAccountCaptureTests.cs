using System.Collections.Concurrent;
using System.Net.Sockets;
using IBApi;
using TradingStuff.IbkrGateway;

namespace TradingStuff.Tests;

/// <summary>
/// Live integration tests for the two broker facts the raw capture layer rests on and that no
/// amount of mocking can establish: that a paper account actually serves the margin tags, and what
/// <c>reqExecutions</c> does — including the shape of the time string on an execution report.
/// </summary>
/// <remarks>
/// <para>
/// Excluded from the default run — set <c>TRADING_TEST_TWS=host:port</c> (e.g. <c>127.0.0.1:7497</c>)
/// and run with <c>--filter "Category=RequiresTws"</c>. Per CLAUDE.md this suite is not a reliable
/// single-run gate; re-run before concluding a failure is real.
/// </para>
/// <para>
/// Both facts are load-bearing and both are assumptions until a socket says otherwise. Shadow record
/// item 8 IS the margin figure, so a paper account that does not serve <c>MaintMarginReq</c> would
/// make the capture silently null for the one column the item needs. And
/// <see cref="IbkrExecutionsClient.TryParseExecutionTime"/> parses a documented format that TWS's
/// own timezone setting can change — hence the verbatim string being the record, and hence this
/// test printing what was actually received rather than only asserting.
/// </para>
/// <para>
/// Read-only throughout: <c>reqAccountSummary</c> and <c>reqExecutions</c> and nothing else. No
/// order is placed, and the connection refuses to proceed on anything but a DU login.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
public sealed class LiveTwsAccountCaptureTests
{
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
    public void The_paper_account_serves_the_margin_tags_the_capture_records()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new LiveCaptureClient(endpoint.Host, endpoint.Port, clientId: 251);

        if (!client.ConnectToPaperAccount())
        {
            return;
        }

        var tags = client.RequestAccountSummary(
            31_100,
            "NetLiquidation,BuyingPower,AvailableFunds,ExcessLiquidity,GrossPositionValue,MaintMarginReq,InitMarginReq");

        Assert.True(
            tags.ContainsKey("MaintMarginReq"),
            "Shadow record item 8 IS the margin requirement. If TWS stops serving MaintMarginReq on a " +
            $"paper account the capture goes silently null for it. Received: {string.Join(", ", tags.Keys)}");

        Assert.True(
            tags.ContainsKey("InitMarginReq"),
            $"InitMarginReq was not served. Received: {string.Join(", ", tags.Keys)}");

        // The values are parsed by PaperCaptureService.ReadTag with InvariantCulture; a value it
        // cannot parse becomes null, which is honest but empty.
        Assert.True(
            decimal.TryParse(tags["MaintMarginReq"], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            $"MaintMarginReq came back as '{tags["MaintMarginReq"]}', which the capture cannot parse.");
    }

    [Fact]
    public void Executions_terminate_on_their_end_callback_and_carry_a_parseable_time()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new LiveCaptureClient(endpoint.Host, endpoint.Port, clientId: 252);

        if (!client.ConnectToPaperAccount())
        {
            return;
        }

        // Everything TWS still holds for this client. The lower bound is deliberately wide: a paper
        // account with no recent trades must still terminate the request, and that termination is
        // what the gateway's ExecutionsRequest completes on.
        var (ended, times) = client.RequestExecutions(
            31_200, DateTimeOffset.UtcNow.AddDays(-2).ToString("yyyyMMdd-HH:mm:ss"));

        Assert.True(
            ended,
            "reqExecutions must terminate with execDetailsEnd even when it returns nothing, or every " +
            "capture pass waits out its whole request timeout and then records a refusal for a " +
            "session that was fine.");

        foreach (var raw in times)
        {
            Assert.True(
                IbkrExecutionsClient.TryParseExecutionTime(raw) is not null,
                $"TWS reported an execution time of '{raw}', which the adapter does not recognise. The " +
                "verbatim string is still captured (executed_at_raw) so nothing is lost, but " +
                "executed_at will be null for every fill until TryParseExecutionTime learns this shape.");
        }
    }

    /// <summary>Minimal EWrapper for the two read-only requests above. Test-scope only.</summary>
    private sealed class LiveCaptureClient(string host, int port, int clientId) : DefaultEWrapper, IDisposable
    {
        private readonly EReaderMonitorSignal _signal = new();
        private readonly ConcurrentDictionary<string, string> _tags = new();
        private readonly ConcurrentBag<string> _executionTimes = [];
        private EClientSocket? _socket;
        private string[] _accounts = [];
        private int _executionsEnded;

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

        public IReadOnlyDictionary<string, string> RequestAccountSummary(int requestId, string tags)
        {
            _tags.Clear();
            _socket!.reqAccountSummary(requestId, "All", tags);
            Thread.Sleep(TimeSpan.FromSeconds(4));
            _socket.cancelAccountSummary(requestId);

            return _tags;
        }

        public (bool Ended, IReadOnlyList<string> Times) RequestExecutions(int requestId, string sinceUtc)
        {
            Interlocked.Exchange(ref _executionsEnded, 0);

            _socket!.reqExecutions(requestId, new ExecutionFilter { AcctCode = string.Empty, Time = sinceUtc });
            Thread.Sleep(TimeSpan.FromSeconds(5));

            return (Volatile.Read(ref _executionsEnded) == 1, [.. _executionTimes]);
        }

        public override void managedAccounts(string accountsList) =>
            Volatile.Write(
                ref _accounts,
                accountsList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        public override void accountSummary(int reqId, string account, string tag, string value, string currency) =>
            _tags[tag] = value;

        public override void execDetails(int reqId, Contract contract, Execution execution) =>
            _executionTimes.Add(execution.Time ?? string.Empty);

        public override void execDetailsEnd(int reqId) => Interlocked.Exchange(ref _executionsEnded, 1);

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
