using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// Orchestrates one development run of the companion VRP-conditioning study: the 21-trading-day
/// version of the parent volatility-forecast study, built to answer the question the parent's
/// one-session horizon structurally cannot — "can a better estimate of future realized volatility
/// improve WHEN you sell volatility, avoid selling it, or size the position?"
/// </summary>
/// <remarks>
/// <para>
/// Everything reusable is reused: <see cref="ReservedHoldout"/> for the holdout clamp,
/// <see cref="VolResidualBarLoader"/> for the bars, <see cref="QlikeRetransformation"/> for the loss
/// and the retransformation rule, <see cref="CalibratedVixFit"/> /
/// <see cref="NonNegativeLeastSquares"/> / <see cref="ElasticNet"/> for the arms,
/// <see cref="VolResidualSplitter"/> for the walk-forward cut,
/// <see cref="StationaryBlockBootstrap"/> and <see cref="DieboldMariano"/> for the inference.
/// </para>
/// <para>
/// <b>The holdout is excluded by construction, before a row loads</b>, exactly as
/// <see cref="VolResidualStudyRunner"/> does it — and the 21-day label makes that stronger rather
/// than weaker here: bars are only ever fetched up to the clamped upper bound, so a decision date
/// near the end simply has no 21 following sessions and is dropped by the builder. No label window
/// can reach into the reserved window because no bar inside it was ever loaded.
/// </para>
/// </remarks>
public sealed class VrpConditioningStudyRunner(
    VolResidualBarLoader barLoader, ISessionClock sessionClock, ILogger<VrpConditioningStudyRunner> logger)
{
    /// <summary>Matches the parent study's floor and the backfill catalog's SPX intraday floor.</summary>
    private static readonly DateOnly EarliestAvailable = new(2010, 1, 1);

    public string? ConnectionStringForDiagnostics => barLoader.ConnectionString;

    public async Task<VrpConditioningRunResponse> RunAsync(
        DateOnly? requestedFrom, DateOnly? requestedTo, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var generatedAt = DateTimeOffset.UtcNow;

        var clamp = ReservedHoldout.ClampToExcludeHoldout(requestedFrom, requestedTo, EarliestAvailable);

        if (clamp.IsEmpty)
        {
            return InsufficientData(
                runId, generatedAt, clamp.From, clamp.To, 0, 0,
                $"The requested range falls entirely inside (or after) the reserved holdout " +
                $"({ReservedHoldout.Start:yyyy-MM-dd} to {ReservedHoldout.End:yyyy-MM-dd}), which this " +
                "development run must never load, score, or display. No usable window remains after clamping.");
        }

        logger.LogInformation(
            "vrp-conditioning dev run {RunId}: requested [{ReqFrom}, {ReqTo}], clamped to [{From}, {To}] (clamped={Clamped})",
            runId, requestedFrom, requestedTo, clamp.From, clamp.To, clamp.WasClamped);

        var spxBars = await barLoader.LoadSpxOneMinuteBarsAsync(clamp.From, clamp.To, cancellationToken);
        var vixDailyCloses = await barLoader.LoadVixDailyClosesAsync(clamp.From, clamp.To, cancellationToken);

        var intradayBars = HistoricalBarAdapter.ToIntradayBars(spxBars).ToList();
        var spxDays = VolatilityPresets.BuildSpxStudyTarget(sessionClock, intradayBars);
        var completeSpxDays = spxDays.Count(d => d.IsComplete);

        var rows = VrpConditioningFeatureBuilder.BuildRawRows(spxDays, vixDailyCloses);

        if (rows.Count == 0)
        {
            return InsufficientData(
                runId, generatedAt, clamp.From, clamp.To, completeSpxDays, 0,
                $"No day in [{clamp.From:yyyy-MM-dd}, {clamp.To:yyyy-MM-dd}] has both a full feature " +
                $"history and a full {VrpConditioningHorizon.LabelTradingDays}-session forward label: " +
                $"{completeSpxDays} complete SPX session(s) and {vixDailyCloses.Count} VIX daily close(s) " +
                $"are available, and a single row needs {VrpConditioningHorizon.MonthlyWindow} sessions " +
                $"behind it plus {VrpConditioningHorizon.LabelTradingDays} ahead of it. The backfill has " +
                "not reached far enough yet.");
        }

        var splits = VolResidualSplitter.Split(
            rows, r => r.Date, WalkForwardFold.Registered(), VrpConditioningHorizon.PurgeRows);

        var scoreable = splits.Where(VrpConditioningFoldRunner.CanScore).ToList();

        if (scoreable.Count == 0)
        {
            var detail = string.Join("; ", splits.Select(s => $"{s.Fold.Name}: {s.Train.Count} train / {s.Test.Count} test rows"));

            return InsufficientData(
                runId, generatedAt, clamp.From, clamp.To, completeSpxDays, rows.Count,
                $"{rows.Count} decision date(s) were built, but no registered walk-forward fold has both " +
                $">= {VrpConditioningFoldRunner.MinimumTrainRows} training rows (after the " +
                $"{VrpConditioningHorizon.PurgeRows}-row purge) and >= 1 test row yet ({detail}).",
                rows);
        }

        var foldResults = scoreable.Select(VrpConditioningFoldRunner.Run).ToList();
        var orderedDays = foldResults.SelectMany(f => f.DailyResults).OrderBy(d => d.Date).ToList();

        var arms = BuildArmSummaries(foldResults);
        var conditioning = BuildConditioning(foldResults, orderedDays);
        var dm = VrpConditioningAdjudication.Compare(foldResults);
        var thinned = VrpConditioningAdjudication.NonOverlappingSubsample(foldResults);

        return new VrpConditioningRunResponse(
            runId,
            IsDevelopmentRun: true,
            generatedAt,
            VrpConditioningRunStatus.Ok,
            InsufficientReason: null,
            new VrpConditioningDataWindow(
                clamp.From, clamp.To, completeSpxDays, rows.Count,
                rows[0].LabelFrom, rows[^1].LabelTo),
            VolResidualHoldoutInfo.Registered,
            VrpConditioningDesign.Registered,
            VrpConditioningArms.Gate,
            arms,
            conditioning,
            dm,
            new VrpConditioningEffectiveSample(
                orderedDays.Count,
                thinned.Count,
                VrpConditioningHorizon.LabelTradingDays,
                $"{orderedDays.Count} scored decision dates carry {thinned.Count} non-overlapping " +
                $"{VrpConditioningHorizon.LabelTradingDays}-session windows. The effective sample is " +
                "smaller still, because volatility is persistent across adjacent windows too."),
            [.. foldResults.OrderBy(f => f.TestFrom).Select(f => f.CorrectionFit)],
            CorrectionNote(foldResults),
            BuildDailyRows(orderedDays),
            VrpConditioningLimitations.Registered);
    }

    /// <summary>
    /// Names the case where the corrected arm is arithmetically identical to the gate, so nobody has
    /// to work out from two identical table rows whether the correction agreed or simply did not
    /// exist. See <c>VrpConditioningFoldRunner.DescribeCorrection</c> for the algebra.
    /// </summary>
    private static string? CorrectionNote(IReadOnlyList<VrpConditioningFoldResult> foldResults)
    {
        if (!foldResults.All(f => f.CorrectionFit.IsNullModel)) return null;

        return
            $"THE RESIDUAL CORRECTION IS INOPERATIVE ON THIS DATA. All {foldResults.Count} fold(s) " +
            "selected the null model: the inner blocked 5-fold CV found no lambda at which any of the " +
            "registered features improved held-out error on the HAR-X residual, so only an intercept " +
            $"survived. An intercept-only correction is a constant multiplicative shift, which the " +
            $"'{VrpConditioningArms.Corrected}' arm's own QLIKE retransformation factor absorbs exactly, " +
            $"making its forecasts IDENTICAL to '{VrpConditioningArms.HarX}' day for day, to floating-point round-off. The two arms " +
            "therefore show identical pooled QLIKE, identical quintile buckets and a degenerate " +
            "Diebold-Mariano row (p = 1, nothing tested). Read that as 'at a 21-trading-day horizon the " +
            "registered residual correction adds nothing', NOT as 'the two models agree'.";
    }

    private static List<VrpConditioningArmConditioning> BuildConditioning(
        IReadOnlyList<VrpConditioningFoldResult> foldResults,
        IReadOnlyList<VrpConditioningDailyResult> orderedDays)
    {
        // Breakpoints are per fold; the pooled table reports the LATEST fold's edges so the labels
        // name a real, frozen cut rather than an average of three different ones. The bucket a day
        // sits in was decided by its OWN fold's training window, in the fold runner.
        var latest = foldResults.OrderBy(f => f.TestFrom).Last();

        return [.. VrpConditioningArms.All.Select(arm =>
            VrpConditioningQuintiles.Aggregate(orderedDays, arm, latest.TrainSpreadBreakpoints[arm]))];
    }

    private static List<VrpConditioningArmSummary> BuildArmSummaries(
        IReadOnlyList<VrpConditioningFoldResult> foldResults)
    {
        List<(string Key, string Label, string Role)> definitions =
        [
            (VrpConditioningArms.Unconditional, "Unconditional (train-window mean of log cumulative RV)", "reference"),
            (VrpConditioningArms.CalibratedVix, "B1: calibrated VIX (QLIKE-fitted on train)", "baseline"),
            (VrpConditioningArms.HarX, "HAR-X (HAR terms + registered Tier-1 VIX features)", "gate"),
            (VrpConditioningArms.Corrected, "Corrected (elastic net on the HAR-X residual)", "candidate"),
        ];

        var gatePooled = PooledQlike(foldResults, VrpConditioningArms.Gate);

        return [.. definitions.Select(d =>
        {
            var folds = foldResults.OrderBy(f => f.TestFrom).Select(f => new VrpConditioningArmFold(
                f.FoldName, f.TrainFrom, f.TrainTo, f.TestFrom, f.TestTo,
                f.DailyResults.Average(x => x.Qlike[d.Key]), f.DailyResults.Count)).ToList();

            var pooled = PooledQlike(foldResults, d.Key);

            return new VrpConditioningArmSummary(
                d.Key, d.Label, d.Role, pooled,
                QlikeRetransformation.ImprovementPercent(pooled, gatePooled), folds);
        })];
    }

    private static double PooledQlike(IReadOnlyList<VrpConditioningFoldResult> foldResults, string arm) =>
        foldResults.SelectMany(f => f.DailyResults).Average(d => d.Qlike[arm]);

    private static List<VrpConditioningDailyRow> BuildDailyRows(
        IReadOnlyList<VrpConditioningDailyResult> orderedDays) =>
        [.. orderedDays.Select(d => new VrpConditioningDailyRow(
            d.Date, d.LabelFrom, d.LabelTo, d.FoldName, d.VixLevel, d.ImpliedVariance,
            d.RealizedVariance, d.RealizedAnnualizedVolPct, d.PremiumCollected, d.PnlPerVegaNotional,
            d.Forecasts, d.Qlike, d.Spread, d.Bucket))];

    private static VrpConditioningRunResponse InsufficientData(
        Guid runId, DateTimeOffset generatedAt, DateOnly from, DateOnly to,
        int sessionsAvailable, int decisionDates, string reason,
        IReadOnlyList<VrpConditioningRawRow>? rows = null) =>
        new(
            runId,
            IsDevelopmentRun: true,
            generatedAt,
            VrpConditioningRunStatus.InsufficientData,
            reason,
            new VrpConditioningDataWindow(
                from, to, sessionsAvailable, decisionDates,
                rows is { Count: > 0 } ? rows[0].LabelFrom : null,
                rows is { Count: > 0 } ? rows[^1].LabelTo : null),
            VolResidualHoldoutInfo.Registered,
            VrpConditioningDesign.Registered,
            VrpConditioningArms.Gate,
            Arms: [],
            Conditioning: [],
            DieboldMariano: [],
            new VrpConditioningEffectiveSample(0, 0, VrpConditioningHorizon.LabelTradingDays, "Nothing was scored."),
            CorrectionFits: [],
            CorrectionIsInoperativeNote: null,
            Daily: [],
            VrpConditioningLimitations.Registered);
}
