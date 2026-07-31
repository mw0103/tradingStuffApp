using System.Collections.Concurrent;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public interface IExecutionEventPublisher
{
    Task PublishAsync(PublishedExecutionEvent executionEvent, CancellationToken cancellationToken);
}

public interface IPublishedExecutionEventStore
{
    IReadOnlyList<PublishedExecutionEvent> List();
}

public sealed class InMemoryExecutionEventPublisher(ILogger<InMemoryExecutionEventPublisher> logger)
    : IExecutionEventPublisher, IPublishedExecutionEventStore
{
    private readonly ConcurrentQueue<PublishedExecutionEvent> _events = new();

    public Task PublishAsync(PublishedExecutionEvent executionEvent, CancellationToken cancellationToken)
    {
        _events.Enqueue(executionEvent);
        logger.LogInformation(
            "Published execution event {EventName} for order {OrderId} with correlation {CorrelationId}",
            executionEvent.Name,
            executionEvent.OrderId,
            executionEvent.CorrelationId);

        return Task.CompletedTask;
    }

    public IReadOnlyList<PublishedExecutionEvent> List() => _events.ToArray();
}
