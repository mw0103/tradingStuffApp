namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// The response body for both <c>POST /research/studies/vol-residual/run</c> and
/// <c>GET /research/studies/vol-residual/latest</c>. The shape is fixed by the task that specified
/// this endpoint — a UI is coded against it in parallel — and must not drift; see the plain string
/// constants in <see cref="VolResidualRunStatus"/> for the two legal <see cref="Status"/> values.
/// </summary>
public sealed record VolResidualRunResponse(
    Guid RunId,
    bool IsDevelopmentRun,
    DateTimeOffset GeneratedAt,
    string Status,
    string? InsufficientReason,
    VolResidualDataWindow DataWindow,
    VolResidualHoldoutInfo ReservedHoldout,
    string GateModelKey,
    IReadOnlyList<VolResidualModelSummary> Models,
    IReadOnlyList<VolResidualDailyRow> Daily);

public static class VolResidualRunStatus
{
    public const string Ok = "ok";
    public const string InsufficientData = "insufficient-data";
}

public sealed record VolResidualDataWindow(DateOnly From, DateOnly To, int SessionsAvailable, int SessionsUsed);

public sealed record VolResidualHoldoutInfo(DateOnly From, DateOnly To, bool Excluded)
{
    public static VolResidualHoldoutInfo Registered { get; } =
        new(ReservedHoldout.Start, ReservedHoldout.End, Excluded: true);
}

public sealed record VolResidualFoldSummary(
    int Fold, DateOnly TrainFrom, DateOnly TrainTo, DateOnly TestFrom, DateOnly TestTo, double Qlike, int Days);

public sealed record VolResidualModelSummary(
    string Key,
    string Label,
    string Role,
    double PooledQlike,
    double ImprovementVsGatePct,
    IReadOnlyList<VolResidualFoldSummary> Folds);

public sealed record VolResidualDailyRow(
    DateOnly Date,
    int Fold,
    double ActualRv,
    IReadOnlyDictionary<string, double> Forecasts,
    IReadOnlyDictionary<string, double> Qlike,
    double CumulativeQlikeDiffVsGate);

public static class VolResidualModelRoles
{
    public const string Reference = "reference";
    public const string Baseline = "baseline";
    public const string Gate = "gate";
    public const string Candidate = "candidate";
}
