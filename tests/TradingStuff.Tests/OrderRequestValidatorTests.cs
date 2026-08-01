using TradingStuff.Contracts;
using TradingStuff.ExecutionService;

namespace TradingStuff.Tests;

/// <summary>
/// Shape validation, which is the first of the two independent places an order has to be a v1
/// structure — the risk engine is the second, and neither trusts the other.
/// </summary>
public sealed class OrderRequestValidatorTests
{
    [Theory]
    [InlineData(StrategyKind.Vertical)]
    [InlineData(StrategyKind.Calendar)]
    [InlineData(StrategyKind.Diagonal)]
    [InlineData(StrategyKind.Straddle)]
    [InlineData(StrategyKind.Strangle)]
    public void Accepts_every_v1_shape_at_matched_quantities(StrategyKind strategy)
    {
        var errors = new OrderRequestValidator().Validate(Shape(strategy, 3, 3));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(StrategyKind.Vertical)]
    [InlineData(StrategyKind.Calendar)]
    [InlineData(StrategyKind.Diagonal)]
    [InlineData(StrategyKind.Straddle)]
    [InlineData(StrategyKind.Strangle)]
    public void Rejects_every_v1_shape_at_unequal_quantities(StrategyKind strategy)
    {
        // A ratio spread is not a defined-risk structure, which is all v1 trades: one long contract
        // has to stand behind each short one, or the short side is partly naked.
        var errors = new OrderRequestValidator().Validate(Shape(strategy, 1, 10));

        Assert.Contains(errors, error => error.Contains("same quantity", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_strategy_kind_it_does_not_handle()
    {
        // The switch had no default, so an out-of-range value off the wire skipped every shape rule
        // and a naked short strangle validated clean.
        var order = Shape(StrategyKind.Strangle, 1, 1) with { Strategy = (StrategyKind)99 };

        var errors = new OrderRequestValidator().Validate(order);

        Assert.Contains(errors, error => error.Contains("Unsupported strategy kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_leg_whose_contract_multiplier_is_not_positive()
    {
        var order = Shape(StrategyKind.Vertical, 1, 1);
        var zeroed = order with
        {
            Legs = [.. order.Legs.Select(leg => leg with { Contract = leg.Contract with { Multiplier = 0 } })],
        };

        var errors = new OrderRequestValidator().Validate(zeroed);

        Assert.Contains(errors, error => error.Contains("positive contract multiplier", StringComparison.Ordinal));
    }

    [Fact]
    public void Still_rejects_a_shape_that_does_not_match_its_strategy()
    {
        var order = Shape(StrategyKind.Vertical, 1, 1) with { Strategy = StrategyKind.Calendar };

        var errors = new OrderRequestValidator().Validate(order);

        Assert.Contains(errors, error => error.Contains("Calendar spreads require", StringComparison.Ordinal));
    }

    /// <summary>A valid shape for each strategy, so only the quantities under test can be wrong.</summary>
    private static SubmitOrderRequest Shape(StrategyKind strategy, int firstQuantity, int secondQuantity)
    {
        var near = RiskSamples.NearExpiry;
        var far = RiskSamples.FarExpiry;

        (OptionContract First, OptionContract Second, OrderSide SecondSide) legs = strategy switch
        {
            StrategyKind.Calendar => (RiskSamples.Call(100m, far), RiskSamples.Call(100m, near), OrderSide.Sell),
            StrategyKind.Diagonal => (RiskSamples.Call(105m, far), RiskSamples.Call(100m, near), OrderSide.Sell),
            StrategyKind.Straddle => (RiskSamples.Call(100m, near), RiskSamples.Put(100m, near), OrderSide.Buy),
            StrategyKind.Strangle => (RiskSamples.Call(105m, near), RiskSamples.Put(95m, near), OrderSide.Buy),
            _ => (RiskSamples.Call(100m, near), RiskSamples.Call(105m, near), OrderSide.Sell),
        };

        return new SubmitOrderRequest(
            "DU1234567",
            strategy,
            OrderType.Market,
            TimeInForce.Day,
            [
                new OrderLegRequest(legs.First, OrderSide.Buy, firstQuantity, PositionEffect.Open),
                new OrderLegRequest(legs.Second, legs.SecondSide, secondQuantity, PositionEffect.Open),
            ],
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "test");
    }
}
