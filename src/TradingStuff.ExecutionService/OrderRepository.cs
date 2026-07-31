using System.Collections.Concurrent;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public interface IOrderRepository
{
    Task SaveAsync(ExecutionOrder order, CancellationToken cancellationToken);

    /// <summary>Records the venue's own id for an order.</summary>
    /// <remarks>
    /// Kept beside the order rather than on <see cref="ExecutionOrder"/> for the same reason conIds
    /// live in an adapter-side cache rather than on <c>OptionContract</c>: broker metadata does not
    /// belong in a record shared across services. It is a separate call from <see cref="SaveAsync"/>
    /// on purpose — every later status update would otherwise have to remember to carry it, and the
    /// one that forgot would drop the only handle by which the order can be cancelled.
    /// </remarks>
    Task LinkBrokerReferenceAsync(Guid orderId, string brokerReference, CancellationToken cancellationToken);

    /// <summary>The venue's id for an order, or null if the venue never returned one.</summary>
    Task<string?> GetBrokerReferenceAsync(Guid orderId, CancellationToken cancellationToken);

    Task<ExecutionOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionOrder>> ListAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, ExecutionOrder> _orders = new();
    private readonly ConcurrentDictionary<Guid, string> _brokerReferences = new();

    public Task SaveAsync(ExecutionOrder order, CancellationToken cancellationToken)
    {
        _orders[order.OrderId] = order;
        return Task.CompletedTask;
    }

    public Task LinkBrokerReferenceAsync(Guid orderId, string brokerReference, CancellationToken cancellationToken)
    {
        _brokerReferences[orderId] = brokerReference;
        return Task.CompletedTask;
    }

    public Task<string?> GetBrokerReferenceAsync(Guid orderId, CancellationToken cancellationToken)
    {
        _brokerReferences.TryGetValue(orderId, out var brokerReference);
        return Task.FromResult(brokerReference);
    }

    public Task<ExecutionOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        _orders.TryGetValue(orderId, out var order);
        return Task.FromResult(order);
    }

    public Task<IReadOnlyList<ExecutionOrder>> ListAsync(CancellationToken cancellationToken)
    {
        var orders = _orders.Values
            .OrderByDescending(order => order.CreatedAt)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ExecutionOrder>>(orders);
    }
}
