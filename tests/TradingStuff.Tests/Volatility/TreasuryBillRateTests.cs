using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Tests.Volatility;

public sealed class TreasuryBillRateTests
{
    [Fact]
    public void A_zero_discount_rate_is_a_zero_continuous_rate()
    {
        Assert.Equal(0.0, TreasuryBillRate.ContinuousFromDiscount(0.0), 12);
    }

    [Fact]
    public void A_five_percent_discount_rate_converts_slightly_above_five_percent()
    {
        // price = 1 - 0.05 * 28/360 = 0.9961111; r = -ln(price) * 365/28 ≈ 0.05077.
        var r = TreasuryBillRate.ContinuousFromDiscount(5.0);

        Assert.InRange(r, 0.0505, 0.0510);
    }

    [Fact]
    public void The_conversion_round_trips_the_bill_price()
    {
        var r = TreasuryBillRate.ContinuousFromDiscount(3.61);

        var priceFromDiscount = 1.0 - (3.61 / 100.0) * 28.0 / 360.0;
        var priceFromContinuous = Math.Exp(-r * 28.0 / 365.0);

        Assert.Equal(priceFromDiscount, priceFromContinuous, 12);
    }
}
