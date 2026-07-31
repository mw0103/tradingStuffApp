using Microsoft.Extensions.Configuration;
using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.RiskService;

namespace TradingStuff.Tests;

/// <summary>
/// The risk engine's refusal paths. Every breach code has a test here, and each is pinned at the
/// boundary value, because the engine's failure mode is not throwing — it is returning a plausible
/// number, usually $0, and letting the order through.
/// </summary>
public sealed class RiskEngineTests
{
    // ---- unpriceable legs ------------------------------------------------------------------

    [Fact]
    public void Refuses_a_leg_that_has_no_quote()
    {
        var order = RiskSamples.LongVertical();
        var request = RiskSamples.Request(order, RiskSamples.Quotes(order).Take(1));

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(request);

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
        Assert.Equal(0m, result.EstimatedMaxLoss);
        Assert.Equal(GreeksVector.Zero, result.ExposureDelta);
    }

    [Fact]
    public void Refuses_a_leg_quoted_zero_by_zero()
    {
        var order = RiskSamples.LongVertical();
        var quotes = new[]
        {
            RiskSamples.Quote(order.Legs[0].Contract, 1.95m, 2.05m),
            RiskSamples.Quote(order.Legs[1].Contract, 0m, 0m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
    }

    [Fact]
    public void Refuses_a_sold_leg_that_has_no_bid()
    {
        // Not the conservative case: pricing the credit at nothing pushes the net debit across zero,
        // which used to hand a credit spread the debit-spread formula and drop the strike width.
        var order = RiskSamples.LongVertical();
        var quotes = new[]
        {
            RiskSamples.Quote(order.Legs[0].Contract, 1.95m, 2.05m),
            RiskSamples.Quote(order.Legs[1].Contract, 0m, 0.05m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
    }

    [Fact]
    public void Prices_a_bought_leg_that_has_no_bid()
    {
        // A zero bid on a leg being bought is a real quote for a nearly worthless option: the offer
        // is what the order pays, and it exists.
        var order = RiskSamples.LongVertical();
        var quotes = new[]
        {
            RiskSamples.Quote(order.Legs[0].Contract, 0m, 0.05m),
            RiskSamples.Quote(order.Legs[1].Contract, 0.02m, 0.04m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.DoesNotContain(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
        Assert.Equal(RiskDecision.Approved, result.Decision);
        Assert.Equal(3m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Refuses_a_quote_whose_greeks_are_all_zero()
    {
        var order = RiskSamples.LongVertical();
        var quotes = new[]
        {
            RiskSamples.Quote(order.Legs[0].Contract, 1.95m, 2.05m),
            RiskSamples.Quote(order.Legs[1].Contract, 0.95m, 1.05m, new OptionGreeks(0m, 0m, 0m, 0m)),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
    }

    [Fact]
    public void Prices_a_quote_whose_delta_alone_is_zero()
    {
        // Only the whole set at zero is the gateway's timeout marker. A single Greek at zero is an
        // ordinary reading and must not cost the order its evaluation.
        var order = RiskSamples.LongVertical();
        var quotes = new[]
        {
            RiskSamples.Quote(order.Legs[0].Contract, 1.95m, 2.05m),
            RiskSamples.Quote(order.Legs[1].Contract, 0.95m, 1.05m, new OptionGreeks(0m, 0.02m, -0.03m, 0.09m)),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.DoesNotContain(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
        Assert.Equal(RiskDecision.Approved, result.Decision);
    }

    [Fact]
    public void A_ten_lot_credit_vertical_on_a_dead_book_is_refused_rather_than_priced_at_zero()
    {
        // The whole defect in one order. Pre-market, every leg comes back 0/0 with zeroed Greeks
        // because the gateway completes a quote partially on timeout. Priced, that is a $0 maximum
        // loss, $0 buying-power impact and zero exposure — clear of a $2,500 per-order limit — and
        // with Execution:Router=ibkr it transmits and fills at the open.
        var order = RiskSamples.CreditVertical(quantity: 10);
        var quotes = order.Legs
            .Select(leg => RiskSamples.Quote(leg.Contract, 0m, 0m, new OptionGreeks(0m, 0m, 0m, 0m)))
            .ToArray();

        var result = new PortfolioRiskEvaluator(RiskLimits.DevelopmentDefaults)
            .Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnpriceableLeg);
    }

    [Fact]
    public void Correlates_quotes_that_carry_broker_enriched_contracts()
    {
        // Quotes were correlated on the whole OptionContract record, so a broker-backed provider
        // echoing back a canonical symbol and a real exchange missed every lookup — and the miss was
        // answered with a continue, which zeroed the leg's premium and Greeks instead of refusing.
        var order = RiskSamples.LongVertical();
        var quotes = RiskSamples.Quotes(order)
            .Select(quote => quote with
            {
                Contract = quote.Contract with
                {
                    Symbol = $"IBKR:{quote.Contract.Underlying}:{quote.Contract.Strike}",
                    Exchange = "CBOE",
                    Underlying = quote.Contract.Underlying.ToLowerInvariant(),
                },
            })
            .ToArray();

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(RiskDecision.Approved, result.Decision);
        Assert.Equal(160m, result.EstimatedMaxLoss);
    }

    // ---- maximum loss: quantity ------------------------------------------------------------

    [Theory]
    [InlineData(1, 360)]
    [InlineData(10, 3_600)]
    public void Credit_vertical_maximum_loss_scales_with_the_contract_count(int quantity, int expectedMaxLoss)
    {
        var order = RiskSamples.CreditVertical(quantity);
        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(expectedMaxLoss, result.EstimatedMaxLoss);
        Assert.Equal(result.EstimatedMaxLoss, result.EstimatedBuyingPowerImpact);
    }

    [Fact]
    public void Refuses_a_ratio_vertical_rather_than_pricing_nine_naked_shorts()
    {
        // One long call against ten short calls is nine naked calls with unbounded loss. The
        // width-times-quantity formula happily produced a number for it.
        var order = RiskSamples.CreditVertical(quantity: 1) with
        {
            Legs =
            [
                RiskSamples.Buy(RiskSamples.Call(105m), 1),
                RiskSamples.Sell(RiskSamples.Call(100m), 10),
            ],
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnequalLegQuantities);
        Assert.Equal(0m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Refuses_a_ratio_calendar_rather_than_pricing_it_at_zero()
    {
        // The cover check asked only whether a compatible long existed, so one long call "covered"
        // ten short calls and the estimate came out at $0.
        var order = RiskSamples.Calendar(quantity: 1) with
        {
            Legs =
            [
                RiskSamples.Buy(RiskSamples.Call(100m, RiskSamples.FarExpiry), 1),
                RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.NearExpiry), 10),
            ],
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnequalLegQuantities);
        Assert.Equal(0m, result.EstimatedMaxLoss);
    }

    // ---- maximum loss: strikes -------------------------------------------------------------

    [Fact]
    public void Vertical_maximum_loss_follows_the_strikes_not_the_sign_of_the_price()
    {
        // A short call spread whose short leg is quoted thin enough that the order computes as a net
        // debit. Branching on that sign chose the debit-spread formula and reported $50 for a
        // position that can lose the whole five-point width.
        var order = RiskSamples.CreditVertical(quantity: 1);
        var quotes = new[]
        {
            RiskSamples.Quote(RiskSamples.Call(105m), 0.50m, 0.60m),
            RiskSamples.Quote(RiskSamples.Call(100m), 0.10m, 0.20m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(550m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Long_vertical_maximum_loss_is_the_debit_alone()
    {
        var order = RiskSamples.LongVertical();

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(160m, result.EstimatedMaxLoss);
        Assert.Equal(RiskDecision.Approved, result.Decision);
    }

    [Fact]
    public void Bull_put_spread_maximum_loss_includes_the_width()
    {
        var order = RiskSamples.Order(
            StrategyKind.Vertical,
            RiskSamples.Buy(RiskSamples.Put(95m)),
            RiskSamples.Sell(RiskSamples.Put(100m)));

        var quotes = new[]
        {
            RiskSamples.Quote(RiskSamples.Put(95m), 0.50m, 0.60m),
            RiskSamples.Quote(RiskSamples.Put(100m), 2.00m, 2.10m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(360m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Bear_put_spread_maximum_loss_is_the_debit_alone()
    {
        var order = RiskSamples.Order(
            StrategyKind.Vertical,
            RiskSamples.Buy(RiskSamples.Put(100m)),
            RiskSamples.Sell(RiskSamples.Put(95m)));

        var quotes = new[]
        {
            RiskSamples.Quote(RiskSamples.Put(100m), 2.00m, 2.10m),
            RiskSamples.Quote(RiskSamples.Put(95m), 0.95m, 1.05m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(115m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Calendar_maximum_loss_is_the_debit()
    {
        var order = RiskSamples.Calendar(quantity: 2);
        var quotes = new[]
        {
            RiskSamples.Quote(RiskSamples.Call(100m, RiskSamples.FarExpiry), 2.90m, 3.00m),
            RiskSamples.Quote(RiskSamples.Call(100m, RiskSamples.NearExpiry), 1.50m, 1.60m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(300m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Diagonal_sold_below_its_long_call_carries_the_strike_width()
    {
        // A diagonal whose short strike sits below the long one concedes that width on the way out.
        // The old formula returned the debit alone for every calendar and diagonal alike.
        var order = RiskSamples.Order(
            StrategyKind.Diagonal,
            RiskSamples.Buy(RiskSamples.Call(105m, RiskSamples.FarExpiry)),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.NearExpiry)));

        var quotes = new[]
        {
            RiskSamples.Quote(RiskSamples.Call(105m, RiskSamples.FarExpiry), 2.90m, 3.00m),
            RiskSamples.Quote(RiskSamples.Call(100m, RiskSamples.NearExpiry), 1.50m, 1.60m),
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(RiskSamples.Request(order, quotes));

        Assert.Equal(650m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Long_strangle_maximum_loss_is_the_premium_paid()
    {
        var order = RiskSamples.Order(
            StrategyKind.Strangle,
            RiskSamples.Buy(RiskSamples.Call(105m)),
            RiskSamples.Buy(RiskSamples.Put(95m)));

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Approved, result.Decision);
        Assert.Equal(120m, result.EstimatedMaxLoss);
    }

    // ---- maximum loss: what the limit price authorises --------------------------------------

    [Fact]
    public void Maximum_loss_uses_the_price_the_limit_order_authorises()
    {
        // The order is allowed to pay $2.50 net whatever the book says at the moment risk runs.
        var order = RiskSamples.LongVertical() with { OrderType = OrderType.Limit, LimitPrice = 2.50m };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(250m, result.EstimatedMaxLoss);
    }

    [Theory]
    [InlineData(0.50)]
    [InlineData(1.60)]
    public void Maximum_loss_keeps_the_quoted_figure_when_the_limit_is_no_worse(double limitPrice)
    {
        // 1.60 is the quoted net exactly: the boundary, where neither figure is worse.
        var order = RiskSamples.LongVertical() with
        {
            OrderType = OrderType.Limit,
            LimitPrice = (decimal)limitPrice,
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(160m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Maximum_loss_ignores_a_limit_price_carried_by_a_market_order()
    {
        // The router does not honour it, so neither does risk.
        var order = RiskSamples.LongVertical() with { OrderType = OrderType.Market, LimitPrice = 25.00m };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(160m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void A_credit_limit_order_is_priced_at_the_credit_it_authorises()
    {
        // Quoted at a $1.40 credit but only committed to $0.50, so $0.50 is what the width is
        // reduced by.
        var order = RiskSamples.CreditVertical(quantity: 1) with
        {
            OrderType = OrderType.Limit,
            LimitPrice = -0.50m,
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(450m, result.EstimatedMaxLoss);
    }

    // ---- shapes with no formula ------------------------------------------------------------

    [Fact]
    public void Refuses_a_strategy_it_has_no_maximum_loss_formula_for()
    {
        // An out-of-range StrategyKind used to fall through to the net debit, and for a naked short
        // strangle that is a credit — so the engine approved it at $0.
        var order = RiskSamples.Order(
            (StrategyKind)99,
            RiskSamples.Sell(RiskSamples.Call(105m)),
            RiskSamples.Sell(RiskSamples.Put(95m)));

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnsupportedStrategy);
        Assert.Equal(0m, result.EstimatedMaxLoss);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Refuses_an_order_that_is_not_two_legs(int legCount)
    {
        var legs = Enumerable.Range(0, legCount)
            .Select(index => RiskSamples.Buy(RiskSamples.Call(100m + index)))
            .ToArray();

        var order = RiskSamples.LongVertical() with { Legs = legs };
        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UnsupportedLegCount);
        Assert.Equal(0m, result.EstimatedMaxLoss);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Refuses_a_non_positive_leg_quantity(int quantity)
    {
        var order = RiskSamples.LongVertical() with
        {
            Legs =
            [
                RiskSamples.Buy(RiskSamples.Call(100m), quantity),
                RiskSamples.Sell(RiskSamples.Call(105m), 1),
            ],
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.NonPositiveLegQuantity);
        Assert.Equal(0m, result.EstimatedMaxLoss);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Refuses_a_leg_whose_contract_multiplier_is_not_positive(int multiplier)
    {
        // Everything the engine computes is denominated by the multiplier, so a zero prices the
        // order at nothing — and TWS resolves the contract by conId, so the order still trades its
        // real size.
        var order = RiskSamples.LongVertical() with
        {
            Legs =
            [
                RiskSamples.Buy(RiskSamples.Call(100m) with { Multiplier = multiplier }),
                RiskSamples.Sell(RiskSamples.Call(105m) with { Multiplier = multiplier }),
            ],
        };

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.NonPositiveMultiplier);
        Assert.Equal(0m, result.EstimatedMaxLoss);
    }

    [Fact]
    public void Refuses_a_time_spread_whose_short_leg_nothing_covers()
    {
        var order = RiskSamples.Order(
            StrategyKind.Calendar,
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.FarExpiry)),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.NearExpiry)));

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UncoveredShortOption);
    }

    [Fact]
    public void Refuses_a_short_volatility_spread()
    {
        var order = RiskSamples.Order(
            StrategyKind.Straddle,
            RiskSamples.Sell(RiskSamples.Call(100m)),
            RiskSamples.Sell(RiskSamples.Put(100m)));

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited)
            .Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.UncoveredShortVolatilitySpread);
    }

    // ---- cover, counted in contracts -------------------------------------------------------

    [Theory]
    [InlineData(10, 10, false)]
    [InlineData(9, 10, true)]
    [InlineData(1, 10, true)]
    [InlineData(11, 10, false)]
    public void Cover_is_counted_in_contracts_not_in_legs(int longQuantity, int shortQuantity, bool uncovered)
    {
        // Reached directly: the leg-quantity guard refuses unequal quantities before this runs, which
        // is the intended ordering and leaves this defence unreachable from Evaluate.
        var order = RiskSamples.Order(
            StrategyKind.Calendar,
            RiskSamples.Buy(RiskSamples.Call(100m, RiskSamples.FarExpiry), longQuantity),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.NearExpiry), shortQuantity));

        Assert.Equal(uncovered, PortfolioRiskEvaluator.HasUncoveredShortLeg(order));
    }

    [Fact]
    public void A_long_expiring_before_the_short_covers_nothing()
    {
        var order = RiskSamples.Order(
            StrategyKind.Calendar,
            RiskSamples.Buy(RiskSamples.Call(100m, RiskSamples.NearExpiry)),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.FarExpiry)));

        Assert.True(PortfolioRiskEvaluator.HasUncoveredShortLeg(order));
    }

    [Fact]
    public void A_long_of_the_other_right_covers_nothing()
    {
        var order = RiskSamples.Order(
            StrategyKind.Calendar,
            RiskSamples.Buy(RiskSamples.Put(100m, RiskSamples.FarExpiry)),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.NearExpiry)));

        Assert.True(PortfolioRiskEvaluator.HasUncoveredShortLeg(order));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void A_long_is_not_spent_twice_across_two_shorts(int longQuantity, bool uncovered)
    {
        var order = RiskSamples.Order(
            StrategyKind.Calendar,
            RiskSamples.Buy(RiskSamples.Call(100m, RiskSamples.FarExpiry), longQuantity),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.FarExpiry), 2),
            RiskSamples.Sell(RiskSamples.Call(100m, RiskSamples.NearExpiry), 1));

        Assert.Equal(uncovered, PortfolioRiskEvaluator.HasUncoveredShortLeg(order));
    }

    // ---- configured limits, at their boundaries --------------------------------------------

    [Theory]
    [InlineData(160.0, false)]
    [InlineData(159.99, true)]
    public void Maximum_loss_breaches_only_above_the_limit(double limit, bool breached)
    {
        var order = RiskSamples.LongVertical();
        var limits = RiskSamples.Unlimited with { MaxLossPerOrder = (decimal)limit };

        var result = new PortfolioRiskEvaluator(limits).Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(breached, result.Breaches.Any(breach => breach.Code == RiskBreachCodes.MaxLossPerOrder));
    }

    [Theory]
    [InlineData(160.0, false)]
    [InlineData(159.99, true)]
    public void Buying_power_breaches_only_above_what_the_account_has(double buyingPower, bool breached)
    {
        var order = RiskSamples.LongVertical();
        var request = RiskSamples.Request(
            order,
            RiskSamples.Quotes(order),
            RiskSamples.Portfolio(buyingPower: (decimal)buyingPower));

        var result = new PortfolioRiskEvaluator(RiskSamples.Unlimited).Evaluate(request);

        Assert.Equal(breached, result.Breaches.Any(breach => breach.Code == RiskBreachCodes.BuyingPower));
    }

    [Theory]
    [InlineData(160.0, false)]
    [InlineData(159.99, true)]
    public void Buying_power_usage_breaches_only_above_the_limit(double limit, bool breached)
    {
        var order = RiskSamples.LongVertical();
        var limits = RiskSamples.Unlimited with { MaxBuyingPowerUsage = (decimal)limit };

        var result = new PortfolioRiskEvaluator(limits).Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(breached, result.Breaches.Any(breach => breach.Code == RiskBreachCodes.MaxBuyingPowerUsage));
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(9, true)]
    public void Contract_count_breaches_only_above_the_limit(int limit, bool breached)
    {
        // Five contracts a leg, two legs: exactly ten.
        var order = RiskSamples.LongVertical(quantity: 5);
        var limits = RiskSamples.Unlimited with { MaxContractsPerOrder = limit };

        var result = new PortfolioRiskEvaluator(limits).Evaluate(RiskSamples.Request(order, RiskSamples.Quotes(order)));

        Assert.Equal(breached, result.Breaches.Any(breach => breach.Code == RiskBreachCodes.MaxContracts));
    }

    [Theory]
    [InlineData(-1_000.0, false)]
    [InlineData(-1_000.01, true)]
    [InlineData(500.0, false)]
    public void Daily_loss_breaches_only_above_the_limit(double dailyPnL, bool breached)
    {
        var order = RiskSamples.LongVertical();
        var limits = RiskSamples.Unlimited with { MaxDailyLoss = 1_000m };
        var request = RiskSamples.Request(
            order,
            RiskSamples.Quotes(order),
            RiskSamples.Portfolio(dailyPnL: (decimal)dailyPnL));

        var result = new PortfolioRiskEvaluator(limits).Evaluate(request);

        Assert.Equal(breached, result.Breaches.Any(breach => breach.Code == RiskBreachCodes.MaxDailyLoss));
    }

    [Theory]
    [InlineData(RiskBreachCodes.MaxDelta, 40.0)]
    [InlineData(RiskBreachCodes.MaxGamma, 2.0)]
    [InlineData(RiskBreachCodes.MaxTheta, 2.0)]
    [InlineData(RiskBreachCodes.MaxVega, 10.0)]
    public void Each_greek_breaches_only_above_its_limit(string code, double exposure)
    {
        var order = RiskSamples.LongVertical();
        var quotes = RiskSamples.GreekSpreadQuotes(order);

        var atTheLimit = new PortfolioRiskEvaluator(RiskSamples.LimitFor(code, (decimal)exposure))
            .Evaluate(RiskSamples.Request(order, quotes));

        var justUnder = new PortfolioRiskEvaluator(RiskSamples.LimitFor(code, (decimal)exposure - 0.01m))
            .Evaluate(RiskSamples.Request(order, quotes));

        Assert.DoesNotContain(atTheLimit.Breaches, breach => breach.Code == code);
        Assert.Contains(justUnder.Breaches, breach => breach.Code == code);
    }

    [Fact]
    public void Greek_limits_measure_the_portfolio_plus_the_order()
    {
        var order = RiskSamples.LongVertical();
        var request = RiskSamples.Request(
            order,
            RiskSamples.GreekSpreadQuotes(order),
            RiskSamples.Portfolio(existingGreeks: new GreeksVector(100m, 0m, 0m, 0m)));

        var result = new PortfolioRiskEvaluator(RiskSamples.LimitFor(RiskBreachCodes.MaxDelta, 100m)).Evaluate(request);

        Assert.Contains(result.Breaches, breach => breach.Code == RiskBreachCodes.MaxDelta);
        Assert.Equal(140m, result.ExposureDelta.Delta + 100m);
    }

    // ---- duplicate submissions -------------------------------------------------------------

    [Fact]
    public void A_second_submission_of_an_approved_client_order_id_is_refused()
    {
        var evaluator = new PortfolioRiskEvaluator(RiskSamples.Unlimited);
        var guard = new DuplicateOrderGuard();
        var order = RiskSamples.LongVertical();
        var request = RiskSamples.Request(order, RiskSamples.Quotes(order));

        var first = RiskEvaluationHandler.Evaluate(request, evaluator, guard);
        var second = RiskEvaluationHandler.Evaluate(request, evaluator, guard);

        Assert.Equal(RiskDecision.Approved, first.Decision);
        Assert.Equal(RiskDecision.Rejected, second.Decision);
        Assert.Contains(second.Breaches, breach => breach.Code == RiskBreachCodes.DuplicateOrder);
    }

    [Fact]
    public void A_rejected_client_order_id_can_be_corrected_and_resubmitted()
    {
        // A rejection never reaches a broker, so burning the id would turn one bad submission into a
        // permanent one — the caller would have to invent a new idempotency key to fix a typo.
        var evaluator = new PortfolioRiskEvaluator(RiskSamples.Unlimited);
        var guard = new DuplicateOrderGuard();
        var order = RiskSamples.Order(
            StrategyKind.Straddle,
            RiskSamples.Sell(RiskSamples.Call(100m)),
            RiskSamples.Sell(RiskSamples.Put(100m)));
        var request = RiskSamples.Request(order, RiskSamples.Quotes(order));

        var first = RiskEvaluationHandler.Evaluate(request, evaluator, guard);
        var second = RiskEvaluationHandler.Evaluate(request, evaluator, guard);

        Assert.DoesNotContain(first.Breaches, breach => breach.Code == RiskBreachCodes.DuplicateOrder);
        Assert.DoesNotContain(second.Breaches, breach => breach.Code == RiskBreachCodes.DuplicateOrder);
        Assert.Contains(second.Breaches, breach => breach.Code == RiskBreachCodes.UncoveredShortVolatilitySpread);
    }

    [Fact]
    public void Concurrent_submissions_of_one_client_order_id_produce_one_approval()
    {
        // IsDuplicate-then-Remember was a check-then-act pair: overlapping submissions of one id all
        // read "not seen" and all got approved, which under Execution:Router=ibkr is that many live
        // orders from one idempotency key.
        //
        // Three deliberate choices here, all measured against the defective version rather than
        // assumed, because a race test that only sometimes observes the race is not a regression
        // guard:
        //   - dedicated threads on a barrier, not pooled tasks: the pool can start a task after the
        //     first submission has already finished, serialising the overlap being measured;
        //   - a padded quote list, because evaluating two legs takes microseconds and the threads
        //     otherwise land outside the window;
        //   - several rounds, because even then a round lands clear of the window now and again.
        // With one round, check-then-act survived roughly one run in five.
        const int Rounds = 8;
        const int Submissions = 32;

        var evaluator = new PortfolioRiskEvaluator(RiskSamples.Unlimited);
        var padding = RiskSamples.Decoys(20_000);

        for (var round = 0; round < Rounds; round++)
        {
            var guard = new DuplicateOrderGuard();
            var order = RiskSamples.LongVertical();
            var request = RiskSamples.Request(order, [.. RiskSamples.Quotes(order), .. padding]);

            var results = new RiskEvaluationResult[Submissions];
            using var start = new Barrier(Submissions);

            var threads = Enumerable.Range(0, Submissions)
                .Select(index => new Thread(() =>
                {
                    start.SignalAndWait();
                    results[index] = RiskEvaluationHandler.Evaluate(request, evaluator, guard);
                }))
                .ToArray();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "A submission thread did not finish.");
            }

            Assert.Equal(1, results.Count(result => result.Decision == RiskDecision.Approved));
            Assert.Equal(
                Submissions - 1,
                results.Count(result => result.Breaches.Any(breach => breach.Code == RiskBreachCodes.DuplicateOrder)));
        }
    }

    [Fact]
    public void An_evaluation_that_throws_leaves_the_client_order_id_usable()
    {
        var evaluator = new PortfolioRiskEvaluator(RiskSamples.Unlimited);
        var guard = new DuplicateOrderGuard();
        var order = RiskSamples.LongVertical();
        var quotes = RiskSamples.Quotes(order);

        Assert.ThrowsAny<Exception>(() => RiskEvaluationHandler.Evaluate(
            new RiskEvaluationRequest(order, null!, quotes, DateTimeOffset.UtcNow),
            evaluator,
            guard));

        var retried = RiskEvaluationHandler.Evaluate(RiskSamples.Request(order, quotes), evaluator, guard);

        Assert.Equal(RiskDecision.Approved, retried.Decision);
    }

    [Fact]
    public void An_order_without_a_client_order_id_is_never_a_duplicate()
    {
        var evaluator = new PortfolioRiskEvaluator(RiskSamples.Unlimited);
        var guard = new DuplicateOrderGuard();
        var order = RiskSamples.LongVertical() with { ClientOrderId = null };
        var request = RiskSamples.Request(order, RiskSamples.Quotes(order));

        var first = RiskEvaluationHandler.Evaluate(request, evaluator, guard);
        var second = RiskEvaluationHandler.Evaluate(request, evaluator, guard);

        Assert.Equal(RiskDecision.Approved, first.Decision);
        Assert.Equal(RiskDecision.Approved, second.Decision);
    }

    [Fact]
    public void Every_breach_code_the_engine_emits_is_in_the_published_vocabulary()
    {
        Assert.Equal(RiskBreachCodes.All.Count, RiskBreachCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(RiskBreachCodes.UnpriceableLeg, RiskBreachCodes.All);
        Assert.Contains(RiskBreachCodes.UnsupportedStrategy, RiskBreachCodes.All);
        Assert.Contains(RiskBreachCodes.UnsupportedLegCount, RiskBreachCodes.All);
        Assert.Contains(RiskBreachCodes.NonPositiveLegQuantity, RiskBreachCodes.All);
        Assert.Contains(RiskBreachCodes.UnequalLegQuantities, RiskBreachCodes.All);
    }

    // ---- configured limits, read from configuration -----------------------------------------

    [Fact]
    public void An_unset_limit_falls_back_to_the_development_default()
    {
        var limits = RiskLimitFactory.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(RiskLimits.DevelopmentDefaults, limits);
    }

    [Fact]
    public void A_configured_limit_is_used()
    {
        var limits = RiskLimitFactory.FromConfiguration(RiskSamples.Configuration("RiskLimits:MaxLossPerOrder", "250"));

        Assert.Equal(250m, limits.MaxLossPerOrder);
    }

    [Theory]
    [InlineData("RiskLimits:MaxLossPerOrder", "2.5k")]
    [InlineData("RiskLimits:MaxLossPerOrder", "$250")]
    [InlineData("RiskLimits:MaxLossPerOrder", "")]
    [InlineData("RiskLimits:MaxContractsPerOrder", "twenty")]
    [InlineData("RiskLimits:MaxAbsDelta", "1O0")]
    public void A_mistyped_limit_stops_the_service_rather_than_loosening_it(string key, string value)
    {
        // The fallback is the loosest limit in the system, so silently substituting it for a typo
        // raises the ceiling the operator was trying to lower — invisibly.
        var error = Assert.Throws<InvalidOperationException>(
            () => RiskLimitFactory.FromConfiguration(RiskSamples.Configuration(key, value)));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Self-contained fixtures. Deliberately not shared with <c>SampleOrders</c>: these pin exact
/// premiums, strikes and Greeks, and a change made for another test's benefit would silently move
/// every expected figure here.
/// </summary>
internal static class RiskSamples
{
    public static readonly DateOnly NearExpiry = new(2026, 8, 21);
    public static readonly DateOnly FarExpiry = new(2026, 9, 18);

    /// <summary>Plausible Greeks for a live option: nothing here is zero.</summary>
    private static readonly OptionGreeks LiveGreeks = new(0.50m, 0.04m, -0.03m, 0.15m);

    public static RiskLimits Unlimited { get; } = new(
        MaxLossPerOrder: 1_000_000m,
        MaxBuyingPowerUsage: 1_000_000m,
        MaxContractsPerOrder: 10_000,
        MaxDailyLoss: 1_000_000m,
        MaxAbsGreeks: new GreeksVector(1_000_000m, 1_000_000m, 1_000_000m, 1_000_000m));

    public static RiskLimits LimitFor(string code, decimal limit) => code switch
    {
        RiskBreachCodes.MaxDelta => Unlimited with { MaxAbsGreeks = Unlimited.MaxAbsGreeks with { Delta = limit } },
        RiskBreachCodes.MaxGamma => Unlimited with { MaxAbsGreeks = Unlimited.MaxAbsGreeks with { Gamma = limit } },
        RiskBreachCodes.MaxTheta => Unlimited with { MaxAbsGreeks = Unlimited.MaxAbsGreeks with { Theta = limit } },
        RiskBreachCodes.MaxVega => Unlimited with { MaxAbsGreeks = Unlimited.MaxAbsGreeks with { Vega = limit } },
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Greek limit."),
    };

    public static OptionContract Call(decimal strike, DateOnly? expiration = null) =>
        Contract(strike, OptionRight.Call, expiration ?? NearExpiry);

    public static OptionContract Put(decimal strike, DateOnly? expiration = null) =>
        Contract(strike, OptionRight.Put, expiration ?? NearExpiry);

    public static OrderLegRequest Buy(OptionContract contract, int quantity = 1) =>
        new(contract, OrderSide.Buy, quantity, PositionEffect.Open);

    public static OrderLegRequest Sell(OptionContract contract, int quantity = 1) =>
        new(contract, OrderSide.Sell, quantity, PositionEffect.Open);

    public static SubmitOrderRequest Order(StrategyKind strategy, params OrderLegRequest[] legs) =>
        new(
            "DU1234567",
            strategy,
            OrderType.Market,
            TimeInForce.Day,
            legs,
            ClientOrderId: Guid.NewGuid(),
            SubmittedBy: "test");

    /// <summary>Buy the 100 call, sell the 105 call: a debit spread worth $1.10 at the quotes below.</summary>
    public static SubmitOrderRequest LongVertical(int quantity = 1) =>
        Order(StrategyKind.Vertical, Buy(Call(100m), quantity), Sell(Call(105m), quantity));

    /// <summary>Sell the 100 call, buy the 105 call: a $1.40 credit against a five-point width.</summary>
    public static SubmitOrderRequest CreditVertical(int quantity = 1) =>
        Order(StrategyKind.Vertical, Buy(Call(105m), quantity), Sell(Call(100m), quantity));

    public static SubmitOrderRequest Calendar(int quantity = 1) =>
        Order(
            StrategyKind.Calendar,
            Buy(Call(100m, FarExpiry), quantity),
            Sell(Call(100m, NearExpiry), quantity));

    public static QuoteSnapshot Quote(
        OptionContract contract,
        decimal bid,
        decimal ask,
        OptionGreeks? greeks = null) =>
        new(
            Guid.NewGuid(),
            contract,
            bid,
            ask,
            decimal.Round((bid + ask) / 2m, 4),
            greeks ?? LiveGreeks,
            DateTimeOffset.UtcNow,
            "test");

    /// <summary>
    /// A quote per leg, priced off the strike so every fixture above has a stable net: the 95 put and
    /// the 105 call are the $0.55 wings, the 100 strike is $2.05.
    /// </summary>
    public static IReadOnlyList<QuoteSnapshot> Quotes(SubmitOrderRequest order) =>
    [
        .. order.Legs.Select(leg => leg.Contract.Strike == 100m
            ? Quote(leg.Contract, 2.00m, 2.10m)
            : Quote(leg.Contract, 0.50m, 0.60m))
    ];

    /// <summary>
    /// Quotes whose Greeks differ between the legs, so the net exposure of a one-by-one vertical is
    /// delta 40, gamma 2, theta -2 and vega 10 rather than cancelling to nothing.
    /// </summary>
    public static IReadOnlyList<QuoteSnapshot> GreekSpreadQuotes(SubmitOrderRequest order) =>
    [
        Quote(order.Legs[0].Contract, 2.00m, 2.10m, new OptionGreeks(0.60m, 0.05m, -0.04m, 0.20m)),
        Quote(order.Legs[1].Contract, 0.95m, 1.05m, new OptionGreeks(0.20m, 0.03m, -0.02m, 0.10m)),
    ];

    /// <summary>
    /// Quotes for contracts no leg refers to, used only to give an evaluation enough work to be
    /// interleaved with. Strikes start well clear of the fixtures above so no key can collide.
    /// </summary>
    public static IReadOnlyList<QuoteSnapshot> Decoys(int count) =>
    [
        .. Enumerable.Range(0, count).Select(index => Quote(Call(1_000m + index), 0.50m, 0.60m))
    ];

    public static PortfolioSnapshot Portfolio(
        decimal buyingPower = 10_000_000m,
        decimal dailyPnL = 0m,
        GreeksVector? existingGreeks = null) =>
        new("DU1234567", buyingPower, dailyPnL, existingGreeks ?? GreeksVector.Zero, []);

    public static RiskEvaluationRequest Request(
        SubmitOrderRequest order,
        IEnumerable<QuoteSnapshot> quotes,
        PortfolioSnapshot? portfolio = null) =>
        new(order, portfolio ?? Portfolio(), [.. quotes], DateTimeOffset.UtcNow);

    public static IConfiguration Configuration(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    private static OptionContract Contract(decimal strike, OptionRight right, DateOnly expiration)
    {
        var rightCode = right == OptionRight.Call ? "C" : "P";

        return new OptionContract($"XYZ{expiration:yyyyMMdd}{rightCode}{strike:0.##}", "XYZ", expiration, strike, right);
    }
}
