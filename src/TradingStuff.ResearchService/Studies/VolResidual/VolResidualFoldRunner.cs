using TradingStuff.Volatility.Baselines;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>One test day's realized outcome and all four models' forecasts and QLIKE losses.</summary>
public sealed record VolResidualDailyResult(
    DateOnly Date,
    string FoldName,
    double ActualVariance,
    IReadOnlyDictionary<string, double> Forecasts,
    IReadOnlyDictionary<string, double> Qlike);

public sealed record VolResidualFoldResult(
    string FoldName,
    DateOnly TrainFrom,
    DateOnly TrainTo,
    DateOnly TestFrom,
    DateOnly TestTo,
    int TrainDays,
    IReadOnlyList<VolResidualDailyResult> DailyResults);

/// <summary>
/// Fits all four registered models — HAR-RV (reference), B1 calibrated VIX, HAR-X (the primary
/// gate), and the corrected/residual candidate — on one fold's training block, and scores them on
/// that fold's test block. Nothing here is called until <see cref="CanScore"/> has already accepted
/// the fold: every fit below assumes there is enough data to support it.
/// </summary>
/// <remarks>
/// Model estimation (OLS / non-negative OLS / elastic net) stays in log-variance space, matching the
/// literature definitions of HAR-RV and HAR-X. What each model reports as its FORECAST is a
/// separate, later step — the training-window QLIKE-minimizing retransformation
/// (<see cref="QlikeRetransformation"/>) — never the raw exponentiated log fit. B1 is the one
/// exception: its own fit already directly minimizes training QLIKE (<see cref="CalibratedVixFit"/>),
/// so no further retransformation is layered on top of it.
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

    public static VolResidualFoldResult Run(VolResidualFoldSplit split)
    {
        if (!CanScore(split))
            throw new InvalidOperationException(
                $"Fold {split.Fold.Name} has {split.Train.Count} training rows and {split.Test.Count} " +
                $"test rows; call {nameof(CanScore)} before {nameof(Run)}.");

        var train = split.Train;
        var test = split.Test;

        var trainActuals = train.Select(r => r.ActualVariance).ToList();
        var trainLogTargets = train.Select(r => Math.Log(r.ActualVariance)).ToList();

        // Tier-1 VIX divergence z-score: standardized on TRAIN moments only, applied to every row
        // (train and test alike) with those same frozen moments — never re-estimated on evaluation
        // data.
        var (vixChangeMean, vixChangeStd) = MeanAndPopulationStd(train.Select(r => r.Vix5DayChange));
        var (spxReturnMean, spxReturnStd) = MeanAndPopulationStd(train.Select(r => r.Spx1DayLogReturn));

        double Divergence(VolResidualRawRow r) =>
            ZScore(r.Vix5DayChange, vixChangeMean, vixChangeStd) * ZScore(r.Spx1DayLogReturn, spxReturnMean, spxReturnStd);

        double[] HarFeatures(VolResidualRawRow r) => [r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22];

        double[] HarxFeatures(VolResidualRawRow r) =>
            [r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22, r.LogPriorVix2, r.Vix5DayChange, Divergence(r)];

        // ---- HAR-RV (reference) ----
        var harCoefficients = OrdinaryLeastSquares.Fit(train.Select(HarFeatures).ToList(), trainLogTargets);
        double HarLogForecast(VolResidualRawRow r) => OrdinaryLeastSquares.Predict(harCoefficients, HarFeatures(r));
        var harFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(HarLogForecast(r))).ToList());
        double HarForecast(VolResidualRawRow r) => harFactor * Math.Exp(HarLogForecast(r));

        // ---- HAR-X (the primary gate) ----
        var harxCoefficients = NonNegativeLeastSquares.Fit(train.Select(HarxFeatures).ToList(), trainLogTargets);
        double HarxLogForecast(VolResidualRawRow r) => NonNegativeLeastSquares.Predict(harxCoefficients, HarxFeatures(r));
        var harxFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(HarxLogForecast(r))).ToList());
        double HarxForecast(VolResidualRawRow r) => harxFactor * Math.Exp(HarxLogForecast(r));

        // ---- B1: calibrated VIX ----
        var b1 = CalibratedVixFit.Fit(train.Select(r => r.LogPriorVix2).ToList(), trainActuals);
        double VixForecast(VolResidualRawRow r) => b1.PredictVariance(r.LogPriorVix2);

        // ---- CORRECTED: elastic net on HAR-X's own residual target ----
        // HAR-X's causal log-forecast and resulting residual, for every row this fold touches
        // (train AND test): the model producing them was fit on train only, so evaluating it on
        // more rows is ordinary prediction, not leakage.
        var all = train.Concat(test).OrderBy(r => r.Date).ToList();
        var harxLogForecastByDate = all.ToDictionary(r => r.Date, HarxLogForecast);
        var residualByDate = all.ToDictionary(r => r.Date, r => Math.Log(r.ActualVariance) - harxLogForecastByDate[r.Date]);

        // Mean of the last 5 signed HAR-X residuals, walked causally in date order: a day's feature
        // uses only STRICTLY PRIOR days' already-realized residuals, whether those prior days fall
        // in train or (for later test days) earlier in the same test block.
        var meanLast5ResidualByDate = new Dictionary<DateOnly, double>();
        var recentResiduals = new Queue<double>();
        foreach (var row in all)
        {
            meanLast5ResidualByDate[row.Date] = recentResiduals.Count == 0 ? 0.0 : recentResiduals.Average();
            recentResiduals.Enqueue(residualByDate[row.Date]);
            if (recentResiduals.Count > 5) recentResiduals.Dequeue();
        }

        double[] CandidateFeatures(VolResidualRawRow r) =>
        [
            r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22,
            r.DayOfWeekDummies[0], r.DayOfWeekDummies[1], r.DayOfWeekDummies[2], r.DayOfWeekDummies[3],
            r.DaysToMonthlyOpex,
            meanLast5ResidualByDate[r.Date],
            r.LogPriorVix2, r.Vix5DayChange, Divergence(r),
        ];

        var candidateModel = ElasticNet.FitWithCrossValidation(
            train.Select(CandidateFeatures).ToList(),
            train.Select(r => residualByDate[r.Date]).ToList());

        double CandidateRawLogForecast(VolResidualRawRow r) =>
            harxLogForecastByDate[r.Date] + candidateModel.Predict(CandidateFeatures(r));

        var candidateFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(CandidateRawLogForecast(r))).ToList());
        double CandidateForecast(VolResidualRawRow r) => candidateFactor * Math.Exp(CandidateRawLogForecast(r));

        // ---- Score the test block ----
        var dailyResults = new List<VolResidualDailyResult>(test.Count);

        foreach (var row in test)
        {
            var forecasts = new Dictionary<string, double>
            {
                [VolResidualModelKeys.Har] = HarForecast(row),
                [VolResidualModelKeys.Vix] = VixForecast(row),
                [VolResidualModelKeys.HarX] = HarxForecast(row),
                [VolResidualModelKeys.Corrected] = CandidateForecast(row),
            };

            var qlike = forecasts.ToDictionary(
                kvp => kvp.Key,
                kvp => QlikeRetransformation.Loss(row.ActualVariance, kvp.Value));

            dailyResults.Add(new VolResidualDailyResult(row.Date, split.Fold.Name, row.ActualVariance, forecasts, qlike));
        }

        return new VolResidualFoldResult(
            split.Fold.Name,
            train.Count > 0 ? train[0].Date : DateOnly.FromDateTime(split.Fold.TrainStart),
            train.Count > 0 ? train[^1].Date : DateOnly.FromDateTime(split.Fold.TrainStart),
            test[0].Date,
            test[^1].Date,
            train.Count,
            dailyResults);
    }

    private static (double Mean, double PopulationStd) MeanAndPopulationStd(IEnumerable<double> values)
    {
        var list = values.ToList();
        var mean = list.Average();
        var variance = list.Sum(v => (v - mean) * (v - mean)) / list.Count;
        return (mean, Math.Sqrt(variance));
    }

    private static double ZScore(double value, double mean, double populationStd) =>
        populationStd <= 1e-14 ? 0.0 : (value - mean) / populationStd;
}
