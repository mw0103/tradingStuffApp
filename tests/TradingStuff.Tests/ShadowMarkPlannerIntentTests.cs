using System.Text.Json;
using TradingStuff.Contracts;
using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Studies.VrpConditioning;

namespace TradingStuff.Tests;

/// <summary>
/// The <c>planner_intent</c> document persisted with every shadow mark.
/// </summary>
/// <remarks>
/// The plan C item this closes is "the decision-time NBBO reaches the record". The planner capturing
/// it is half the claim; the other half is that it survives into <c>research.vol_shadow_marks</c>,
/// and a shape only an HTTP round trip could see is one nothing checks.
/// </remarks>
public sealed class ShadowMarkPlannerIntentTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static OptionContract Put(decimal strike) =>
        new($"SPY20260904P{strike:F0}", "SPY", new DateOnly(2026, 9, 4), strike, OptionRight.Put, TradingClass: "SPY");

    private static PlannedOrder Order(IReadOnlyList<PlannedLegQuote>? legQuotes)
    {
        OrderLegRequest[] legs =
        [
            new(Put(725), OrderSide.Sell, 1, PositionEffect.Open),
            new(Put(724), OrderSide.Buy, 1, PositionEffect.Open),
        ];

        var request = new SubmitOrderRequest(
            "SHADOW", StrategyKind.Vertical, OrderType.Limit, TimeInForce.Day, legs,
            LimitPrice: -0.37m, StopPrice: null, ClientOrderId: Guid.NewGuid(), SubmittedBy: "test");

        return new PlannedOrder(request, -0.37m, LimitPriceSources.ComputedMarketable, "SPY 725/724 put credit spread", legQuotes);
    }

    [Fact]
    public void A_planned_intent_carries_both_legs_nbbo_into_the_persisted_document()
    {
        var quotes = new PlannedLegQuote[]
        {
            new("SPY", new DateOnly(2026, 9, 4), 725m, "Put", "Sell", 1.37m, 1.40m, 1.38m,
                new DateTimeOffset(2026, 8, 6, 19, 55, 0, TimeSpan.Zero), "ibkr-delayed"),
            new("SPY", new DateOnly(2026, 9, 4), 724m, "Put", "Buy", 0.92m, 0.95m, 0.93m,
                new DateTimeOffset(2026, 8, 6, 19, 55, 0, TimeSpan.Zero), "ibkr-delayed"),
        };

        var json = JsonSerializer.Serialize(
            VolShadowMarkEndpoints.DescribeIntent(OrderPlanResult.Planned(Order(quotes))), SerializerOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("planned").GetBoolean());
        Assert.Equal(-0.37m, root.GetProperty("netLimit").GetDecimal());

        var legs = root.GetProperty("legQuotes");
        Assert.Equal(2, legs.GetArrayLength());
        Assert.Equal(725m, legs[0].GetProperty("strike").GetDecimal());
        Assert.Equal(1.37m, legs[0].GetProperty("bid").GetDecimal());
        Assert.Equal(1.40m, legs[0].GetProperty("ask").GetDecimal());
        Assert.Equal("Sell", legs[0].GetProperty("side").GetString());
        Assert.Equal("ibkr-delayed", legs[0].GetProperty("source").GetString());
    }

    [Fact]
    public void An_operator_supplied_price_records_no_quotes_rather_than_empty_ones()
    {
        var json = JsonSerializer.Serialize(
            VolShadowMarkEndpoints.DescribeIntent(OrderPlanResult.Planned(Order(null))), SerializerOptions);

        using var document = JsonDocument.Parse(json);

        // Null means "no quote was consulted", which is not the same claim as "the legs had no
        // market" — and item 7 is unusable if the two are indistinguishable.
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("legQuotes").ValueKind);
    }

    [Fact]
    public void A_refusal_records_its_reason_and_claims_nothing_was_planned()
    {
        var json = JsonSerializer.Serialize(
            VolShadowMarkEndpoints.DescribeIntent(OrderPlanResult.Refused("The chain window contains no puts.")),
            SerializerOptions);

        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("planned").GetBoolean());
        Assert.Equal("The chain window contains no puts.", document.RootElement.GetProperty("refusal").GetString());
    }
}
