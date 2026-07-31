using System.Security.Cryptography;
using System.Text;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public sealed class ExecutionWorkflow(
    OrderRequestValidator validator,
    IMarketDataClient marketDataClient,
    IRiskClient riskClient,
    IPortfolioProvider portfolioProvider,
    IOrderRouter orderRouter,
    IOrderRepository orderRepository,
    IExecutionEventPublisher eventPublisher,
    ILogger<ExecutionWorkflow> logger)
{
    public async Task<SubmitOrderResponse> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = validator.Validate(request);
        if (validationErrors.Count > 0)
        {
            throw new OrderValidationException(validationErrors);
        }

        var orderId = DeriveOrderId(request);

        if (await orderRepository.GetAsync(orderId, cancellationToken) is { } alreadySubmitted)
        {
            return await ResubmitAsync(alreadySubmitted, cancellationToken);
        }

        if (request.ClientOrderId is null)
        {
            logger.LogWarning(
                "Order {OrderId} carries no ClientOrderId, so a resubmission of it cannot be " +
                "recognised as one and the gateway's duplicate-transmission guard has no stable id " +
                "to key on. Supply one for anything routed to a broker.",
                orderId);
        }

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
            await PublishLifecycleAsync(rejectedOrder, 0, cancellationToken);

            return new SubmitOrderResponse(orderId, correlationId, rejectedOrder.Status, riskDecision, []);
        }

        events.Add(NewEvent(orderId, OrderLifecycleStatus.RiskApproved, "Risk service approved the order.", correlationId, events[^1].EventId));
        events.Add(NewEvent(orderId, OrderLifecycleStatus.Submitted, $"Order submitted via the '{orderRouter.Name}' router.", correlationId, events[^1].EventId));

        // Persisted BEFORE the router is called, and that ordering is the safety property rather than
        // a tidiness preference. Saving afterwards leaves one crash window in which the venue holds a
        // live order this service has never heard of: no order id, no audit trail, absent from
        // /orders, and — because the id is what the gateway deduplicates on — nothing a retry can
        // key to. Saving first inverts the window into a record with no venue order, which is
        // visible, reconcilable against GET /ibkr/orders/open, and harmless if it is never resolved.
        var submittedOrder = new ExecutionOrder(
            orderId,
            correlationId,
            request,
            OrderLifecycleStatus.Submitted,
            quoteResponse.Quotes,
            riskDecision,
            [],
            events,
            now,
            DateTimeOffset.UtcNow);

        await orderRepository.SaveAsync(submittedOrder, cancellationToken);
        await PublishLifecycleAsync(submittedOrder, 0, cancellationToken);

        return await RouteAndRecordAsync(submittedOrder, events.Count, brokerReference: null, cancellationToken);
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

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Order cancelled." : request.Reason;
        var brokerReference = await orderRepository.GetBrokerReferenceAsync(orderId, cancellationToken);

        // The venue is asked, and its answer is what gets recorded. Flipping the status to Cancelled
        // locally and persisting it is not a weaker version of cancelling — it is the opposite of
        // one: the operator is told the order is dead while it keeps working at the broker, and it
        // can fill minutes later against a position they believe they never opened.
        var cancelled = await orderRouter.CancelAsync(orderId, brokerReference, reason, cancellationToken);

        // An unacknowledged cancel changes nothing about the order, so nothing about the order
        // changes here either. Only the attempt is recorded.
        var status = cancelled.Acknowledged ? cancelled.Status : order.Status;

        var cancelEvent = NewEvent(
            orderId,
            status,
            $"Cancel requested ({reason}). {cancelled.Message}",
            order.CorrelationId,
            order.Events.LastOrDefault()?.EventId);

        var updated = order with
        {
            Status = status,
            // The venue owns its own fill list. An empty one means it reported nothing new, not that
            // the fills already recorded never happened — a partially filled order that is then
            // cancelled must keep the part that filled.
            Fills = cancelled.Fills.Count > 0 ? cancelled.Fills : order.Fills,
            Events = order.Events.Concat([cancelEvent]).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await orderRepository.SaveAsync(updated, cancellationToken);
        await eventPublisher.PublishAsync(ToPublishedEvent(cancelEvent, updated), cancellationToken);

        if (!cancelled.Acknowledged)
        {
            // Recorded first, so the attempt is in the audit trail, then surfaced as a failure: a 200
            // carrying an unchanged order reads as success to anyone who does not diff the status,
            // which is the same misreport in a quieter form.
            throw new OrderCancelFailedException(orderId, cancelled.Message);
        }

        return updated;
    }

    public async Task<ExecutionOrder?> ReplaceAsync(Guid orderId, ReplaceOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orderRepository.GetAsync(orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        if (!orderRouter.SupportsReplace)
        {
            // Refused rather than recorded. Rewriting the limit price of an order the venue still
            // holds at the old one produces a record that cannot be told apart from a replace that
            // worked, and the operator's next decision is made on the price they think is resting.
            // Cancel-and-resubmit reaches the venue; this would not.
            throw new ReplaceNotSupportedException(orderId, orderRouter.Name);
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

    /// <summary>
    /// A submission whose internal order id is already on record: a retry, a duplicated request, a
    /// client replaying after a timeout. It never becomes a second order.
    /// </summary>
    /// <remarks>
    /// A settled order replays its recorded outcome. An unsettled one is handed to the venue again
    /// under the <em>same</em> internal order id, which is the only way to establish what the venue
    /// actually holds: the gateway keys its persisted internal→broker map on that id and answers
    /// with the existing order instead of transmitting a second one.
    /// <para>
    /// Risk is deliberately not re-evaluated. The decision was already made for this id, and the
    /// risk service remembers approvals by client order id — so a replay would come back
    /// <c>DUPLICATE_ORDER</c> and stamp <c>RiskRejected</c> on an order that may be live at the
    /// broker, which is the same class of lie as the cancel defect.
    /// </para>
    /// </remarks>
    private async Task<SubmitOrderResponse> ResubmitAsync(ExecutionOrder existing, CancellationToken cancellationToken)
    {
        if (IsSettled(existing.Status))
        {
            logger.LogInformation(
                "Order {OrderId} was already submitted and is {Status}; replaying its recorded outcome " +
                "rather than placing it again.",
                existing.OrderId,
                existing.Status);

            return new SubmitOrderResponse(
                existing.OrderId,
                existing.CorrelationId,
                existing.Status,
                existing.RiskDecision,
                existing.Fills);
        }

        logger.LogWarning(
            "Order {OrderId} is on record as {Status} with no settled outcome; re-routing it to " +
            "'{Router}' under the same id so the venue, not this service, says what it holds.",
            existing.OrderId,
            existing.Status,
            orderRouter.Name);

        var brokerReference = await orderRepository.GetBrokerReferenceAsync(existing.OrderId, cancellationToken);

        var events = existing.Events.Concat([
            NewEvent(
                existing.OrderId,
                existing.Status,
                $"Resubmission of an order recorded as {existing.Status}; re-routed to " +
                $"'{orderRouter.Name}' under the same order id rather than placed again.",
                existing.CorrelationId,
                existing.Events.LastOrDefault()?.EventId)
        ]).ToArray();

        var resubmitted = existing with
        {
            Events = events,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return await RouteAndRecordAsync(resubmitted, existing.Events.Count, brokerReference, cancellationToken);
    }

    /// <summary>
    /// Routes an already-persisted order and records what came back.
    /// </summary>
    /// <param name="publishedEventCount">
    /// How many of the order's events have already been published, so the tail is published once and
    /// the earlier events are not republished on every later save.
    /// </param>
    /// <param name="brokerReference">The venue id already on record, if any, so a failure cannot drop it.</param>
    private async Task<SubmitOrderResponse> RouteAndRecordAsync(
        ExecutionOrder order,
        int publishedEventCount,
        string? brokerReference,
        CancellationToken cancellationToken)
    {
        RoutedOrderResult routed;

        try
        {
            routed = await orderRouter.RouteAsync(order.OrderId, order.Request, order.Quotes, cancellationToken);
        }
        catch (Exception exception)
        {
            // The order stays Submitted on purpose. A transport failure here is ambiguous — the
            // request may well have reached the venue and been accepted — and recording Failed would
            // assert the one thing nobody knows. "Handed over, outcome unestablished" is what
            // reconciliation needs to see.
            var unresolved = Append(
                order,
                OrderLifecycleStatus.Submitted,
                $"Routing outcome unknown: {exception.Message}. The order may be live at the venue; " +
                "reconcile before acting. Resubmitting the same client order id is safe — it will " +
                "not place a second order.");

            await orderRepository.SaveAsync(unresolved, cancellationToken);
            await PublishLifecycleAsync(unresolved, publishedEventCount, cancellationToken);

            throw new OrderRoutingFailedException(order.OrderId, order.CorrelationId, exception);
        }

        // Recorded before the order itself, because this is the only handle by which the order can
        // later be cancelled. A crash between the two leaves an id for an order still marked
        // Submitted, which reconciles; the other ordering can leave a routed order with no id.
        if (routed.BrokerReference is { } reference)
        {
            await orderRepository.LinkBrokerReferenceAsync(order.OrderId, reference, cancellationToken);
            brokerReference = reference;
        }

        var events = order.Events.ToList();

        if (routed.Fills.Count > 0 || routed.Status != OrderLifecycleStatus.Submitted)
        {
            var detail = brokerReference is { } known ? $" (broker order {known})" : string.Empty;
            var message = routed.Message ?? $"Router '{orderRouter.Name}' produced {routed.Fills.Count} fill(s).";

            events.Add(NewEvent(order.OrderId, routed.Status, message + detail, order.CorrelationId, events.LastOrDefault()?.EventId));
        }

        var routedOrder = order with
        {
            Status = routed.Status,
            Fills = routed.Fills,
            Events = events.ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await orderRepository.SaveAsync(routedOrder, cancellationToken);
        await PublishLifecycleAsync(routedOrder, publishedEventCount, cancellationToken);

        return new SubmitOrderResponse(
            routedOrder.OrderId,
            routedOrder.CorrelationId,
            routedOrder.Status,
            routedOrder.RiskDecision,
            routedOrder.Fills);
    }

    /// <summary>
    /// The internal order id for a request: the same value every time the same client order id is
    /// submitted, without needing any storage to make it so.
    /// </summary>
    /// <remarks>
    /// The gateway refuses to transmit an internal order id it has already transmitted, and that
    /// refusal is the last thing standing between a retried <c>POST /orders</c> and two live broker
    /// orders — but it is keyed on the id this service sends, so a fresh <c>Guid.NewGuid()</c> per
    /// attempt defeats it completely. Deriving the id from the request makes a retry carry the same
    /// id by construction, which matters most in the case the order repository cannot help with: a
    /// restart with an empty in-memory store still sends the gateway an id its persisted map
    /// recognises.
    /// <para>
    /// Keyed on account plus client order id, matching the risk service's duplicate guard — a client
    /// order id is only unique within the account that submitted it. Shaped as a version-5 UUID so
    /// the value is a well-formed id rather than 16 arbitrary bytes; a request without a client order
    /// id gets a random id and, unavoidably, no idempotency.
    /// </para>
    /// </remarks>
    private static Guid DeriveOrderId(SubmitOrderRequest request)
    {
        if (request.ClientOrderId is not { } clientOrderId)
        {
            return Guid.NewGuid();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{request.AccountId}:{clientOrderId:N}"));
        var id = hash.AsSpan(0, 16).ToArray();

        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);

        return new Guid(id, bigEndian: true);
    }

    /// <summary>Statuses that answer "what happened to this order?" on their own.</summary>
    /// <remarks>
    /// <see cref="OrderLifecycleStatus.Failed"/> is included even though it can mean the gateway
    /// refused an order it had already transmitted in an earlier session: re-routing such an order
    /// cannot improve on the recorded refusal, and the message on it names the reconciliation step.
    /// </remarks>
    private static bool IsSettled(OrderLifecycleStatus status) =>
        status is OrderLifecycleStatus.Filled
            or OrderLifecycleStatus.Cancelled
            or OrderLifecycleStatus.RiskRejected
            or OrderLifecycleStatus.Rejected
            or OrderLifecycleStatus.Failed;

    private static ExecutionOrder Append(ExecutionOrder order, OrderLifecycleStatus status, string message) =>
        order with
        {
            Status = status,
            Events = order.Events.Concat([
                NewEvent(order.OrderId, status, message, order.CorrelationId, order.Events.LastOrDefault()?.EventId)
            ]).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private async Task PublishLifecycleAsync(ExecutionOrder order, int fromIndex, CancellationToken cancellationToken)
    {
        for (var index = fromIndex; index < order.Events.Count; index++)
        {
            await eventPublisher.PublishAsync(ToPublishedEvent(order.Events[index], order), cancellationToken);
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

/// <summary>
/// The order was handed to the venue and no outcome came back, so whether it exists at the broker is
/// unknown.
/// </summary>
/// <remarks>
/// Carries the order id because the record exists — it was persisted before routing — and finding it
/// is the first step of reconciling. A caller who gets this must not assume the order was not placed.
/// </remarks>
public sealed class OrderRoutingFailedException(Guid orderId, Guid correlationId, Exception innerException)
    : Exception(
        $"Order {orderId} was routed and no outcome was established: {innerException.Message}",
        innerException)
{
    public Guid OrderId { get; } = orderId;

    public Guid CorrelationId { get; } = correlationId;
}

/// <summary>The venue could not be asked to cancel, so the order must be assumed still working.</summary>
public sealed class OrderCancelFailedException(Guid orderId, string reason)
    : Exception($"Order {orderId} was not confirmed cancelled: {reason}")
{
    public Guid OrderId { get; } = orderId;
}

/// <summary>The configured venue cannot change an order in place, so the replace was refused.</summary>
public sealed class ReplaceNotSupportedException(Guid orderId, string routerName)
    : Exception(
        $"Order {orderId} is routed through the '{routerName}' venue, which this service cannot " +
        "modify in place. Cancel the order and submit a replacement — recording a new price here " +
        "would change nothing at the venue.")
{
    public Guid OrderId { get; } = orderId;

    public string RouterName { get; } = routerName;
}
