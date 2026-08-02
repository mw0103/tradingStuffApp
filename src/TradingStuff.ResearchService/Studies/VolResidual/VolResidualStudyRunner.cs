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

    /// <summary>
    /// Why an exploratory run is exploratory, naming the ladder rule it sits outside. Carried in the
    /// API response and in the persisted artifact so the status travels with the numbers.
    /// </summary>
    public const string ExploratoryGbtReason =
        "Ladder rung 4 (gradient-boosted trees) is registered to run ONLY IF rung 3 passes the H1 " +
        "gate — docs/research/volatility-forecast-residual-study.md, 'Baseline ladder and promotion " +
        "gates': \"gradient-boosted trees (depth <= 3, <= 200 trees, min-child >= 50) — only if rung 3 " +
        "passes the H1 gate. Running GBT after a linear failure is the canonical false-discovery move " +
        "and is banned\". Rung 3 (the corrected elastic net) has FAILED H1 on this data. This run is " +
        "therefore outside the registered ladder: exploratory only, never registrable, and not " +
        "eligible for any claim. It is not written to research.registered_trials and does not consume " +
        "a registered-variant slot.";

    /// <summary>The one sentence an exploratory result is permitted.</summary>
    public const string ExploratoryClaim =
        "EXPLORATORY — not eligible for any claim. A difference in loss here is a finding about model " +
        "class, not evidence of edge.";

    public Task<VolResidualRunResponse> RunAsync(
        DateOnly? requestedFrom, DateOnly? requestedTo, CancellationToken cancellationToken) =>
        RunAsync(requestedFrom, requestedTo, includeExploratoryGbt: false, cancellationToken);

    /// <param name="includeExploratoryGbt">
    /// Additionally fit and score ladder rung 4. See <see cref="ExploratoryGbtReason"/> — this taints
    /// the whole run as exploratory and non-registrable, deliberately, rather than quarantining the
    /// GBT numbers inside an otherwise clean-looking response.
    /// </param>
    public async Task<VolResidualRunResponse> RunAsync(
        DateOnly? requestedFrom, DateOnly? requestedTo, bool includeExploratoryGbt, CancellationToken cancellationToken)
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
                        "clamping.",
                includeExploratoryGbt);
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
                        "The backfill has not reached far enough yet.",
                includeExploratoryGbt);
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
                        $"yet ({detail}). The backfill has not reached far enough into any fold's window.",
                includeExploratoryGbt);
        }

        var foldResults = scoreable.Select(split => VolResidualFoldRunner.Run(split, includeExploratoryGbt)).ToList();

        var models = BuildModelSummaries(foldResults, includeExploratoryGbt);
        var daily = BuildDailyRows(foldResults);

        // H1 is adjudicated for the REGISTERED candidate, whether or not an exploratory rung also
        // ran. The exploratory rung never gets a verdict object: producing one would manufacture the
        // eligibility the exploratory tagging exists to deny.
        var h1 = VolResidualAdjudication.Adjudicate(foldResults, VolResidualModelKeys.RegisteredCandidate);

        var exploratory = includeExploratoryGbt ? BuildExploratoryRung(foldResults) : null;

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
            daily,
            h1,
            IsExploratory: includeExploratoryGbt,
            Registrable: !includeExploratoryGbt,
            ExploratoryReason: includeExploratoryGbt ? ExploratoryGbtReason : null,
            Exploratory: exploratory);
    }

    private static VolResidualExploratoryRung? BuildExploratoryRung(IReadOnlyList<VolResidualFoldResult> foldResults)
    {
        var comparison = VolResidualAdjudication.CompareOnly(foldResults, VolResidualModelKeys.Gbt);
        if (comparison is not { } dm) return null;

        var pooled = PooledQlike(foldResults, VolResidualModelKeys.Gbt);
        var gatePooled = PooledQlike(foldResults, VolResidualModelKeys.Gate);

        return new VolResidualExploratoryRung(
            VolResidualModelKeys.Gbt,
            "Rung 4: gradient-boosted trees (EXPLORATORY)",
            IsExploratory: true,
            Registrable: false,
            ExploratoryGbtReason,
            ExploratoryClaim,
            pooled,
            QlikeRetransformation.ImprovementPercent(pooled, gatePooled),
            dm.MarginAdjusted,
            dm.Unadjusted,
            GradientBoostedTreeHyperparameters.Describe(),
            foldResults.Sum(f => f.GbtFloorHits),
            "No retransformation is applied, deliberately. The registration retransforms every model " +
            "estimated on a TRANSFORMED target and explicitly does not retransform one that already " +
            "forecasts level-scale variance directly. This rung is fit on level-scale variance under " +
            "squared error, so its output already targets the conditional mean; a smearing factor " +
            "would correct for a log transform that was never taken.");
    }

    private static VolResidualRunResponse InsufficientData(
        Guid runId, DateTimeOffset generatedAt, DateOnly from, DateOnly to,
        int sessionsAvailable, int sessionsUsed, string reason, bool includeExploratoryGbt = false) =>
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
            Daily: [],
            H1: null,
            IsExploratory: includeExploratoryGbt,
            Registrable: !includeExploratoryGbt,
            ExploratoryReason: includeExploratoryGbt ? ExploratoryGbtReason : null,
            Exploratory: null);

    private static List<VolResidualModelSummary> BuildModelSummaries(
        IReadOnlyList<VolResidualFoldResult> foldResults, bool includeExploratoryGbt)
    {
        // Derived from the catalog, never a parallel list: a method the fold runner fitted but the
        // reporting layer forgot would be invisible in exactly the way that lets a result go
        // unexamined. Adding a catalog entry is sufficient to have it reported.
        var methods = includeExploratoryGbt
            ? VolResidualMethodCatalog.Registered.Concat(VolResidualMethodCatalog.Exploratory).ToList()
            : VolResidualMethodCatalog.Registered.ToList();

        var definitions = methods.Select(m => (m.Key, m.Label, m.Role)).ToList();

        var gatePooled = PooledQlike(foldResults, VolResidualModelKeys.Gate);

        var summaries = new List<VolResidualModelSummary>(definitions.Count);
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
                day.Date, FoldOrdinal(day.FoldName), day.ActualVariance, day.Forecasts, day.Qlike, cumulativeDiff,
                day.PriorVix, day.VixRegime));
        }

        return rows;
    }

    /// <summary>"F1" -> 1, "F2" -> 2, ... — the registered fold's position, not an index into whatever subset scored.</summary>
    private static int FoldOrdinal(string foldName) =>
        int.TryParse(foldName.TrimStart('F'), out var ordinal) ? ordinal : 0;
}
