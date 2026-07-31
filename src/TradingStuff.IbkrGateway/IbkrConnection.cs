using IBApi;
using Microsoft.Extensions.Options;
using TradingStuff.IbkrGateway.Pacing;

namespace TradingStuff.IbkrGateway;

public sealed record IbkrConnectionStatus(
    bool Connected,
    string Host,
    int Port,
    int ClientId,
    int ServerVersion,
    IReadOnlyList<string> ManagedAccounts,
    int MarketDataType,
    bool TradingPermitted,
    string? TradingBlockedReason,
    string? LastError,
    DateTimeOffset? ConnectedAt,
    int InFlightRequests);

/// <summary>
/// Owns the process's single TWS socket: connection, the EReader pump, reconnect, and the
/// paper-account safety gate.
/// </summary>
/// <remarks>
/// Exactly one of these exists per service, and only this service connects to TWS. A TWS connection
/// is stateful and single-owner per client id — request ids, ticker ids, and the order id sequence
/// are all connection-scoped. Two services connecting independently against one account produce two
/// independent order id sequences, which is how orders get orphaned and fills get lost.
/// </remarks>
public sealed class IbkrConnection : IHostedService, IDisposable
{
    private readonly IbkrOptions _options;
    private readonly IbkrRequestRegistry _registry;
    private readonly IbkrClientWrapper _wrapper;
    private readonly IbkrPacingGovernor _pacingGovernor;
    private readonly ILogger<IbkrConnection> _logger;
    private readonly Lock _gate = new();

    /// <summary>
    /// Raised whenever every standing subscription must be re-issued: a brand-new
    /// <c>EClientSocket</c> after a reconnect (nothing is subscribed on it yet), or TWS's 1101
    /// notice on a socket that never dropped ("connectivity restored, data lost"). Handlers run on
    /// whatever thread raised the underlying event — including the EReader pump — so they must not
    /// block; fire-and-forget the actual replay work.
    /// </summary>
    public event Action? SubscriptionsMustReplay;

    private EClientSocket? _client;
    private EReaderMonitorSignal? _signal;
    private CancellationTokenSource? _lifetime;
    private Task? _connectionLoop;

    private string[] _managedAccounts = [];
    private bool _tradingPermitted;
    private string? _tradingBlockedReason = "Not connected.";
    private string? _lastError;
    private DateTimeOffset? _connectedAt;

    /// <summary>
    /// When the most recent session was established. Unlike <see cref="_connectedAt"/> this is not
    /// cleared on disconnect, so the reconnect loop can tell how long the dead session lasted.
    /// </summary>
    private DateTimeOffset? _lastSessionStartedAt;

    private int _nextValidOrderId = -1;
    private int _disposed;

    public IbkrConnection(
        IOptions<IbkrOptions> options,
        IbkrRequestRegistry registry,
        IbkrClientWrapper wrapper,
        IbkrPacingGovernor pacingGovernor,
        ILogger<IbkrConnection> logger)
    {
        _options = options.Value;
        _registry = registry;
        _wrapper = wrapper;
        _pacingGovernor = pacingGovernor;
        _logger = logger;

        _wrapper.ManagedAccountsReceived += OnManagedAccounts;
        _wrapper.NextValidIdReceived += orderId =>
        {
            Interlocked.Exchange(ref _nextValidOrderId, orderId);

            // Requests and orders share one id sequence, so it must start above whatever TWS has
            // already issued on this account.
            _registry.SeedFrom(orderId);
        };
        _wrapper.ConnectionClosedReceived += OnConnectionClosed;
        _wrapper.ConnectivityChanged += OnConnectivityChanged;
    }

    public bool IsConnected => _client?.IsConnected() == true;

    public IbkrRequestRegistry Registry => _registry;

    public IbkrConnectionStatus GetStatus()
    {
        lock (_gate)
        {
            return new IbkrConnectionStatus(
                IsConnected,
                _options.Host,
                _options.Port,
                _options.ClientId,
                _client?.ServerVersion ?? 0,
                _managedAccounts,
                _options.MarketDataType,
                _tradingPermitted,
                _tradingBlockedReason,
                _lastError,
                _connectedAt,
                _registry.InFlightCount);
        }
    }

    /// <summary>The live socket, or a thrown <see cref="IbkrConnectionException"/> if there isn't one.</summary>
    public EClientSocket RequireClient()
    {
        var client = _client;

        if (client is null || !client.IsConnected())
        {
            throw new IbkrConnectionException(
                $"Not connected to TWS at {_options.Host}:{_options.Port}. " +
                "Check that TWS/IB Gateway is running, that 'Enable ActiveX and Socket Clients' is on, " +
                "and that this host is listed under Trusted IPs.");
        }

        return client;
    }

    /// <summary>
    /// Gate that every real order-placement path must call first.
    /// </summary>
    /// <remarks>
    /// <see cref="IbkrOrderClient"/> is the only caller, and calls it before touching the socket. The
    /// read-only account and market-data paths deliberately do not: refusing to read data helps
    /// nobody, and blocking placement is what matters.
    /// </remarks>
    public void EnsureTradingPermitted()
    {
        lock (_gate)
        {
            if (!_tradingPermitted)
            {
                throw new InvalidOperationException(
                    $"Order placement is blocked: {_tradingBlockedReason ?? "trading is not permitted."}");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Deliberately not awaited: TWS being down must not stop the service from starting. It
        // reports unhealthy and keeps retrying instead.
        _connectionLoop = Task.Run(() => RunConnectionLoopAsync(_lifetime.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
        {
            try
            {
                await _lifetime.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Dispose already ran; nothing left to cancel.
            }
        }

        Disconnect();

        if (_connectionLoop is not null)
        {
            await Task.WhenAny(_connectionLoop, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds);
        var maxDelay = TimeSpan.FromSeconds(_options.MaxReconnectDelaySeconds);

        // A session that dies almost immediately counts as a failure for backoff purposes. TWS can
        // accept the socket and then reset it — while a modal dialog is open, when the client id is
        // already in use, or when the API connection limit is reached. Resetting the delay on a
        // "successful" connect would then reconnect at the base interval forever, hammering TWS.
        var minimumHealthySession = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                var previousSessionStart = _lastSessionStartedAt;

                if (TryConnect() && !WasShortLived(previousSessionStart, minimumHealthySession))
                {
                    delay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds);
                }
                else
                {
                    // Backoff: TWS restarts daily on a schedule and is simply absent for a while.
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));

                    if (WasShortLived(previousSessionStart, minimumHealthySession))
                    {
                        _logger.LogWarning(
                            "The previous TWS session lasted under {Seconds}s. Backing off to {Delay}s. " +
                            "Check TWS for a modal dialog awaiting input, a duplicate client id, or the " +
                            "API connection limit.",
                            minimumHealthySession.TotalSeconds,
                            delay.TotalSeconds);
                    }
                }
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>True when a session was established but died before proving itself stable.</summary>
    private static bool WasShortLived(DateTimeOffset? sessionStart, TimeSpan threshold) =>
        sessionStart is { } start && DateTimeOffset.UtcNow - start < threshold;

    private bool TryConnect()
    {
        try
        {
            Disconnect();

            var signal = new EReaderMonitorSignal();
            var client = new EClientSocket(_wrapper, signal);

            _logger.LogInformation(
                "Connecting to TWS at {Host}:{Port} as client {ClientId}.",
                _options.Host,
                _options.Port,
                _options.ClientId);

            // Blocking: with AsyncEConnect false (the default) eConnect performs the handshake
            // inline and calls startApi() itself. This runs on the connection loop, never a request.
            client.eConnect(_options.Host, _options.Port, _options.ClientId);

            if (!client.IsConnected())
            {
                SetLastError($"Connection to {_options.Host}:{_options.Port} was refused.");
                return false;
            }

            // The pump must run before anything else: no callback is delivered until processMsgs
            // runs, so without it the connection looks healthy and every request hangs.
            var reader = new EReader(client, signal);
            reader.Start();

            var pump = new Thread(() => RunMessagePump(client, reader, signal))
            {
                IsBackground = true,
                Name = "ibkr-ereader-pump",
            };
            pump.Start();

            lock (_gate)
            {
                _client = client;
                _signal = signal;
                _connectedAt = DateTimeOffset.UtcNow;
                _lastSessionStartedAt = _connectedAt;
                _lastError = null;
            }

            client.reqMarketDataType(_options.MarketDataType);

            _logger.LogInformation(
                "Connected to TWS (server version {ServerVersion}), market data type {MarketDataType}.",
                client.ServerVersion,
                _options.MarketDataType);

            return true;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            _logger.LogWarning(ex, "Failed to connect to TWS at {Host}:{Port}.", _options.Host, _options.Port);
            return false;
        }
    }

    private void RunMessagePump(EClientSocket client, EReader reader, EReaderMonitorSignal signal)
    {
        try
        {
            while (client.IsConnected())
            {
                signal.waitForSignal();
                reader.processMsgs();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TWS message pump stopped.");
            SetLastError(ex.Message);
        }
        finally
        {
            _registry.FailAll(new IbkrConnectionException("The TWS message pump stopped."));
        }
    }

    private void OnManagedAccounts(string accountsList)
    {
        var accounts = accountsList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Paper accounts are DU-prefixed; a U prefix is live money. Without this check a misconfigured
        // port (7496 instead of 7497) silently points the whole service at a funded account.
        var livePresent = accounts.Any(account => !account.StartsWith("DU", StringComparison.OrdinalIgnoreCase));

        lock (_gate)
        {
            _managedAccounts = accounts;

            if (livePresent && !_options.AllowLiveTrading)
            {
                _tradingPermitted = false;
                _tradingBlockedReason =
                    "Connected account is not a paper (DU) account and IBKR:AllowLiveTrading is false.";
            }
            else if (livePresent)
            {
                _tradingPermitted = true;
                _tradingBlockedReason = null;
            }
            else
            {
                _tradingPermitted = true;
                _tradingBlockedReason = null;
            }
        }

        if (livePresent && !_options.AllowLiveTrading)
        {
            // Loud on purpose. Market data still flows — refusing to read data helps nobody — but no
            // order may leave this process.
            _logger.LogCritical(
                "TWS reported a non-paper account while IBKR:AllowLiveTrading is false. " +
                "Market data is available; order placement is BLOCKED. " +
                "Expected a DU-prefixed paper account on port 7497 (TWS) or 4002 (Gateway).");
        }
        else
        {
            _logger.LogInformation("Connected to {Count} managed account(s).", accounts.Length);
        }

        // managedAccounts is TWS's confirmation that a fresh startApi handshake completed. A brand
        // new EClientSocket has zero real standing subscriptions no matter what the pacing
        // governor's ledger still thinks, so the ledger is reset before anything replays leases
        // against it.
        _pacingGovernor.ResetLineLedgerForReconnect();
        SubscriptionsMustReplay?.Invoke();
    }

    private void OnConnectionClosed()
    {
        lock (_gate)
        {
            _connectedAt = null;
            _tradingPermitted = false;
            _tradingBlockedReason = "Not connected.";
        }
    }

    private void OnConnectivityChanged(int errorCode)
    {
        if (errorCode == IbkrErrorCodes.ConnectivityRestoredDataLost)
        {
            // 1101: the TWS-to-exchange link blipped and recovered without OUR socket ever
            // dropping (connectionClosed never fired), but every streaming subscription on it is
            // gone regardless. Same remedy as a fresh connect: reset the ledger and replay.
            _logger.LogWarning("TWS connectivity restored with data lost; streaming subscriptions must be re-established.");
            _pacingGovernor.ResetLineLedgerForReconnect();
            SubscriptionsMustReplay?.Invoke();
        }
    }

    private void SetLastError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    private void Disconnect()
    {
        EClientSocket? client;

        lock (_gate)
        {
            client = _client;
            _client = null;
            _signal = null;
        }

        if (client is null)
        {
            return;
        }

        try
        {
            client.eDisconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring error while disconnecting from TWS.");
        }
    }

    public void Dispose()
    {
        // Dispose can be reached more than once: this instance is registered both as a singleton and
        // as the hosted service, and an exception thrown here surfaces as an unhandled crash during
        // host shutdown rather than a clean stop.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a prior shutdown path.
        }

        _lifetime?.Dispose();
        Disconnect();
    }
}
