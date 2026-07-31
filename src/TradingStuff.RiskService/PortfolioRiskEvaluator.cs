using TradingStuff.Contracts;

namespace TradingStuff.RiskService;

public sealed class PortfolioRiskEvaluator(RiskLimits limits)
{
    public RiskEvaluationResult Evaluate(RiskEvaluationRequest request)
    {
        var breaches = new List<RiskLimitBreach>();
        var exposureDelta = CalculateExposureDelta(request.Order, request.Quotes);
        var estimatedMaxLoss = EstimateMaxLoss(request.Order, request.Quotes, breaches);
        var estimatedBuyingPowerImpact = estimatedMaxLoss;
        var totalContracts = request.Order.Legs.Sum(leg => leg.Quantity);

        if (totalContracts > limits.MaxContractsPerOrder)
        {
            breaches.Add(new RiskLimitBreach(
                "MAX_CONTRACTS",
                "Order contract count exceeds the configured per-order limit.",
                totalContracts,
                limits.MaxContractsPerOrder));
        }

        if (estimatedMaxLoss > limits.MaxLossPerOrder)
        {
            breaches.Add(new RiskLimitBreach(
                "MAX_LOSS_PER_ORDER",
                "Estimated maximum loss exceeds the configured per-order limit.",
                estimatedMaxLoss,
                limits.MaxLossPerOrder));
        }

        if (estimatedBuyingPowerImpact > request.Portfolio.BuyingPower)
        {
            breaches.Add(new RiskLimitBreach(
                "BUYING_POWER",
                "Estimated buying-power impact exceeds available buying power.",
                estimatedBuyingPowerImpact,
                request.Portfolio.BuyingPower));
        }

        if (estimatedBuyingPowerImpact > limits.MaxBuyingPowerUsage)
        {
            breaches.Add(new RiskLimitBreach(
                "MAX_BUYING_POWER_USAGE",
                "Estimated buying-power impact exceeds the configured order limit.",
                estimatedBuyingPowerImpact,
                limits.MaxBuyingPowerUsage));
        }

        var currentDailyLoss = Math.Max(0m, -request.Portfolio.DailyPnL);
        if (currentDailyLoss > limits.MaxDailyLoss)
        {
            breaches.Add(new RiskLimitBreach(
                "MAX_DAILY_LOSS",
                "Current daily loss exceeds the configured limit.",
                currentDailyLoss,
                limits.MaxDailyLoss));
        }

        AddGreekBreaches(request.Portfolio.ExistingGreeks + exposureDelta, breaches);

        return new RiskEvaluationResult(
            Guid.NewGuid(),
            breaches.Count == 0 ? RiskDecision.Approved : RiskDecision.Rejected,
            breaches,
            exposureDelta,
            estimatedMaxLoss,
            estimatedBuyingPowerImpact,
            DateTimeOffset.UtcNow);
    }

    private void AddGreekBreaches(GreeksVector projectedExposure, List<RiskLimitBreach> breaches)
    {
        AddGreekBreach("DELTA", projectedExposure.Delta, limits.MaxAbsGreeks.Delta, breaches);
        AddGreekBreach("GAMMA", projectedExposure.Gamma, limits.MaxAbsGreeks.Gamma, breaches);
        AddGreekBreach("THETA", projectedExposure.Theta, limits.MaxAbsGreeks.Theta, breaches);
        AddGreekBreach("VEGA", projectedExposure.Vega, limits.MaxAbsGreeks.Vega, breaches);
    }

    private static void AddGreekBreach(string greek, decimal actual, decimal limit, List<RiskLimitBreach> breaches)
    {
        var absoluteActual = Math.Abs(actual);
        if (absoluteActual <= limit)
        {
            return;
        }

        breaches.Add(new RiskLimitBreach(
            $"MAX_{greek}",
            $"Projected absolute {greek.ToLowerInvariant()} exposure exceeds the configured limit.",
            absoluteActual,
            limit));
    }

    private static GreeksVector CalculateExposureDelta(SubmitOrderRequest order, IReadOnlyList<QuoteSnapshot> quotes)
    {
        var quoteByContract = quotes.ToDictionary(quote => quote.Contract);
        var exposure = GreeksVector.Zero;

        foreach (var leg in order.Legs)
        {
            if (!quoteByContract.TryGetValue(leg.Contract, out var quote))
            {
                continue;
            }

            var direction = leg.Side == OrderSide.Buy ? 1m : -1m;
            var multiplier = leg.Contract.Multiplier * leg.Quantity * direction;

            exposure += new GreeksVector(
                quote.Greeks.Delta * multiplier,
                quote.Greeks.Gamma * multiplier,
                quote.Greeks.Theta * multiplier,
                quote.Greeks.Vega * multiplier);
        }

        return exposure;
    }

    private static decimal EstimateMaxLoss(
        SubmitOrderRequest order,
        IReadOnlyList<QuoteSnapshot> quotes,
        List<RiskLimitBreach> breaches)
    {
        var quoteByContract = quotes.ToDictionary(quote => quote.Contract);
        var netDebit = CalculateNetDebit(order, quoteByContract);

        return order.Strategy switch
        {
            StrategyKind.Vertical => EstimateVerticalMaxLoss(order, netDebit),
            StrategyKind.Calendar or StrategyKind.Diagonal => EstimateDefinedTimeSpreadMaxLoss(order, netDebit, breaches),
            StrategyKind.Straddle or StrategyKind.Strangle => EstimateVolatilitySpreadMaxLoss(order, netDebit, breaches),
            _ => Math.Max(0m, netDebit)
        };
    }

    private static decimal CalculateNetDebit(
        SubmitOrderRequest order,
        IReadOnlyDictionary<OptionContract, QuoteSnapshot> quoteByContract)
    {
        var netDebit = 0m;

        foreach (var leg in order.Legs)
        {
            if (!quoteByContract.TryGetValue(leg.Contract, out var quote))
            {
                continue;
            }

            var price = leg.Side == OrderSide.Buy ? quote.Ask : quote.Bid;
            var signedPrice = leg.Side == OrderSide.Buy ? price : -price;
            netDebit += signedPrice * leg.Contract.Multiplier * leg.Quantity;
        }

        return netDebit;
    }

    private static decimal EstimateVerticalMaxLoss(SubmitOrderRequest order, decimal netDebit)
    {
        var legs = order.Legs;
        var width = Math.Abs(legs[0].Contract.Strike - legs[1].Contract.Strike);
        var quantity = Math.Max(legs[0].Quantity, legs[1].Quantity);
        var spreadWidthRisk = width * legs[0].Contract.Multiplier * quantity;

        if (netDebit >= 0m)
        {
            return netDebit;
        }

        return Math.Max(0m, spreadWidthRisk + netDebit);
    }

    private static decimal EstimateDefinedTimeSpreadMaxLoss(
        SubmitOrderRequest order,
        decimal netDebit,
        List<RiskLimitBreach> breaches)
    {
        if (HasUncoveredShortLeg(order))
        {
            breaches.Add(new RiskLimitBreach(
                "UNCOVERED_SHORT_OPTION",
                "Short option leg is not covered by a compatible long leg.",
                1m,
                0m));
        }

        return Math.Max(0m, netDebit);
    }

    private static decimal EstimateVolatilitySpreadMaxLoss(
        SubmitOrderRequest order,
        decimal netDebit,
        List<RiskLimitBreach> breaches)
    {
        if (order.Legs.Any(leg => leg.Side == OrderSide.Sell))
        {
            breaches.Add(new RiskLimitBreach(
                "UNCOVERED_SHORT_VOLATILITY_SPREAD",
                "Short straddles and strangles are rejected in v1 because loss is not bounded.",
                1m,
                0m));
        }

        return Math.Max(0m, netDebit);
    }

    private static bool HasUncoveredShortLeg(SubmitOrderRequest order)
    {
        foreach (var shortLeg in order.Legs.Where(leg => leg.Side == OrderSide.Sell))
        {
            var covered = order.Legs.Any(longLeg =>
                longLeg.Side == OrderSide.Buy &&
                longLeg.Contract.Underlying == shortLeg.Contract.Underlying &&
                longLeg.Contract.Right == shortLeg.Contract.Right &&
                longLeg.Contract.Expiration >= shortLeg.Contract.Expiration);

            if (!covered)
            {
                return true;
            }
        }

        return false;
    }
}
