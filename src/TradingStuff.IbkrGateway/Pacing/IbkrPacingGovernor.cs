using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace TradingStuff.IbkrGateway.Pacing;

/// <summary>How a socket message is scheduled when the message budget is exhausted.</summary>
public enum SocketMessageClass
{
    /// <summary>Waits its turn when the token bucket is empty.</summary>
    Normal = 0,

    /// <summary>
    /// Never waits — order placement and cancellation must not queue behind data requests — but
    /// still consumes tokens, driving the balance negative so subsequent normal traffic pays for it.
    /// </summary>
    Order = 1,
}

/// <summary>Which slice of the market-data-line budget an acquisition draws from.</summary>
public enum LineClass
{
    /// <summary>Execution-path transient quotes. May use the full line cap, including the reserve.</summary>
    Execution = 0,

    /// <summary>
    /// Research recording and other non-execution consumers. Capped below the execution reserve so
    /// a full recorder can never starve pre-trade quoting.
    /// </summary>
    Research = 1,
}

/// <summary>A held market-data line. Dispose (or pass back through the socket) to release it.</summary>
public sealed class LineLease : IDisposable
{
    private IbkrPacingGovernor? _owner;

    internal LineLease(IbkrPacingGovernor owner, LineClass lineClass)
    {
        _owner = owner;
        Class = lineClass;
    }

    public LineClass Class { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLine(Class);
}

/// <summary>
/// The historical budget cannot admit the request within the acquire timeout. Carries how long the
/// caller should back off — the future HTTP surface maps this to 429 + Retry-After.
/// </summary>
public sealed class IbkrPacingRejectedException(TimeSpan retryAfter)
    : TimeoutException($"Historical request budget exhausted; retry after {retryAfter.TotalSeconds:F0}s.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>Pacing budgets, under <c>IBKR:Pacing</c>. Defaults sit ~10% inside TWS's documented limits.</summary>
public sealed class IbkrPacingOptions
{
    /// <summary>TWS disconnects a client exceeding ~50 messages/second.</summary>
    public double MessagesPerSecond { get; set; } = 45;

    /// <summary>Bucket capacity: how many messages may burst before the rate applies.</summary>
    public int MessageBurst { get; set; } = 45;

    /// <summary>TWS paces historical data at ~60 requests per 10 minutes; BID_ASK counts double.</summary>
    public int HistoricalWindowRequests { get; set; } = 54;

    public int HistoricalWindowMinutes { get; set; } = 10;

    /// <summary>An identical historical request within this window is a pacing violation.</summary>
    public int IdenticalRequestCooldownSeconds { get; set; } = 15;

    /// <summary>Six or more requests for the same contract within two seconds is a violation.</summary>
    public int SameContractWindowSeconds { get; set; } = 2;

    public int SameContractWindowRequests { get; set; } = 5;

    /// <summary>
    /// Ceiling on concurrently held market-data lines. The account default is 100, shared across
    /// every API client and TWS watchlist of the username, so this stays below it.
    /// </summary>
    public int LineCap { get; set; } = 90;

    /// <summary>Lines only <see cref="LineClass.Execution"/> acquisitions may use.</summary>
    public int ExecutionReservedLines { get; set; } = 10;

    /// <summary>Give up waiting for budget after this long rather than queueing unboundedly.</summary>
    public int AcquireTimeoutSeconds { get; set; } = 30;

    /// <summary>Concurrent quote subscriptions per batch request — well inside the line budget.</summary>
    public int QuoteFanOutLimit { get; set; } = 16;
}

/// <summary>
/// The single authority for every TWS pacing constraint: the socket message rate, the historical
/// request window, and the market-data-line budget.
/// </summary>
/// <remarks>
/// Every outbound socket call must pass through this class (via <see cref="PacedSocket"/>). TWS
/// enforces these limits by erroring or disconnecting; enforcing them here first turns a
/// connection-killing violation into a short wait.
/// </remarks>
public sealed class IbkrPacingGovernor
{
    private readonly IbkrPacingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<IbkrPacingGovernor> _logger;

    private readonly Lock _gate = new();

    // Message token bucket. Balance may go negative: Order-class traffic takes tokens without
    // waiting, and Normal-class traffic then waits for the refill to cover the debt.
    private double _messageTokens;
    private DateTimeOffset _lastRefill;

    // Historical request window: (completion time, cost) entries inside the sliding window, plus
    // per-key cooldowns and a per-contract short window.
    private readonly Queue<(DateTimeOffset At, int Cost)> _historicalWindow = new();
    private int _historicalWindowCost;
    private readonly Dictionary<string, DateTimeOffset> _identicalRequests = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _contractWindows = [];

    // Line ledger.
    private int _executionLines;
    private int _researchLines;
    private readonly List<LineWaiter> _lineWaiters = [];

    private readonly Histogram<double> _waitHistogram;
    private readonly Counter<long> _rejections;

    public IbkrPacingGovernor(
        IOptions<IbkrOptions> options,
        TimeProvider timeProvider,
        IMeterFactory meterFactory,
        ILogger<IbkrPacingGovernor> logger)
    {
        _options = options.Value.Pacing;
        _time = timeProvider;
        _logger = logger;
        _messageTokens = _options.MessageBurst;
        _lastRefill = _time.GetUtcNow();

        var meter = meterFactory.Create("TradingStuff.IbkrGateway");
        _waitHistogram = meter.CreateHistogram<double>("ibkr.pacing.wait", unit: "ms");
        _rejections = meter.CreateCounter<long>("ibkr.pacing.rejections");
        meter.CreateObservableGauge("ibkr.lines.in_use", () => _executionLines + _researchLines);
        meter.CreateObservableGauge("ibkr.lines.cap", () => _options.LineCap);
    }

    private sealed record LineWaiter(LineClass Class, TaskCompletionSource<LineLease> Completion);

    // ---- socket message rate ------------------------------------------------------------------

    public async Task AcquireMessagesAsync(int count, SocketMessageClass messageClass, CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var deadline = started + TimeSpan.FromSeconds(_options.AcquireTimeoutSeconds);

        while (true)
        {
            TimeSpan wait;

            lock (_gate)
            {
                RefillMessageTokens();

                // Order-class jumps the queue, but only down to a floor of one burst of debt:
                // an unbounded bypass would let a storm of jumps exceed the wire rate outright.
                var admit = messageClass == SocketMessageClass.Order
                    ? _messageTokens > -_options.MessageBurst
                    : _messageTokens >= count;

                if (admit)
                {
                    _messageTokens -= count;
                    _waitHistogram.Record((_time.GetUtcNow() - started).TotalMilliseconds);
                    return;
                }

                wait = TimeSpan.FromSeconds((count - _messageTokens) / _options.MessagesPerSecond);
            }

            if (_time.GetUtcNow() + wait > deadline)
            {
                _rejections.Add(1);
                throw new TimeoutException(
                    $"Socket message budget not available within {_options.AcquireTimeoutSeconds}s.");
            }

            await Task.Delay(wait, _time, cancellationToken);
        }
    }

    private void RefillMessageTokens()
    {
        var now = _time.GetUtcNow();
        var elapsed = (now - _lastRefill).TotalSeconds;

        if (elapsed <= 0)
        {
            return;
        }

        _messageTokens = Math.Min(_options.MessageBurst, _messageTokens + (elapsed * _options.MessagesPerSecond));
        _lastRefill = now;
    }

    // ---- historical request window ------------------------------------------------------------

    /// <summary>
    /// Waits until a historical data request is safe to send, then records it against the window.
    /// </summary>
    /// <param name="requestKey">
    /// The exact request identity (contract + endTime + duration + whatToShow + barSize + useRTH) —
    /// repeating it inside the cooldown is a pacing violation.
    /// </param>
    /// <param name="contractKey">Contract + exchange + tick type, for the per-contract short window.</param>
    /// <param name="countsDouble">BID_ASK requests count twice against the window.</param>
    public async Task AcquireHistoricalAsync(
        string requestKey,
        string contractKey,
        bool countsDouble,
        CancellationToken cancellationToken)
    {
        var started = _time.GetUtcNow();
        var deadline = started + TimeSpan.FromSeconds(_options.AcquireTimeoutSeconds);
        var cost = countsDouble ? 2 : 1;

        while (true)
        {
            TimeSpan wait;

            lock (_gate)
            {
                var now = _time.GetUtcNow();
                var earliest = EarliestHistoricalSlot(now, requestKey, contractKey, cost);

                if (earliest <= now)
                {
                    _historicalWindow.Enqueue((now, cost));
                    _historicalWindowCost += cost;
                    _identicalRequests[requestKey] = now;
                    ContractWindow(contractKey).Enqueue(now);
                    PruneHistoricalStateLocked(now);
                    _waitHistogram.Record((now - started).TotalMilliseconds);
                    return;
                }

                wait = earliest - now;
            }

            if (_time.GetUtcNow() + wait > deadline)
            {
                // Rejected immediately rather than parking the caller for minutes: the backfill
                // coordinator is built to back off on this and come back at RetryAfter.
                _rejections.Add(1);
                throw new IbkrPacingRejectedException(wait);
            }

            await Task.Delay(wait, _time, cancellationToken);
        }
    }

    private DateTimeOffset EarliestHistoricalSlot(DateTimeOffset now, string requestKey, string contractKey, int cost)
    {
        var window = TimeSpan.FromMinutes(_options.HistoricalWindowMinutes);

        while (_historicalWindow.TryPeek(out var oldest) && oldest.At <= now - window)
        {
            _historicalWindow.Dequeue();
            _historicalWindowCost -= oldest.Cost;
        }

        var earliest = now;

        if (_historicalWindowCost + cost > _options.HistoricalWindowRequests)
        {
            // Freed when enough of the oldest entries age out of the sliding window.
            var needed = (_historicalWindowCost + cost) - _options.HistoricalWindowRequests;
            var freed = 0;

            foreach (var entry in _historicalWindow)
            {
                freed += entry.Cost;

                if (freed >= needed)
                {
                    earliest = Max(earliest, entry.At + window);
                    break;
                }
            }
        }

        if (_identicalRequests.TryGetValue(requestKey, out var lastIdentical))
        {
            earliest = Max(earliest, lastIdentical + TimeSpan.FromSeconds(_options.IdenticalRequestCooldownSeconds));
        }

        var contractWindow = ContractWindow(contractKey);
        var shortWindow = TimeSpan.FromSeconds(_options.SameContractWindowSeconds);

        while (contractWindow.TryPeek(out var oldest) && oldest <= now - shortWindow)
        {
            contractWindow.Dequeue();
        }

        if (contractWindow.Count >= _options.SameContractWindowRequests)
        {
            earliest = Max(earliest, contractWindow.Peek() + shortWindow);
        }

        return earliest;
    }

    /// <summary>
    /// Drops entries that can no longer affect any pacing decision, so a long-running gateway
    /// walking thousands of backfill slices does not grow these maps without bound.
    /// </summary>
    private void PruneHistoricalStateLocked(DateTimeOffset now)
    {
        var cooldownFloor = now - TimeSpan.FromSeconds(_options.IdenticalRequestCooldownSeconds);
        var shortWindowFloor = now - TimeSpan.FromSeconds(_options.SameContractWindowSeconds);

        foreach (var (key, lastSeen) in _identicalRequests)
        {
            if (lastSeen < cooldownFloor)
            {
                _identicalRequests.Remove(key);
            }
        }

        foreach (var (key, window) in _contractWindows)
        {
            while (window.TryPeek(out var oldest) && oldest <= shortWindowFloor)
            {
                window.Dequeue();
            }

            if (window.Count == 0)
            {
                _contractWindows.Remove(key);
            }
        }
    }

    private Queue<DateTimeOffset> ContractWindow(string contractKey)
    {
        if (!_contractWindows.TryGetValue(contractKey, out var window))
        {
            window = new Queue<DateTimeOffset>();
            _contractWindows[contractKey] = window;
        }

        return window;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    // ---- market-data line ledger --------------------------------------------------------------

    public async Task<LineLease> AcquireLineAsync(LineClass lineClass, CancellationToken cancellationToken)
    {
        TaskCompletionSource<LineLease> completion;

        lock (_gate)
        {
            if (TryAcquireLineLocked(lineClass))
            {
                return new LineLease(this, lineClass);
            }

            completion = new TaskCompletionSource<LineLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lineWaiters.Add(new LineWaiter(lineClass, completion));
        }

        try
        {
            return await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(_options.AcquireTimeoutSeconds), _time, cancellationToken);
        }
        catch (Exception exception)
        {
            // Timeout OR caller cancellation (HTTP aborts cancel queued quote waiters routinely).
            // Either way the waiter must be withdrawn, or a later grant hands a line to a task
            // nobody awaits and the ledger leaks until process restart.
            lock (_gate)
            {
                _lineWaiters.RemoveAll(waiter => waiter.Completion == completion);
            }

            // A grant can race the abandonment. Cancelling the completion source decides the race
            // atomically: if the grant already won, take the lease and hand it straight back.
            if (!completion.TrySetCanceled())
            {
                (await completion.Task).Dispose();
            }

            if (exception is TimeoutException)
            {
                _rejections.Add(1);
                throw new TimeoutException(
                    $"No market-data line available within {_options.AcquireTimeoutSeconds}s ({DescribeLines()}).");
            }

            throw;
        }
    }

    /// <summary>
    /// Zeroes the line ledger because the underlying socket's own subscription state was just
    /// invalidated — a fresh <c>EClientSocket</c> after a reconnect holds zero real TWS lines
    /// regardless of what this ledger thought a moment ago, and TWS's 1101 notice ("connectivity
    /// restored, data lost") says the same for a socket that never dropped. Call ONLY from the
    /// reconnect/replay path; any <see cref="LineLease"/> issued before the reset still decrements
    /// on <see cref="Dispose"/>, which the existing clamp-at-zero in <see cref="ReleaseLine"/>
    /// already tolerates safely.
    /// </summary>
    /// <remarks>
    /// Deliberately does not attempt to re-grant queued waiters against the freed capacity —
    /// leaving them queued is safe (they will simply wait slightly longer) and reconnect is rare
    /// enough that the added complexity is not worth it for a first cut.
    /// </remarks>
    public void ResetLineLedgerForReconnect()
    {
        lock (_gate)
        {
            _executionLines = 0;
            _researchLines = 0;
        }
    }

    private bool TryAcquireLineLocked(LineClass lineClass)
    {
        var total = _executionLines + _researchLines;

        if (total >= _options.LineCap)
        {
            return false;
        }

        if (lineClass == LineClass.Research &&
            _researchLines >= _options.LineCap - _options.ExecutionReservedLines)
        {
            return false;
        }

        if (lineClass == LineClass.Execution)
        {
            _executionLines++;
        }
        else
        {
            _researchLines++;
        }

        return true;
    }

    internal void ReleaseLine(LineClass lineClass)
    {
        LineWaiter? granted = null;

        lock (_gate)
        {
            if (lineClass == LineClass.Execution)
            {
                _executionLines = Math.Max(0, _executionLines - 1);
            }
            else
            {
                _researchLines = Math.Max(0, _researchLines - 1);
            }

            for (var index = 0; index < _lineWaiters.Count; index++)
            {
                if (TryAcquireLineLocked(_lineWaiters[index].Class))
                {
                    granted = _lineWaiters[index];
                    _lineWaiters.RemoveAt(index);
                    break;
                }
            }
        }

        if (granted is not null && !granted.Completion.TrySetResult(new LineLease(this, granted.Class)))
        {
            // The waiter timed out and cancelled its completion source after we counted the line
            // for it: give the line back, which also wakes the next eligible waiter.
            ReleaseLine(granted.Class);
        }
    }

    public LineBudgetSnapshot GetLineBudget()
    {
        lock (_gate)
        {
            return new LineBudgetSnapshot(
                _options.LineCap,
                _executionLines,
                _researchLines,
                _options.ExecutionReservedLines,
                _lineWaiters.Count);
        }
    }

    private string DescribeLines()
    {
        lock (_gate)
        {
            return $"execution {_executionLines}, research {_researchLines}, cap {_options.LineCap}";
        }
    }
}

/// <summary>Point-in-time view of the market-data-line ledger.</summary>
public sealed record LineBudgetSnapshot(
    int Cap,
    int ExecutionInUse,
    int ResearchInUse,
    int ExecutionReserved,
    int Waiting);
