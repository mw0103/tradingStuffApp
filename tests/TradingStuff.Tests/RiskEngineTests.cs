using TradingStuff.Contracts;
using TradingStuff.RiskService;

namespace TradingStuff.Tests;

/// <summary>
/// Coverage for every breach code the risk engine can raise.
/// </summary>
/// <remarks>
/// These checks are the last thing between a submitted order and a real venue, and they now run on
/// real account data rather than fixed development figures. Each code gets a test that makes exactly
/// one limit bite, so a failure names the rule that broke rather than "risk rejected something".
/// </remarks>
public sealed class RiskEngineTests
{
    private static readonly DateOnly NearExpiry = new(2026, 8, 21);
    private static readonly DateOnly FarExpiry = new(2026, 9, 18);

    /// <summary>Limits loose enough that nothing bites; tighten one per test.</summary>
    private static RiskLimits Permissive => new(
        MaxLossPerOrder: 10_000m,
        MaxBuyingPowerUsage: 10_000m,
        MaxContractsPerOrder: 20,
        MaxDailyLoss: 1_000m,
        MaxAbsGreeks: new GreeksVector(1_000m, 1_000m, 1_000m, 1_000m));

    private static PortfolioSnapshot Portfolio(
        decimal buyingPower = 50_000m,
        decimal dailyPnL = 0m,
        GreeksVector? existingGreeks = null) =>
        new("DU1234567", buyingPower, dailyPnL, existingGreeks ?? GreeksVector.Zero, []);

    // ---- the standard debit vertical every test starts from ------------------------------------
    // Long 100C at 2.05 ask, short 105C at 0.95 bid, one lot, multiplier 100:
    //   net debit  = 205 - 95 = 110
    //   delta      = 80 - 20  = 60      gamma = 5 - 1 = 4
    //   theta      = -10 + 2  = -8      vega  = 20 - 4 = 16
    // Greeks differ per leg on purpose: identical ones cancel and would make the theta and vega
    // limits untestable.

    private static OptionContract Call(decimal strike, DateOnly? expiration = null, string exchange = "SMART")
    {
        var expiry = expiration ?? NearExpiry;
        return new OptionContract($"XYZ{expiry:yyyyMMdd}C{strike:0.##}", "XYZ", expiry, strike, OptionRight.Call, exchange);
    }

    private static OptionContract Put(decimal strike, DateOnly? expiration = null)
    {
        var expiry = expiration ?? NearExpiry;
        return new OptionContract($"XYZ{expiry:yyyyMMdd}P{strike:0.##}", "XYZ", expiry, strike, OptionRight.Put);
    }

    private static SubmitOrderRequest Vertical(int quantity = 1, bool credit = false) =>
        new(
            "DU1234567",
            StrategyKind.Vertical,
            OrderType.Limit,
            TimeInForce.Day,
            credit
                ? [
                    new OrderLegRequest(Call(105m), OrderSide.Buy, quantity, PositionEffect.Open),
                    new OrderLegRequest(Call(100m), OrderSide.Sell, quantity, PositionEffect.Open),
                ]
                : [
                    new OrderLegRequest(Call(100m), OrderSide.Buy, quantity, PositionEffect.Open),
                    new OrderLegRequest(Call(105m), OrderSide.Sell, quantity, PositionEffect.Open),
                ],
            ClientOrderId: Guid.NewGuid());

    private static QuoteSnapshot Quote(OptionContract contract, decimal bid, decimal ask, OptionGreeks greeks) =>
        new(Guid.NewGuid(), contract, bid, ask, (bid + ask) / 2m, greeks, DateTimeOffset.UtcNow, "test");

    /// <summary>Quotes matching the legs of <see cref="Vertical"/>.</summary>
    private static IReadOnlyList<QuoteSnapshot> VerticalQuotes(string exchange = "SMART") =>
    [
        Quote(Call(100m, exchange: exchange), 1.95m, 2.05m, new OptionGreeks(0.80m, 0.05m, -0.10m, 0.20m)),
        Quote(Call(105m, exchange: exchange), 0.95m, 1.05m, new OptionGreeks(0.20m, 0.01m, -0.02m, 0.04m)),
    ];

    private static RiskEvaluationResult Evaluate(
        SubmitOrderRequest order,
        RiskLimits? limits = null,
        PortfolioSnapshot? portfolio = null,
        IReadOnlyList<QuoteSnapshot>? quotes = null) =>
        new PortfolioRiskEvaluator(limits ?? Permissive).Evaluate(new RiskEvaluationRequest(
            order,
            portfolio ?? Portfolio(),
            quotes ?? VerticalQuotes(),
            DateTimeOffset.UtcNow));

    private static void AssertBreached(RiskEvaluationResult result, string code)
    {
        Assert.Equal(RiskDecision.Rejected, result.Decision);
        Assert.Contains(result.Breaches, breach => breach.Code == code);
    }

    // ---- baseline ------------------------------------------------------------------------------

    [Fact]
    public void A_modest_vertical_inside_every_limit_is_approved()
    {
        // The control. Without it, a test that expects a rejection cannot distinguish "this rule
        // fired" from "everything gets rejected".
        var result = Evaluate(Vertical());

        Assert.Equal(RiskDecision.Approved, result.Decision);
        Assert.Empty(result.Breaches);
        Assert.Equal(110m, result.EstimatedMaxLoss);
        Assert.Equal(60m, result.ExposureDelta.Delta);
    }

    // ---- size and loss -------------------------------------------------------------------------

    [Fact]
    public void MAX_CONTRACTS_counts_every_leg_not_the_spread_count()
    {
        // 11 lots on each of two legs is 22 contracts against a limit of 20, even though it is only
        // 11 spreads.
        AssertBreached(Evaluate(Vertical(quantity: 11)), "MAX_CONTRACTS");
    }

    [Fact]
    public void MAX_LOSS_PER_ORDER_bites_on_the_estimated_loss()
    {
        var limits = Permissive with { MaxLossPerOrder = 50m };

        AssertBreached(Evaluate(Vertical(), limits), "MAX_LOSS_PER_ORDER");
    }

    [Fact]
    public void BUYING_POWER_bites_when_the_account_cannot_cover_the_order()
    {
        // The limit here is the account's, not a configured ceiling — this is the check that only
        // became meaningful once the portfolio stopped being a stub.
        var result = Evaluate(Vertical(), portfolio: Portfolio(buyingPower: 50m));

        AssertBreached(result, "BUYING_POWER");
        Assert.Equal(50m, result.Breaches.Single(b => b.Code == "BUYING_POWER").Limit);
    }

    [Fact]
    public void MAX_BUYING_POWER_USAGE_is_a_configured_ceiling_separate_from_the_account()
    {
        // Distinct from BUYING_POWER: the account can well afford this, policy says no.
        var limits = Permissive with { MaxBuyingPowerUsage = 50m };
        var result = Evaluate(Vertical(), limits, Portfolio(buyingPower: 1_000_000m));

        AssertBreached(result, "MAX_BUYING_POWER_USAGE");
        Assert.DoesNotContain(result.Breaches, breach => breach.Code == "BUYING_POWER");
    }

    [Fact]
    public void A_credit_vertical_risks_the_width_less_the_credit()
    {
        // Buy 105C at 1.05, sell 100C at 1.95: net credit 90 on a 5-wide spread, so 500 - 90 = 410.
        var quotes = new[]
        {
            Quote(Call(105m), 0.95m, 1.05m, new OptionGreeks(0.20m, 0.01m, -0.02m, 0.04m)),
            Quote(Call(100m), 1.95m, 2.05m, new OptionGreeks(0.80m, 0.05m, -0.10m, 0.20m)),
        };

        var result = Evaluate(Vertical(credit: true), quotes: quotes);

        Assert.Equal(410m, result.EstimatedMaxLoss);
    }

    // ---- daily loss ----------------------------------------------------------------------------

    [Fact]
    public void MAX_DAILY_LOSS_bites_on_a_losing_day()
    {
        // Cannot fire at all while daily P&L is stubbed at zero, which is why it went untested for
        // so long.
        AssertBreached(Evaluate(Vertical(), portfolio: Portfolio(dailyPnL: -1_500m)), "MAX_DAILY_LOSS");
    }

    [Fact]
    public void A_profitable_day_is_not_a_loss()
    {
        // Guards the sign: a large positive P&L must not read as a large loss.
        var result = Evaluate(Vertical(), portfolio: Portfolio(dailyPnL: 5_000m));

        Assert.DoesNotContain(result.Breaches, breach => breach.Code == "MAX_DAILY_LOSS");
    }

    // ---- Greeks --------------------------------------------------------------------------------

    [Theory]
    [InlineData("MAX_DELTA")]
    [InlineData("MAX_GAMMA")]
    [InlineData("MAX_THETA")]
    [InlineData("MAX_VEGA")]
    public void Each_greek_limit_bites_independently(string code)
    {
        // Order exposure is delta 60, gamma 4, theta -8, vega 16. Tighten one limit below its
        // exposure and leave the rest loose, so exactly one code can fire.
        var tight = code switch
        {
            "MAX_DELTA" => new GreeksVector(1m, 1_000m, 1_000m, 1_000m),
            "MAX_GAMMA" => new GreeksVector(1_000m, 1m, 1_000m, 1_000m),
            "MAX_THETA" => new GreeksVector(1_000m, 1_000m, 1m, 1_000m),
            _ => new GreeksVector(1_000m, 1_000m, 1_000m, 1m),
        };

        var result = Evaluate(Vertical(), Permissive with { MaxAbsGreeks = tight });

        AssertBreached(result, code);
        Assert.Single(result.Breaches);
    }

    [Fact]
    public void Greek_limits_are_absolute_so_negative_exposure_breaches_too()
    {
        // Theta exposure is -8. A limit comparison that forgot the absolute value would let short
        // theta run unbounded.
        var result = Evaluate(Vertical(), Permissive with { MaxAbsGreeks = new GreeksVector(1_000m, 1_000m, 1m, 1_000m) });

        AssertBreached(result, "MAX_THETA");
        Assert.Equal(8m, result.Breaches.Single().Actual);
    }

    [Fact]
    public void Greek_limits_measure_existing_exposure_plus_the_order()
    {
        // The whole point of real portfolio data: an order that is fine in isolation is not fine on
        // top of a book that is already at the limit.
        var portfolio = Portfolio(existingGreeks: new GreeksVector(450m, 0m, 0m, 0m));
        var limits = Permissive with { MaxAbsGreeks = new GreeksVector(500m, 1_000m, 1_000m, 1_000m) };

        var result = Evaluate(Vertical(), limits, portfolio);

        AssertBreached(result, "MAX_DELTA");
        Assert.Equal(510m, result.Breaches.Single().Actual);   // 450 existing + 60 incoming
    }

    [Fact]
    public void An_order_that_reduces_existing_exposure_is_not_penalised_for_its_size()
    {
        // Short delta on top of a long book nets down. Summing absolute values instead of signed
        // ones would reject the very orders that de-risk the account.
        var portfolio = Portfolio(existingGreeks: new GreeksVector(-450m, 0m, 0m, 0m));
        var limits = Permissive with { MaxAbsGreeks = new GreeksVector(500m, 1_000m, 1_000m, 1_000m) };

        var result = Evaluate(Vertical(), limits, portfolio);

        Assert.Equal(RiskDecision.Approved, result.Decision);
    }

    // ---- uncovered shorts ----------------------------------------------------------------------

    [Fact]
    public void UNCOVERED_SHORT_OPTION_bites_when_the_long_leg_expires_first()
    {
        // A reverse calendar: short the far-dated call, long the near-dated one. The long expires
        // while the short is still open, so the risk is unbounded from that point.
        var order = new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Calendar,
            OrderType.Limit,
            TimeInForce.Day,
            [
                new OrderLegRequest(Call(100m, NearExpiry), OrderSide.Buy, 1, PositionEffect.Open),
                new OrderLegRequest(Call(100m, FarExpiry), OrderSide.Sell, 1, PositionEffect.Open),
            ],
            ClientOrderId: Guid.NewGuid());

        var quotes = new[]
        {
            Quote(Call(100m, NearExpiry), 1.95m, 2.05m, new OptionGreeks(0.50m, 0.05m, -0.10m, 0.20m)),
            Quote(Call(100m, FarExpiry), 2.95m, 3.05m, new OptionGreeks(0.55m, 0.04m, -0.06m, 0.30m)),
        };

        AssertBreached(Evaluate(order, quotes: quotes), "UNCOVERED_SHORT_OPTION");
    }

    [Fact]
    public void A_normal_calendar_with_the_long_leg_dated_later_is_covered()
    {
        var order = new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Calendar,
            OrderType.Limit,
            TimeInForce.Day,
            [
                new OrderLegRequest(Call(100m, FarExpiry), OrderSide.Buy, 1, PositionEffect.Open),
                new OrderLegRequest(Call(100m, NearExpiry), OrderSide.Sell, 1, PositionEffect.Open),
            ],
            ClientOrderId: Guid.NewGuid());

        var quotes = new[]
        {
            Quote(Call(100m, FarExpiry), 2.95m, 3.05m, new OptionGreeks(0.55m, 0.04m, -0.06m, 0.30m)),
            Quote(Call(100m, NearExpiry), 1.95m, 2.05m, new OptionGreeks(0.50m, 0.05m, -0.10m, 0.20m)),
        };

        var result = Evaluate(order, quotes: quotes);

        Assert.DoesNotContain(result.Breaches, breach => breach.Code == "UNCOVERED_SHORT_OPTION");
    }

    [Fact]
    public void UNCOVERED_SHORT_VOLATILITY_SPREAD_rejects_a_short_strangle()
    {
        var order = new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Strangle,
            OrderType.Limit,
            TimeInForce.Day,
            [
                new OrderLegRequest(Call(110m), OrderSide.Sell, 1, PositionEffect.Open),
                new OrderLegRequest(Put(90m), OrderSide.Sell, 1, PositionEffect.Open),
            ],
            ClientOrderId: Guid.NewGuid());

        var quotes = new[]
        {
            Quote(Call(110m), 0.95m, 1.05m, new OptionGreeks(0.20m, 0.01m, -0.02m, 0.04m)),
            Quote(Put(90m), 0.95m, 1.05m, new OptionGreeks(-0.20m, 0.01m, -0.02m, 0.04m)),
        };

        AssertBreached(Evaluate(order, quotes: quotes), "UNCOVERED_SHORT_VOLATILITY_SPREAD");
    }

    [Fact]
    public void A_long_straddle_has_bounded_loss_and_is_allowed()
    {
        var order = new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Straddle,
            OrderType.Limit,
            TimeInForce.Day,
            [
                new OrderLegRequest(Call(100m), OrderSide.Buy, 1, PositionEffect.Open),
                new OrderLegRequest(Put(100m), OrderSide.Buy, 1, PositionEffect.Open),
            ],
            ClientOrderId: Guid.NewGuid());

        var quotes = new[]
        {
            Quote(Call(100m), 1.95m, 2.05m, new OptionGreeks(0.50m, 0.05m, -0.10m, 0.20m)),
            Quote(Put(100m), 1.95m, 2.05m, new OptionGreeks(-0.50m, 0.05m, -0.10m, 0.20m)),
        };

        var result = Evaluate(order, quotes: quotes);

        Assert.Equal(RiskDecision.Approved, result.Decision);
        Assert.Equal(410m, result.EstimatedMaxLoss);   // the full debit, 205 + 205
    }

    // ---- duplicate orders ----------------------------------------------------------------------

    [Fact]
    public void DUPLICATE_ORDER_is_detected_per_account_and_client_order_id()
    {
        var guard = new DuplicateOrderGuard();
        var order = Vertical();

        Assert.False(guard.IsDuplicate(order));

        guard.Remember(order);

        Assert.True(guard.IsDuplicate(order));
    }

    [Fact]
    public void The_same_client_order_id_on_a_different_account_is_not_a_duplicate()
    {
        var guard = new DuplicateOrderGuard();
        var order = Vertical();

        guard.Remember(order);

        Assert.False(guard.IsDuplicate(order with { AccountId = "DU7654321" }));
    }

    [Fact]
    public void An_order_without_a_client_order_id_is_never_a_duplicate()
    {
        // Nothing to correlate on, so the guard must not collapse them all into one.
        var guard = new DuplicateOrderGuard();
        var order = Vertical() with { ClientOrderId = null };

        guard.Remember(order);

        Assert.False(guard.IsDuplicate(order));
    }

    // ---- quote correlation ---------------------------------------------------------------------

    [Fact]
    public void Quotes_still_correlate_when_the_broker_returns_an_enriched_contract()
    {
        // Regression, and the most dangerous failure mode here: correlating on the whole
        // OptionContract record means one differing property — the routing exchange, a synthetic
        // symbol — drops the leg silently. Exposure and net debit then compute as ZERO, every
        // money and Greek limit passes, and the order is approved. Risk must never fail open.
        var result = Evaluate(Vertical(), quotes: VerticalQuotes(exchange: "CBOE"));

        Assert.Equal(110m, result.EstimatedMaxLoss);
        Assert.Equal(60m, result.ExposureDelta.Delta);
    }

    [Fact]
    public void MISSING_QUOTE_bites_when_no_leg_has_a_quote()
    {
        // A quote set that matches nothing must not read as a risk-free order.
        var unrelated = new[]
        {
            Quote(Call(500m), 1.95m, 2.05m, new OptionGreeks(0.80m, 0.05m, -0.10m, 0.20m)),
        };

        var result = Evaluate(Vertical(), quotes: unrelated);

        AssertBreached(result, "MISSING_QUOTE");
        Assert.Equal(2m, result.Breaches.Single(b => b.Code == "MISSING_QUOTE").Actual);
    }

    [Fact]
    public void MISSING_QUOTE_bites_when_only_some_legs_are_quoted()
    {
        // The dangerous shape: the short leg is priced and the long one is not, so the order looks
        // like a pure credit with no offsetting cost. Partial data must reject, not approve.
        var partial = new[] { VerticalQuotes()[1] };

        var result = Evaluate(Vertical(), quotes: partial);

        AssertBreached(result, "MISSING_QUOTE");
        Assert.Equal(1m, result.Breaches.Single(b => b.Code == "MISSING_QUOTE").Actual);
    }

    [Fact]
    public void Repeated_legs_on_one_contract_do_not_throw()
    {
        // The market-data service returns one quote per leg, so a two-leg order on the same contract
        // yields two identical quote keys. Building the lookup with ToDictionary throws on that.
        var order = new SubmitOrderRequest(
            "DU1234567",
            StrategyKind.Vertical,
            OrderType.Limit,
            TimeInForce.Day,
            [
                new OrderLegRequest(Call(100m), OrderSide.Buy, 1, PositionEffect.Open),
                new OrderLegRequest(Call(100m), OrderSide.Sell, 1, PositionEffect.Open),
            ],
            ClientOrderId: Guid.NewGuid());

        var quotes = new[]
        {
            Quote(Call(100m), 1.95m, 2.05m, new OptionGreeks(0.80m, 0.05m, -0.10m, 0.20m)),
            Quote(Call(100m), 1.95m, 2.05m, new OptionGreeks(0.80m, 0.05m, -0.10m, 0.20m)),
        };

        var result = Evaluate(order, quotes: quotes);

        Assert.NotNull(result);
    }
}
