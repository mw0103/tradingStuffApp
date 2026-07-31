using System.Collections.Concurrent;

namespace TradingStuff.IbkrGateway;

/// <summary>A request awaiting completion via TWS callbacks.</summary>
internal interface IPendingRequest
{
    void Fail(Exception error);
}

/// <summary>
/// A request that accumulates callback items and completes on its matching <c>...End</c> callback —
/// the shape of <c>reqContractDetails</c> and <c>reqSecDefOptParams</c>.
/// </summary>
internal sealed class ListRequest<T> : IPendingRequest
{
    // RunContinuationsAsynchronously is essential: completions are signalled from the EReader pump
    // thread, and running continuations inline would stall message processing for every other
    // in-flight request.
    private readonly TaskCompletionSource<IReadOnlyList<T>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<T> _items = [];

    public Task<IReadOnlyList<T>> Task => _completion.Task;

    public void Add(T item)
    {
        lock (_items)
        {
            _items.Add(item);
        }
    }

    public void Complete()
    {
        lock (_items)
        {
            _completion.TrySetResult(_items.ToArray());
        }
    }

    public void Fail(Exception error) => _completion.TrySetException(error);
}

/// <summary>
/// Correlates in-flight TWS requests by request id, and allocates the ids.
/// </summary>
/// <remarks>
/// Every pending request must be reachable from both its success callback and the
/// <c>error</c> callback. A request that only the success path can complete hangs forever when TWS
/// rejects it — the single most common defect in TWS API adapters.
/// </remarks>
public sealed class IbkrRequestRegistry
{
    private readonly ConcurrentDictionary<int, IPendingRequest> _pending = new();
    private int _nextRequestId;

    /// <summary>
    /// Monotonic id shared by request ids, market-data ticker ids, and order ids.
    /// </summary>
    /// <remarks>
    /// One sequence for all three on purpose. TWS reports failures through a single
    /// <c>error(int id, ...)</c> callback that does not say whether the id is a request or an order,
    /// so overlapping sequences make routing ambiguous — and both would otherwise start at 1.
    /// IBKR's own guidance is to share the sequence, seeded from <c>nextValidId</c>.
    /// </remarks>
    public int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>
    /// Raises the sequence so the next allocation is at least TWS's <c>nextValidId</c>.
    /// </summary>
    /// <remarks>
    /// Order ids must be unique and increasing per connection; reusing one is treated as a
    /// modification of the existing order rather than a new one.
    /// </remarks>
    public void SeedFrom(int nextValidOrderId)
    {
        var floor = nextValidOrderId - 1;

        while (true)
        {
            var current = Volatile.Read(ref _nextRequestId);

            if (current >= floor)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _nextRequestId, floor, current) == current)
            {
                return;
            }
        }
    }

    public int InFlightCount => _pending.Count;

    internal void Register(int requestId, IPendingRequest request) => _pending[requestId] = request;

    internal void Remove(int requestId) => _pending.TryRemove(requestId, out _);

    internal T? Get<T>(int requestId)
        where T : class, IPendingRequest =>
        _pending.TryGetValue(requestId, out var request) ? request as T : null;

    /// <summary>Faults a single request. Returns false when the id is unknown or already settled.</summary>
    internal bool Fail(int requestId, Exception error)
    {
        if (!_pending.TryRemove(requestId, out var request))
        {
            return false;
        }

        request.Fail(error);
        return true;
    }

    /// <summary>Faults everything in flight — used when the socket drops.</summary>
    internal void FailAll(Exception error)
    {
        foreach (var requestId in _pending.Keys)
        {
            if (_pending.TryRemove(requestId, out var request))
            {
                request.Fail(error);
            }
        }
    }
}
