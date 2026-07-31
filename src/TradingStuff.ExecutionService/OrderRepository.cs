using System.Collections.Concurrent;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public interface IOrderRepository
{
    Task SaveAsync(ExecutionOrder order, CancellationToken cancellationToken);

    Task<ExecutionOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionOrder>> ListAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, ExecutionOrder> _orders = new();

    public Task SaveAsync(ExecutionOrder order, CancellationToken cancellationToken)
    {
        _orders[order.OrderId] = order;
        return Task.CompletedTask;
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
