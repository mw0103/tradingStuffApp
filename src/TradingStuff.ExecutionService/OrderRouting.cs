using System.Net.Http.Json;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

/// <summary>Outcome of handing an approved order to a venue.</summary>
public sealed record RoutedOrderResult(
    OrderLifecycleStatus Status,
    IReadOnlyList<FillReport> Fills,
    string? BrokerReference = null,
    string? Message = null);

/// <summary>What a venue reported after being asked to cancel an order.</summary>
/// <param name="Acknowledged">
/// True only when the venue was actually asked and answered. False means the request never got
/// there — no broker id on record, the gateway unreachable, a refusal — and the caller must keep the
/// status it already had rather than adopting anything from this result. "I could not ask" and "it
/// is cancelled" are the two answers that must never be confused, because only one of them means
/// the order can no longer fill.
/// </param>
/// <param name="Status">
/// The venue's state <em>after</em> the cancel, not the cancellation that was requested. An order
/// that filled before the cancel arrived comes back <see cref="OrderLifecycleStatus.Filled"/>, and
/// one whose cancel TWS has not yet confirmed comes back <see cref="OrderLifecycleStatus.Submitted"/>
/// (IBKR's <c>PendingCancel</c> is not terminal — the order can still fill).
/// </param>
public sealed record CancelOrderResult(
    bool Acknowledged,
    OrderLifecycleStatus Status,
    IReadOnlyList<FillReport> Fills,
    string Message);

/// <summary>
/// Where an approved order actually goes. Selected by <c>Execution:Router</c>.
/// </summary>
/// <remarks>
/// The simulated engine stays the default. Real broker routing is opt-in per environment so that no
/// test, and no default configuration, can reach <c>placeOrder</c>.
/// <para>
/// Cancellation is part of this seam rather than a local state change because the venue, not this
/// service, decides whether an order is dead. A cancel that only flips a row reports a working
/// order as cancelled and lets it fill afterwards, which inverts the single property the endpoint
/// exists to provide.
/// </para>
/// </remarks>
public interface IOrderRouter
{
    /// <summary>Recorded on lifecycle events so an order's venue is auditable after the fact.</summary>
    string Name { get; }

    /// <summary>Whether an order already at this venue can have its terms changed in place.</summary>
    /// <remarks>
    /// False means the workflow refuses a replace instead of rewriting its own record. A record
    /// showing a limit price the venue never received is worse than a refusal: it is
    /// indistinguishable from a replace that worked, so every later reader — operator, audit,
    /// risk — believes a price that does not exist anywhere else.
    /// </remarks>
    bool SupportsReplace { get; }

    Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken);

    /// <summary>Asks the venue to cancel an order and reports what the venue says afterwards.</summary>
    /// <param name="brokerReference">
    /// The venue's own id for this order, as returned by <see cref="RouteAsync"/> and recorded by
    /// <see cref="IOrderRepository.LinkBrokerReferenceAsync"/>. Null when none was ever recorded,
    /// which is not the same as "there is nothing at the venue".
    /// </param>
    Task<CancelOrderResult> CancelAsync(
        Guid orderId,
        string? brokerReference,
        string reason,
        CancellationToken cancellationToken);
}

/// <summary>Simulates fills locally against the supplied quotes. No broker involved.</summary>
public sealed class PaperOrderRouter(PaperExecutionEngine engine) : IOrderRouter
{
    public string Name => "paper";

    /// <summary>Nothing rests anywhere but this process, so its own record is the whole truth.</summary>
    public bool SupportsReplace => true;

    public Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken)
    {
        var result = engine.Execute(orderId, request, quotes);

        return Task.FromResult(new RoutedOrderResult(result.Status, result.Fills));
    }

    public Task<CancelOrderResult> CancelAsync(
        Guid orderId,
        string? brokerReference,
        string reason,
        CancellationToken cancellationToken) =>
        // The simulated order exists only as the record itself, so recording the cancel is what
        // performs it. Acknowledged is unconditionally true here and that is not an assumption:
        // there is no other party that could disagree.
        Task.FromResult(new CancelOrderResult(
            Acknowledged: true,
            OrderLifecycleStatus.Cancelled,
            [],
            "Simulated order cancelled; no venue holds it."));
}

/// <summary>Routes the order to the IBKR gateway, which owns the TWS socket.</summary>
public sealed class IbkrOrderRouter(HttpClient httpClient, ILogger<IbkrOrderRouter> logger) : IOrderRouter
{
    public string Name => "ibkr";

    /// <summary>
    /// The gateway exposes place and cancel and nothing else, and a TWS replace is a
    /// <c>placeOrder</c> call site reusing the live order id — not something to add without a paper
    /// round trip behind it. Until then the honest answer to a replace is a refusal.
    /// </summary>
    public bool SupportsReplace => false;

    public async Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            "/ibkr/orders",
            new { InternalOrderId = orderId, Order = request },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "IBKR gateway refused order {OrderId}: {Status} {Problem}",
                orderId,
                response.StatusCode,
                problem);

            // A broker refusal is an order outcome, not an exception for the caller to handle.
            return new RoutedOrderResult(
                OrderLifecycleStatus.Failed,
                [],
                Message: $"IBKR gateway returned {(int)response.StatusCode}: {problem}");
        }

        var state = await response.Content.ReadFromJsonAsync<IbkrOrderStateDto>(cancellationToken)
                    ?? throw new InvalidOperationException("IBKR gateway returned an empty order state.");

        return new RoutedOrderResult(
            state.Status,
            state.Fills,
            BrokerReference: state.IbkrOrderId.ToString(),
            Message: state.Message);
    }

    public async Task<CancelOrderResult> CancelAsync(
        Guid orderId,
        string? brokerReference,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(brokerReference, out var ibkrOrderId))
        {
            // No broker id means no accepted placement ever came back for this order — but the
            // placement may still have been transmitted and its response lost, so the order can be
            // live. Reporting it cancelled here would be exactly the lie this path exists to remove.
            logger.LogError(
                "Cannot cancel order {OrderId}: no IBKR order id is recorded for it.",
                orderId);

            return new CancelOrderResult(
                Acknowledged: false,
                OrderLifecycleStatus.Submitted,
                [],
                $"No IBKR order id is recorded for order {orderId}, so no cancel was sent. It may " +
                "still be working at the broker — reconcile against GET /ibkr/orders/open.");
        }

        var response = await httpClient.PostAsJsonAsync(
            $"/ibkr/orders/{ibkrOrderId}/cancel",
            new CancelOrderRequest(reason),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "IBKR gateway refused the cancel of order {OrderId} (IBKR order {IbkrOrderId}): {Status} {Problem}",
                orderId,
                ibkrOrderId,
                response.StatusCode,
                problem);

            return new CancelOrderResult(
                Acknowledged: false,
                OrderLifecycleStatus.Submitted,
                [],
                $"IBKR gateway returned {(int)response.StatusCode} for the cancel of IBKR order " +
                $"{ibkrOrderId}: {problem}. The order was not confirmed cancelled.");
        }

        var state = await response.Content.ReadFromJsonAsync<IbkrOrderStateDto>(cancellationToken)
                    ?? throw new InvalidOperationException("IBKR gateway returned an empty order state.");

        // Whatever the broker says, including "still working": TWS confirms a cancel asynchronously,
        // so the usual state here is PendingCancel, which the gateway maps to Submitted because such
        // an order can still fill. Recording Cancelled at this point would re-create the defect.
        return new CancelOrderResult(
            Acknowledged: true,
            state.Status,
            state.Fills,
            $"IBKR order {ibkrOrderId} reports {state.RawStatus} after the cancel request." +
            (string.IsNullOrWhiteSpace(state.Message) ? string.Empty : $" {state.Message}"));
    }

    /// <summary>Mirror of the gateway's order state; only the fields execution cares about.</summary>
    private sealed record IbkrOrderStateDto(
        int IbkrOrderId,
        long PermId,
        OrderLifecycleStatus Status,
        string RawStatus,
        IReadOnlyList<FillReport> Fills,
        string? Message);
}

public static class OrderRouters
{
    public const string Paper = "paper";
    public const string Ibkr = "ibkr";

    /// <summary>
    /// True only for the exact opt-in value. Anything else — including a typo or null — stays on the
    /// simulated engine rather than silently routing real orders to a broker.
    /// </summary>
    public static bool UsesIbkr(string? router) =>
        string.Equals(router, Ibkr, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Startup guard on the two settings that must agree before real orders are transmitted.
/// </summary>
/// <remarks>
/// <c>Execution:Router</c> and <c>Portfolio:Source</c> each degrade to their safe value on an
/// unrecognised string, which is right individually and wrong together: with the router opted in and
/// a typo'd portfolio source, real orders are transmitted while risk evaluates them against fixed
/// development figures — a fabricated buying power, no positions, and a daily P&amp;L of zero, so
/// <c>MAX_DAILY_LOSS</c> cannot fire at all and the Greek limits measure only the order in hand.
/// Every individual setting is fail-safe and the combination is not, which is precisely the shape
/// that gets missed.
/// <para>
/// There is deliberately no override flag. Its only possible meaning would be "transmit real orders
/// against numbers nobody checked", and the correct response to wanting it is to set
/// <c>Portfolio__Source=ibkr</c>. Refusing to start is fail-closed: an ExecutionService that will
/// not boot cannot place anything.
/// </para>
/// </remarks>
public static class ExecutionSafetyConfiguration
{
    public static void EnsureRouterAndPortfolioAgree(IConfiguration configuration)
    {
        var router = configuration["Execution:Router"];
        var portfolioSource = configuration["Portfolio:Source"];

        if (!OrderRouters.UsesIbkr(router) || PortfolioSources.UsesIbkr(portfolioSource))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Execution:Router is '{router}', so approved orders are transmitted to IBKR, but " +
            $"Portfolio:Source is '{portfolioSource ?? "(unset)"}' rather than " +
            $"'{PortfolioSources.Ibkr}' — real orders would be risk-checked against fabricated " +
            "development figures. Set Portfolio__Source=ibkr, or route to the paper engine.");
    }
}
