namespace TradingStuff.Contracts;

public enum AssetClass
{
    Option
}

public enum OptionRight
{
    Call,
    Put
}

public enum OrderSide
{
    Buy,
    Sell
}

public enum PositionEffect
{
    Open,
    Close
}

public enum OrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

public enum TimeInForce
{
    Day,
    GoodTillCanceled,
    ImmediateOrCancel,
    FillOrKill
}

public enum StrategyKind
{
    Vertical,
    Calendar,
    Diagonal,
    Straddle,
    Strangle
}

public enum OrderLifecycleStatus
{
    Received,
    Rejected,
    RiskApproved,
    RiskRejected,
    Submitted,
    PartiallyFilled,
    Filled,
    Cancelled,
    ReplaceRequested,
    Failed
}

public enum RiskDecision
{
    Approved,
    Rejected
}

public enum FillLiquidity
{
    Simulated,
    BrokerReported
}

public sealed record OptionContract(
    string Symbol,
    string Underlying,
    DateOnly Expiration,
    decimal Strike,
    OptionRight Right,
    string Exchange = "SMART",
    string Currency = "USD",
    int Multiplier = 100);

public sealed record OrderLegRequest(
    OptionContract Contract,
    OrderSide Side,
    int Quantity,
    PositionEffect PositionEffect,
    decimal? LimitPrice = null);

public sealed record SubmitOrderRequest(
    string AccountId,
    StrategyKind Strategy,
    OrderType OrderType,
    TimeInForce TimeInForce,
    IReadOnlyList<OrderLegRequest> Legs,
    decimal? LimitPrice = null,
    decimal? StopPrice = null,
    Guid? ClientOrderId = null,
    string? SubmittedBy = null);

public sealed record ReplaceOrderRequest(
    decimal? LimitPrice,
    decimal? StopPrice,
    TimeInForce? TimeInForce);

public sealed record CancelOrderRequest(string Reason);

public sealed record OptionGreeks(
    decimal Delta,
    decimal Gamma,
    decimal Theta,
    decimal Vega);

public sealed record GreeksVector(
    decimal Delta,
    decimal Gamma,
    decimal Theta,
    decimal Vega)
{
    public static GreeksVector Zero { get; } = new(0, 0, 0, 0);

    public static GreeksVector operator +(GreeksVector left, GreeksVector right) =>
        new(left.Delta + right.Delta, left.Gamma + right.Gamma, left.Theta + right.Theta, left.Vega + right.Vega);
}

public sealed record QuoteSnapshot(
    Guid QuoteId,
    OptionContract Contract,
    decimal Bid,
    decimal Ask,
    decimal Last,
    OptionGreeks Greeks,
    DateTimeOffset CapturedAt,
    string Source);

public sealed record PositionSnapshot(
    OptionContract Contract,
    int Quantity,
    decimal AveragePrice,
    GreeksVector GreeksExposure);

public sealed record PortfolioSnapshot(
    string AccountId,
    decimal BuyingPower,
    decimal DailyPnL,
    GreeksVector ExistingGreeks,
    IReadOnlyList<PositionSnapshot> Positions);

public sealed record RiskLimits(
    decimal MaxLossPerOrder,
    decimal MaxBuyingPowerUsage,
    int MaxContractsPerOrder,
    decimal MaxDailyLoss,
    GreeksVector MaxAbsGreeks)
{
    public static RiskLimits DevelopmentDefaults { get; } =
        new(2_500m, 5_000m, 20, 1_000m, new GreeksVector(500m, 75m, 500m, 500m));
}

public sealed record RiskEvaluationRequest(
    SubmitOrderRequest Order,
    PortfolioSnapshot Portfolio,
    IReadOnlyList<QuoteSnapshot> Quotes,
    DateTimeOffset RequestedAt);

public sealed record RiskLimitBreach(
    string Code,
    string Message,
    decimal Actual,
    decimal Limit);

public sealed record RiskEvaluationResult(
    Guid DecisionId,
    RiskDecision Decision,
    IReadOnlyList<RiskLimitBreach> Breaches,
    GreeksVector ExposureDelta,
    decimal EstimatedMaxLoss,
    decimal EstimatedBuyingPowerImpact,
    DateTimeOffset EvaluatedAt);

public sealed record MarketDataQuoteRequest(
    IReadOnlyList<OrderLegRequest> Legs);

public sealed record MarketDataQuoteResponse(
    IReadOnlyList<QuoteSnapshot> Quotes,
    DateTimeOffset CapturedAt,
    string Source);

public sealed record FillReport(
    Guid FillId,
    Guid OrderId,
    int LegIndex,
    int Quantity,
    decimal Price,
    FillLiquidity Liquidity,
    DateTimeOffset FilledAt);

public sealed record OrderLifecycleEvent(
    Guid EventId,
    Guid OrderId,
    OrderLifecycleStatus Status,
    string Message,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    Guid? CausationId = null);

public sealed record ExecutionOrder(
    Guid OrderId,
    Guid CorrelationId,
    SubmitOrderRequest Request,
    OrderLifecycleStatus Status,
    IReadOnlyList<QuoteSnapshot> Quotes,
    RiskEvaluationResult? RiskDecision,
    IReadOnlyList<FillReport> Fills,
    IReadOnlyList<OrderLifecycleEvent> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SubmitOrderResponse(
    Guid OrderId,
    Guid CorrelationId,
    OrderLifecycleStatus Status,
    RiskEvaluationResult? RiskDecision,
    IReadOnlyList<FillReport> Fills);

public sealed record PublishedExecutionEvent(
    string Name,
    Guid EventId,
    Guid OrderId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    object Payload);
