using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public sealed class OrderRequestValidator
{
    public IReadOnlyList<string> Validate(SubmitOrderRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            errors.Add("AccountId is required.");
        }

        if (request.Legs.Count != 2)
        {
            errors.Add("V1 supports two-leg option strategies only.");
            return errors;
        }

        if (request.Legs.Any(leg => leg.Quantity <= 0))
        {
            errors.Add("Every leg quantity must be positive.");
        }

        // V1 trades defined-risk two-leg structures (docs/PLAN.md). Unequal leg quantities make a
        // ratio spread, which is not one: a 1x10 "vertical" is nine naked short calls, and every
        // max-loss formula downstream assumes one long contract stands behind each short one.
        if (request.Legs.Select(leg => leg.Quantity).Distinct().Count() != 1)
        {
            errors.Add("All legs must have the same quantity; ratio spreads are out of scope for v1.");
        }

        if (request.Legs.Select(leg => leg.Contract.Underlying).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            errors.Add("All legs must share the same underlying.");
        }

        if (request.Legs.Select(leg => leg.Contract.Multiplier).Distinct().Count() != 1)
        {
            errors.Add("All legs must share the same contract multiplier.");
        }

        // The multiplier scales every money figure the risk engine computes, and the broker never
        // sees it — conIds identify the contract at TWS. A leg claiming zero therefore prices as
        // free and still trades its real size.
        if (request.Legs.Any(leg => leg.Contract.Multiplier <= 0))
        {
            errors.Add("Every leg must carry a positive contract multiplier.");
        }

        ValidateOrderType(request, errors);
        ValidateStrategy(request, errors);

        return errors;
    }

    private static void ValidateOrderType(SubmitOrderRequest request, List<string> errors)
    {
        if (request.OrderType is OrderType.Limit or OrderType.StopLimit && request.LimitPrice is null)
        {
            errors.Add("Limit and stop-limit orders require LimitPrice.");
        }

        if (request.OrderType is OrderType.Stop or OrderType.StopLimit && request.StopPrice is null)
        {
            errors.Add("Stop and stop-limit orders require StopPrice.");
        }
    }

    private static void ValidateStrategy(SubmitOrderRequest request, List<string> errors)
    {
        var first = request.Legs[0];
        var second = request.Legs[1];
        var oppositeSides = first.Side != second.Side;
        var sameSide = first.Side == second.Side;
        var sameExpiry = first.Contract.Expiration == second.Contract.Expiration;
        var sameRight = first.Contract.Right == second.Contract.Right;
        var sameStrike = first.Contract.Strike == second.Contract.Strike;

        switch (request.Strategy)
        {
            case StrategyKind.Vertical:
                Require(oppositeSides, "Vertical spreads require one buy leg and one sell leg.", errors);
                Require(sameExpiry, "Vertical spreads require the same expiration.", errors);
                Require(sameRight, "Vertical spreads require the same option right.", errors);
                Require(!sameStrike, "Vertical spreads require different strikes.", errors);
                break;

            case StrategyKind.Calendar:
                Require(oppositeSides, "Calendar spreads require one buy leg and one sell leg.", errors);
                Require(!sameExpiry, "Calendar spreads require different expirations.", errors);
                Require(sameRight, "Calendar spreads require the same option right.", errors);
                Require(sameStrike, "Calendar spreads require the same strike.", errors);
                break;

            case StrategyKind.Diagonal:
                Require(oppositeSides, "Diagonal spreads require one buy leg and one sell leg.", errors);
                Require(!sameExpiry, "Diagonal spreads require different expirations.", errors);
                Require(sameRight, "Diagonal spreads require the same option right.", errors);
                Require(!sameStrike, "Diagonal spreads require different strikes.", errors);
                break;

            case StrategyKind.Straddle:
                Require(sameSide, "Straddles require both legs on the same side.", errors);
                Require(sameExpiry, "Straddles require the same expiration.", errors);
                Require(!sameRight, "Straddles require one call and one put.", errors);
                Require(sameStrike, "Straddles require the same strike.", errors);
                break;

            case StrategyKind.Strangle:
                Require(sameSide, "Strangles require both legs on the same side.", errors);
                Require(sameExpiry, "Strangles require the same expiration.", errors);
                Require(!sameRight, "Strangles require one call and one put.", errors);
                Require(!sameStrike, "Strangles require different strikes.", errors);
                break;

            default:
                // Nothing falls through this switch un-inspected. An out-of-range StrategyKind off
                // the wire used to skip every shape rule above — a naked short strangle submitted as
                // strategy 99 validated clean — and a member added to the enum without a case here
                // would inherit the same hole.
                errors.Add($"Unsupported strategy kind '{request.Strategy}'.");
                break;
        }
    }

    private static void Require(bool condition, string error, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(error);
        }
    }
}
