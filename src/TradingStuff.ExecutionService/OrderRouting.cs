using System.Net.Http.Json;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

/// <summary>Outcome of handing an approved order to a venue.</summary>
public sealed record RoutedOrderResult(
    OrderLifecycleStatus Status,
    IReadOnlyList<FillReport> Fills,
    string? BrokerReference = null,
    string? Message = null);

/// <summary>
/// Where an approved order actually goes. Selected by <c>Execution:Router</c>.
/// </summary>
/// <remarks>
/// The simulated engine stays the default. Real broker routing is opt-in per environment so that no
/// test, and no default configuration, can reach <c>placeOrder</c>.
/// </remarks>
public interface IOrderRouter
{
    /// <summary>Recorded on lifecycle events so an order's venue is auditable after the fact.</summary>
    string Name { get; }

    Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken);
}

/// <summary>Simulates fills locally against the supplied quotes. No broker involved.</summary>
public sealed class PaperOrderRouter(PaperExecutionEngine engine) : IOrderRouter
{
    public string Name => "paper";

    public Task<RoutedOrderResult> RouteAsync(
        Guid orderId,
        SubmitOrderRequest request,
        IReadOnlyList<QuoteSnapshot> quotes,
        CancellationToken cancellationToken)
    {
        var result = engine.Execute(orderId, request, quotes);

        return Task.FromResult(new RoutedOrderResult(result.Status, result.Fills));
    }
}

/// <summary>Routes the order to the IBKR gateway, which owns the TWS socket.</summary>
public sealed class IbkrOrderRouter(HttpClient httpClient, ILogger<IbkrOrderRouter> logger) : IOrderRouter
{
    public string Name => "ibkr";

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
