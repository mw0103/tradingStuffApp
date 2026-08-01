using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Orchestrates one development run of the volatility-forecast-residual study: load SPX/VIX bars
/// from <c>research.bars</c>, build the registered no-look-ahead dataset, walk-forward fit and score
/// HAR-RV, B1, HAR-X and the corrected/residual candidate, and shape the result into the fixed API
/// contract.
/// </summary>
/// <remarks>
/// Every hard constraint the study was built against lives at a specific step here:
/// <list type="bullet">
/// <item>the reserved holdout is excluded before a single row is loaded (<see cref="ReservedHoldout.ClampToExcludeHoldout"/>), not filtered out afterward;</item>
/// <item>insufficient data is a first-class, honestly-reasoned status, never an empty-but-"ok" result (<see cref="VolResidualModelKeys"/> is not even referenced on that path);</item>
/// <item>QLIKE is the only loss computed, in its frozen form (<see cref="QlikeRetransformation"/>);</item>
/// <item>retransformation is training-window-only and model-specific (<see cref="VolResidualFoldRunner"/>).</item>
/// </list>
/// </remarks>
public sealed class VolResidualStudyRunner(
    VolResidualBarLoader barLoader, ISessionClock sessionClock, ILogger<VolResidualStudyRunner> logger)
{
    /// <summary>Floor for an unbounded ("all available") request — matches the backfill catalog's own SPX intraday floor.</summary>
    private static readonly DateOnly EarliestAvailable = new(2010, 1, 1);

    /// <summary>Exposed only so the endpoint can fail fast with a 503 before attempting a run.</summary>
    public string? ConnectionStringForDiagnostics => barLoader.ConnectionString;

    public async Task<VolResidualRunResponse> RunAsync(
        DateOnly? requestedFrom, DateOnly? requestedTo, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var generatedAt = DateTimeOffset.UtcNow;

        var clamp = ReservedHoldout.ClampToExcludeHoldout(requestedFrom, requestedTo, EarliestAvailable);

        if (clamp.IsEmpty)
        {
            return InsufficientData(
                runId, generatedAt, clamp.From, clamp.To, sessionsAvailable: 0, sessionsUsed: 0,
                reason: $"The requested range falls entirely inside (or after) the reserved holdout " +
                        $"({ReservedHoldout.Start:yyyy-MM-dd} to {ReservedHoldout.End:yyyy-MM-dd}), which this " +
                        "development run must never load, score, or display. No usable window remains after " +
                        "clamping.");
        }

        logger.LogInformation(
            "vol-residual dev run {RunId}: requested [{ReqFrom}, {ReqTo}], clamped to [{From}, {To}] (clamped={Clamped})",
            runId, requestedFrom, requestedTo, clamp.From, clamp.To, clamp.WasClamped);

        var spxBars = await barLoader.LoadSpxOneMinuteBarsAsync(clamp.From, clamp.To, cancellationToken);
        var vixDailyCloses = await barLoader.LoadVixDailyClosesAsync(clamp.From, clamp.To, cancellationToken);

        var intradayBars = HistoricalBarAdapter.ToIntradayBars(spxBars).ToList();
        var spxDays = VolatilityPresets.BuildSpxStudyTarget(sessionClock, intradayBars);
        var completeSpxDays = spxDays.Count(d => d.IsComplete);

        var rawRows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vixDailyCloses);

        if (rawRows.Count == 0)
        {
            return InsufficientData(
                runId, generatedAt, clamp.From, clamp.To, completeSpxDays, sessionsUsed: 0,
                reason: $"No day in [{clamp.From:yyyy-MM-dd}, {clamp.To:yyyy-MM-dd}] has a full registered " +
                        $"feature row yet: {completeSpxDays} complete SPX session(s) and {vixDailyCloses.Count} " +
                        "VIX daily close(s) are currently available, but the HAR triplet alone needs 22 prior " +
                        "complete SPX sessions plus an aligned VIX close before the first day can be scored. " +
                        "The backfill has not reached far enough yet.");
        }

        var splits = VolResidualSplitter.Split(rawRows, WalkForwardFold.Registered());
        var scoreable = splits.Where(VolResidualFoldRunner.CanScore).ToList();

        if (scoreable.Count == 0)
        {
            var detail = string.Join("; ", splits.Select(s =>
                $"{s.Fold.Name}: {s.Train.Count} train / {s.Test.Count} test rows"));

            return InsufficientData(
                runId, generatedAt, clamp.From, clamp.To, completeSpxDays, rawRows.Count,
                reason: $"{rawRows.Count} feature row(s) were built, but no registered walk-forward fold has " +
                        $"both >= {VolResidualFoldRunner.MinimumTrainRows} training rows and >= 1 test row " +
                        $"yet ({detail}). The backfill has not reached far enough into any fold's window.");
        }

        var foldResults = scoreable.Select(VolResidualFoldRunner.Run).ToList();

        var models = BuildModelSummaries(foldResults);
        var daily = BuildDailyRows(foldResults);

        return new VolResidualRunResponse(
            runId,
            IsDevelopmentRun: true,
            generatedAt,
            VolResidualRunStatus.Ok,
            InsufficientReason: null,
            new VolResidualDataWindow(clamp.From, clamp.To, completeSpxDays, rawRows.Count),
            VolResidualHoldoutInfo.Registered,
            VolResidualModelKeys.Gate,
            models,
            daily);
    }

    private static VolResidualRunResponse InsufficientData(
        Guid runId, DateTimeOffset generatedAt, DateOnly from, DateOnly to,
        int sessionsAvailable, int sessionsUsed, string reason) =>
        new(
            runId,
            IsDevelopmentRun: true,
            generatedAt,
            VolResidualRunStatus.InsufficientData,
            InsufficientReason: reason,
            new VolResidualDataWindow(from, to, sessionsAvailable, sessionsUsed),
            VolResidualHoldoutInfo.Registered,
            VolResidualModelKeys.Gate,
            Models: [],
            Daily: []);

    private static List<VolResidualModelSummary> BuildModelSummaries(IReadOnlyList<VolResidualFoldResult> foldResults)
    {
        (string Key, string Label, string Role)[] definitions =
        [
            (VolResidualModelKeys.Har, "HAR-RV", VolResidualModelRoles.Reference),
            (VolResidualModelKeys.Vix, "B1: calibrated VIX", VolResidualModelRoles.Baseline),
            (VolResidualModelKeys.HarX, "HAR-X (primary gate)", VolResidualModelRoles.Gate),
            (VolResidualModelKeys.Corrected, "Corrected (elastic net on HAR-X residual)", VolResidualModelRoles.Candidate),
        ];

        var gatePooled = PooledQlike(foldResults, VolResidualModelKeys.Gate);

        var summaries = new List<VolResidualModelSummary>(definitions.Length);
        foreach (var (key, label, role) in definitions)
        {
            var folds = foldResults.Select(fold => new VolResidualFoldSummary(
                FoldOrdinal(fold.FoldName),
                fold.TrainFrom, fold.TrainTo, fold.TestFrom, fold.TestTo,
                fold.DailyResults.Average(d => d.Qlike[key]),
                fold.DailyResults.Count)).ToList();

            var pooled = PooledQlike(foldResults, key);

            summaries.Add(new VolResidualModelSummary(
                key, label, role, pooled, QlikeRetransformation.ImprovementPercent(pooled, gatePooled), folds));
        }

        return summaries;
    }

    private static double PooledQlike(IReadOnlyList<VolResidualFoldResult> foldResults, string modelKey) =>
        foldResults.SelectMany(f => f.DailyResults).Average(d => d.Qlike[modelKey]);

    private static List<VolResidualDailyRow> BuildDailyRows(IReadOnlyList<VolResidualFoldResult> foldResults)
    {
        var ordered = foldResults
            .SelectMany(f => f.DailyResults)
            .OrderBy(d => d.Date)
            .ToList();

        var rows = new List<VolResidualDailyRow>(ordered.Count);
        double cumulativeDiff = 0.0;

        foreach (var day in ordered)
        {
            var gateLoss = day.Qlike[VolResidualModelKeys.Gate];
            var candidateLoss = day.Qlike[VolResidualModelKeys.Corrected];
            cumulativeDiff += gateLoss - candidateLoss;

            rows.Add(new VolResidualDailyRow(
                day.Date, FoldOrdinal(day.FoldName), day.ActualVariance, day.Forecasts, day.Qlike, cumulativeDiff));
        }

        return rows;
    }

    /// <summary>"F1" -> 1, "F2" -> 2, ... — the registered fold's position, not an index into whatever subset scored.</summary>
    private static int FoldOrdinal(string foldName) =>
        int.TryParse(foldName.TrimStart('F'), out var ordinal) ? ordinal : 0;
}
