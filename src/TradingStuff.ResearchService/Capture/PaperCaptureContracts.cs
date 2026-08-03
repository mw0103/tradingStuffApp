namespace TradingStuff.ResearchService.Capture;

/// <summary>
/// Stable <c>capture_source</c> values. Provenance, not decoration: a fill captured from a different
/// surface later — a broker statement import, say — must be distinguishable from these without
/// anyone having to remember which was which.
/// </summary>
public static class CaptureSources
{
    public const string GatewayExecutions = "ibkr-gateway/reqExecutions";

    public const string GatewayAccount = "ibkr-gateway/account-streams";
}

/// <summary>
/// One row destined for <c>research.paper_fills</c>: a broker execution report, unprojected.
/// </summary>
/// <param name="ExecutedAtRaw">
/// TWS's own time string, verbatim. It is the record; <paramref name="ExecutedAt"/> is a parse of
/// it and is null when the shape was not recognised, never a capture-time substitute.
/// </param>
public sealed record PaperFill(
    DateOnly TradingDate,
    string AccountId,
    string ExecId,
    long? PermId,
    int? IbkrOrderId,
    int? ClientId,
    int ConId,
    string Symbol,
    string SecType,
    DateOnly? Expiration,
    decimal? Strike,
    string? OptionRight,
    string? TradingClass,
    int? Multiplier,
    string Side,
    decimal Quantity,
    decimal Price,
    string ExecutedAtRaw,
    DateTimeOffset? ExecutedAt,
    string? Exchange,
    decimal? Commission,
    string? CommissionCurrency,
    decimal? RealizedPnL,
    string CaptureSource);

/// <summary>
/// One post-close reading of the paper account, destined for <c>research.paper_account_snapshots</c>.
/// </summary>
/// <param name="SummaryJson">
/// Every account-summary tag TWS returned, unparsed. The typed money fields are a projection of it;
/// this is the provenance, so a tag nobody has a column for is still on the record.
/// </param>
/// <param name="PositionsJson">
/// One entry per open position as <c>reqPositionsMulti</c> reported it. No Greeks and no marks —
/// that stream carries neither, and inventing them is exactly what a raw capture must not do.
/// </param>
public sealed record PaperAccountCapture(
    DateOnly TradingDate,
    DateTimeOffset SnapshotAt,
    string AccountId,
    decimal? NetLiquidation,
    decimal? MaintenanceMargin,
    decimal? InitMargin,
    decimal? ExcessLiquidity,
    decimal? AvailableFunds,
    decimal? BuyingPower,
    decimal? GrossPositionValue,
    string? Currency,
    string SummaryJson,
    string PositionsJson,
    int PositionCount,
    IReadOnlyList<PaperFill> Fills,
    string CaptureSource);

/// <summary>What one capture pass wrote, as the store measured it on the way out.</summary>
/// <param name="Stored">
/// False when a snapshot for the trading date already existed. Not an error: the pass is idempotent
/// by design and a re-run is the intended way to recover from a partial evening.
/// </param>
/// <param name="FillsWritten">
/// New <c>paper_fills</c> rows. Lower than the number pulled when TWS replayed executions a previous
/// pass already captured — the exec-id unique constraint is what makes a re-run add nothing.
/// </param>
public sealed record PaperCaptureOutcome(bool Stored, int FillsWritten, int FillsPulled);

/// <summary>One row of <c>research.paper_account_snapshots</c>, as a reader sees it.</summary>
public sealed record PaperAccountSnapshotRow(
    long SnapshotId,
    DateOnly TradingDate,
    DateTimeOffset SnapshotAt,
    string? AccountId,
    decimal? NetLiquidation,
    decimal? MaintenanceMargin,
    decimal? InitMargin,
    decimal? ExcessLiquidity,
    decimal? AvailableFunds,
    decimal? BuyingPower,
    decimal? GrossPositionValue,
    string? Currency,
    string? SummaryJson,
    string? PositionsJson,
    int? PositionCount,
    int? FillCount,
    string? RefusalKind,
    string? Refusal,
    string CaptureSource);

/// <summary>One row of <c>research.paper_fills</c>, as a reader sees it.</summary>
public sealed record PaperFillRow(
    long FillId,
    DateTimeOffset CapturedAt,
    DateOnly TradingDate,
    string AccountId,
    string ExecId,
    long? PermId,
    int? IbkrOrderId,
    int ConId,
    string Symbol,
    string SecType,
    DateOnly? Expiration,
    decimal? Strike,
    string? OptionRight,
    string Side,
    decimal Quantity,
    decimal Price,
    string ExecutedAtRaw,
    DateTimeOffset? ExecutedAt,
    decimal? Commission,
    string? CommissionCurrency,
    string CaptureSource);
