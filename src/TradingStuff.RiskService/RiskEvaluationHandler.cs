using System.Collections.Concurrent;
using TradingStuff.Contracts;

namespace TradingStuff.RiskService;

/// <summary>
/// The body of <c>POST /risk/evaluate-order</c>: claim the client order id, evaluate, and release
/// the claim if nothing can have been traded on it.
/// </summary>
/// <remarks>
/// Extracted from the endpoint lambda so the claim/release pairing is unit-testable. It is a
/// split-path lifetime — the claim is taken on one path and released on two others — which is the
/// shape that has produced this project's worst defects.
/// </remarks>
public static class RiskEvaluationHandler
{
    public static RiskEvaluationResult Evaluate(
        RiskEvaluationRequest request,
        PortfolioRiskEvaluator evaluator,
        DuplicateOrderGuard duplicateGuard)
    {
        // Claimed before the evaluation rather than remembered after approving it. The previous
        // IsDuplicate-then-Remember pair was a check-then-act: two submissions of the same client
        // order id that overlapped both read "not seen", both evaluated, and both were approved —
        // which with Execution:Router=ibkr is two live orders from one idempotency key.
        if (!duplicateGuard.TryClaim(request.Order))
        {
            return new RiskEvaluationResult(
                Guid.NewGuid(),
                RiskDecision.Rejected,
                [new RiskLimitBreach(RiskBreachCodes.DuplicateOrder, "Client order id has already been evaluated.", 1m, 0m)],
                GreeksVector.Zero,
                0m,
                0m,
                DateTimeOffset.UtcNow);
        }

        RiskEvaluationResult result;

        try
        {
            result = evaluator.Evaluate(request);
        }
        catch
        {
            // No decision was produced, so no order can have been routed on one.
            duplicateGuard.Release(request.Order);
            throw;
        }

        if (result.Decision == RiskDecision.Rejected)
        {
            // A rejected order never reaches a broker, so its client order id is free again —
            // correcting the order and resubmitting it under the same idempotency key is the normal
            // thing to do, and burning the id would turn one bad submission into a permanent one.
            duplicateGuard.Release(request.Order);
        }

        // An approved id stays claimed. From here the risk service cannot observe whether the order
        // reached TWS, and the only safe assumption is that it did: a retry under the same id must
        // not be able to produce a second live order. A caller that needs to retry issues a new
        // client order id, having first read back the original order's status.
        return result;
    }
}

/// <summary>
/// Holds the client order ids that have been claimed for evaluation, so one id cannot become two
/// orders.
/// </summary>
/// <remarks>
/// Claim and release are single atomic dictionary operations; anything built from a separate read
/// and write would reintroduce the race this exists to close.
/// <para>Process-local and unbounded, which is only adequate because there is one risk-service
/// instance and orders do not yet persist. Once orders live in Postgres this belongs there, as a
/// unique constraint on (account, client order id).</para>
/// </remarks>
public sealed class DuplicateOrderGuard
{
    private readonly ConcurrentDictionary<string, byte> _claimedClientOrderIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Claims a client order id. False means another evaluation already holds it. An order with no
    /// client order id supplied no idempotency key, so there is nothing to claim and nothing to
    /// deduplicate against.
    /// </summary>
    public bool TryClaim(SubmitOrderRequest order) =>
        order.ClientOrderId is not { } clientOrderId ||
        _claimedClientOrderIds.TryAdd(Key(order.AccountId, clientOrderId), 0);

    /// <summary>
    /// Releases a claim. Only safe for an order that provably did not reach a broker — a rejection,
    /// or an evaluation that threw.
    /// </summary>
    public void Release(SubmitOrderRequest order)
    {
        if (order.ClientOrderId is { } clientOrderId)
        {
            _claimedClientOrderIds.TryRemove(Key(order.AccountId, clientOrderId), out _);
        }
    }

    private static string Key(string accountId, Guid clientOrderId) => $"{accountId}:{clientOrderId:N}";
}
