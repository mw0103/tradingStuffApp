namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>One test day's realized outcome and every fitted model's forecast and QLIKE loss.</summary>
/// <param name="PriorVix">Prior-close VIX in index points, recovered from the row's registered <c>LogPriorVix2</c> feature.</param>
/// <param name="VixRegime">
/// <c>"low"</c> or <c>"high"</c> against this fold's TRAINING-window median prior VIX. H1 requires
/// the improvement to be positive in both halves, and the registration requires regime thresholds to
/// be train-defined — so the threshold is fixed here, where the training block is in scope, rather
/// than by a median taken over the evaluation sample later.
/// </param>
public sealed record VolResidualDailyResult(
    DateOnly Date,
    string FoldName,
    double ActualVariance,
    IReadOnlyDictionary<string, double> Forecasts,
    IReadOnlyDictionary<string, double> Qlike,
    double PriorVix,
    string VixRegime);

public sealed record VolResidualFoldResult(
    string FoldName,
    DateOnly TrainFrom,
    DateOnly TrainTo,
    DateOnly TestFrom,
    DateOnly TestTo,
    int TrainDays,
    IReadOnlyList<VolResidualDailyResult> DailyResults,
    // Test days on which the exploratory GBT forecast was raised by its positivity floor. Zero on a
    // registered run, which does not fit a GBT at all.
    int GbtFloorHits = 0);

/// <summary>
/// Fits every method in <see cref="VolResidualMethodCatalog"/> on one fold's training block and
/// scores them on that fold's test block. Nothing here is called until <see cref="CanScore"/> has
/// already accepted the fold: every fit assumes there is enough data to support it.
/// </summary>
/// <remarks>
/// <para>
/// The runner owns the parts that must be identical across methods — the fold context, the
/// catalog order, the QLIKE scoring, the train-defined VIX regime split — and nothing about any
/// individual method. What a method fits, in which space, and whether it retransforms is the
/// method's own business; see <see cref="VolResidualMethod"/>.
/// </para>
/// <para>
/// Model estimation stays in log-variance space where the literature defines it (HAR-RV, HAR-X);
/// what each model reports as its FORECAST is a separate, later step — the training-window
/// QLIKE-minimizing retransformation (<see cref="QlikeRetransformation"/>) — never the raw
/// exponentiated log fit. B1 is the one exception: its own fit already minimizes training QLIKE
/// directly, so no further retransformation is layered on top of it.
/// </para>
/// </remarks>
public static class VolResidualFoldRunner
{
    /// <summary>
    /// The smallest training block this runner will fit a fold from. Not a registered constant —
    /// it exists purely so a fold with too little history fails visibly (the fold is skipped, not
    /// silently fit on a handful of rows) rather than throwing out of
    /// <see cref="NonNegativeLeastSquares"/> or <see cref="ElasticNet"/> partway through a run.
    /// Twelve is comfortably above HAR-X's six parameters (needs &gt; columns rows to be
    /// non-degenerate) and above the elastic net's 5-fold blocked CV floor (needs &gt;= 10 rows).
    /// </summary>
    public const int MinimumTrainRows = 30;

    public static bool CanScore(VolResidualFoldSplit split) =>
        split.Train.Count >= MinimumTrainRows && split.Test.Count > 0;

    public static VolResidualFoldResult Run(VolResidualFoldSplit split) => Run(split, includeExploratoryGbt: false);

    /// <param name="includeExploratoryGbt">
    /// Fits and scores the exploratory catalog in addition to the registered one. This is OUTSIDE
    /// the registered ladder — rung 4 runs only if rung 3 passes the H1 gate, and it has not — so
    /// it is opt-in per run and every consumer of the result is told so; see
    /// <see cref="VolResidualExploratoryRung"/>.
    /// </param>
    public static VolResidualFoldResult Run(VolResidualFoldSplit split, bool includeExploratoryGbt)
    {
        if (!CanScore(split))
            throw new InvalidOperationException(
                $"Fold {split.Fold.Name} has {split.Train.Count} training rows and {split.Test.Count} " +
                $"test rows; call {nameof(CanScore)} before {nameof(Run)}.");

        var context = VolResidualFoldContext.Build(split);

        // Registered methods first, always and unconditionally; the exploratory catalog after, so
        // nothing it fits can be a prerequisite of anything registered. The characterization suite
        // asserts the stronger form of that property: registered forecasts are bit-identical with
        // the exploratory catalog on or off.
        var methods = includeExploratoryGbt
            ? VolResidualMethodCatalog.Registered.Concat(VolResidualMethodCatalog.Exploratory).ToList()
            : VolResidualMethodCatalog.Registered.ToList();

        foreach (var method in methods)
        {
            context.Fitted[method.Key] = method.Fit(context);
        }

        // ---- VIX halves, threshold from the TRAINING window only ----
        // H1 requires the improvement to be positive in both VIX halves; the registration requires
        // regime thresholds to be train-defined. Splitting on the median of the test block would
        // define the regimes with the data used to judge them.
        var trainMedianLogVix2 = Median(split.Train.Select(r => r.LogPriorVix2));

        // ---- Score the test block ----
        var dailyResults = new List<VolResidualDailyResult>(split.Test.Count);
        var gbtFloorHits = 0;

        foreach (var row in split.Test)
        {
            var forecasts = new Dictionary<string, double>();
            foreach (var method in methods)
            {
                var fitted = context.Fitted[method.Key];
                forecasts[method.Key] = fitted.Forecast(row);
                if (fitted.FloorBinds is not null && fitted.FloorBinds(row)) gbtFloorHits++;
            }

            var qlike = forecasts.ToDictionary(
                kvp => kvp.Key,
                kvp => QlikeRetransformation.Loss(row.ActualVariance, kvp.Value));

            // LogPriorVix2 = log((VIX/100)^2), so VIX = 100 * exp(LogPriorVix2 / 2). Reported for
            // readability only; the regime split below is made on the monotone log scale directly,
            // so the two orderings are identical and no rounding can move a day between halves.
            var priorVix = 100.0 * Math.Exp(row.LogPriorVix2 / 2.0);
            var regime = row.LogPriorVix2 <= trainMedianLogVix2
                ? VolResidualVixRegimes.Low
                : VolResidualVixRegimes.High;

            dailyResults.Add(new VolResidualDailyResult(
                row.Date, split.Fold.Name, row.ActualVariance, forecasts, qlike, priorVix, regime));
        }

        return new VolResidualFoldResult(
            split.Fold.Name,
            split.Train.Count > 0 ? split.Train[0].Date : DateOnly.FromDateTime(split.Fold.TrainStart),
            split.Train.Count > 0 ? split.Train[^1].Date : DateOnly.FromDateTime(split.Fold.TrainStart),
            split.Test[0].Date,
            split.Test[^1].Date,
            split.Train.Count,
            dailyResults,
            gbtFloorHits);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : 0.5 * (sorted[middle - 1] + sorted[middle]);
    }
}
