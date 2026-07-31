using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public sealed class ExecutionWorkflow(
    OrderRequestValidator validator,
    IMarketDataClient marketDataClient,
    IRiskClient riskClient,
    IPortfolioProvider portfolioProvider,
    PaperExecutionEngine paperExecutionEngine,
    IOrderRepository orderRepository,
    IExecutionEventPublisher eventPublisher)
{
    public async Task<SubmitOrderResponse> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = validator.Validate(request);
        if (validationErrors.Count > 0)
        {
            throw new OrderValidationException(validationErrors);
        }

        var orderId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var events = new List<OrderLifecycleEvent>
        {
            NewEvent(orderId, OrderLifecycleStatus.Received, "Order received.", correlationId, null)
        };

        var quoteResponse = await marketDataClient.GetQuotesAsync(new MarketDataQuoteRequest(request.Legs), cancellationToken);
        var portfolio = await portfolioProvider.GetPortfolioAsync(request.AccountId, cancellationToken);
        var riskRequest = new RiskEvaluationRequest(request, portfolio, quoteResponse.Quotes, DateTimeOffset.UtcNow);
        var riskDecision = await riskClient.EvaluateAsync(riskRequest, cancellationToken);

        if (riskDecision.Decision == RiskDecision.Rejected)
        {
            events.Add(NewEvent(orderId, OrderLifecycleStatus.RiskRejected, "Risk service rejected the order.", correlationId, events[^1].EventId));

            var rejectedOrder = new ExecutionOrder(
                orderId,
                correlationId,
                request,
                OrderLifecycleStatus.RiskRejected,
                quoteResponse.Quotes,
                riskDecision,
                [],
                events,
                now,
                DateTimeOffset.UtcNow);

            await orderRepository.SaveAsync(rejectedOrder, cancellationToken);
            await PublishLifecycleAsync(rejectedOrder, cancellationToken);

            return new SubmitOrderResponse(orderId, correlationId, rejectedOrder.Status, riskDecision, []);
        }

        events.Add(NewEvent(orderId, OrderLifecycleStatus.RiskApproved, "Risk service approved the order.", correlationId, events[^1].EventId));
        events.Add(NewEvent(orderId, OrderLifecycleStatus.Submitted, "Order submitted to paper execution engine.", correlationId, events[^1].EventId));

        var paperResult = paperExecutionEngine.Execute(orderId, request, quoteResponse.Quotes);
        if (paperResult.Fills.Count > 0)
        {
            events.Add(NewEvent(orderId, paperResult.Status, $"Paper engine produced {paperResult.Fills.Count} fill(s).", correlationId, events[^1].EventId));
        }

        var acceptedOrder = new ExecutionOrder(
            orderId,
            correlationId,
            request,
            paperResult.Status,
            quoteResponse.Quotes,
            riskDecision,
            paperResult.Fills,
            events,
            now,
            DateTimeOffset.UtcNow);

        await orderRepository.SaveAsync(acceptedOrder, cancellationToken);
        await PublishLifecycleAsync(acceptedOrder, cancellationToken);

        return new SubmitOrderResponse(orderId, correlationId, acceptedOrder.Status, riskDecision, paperResult.Fills);
    }

    public Task<ExecutionOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        orderRepository.GetAsync(orderId, cancellationToken);

    public async Task<ExecutionOrder?> CancelAsync(Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orderRepository.GetAsync(orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        if (order.Status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled or OrderLifecycleStatus.RiskRejected)
        {
            return order;
        }

        var cancelEvent = NewEvent(
            orderId,
            OrderLifecycleStatus.Cancelled,
            string.IsNullOrWhiteSpace(request.Reason) ? "Order cancelled." : request.Reason,
            order.CorrelationId,
            order.Events.LastOrDefault()?.EventId);

        var updated = order with
        {
            Status = OrderLifecycleStatus.Cancelled,
            Events = order.Events.Concat([cancelEvent]).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await orderRepository.SaveAsync(updated, cancellationToken);
        await eventPublisher.PublishAsync(ToPublishedEvent(cancelEvent, updated), cancellationToken);

        return updated;
    }

    public async Task<ExecutionOrder?> ReplaceAsync(Guid orderId, ReplaceOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orderRepository.GetAsync(orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        if (order.Status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled or OrderLifecycleStatus.RiskRejected)
        {
            return order;
        }

        var replacedRequest = order.Request with
        {
            LimitPrice = request.LimitPrice ?? order.Request.LimitPrice,
            StopPrice = request.StopPrice ?? order.Request.StopPrice,
            TimeInForce = request.TimeInForce ?? order.Request.TimeInForce
        };

        var replaceEvent = NewEvent(
            orderId,
            OrderLifecycleStatus.ReplaceRequested,
            "Order replace accepted in paper state.",
            order.CorrelationId,
            order.Events.LastOrDefault()?.EventId);

        var updated = order with
        {
            Request = replacedRequest,
            Status = OrderLifecycleStatus.ReplaceRequested,
            Events = order.Events.Concat([replaceEvent]).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await orderRepository.SaveAsync(updated, cancellationToken);
        await eventPublisher.PublishAsync(ToPublishedEvent(replaceEvent, updated), cancellationToken);

        return updated;
    }

    private async Task PublishLifecycleAsync(ExecutionOrder order, CancellationToken cancellationToken)
    {
        foreach (var lifecycleEvent in order.Events)
        {
            await eventPublisher.PublishAsync(ToPublishedEvent(lifecycleEvent, order), cancellationToken);
        }
    }

    private static OrderLifecycleEvent NewEvent(
        Guid orderId,
        OrderLifecycleStatus status,
        string message,
        Guid correlationId,
        Guid? causationId) =>
        new(Guid.NewGuid(), orderId, status, message, DateTimeOffset.UtcNow, correlationId, causationId);

    private static PublishedExecutionEvent ToPublishedEvent(OrderLifecycleEvent lifecycleEvent, ExecutionOrder order) =>
        new(
            lifecycleEvent.Status.ToString(),
            lifecycleEvent.EventId,
            lifecycleEvent.OrderId,
            lifecycleEvent.CorrelationId,
            lifecycleEvent.OccurredAt,
            new
            {
                order.Request.AccountId,
                order.Request.Strategy,
                order.Status,
                lifecycleEvent.Message,
                FillCount = order.Fills.Count,
                RiskDecision = order.RiskDecision?.Decision
            });
}

public sealed class OrderValidationException(IReadOnlyList<string> errors) : Exception("Order request validation failed.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
