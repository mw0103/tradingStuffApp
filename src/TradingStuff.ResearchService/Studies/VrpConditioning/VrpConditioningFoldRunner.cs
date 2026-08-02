using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Volatility.Baselines;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>The four forecast arms. Same data, same horizon, same loss, same retransformation rule.</summary>
public static class VrpConditioningArms
{
    /// <summary>Train-window mean of log cumulative RV: the do-nothing baseline.</summary>
    public const string Unconditional = "UNCONDITIONAL";

    /// <summary>B1, <c>exp(a + b*log q)</c>, fitted by minimising TRAINING-window QLIKE.</summary>
    public const string CalibratedVix = "CALIBRATED_VIX";

    /// <summary>HAR terms plus the registered Tier-1 VIX features, positivity-constrained.</summary>
    public const string HarX = "HARX";

    /// <summary>HAR-X plus the elastic-net residual correction.</summary>
    public const string Corrected = "CORRECTED";

    /// <summary>
    /// The parent study's best dev candidate carried to this horizon: the continuous/jump split
    /// with quarticity attenuation as the base, the elastic-net residual correction on top
    /// (CORRECTED-QCJ, dev sweep 2026-08-02). Exploratory at this horizon exactly as it is at the
    /// parent's - present so the decision layer can ask whether the better daily forecast is also
    /// the better 21-day decision input, which is the study's actual question.
    /// </summary>
    public const string QcjCorrected = "QCJ_CORRECTED";

    /// <summary>
    /// The comparator every DM row is measured against. HAR-X, for the same reason the parent study
    /// uses it: it is information-matched to the corrected arm on the contested dimension, so a
    /// difference against it is attributable to the mapping rather than to the inputs. Reporting the
    /// unconditional arm against this gate is also how "does anything beat doing nothing?" gets
    /// answered — read that row's sign backwards.
    /// </summary>
    public const string Gate = HarX;

    public static readonly IReadOnlyList<string> All = [Unconditional, CalibratedVix, HarX, Corrected, QcjCorrected];
}

/// <summary>
/// One decision date's outcome: what every arm forecast, what actually happened over the 21 sessions
/// that followed, and the conditioning quantities derived from the two.
/// </summary>
/// <param name="Spread">
/// <c>impliedVar - forecastVar</c>, one entry per arm. The conditioning variable.
/// </param>
/// <param name="Bucket">
/// Quintile 1..5 per arm, assigned from that fold's TRAINING-window spread breakpoints. Never from
/// the evaluation sample — see <see cref="VrpConditioningQuintiles"/>.
/// </param>
/// <param name="PremiumCollected">
/// <c>impliedVar - realizedVar</c>: the variance premium actually collected over the window. Arm-
/// independent — the arms decide WHICH bucket a day lands in, never what the day paid.
/// </param>
/// <param name="PnlPerVegaNotional">
/// The variance-swap-style short payoff per unit vega notional, in annualized volatility points.
/// See <see cref="VrpConditioningHorizon.ShortVarianceSwapPayoffPerVegaNotional"/> for the very long
/// list of things it does not include.
/// </param>
public sealed record VrpConditioningDailyResult(
    DateOnly Date,
    DateOnly LabelFrom,
    DateOnly LabelTo,
    string FoldName,
    double RealizedVariance,
    double ImpliedVariance,
    double VixLevel,
    double RealizedAnnualizedVolPct,
    IReadOnlyDictionary<string, double> Forecasts,
    IReadOnlyDictionary<string, double> Qlike,
    IReadOnlyDictionary<string, double> Spread,
    IReadOnlyDictionary<string, int> Bucket,
    double PremiumCollected,
    double PnlPerVegaNotional);

public sealed record VrpConditioningFoldResult(
    string FoldName,
    DateOnly TrainFrom,
    DateOnly TrainTo,
    DateOnly TestFrom,
    DateOnly TestTo,
    int TrainRows,
    IReadOnlyDictionary<string, double[]> TrainSpreadBreakpoints,
    IReadOnlyList<VrpConditioningDailyResult> DailyResults,
    VrpConditioningCorrectionFit CorrectionFit);

/// <summary>
/// Fits all four arms on one fold's training block and scores them on its test block.
/// </summary>
/// <remarks>
/// <para>
/// Estimation stays in log-variance space; what an arm REPORTS as its forecast is the separate,
/// later, training-window-only, MODEL-SPECIFIC <see cref="QlikeRetransformation"/> step — never a
/// raw exponentiated log fit, and never a factor shared between arms. B1 is the one arm with no
/// trailing factor, because its own two parameters are already fitted directly against training
/// QLIKE on the variance scale.
/// </para>
/// <para>
/// <b>The one place this differs materially from the parent fold runner, and it is the whole
/// difficulty of the 21-day label.</b> The registered Tier-0 feature "mean of the last 5 signed
/// baseline residuals" is causal for a 1-day label if you walk the series in date order and enqueue
/// each day's residual after reading the queue. For a 21-day label that is a LEAK: the residual for
/// decision date <c>s</c> is not observable until <c>s</c>'s label window closes, 21 sessions later.
/// Here the queue advances on <see cref="VrpConditioningRawRow.LabelTo"/> — a residual becomes
/// available to a decision date only once its own label has finished — so at date <c>t</c> the five
/// residuals in hand are those of decision dates around <c>t-21</c>, which is what a live decision
/// would actually have.
/// </para>
/// </remarks>
public static class VrpConditioningFoldRunner
{
    /// <summary>
    /// The smallest training block this runner will fit from. Above HAR-X's six parameters and above
    /// <see cref="ElasticNet"/>'s blocked-5-fold floor, so a thin fold is SKIPPED visibly rather
    /// than throwing part-way through a run.
    /// </summary>
    public const int MinimumTrainRows = 60;

    public static bool CanScore((WalkForwardFold Fold, List<VrpConditioningRawRow> Train, List<VrpConditioningRawRow> Test) split) =>
        split.Train.Count >= MinimumTrainRows && split.Test.Count > 0;

    public static VrpConditioningFoldResult Run(
        (WalkForwardFold Fold, List<VrpConditioningRawRow> Train, List<VrpConditioningRawRow> Test) split)
    {
        if (!CanScore(split))
            throw new InvalidOperationException(
                $"Fold {split.Fold.Name} has {split.Train.Count} training rows and {split.Test.Count} " +
                $"test rows; call {nameof(CanScore)} before {nameof(Run)}.");

        var train = split.Train;
        var test = split.Test;

        var trainActuals = train.Select(r => r.LabelCumulativeVariance).ToList();
        var trainLogTargets = train.Select(r => Math.Log(r.LabelCumulativeVariance)).ToList();

        // Tier-1 divergence z-score: TRAIN moments only, applied unchanged to test rows.
        var (vixChangeMean, vixChangeStd) = MeanAndPopulationStd(train.Select(r => r.Vix5DayChange));
        var (spxReturnMean, spxReturnStd) = MeanAndPopulationStd(train.Select(r => r.Spx1DayLogReturn));

        double Divergence(VrpConditioningRawRow r) =>
            ZScore(r.Vix5DayChange, vixChangeMean, vixChangeStd) * ZScore(r.Spx1DayLogReturn, spxReturnMean, spxReturnStd);

        double[] HarxFeatures(VrpConditioningRawRow r) =>
            [r.LogRv, r.MeanLogRv5, r.MeanLogRv22, r.LogImpliedVariance, r.Vix5DayChange, Divergence(r)];

        // ---- ARM 1: unconditional ----
        // The registered do-nothing baseline: the train-window mean of log cumulative RV. It goes
        // through the same retransformation rule as everything else rather than being exempted, and
        // the rule collapses on it to something worth naming: the factor is
        // mean(y_i / exp(meanLog)), so the reported forecast is exactly mean(y) — the QLIKE-optimal
        // CONSTANT forecast. That is a property of the rule, not a shortcut around it.
        var unconditionalRawLog = trainLogTargets.Average();
        var unconditionalRaw = Math.Exp(unconditionalRawLog);
        var unconditionalFactor = QlikeRetransformation.FitFactor(
            trainActuals, [.. Enumerable.Repeat(unconditionalRaw, train.Count)]);
        var unconditionalForecast = unconditionalFactor * unconditionalRaw;

        // ---- ARM 2: B1, calibrated VIX ----
        var b1 = CalibratedVixFit.Fit(train.Select(r => r.LogImpliedVariance).ToList(), trainActuals);
        double CalibratedVixForecast(VrpConditioningRawRow r) => b1.PredictVariance(r.LogImpliedVariance);

        // ---- ARM 3: HAR-X ----
        var harxCoefficients = NonNegativeLeastSquares.Fit(train.Select(HarxFeatures).ToList(), trainLogTargets);
        double HarxLogForecast(VrpConditioningRawRow r) => NonNegativeLeastSquares.Predict(harxCoefficients, HarxFeatures(r));
        var harxFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(HarxLogForecast(r))).ToList());
        double HarxForecast(VrpConditioningRawRow r) => harxFactor * Math.Exp(HarxLogForecast(r));

        // ---- ARM 4: corrected = HAR-X + elastic net on its residual ----
        var all = train.Concat(test).OrderBy(r => r.Date).ToList();
        var harxLogForecastByDate = all.ToDictionary(r => r.Date, HarxLogForecast);
        var residualByDate = all.ToDictionary(
            r => r.Date, r => Math.Log(r.LabelCumulativeVariance) - harxLogForecastByDate[r.Date]);

        var meanLast5ResidualByDate = LaggedResidualMeans(all, residualByDate);

        double[] CandidateFeatures(VrpConditioningRawRow r) =>
        [
            r.LogRv, r.MeanLogRv5, r.MeanLogRv22,
            r.DayOfWeekDummies[0], r.DayOfWeekDummies[1], r.DayOfWeekDummies[2], r.DayOfWeekDummies[3],
            r.DaysToMonthlyOpex,
            meanLast5ResidualByDate[r.Date],
            r.LogImpliedVariance, r.Vix5DayChange, Divergence(r),
            r.SpxDrawdown22,
        ];

        var candidateModel = ElasticNet.FitWithCrossValidation(
            train.Select(CandidateFeatures).ToList(),
            train.Select(r => residualByDate[r.Date]).ToList());

        double CorrectedRawLogForecast(VrpConditioningRawRow r) =>
            harxLogForecastByDate[r.Date] + candidateModel.Predict(CandidateFeatures(r));

        var correctedFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(CorrectedRawLogForecast(r))).ToList());
        double CorrectedForecast(VrpConditioningRawRow r) => correctedFactor * Math.Exp(CorrectedRawLogForecast(r));

        // ---- ARM 5: QCJ-corrected - the parent study's winning composition at this horizon ----
        // Base: HAR-X with the daily term split into log bipower + jump share and attenuated by
        // train-centred sqrt(RQ) (the parent's A5). Correction: the same elastic net, on THIS
        // base's residual, with the same label-aware lagged-residual causality rule.
        var trainMeanSqrtRq = train.Average(r => Math.Sqrt(Math.Max(r.RealizedQuarticity, 0.0)));

        double[] QcjFeatures(VrpConditioningRawRow r)
        {
            var rv = Math.Exp(r.LogRv);
            var bv = r.BipowerVariation > 0.0 ? r.BipowerVariation : rv;
            var jump = Math.Max(r.JumpVariation, 0.0);
            var total = bv + jump;
            var jumpShare = r.BipowerVariation > 0.0 && total > 0.0 ? Math.Clamp(jump / total, 0.0, 1.0) : 0.0;
            var logBipower = Math.Log(Math.Max(bv, 1e-6 * rv));
            var attenuation = (Math.Sqrt(Math.Max(r.RealizedQuarticity, 0.0)) - trainMeanSqrtRq) * logBipower;
            return
            [
                logBipower, jumpShare, r.MeanLogRv5, r.MeanLogRv22,
                r.LogImpliedVariance, r.Vix5DayChange, Divergence(r), attenuation,
            ];
        }

        var qcjBaseCoefficients = OrdinaryLeastSquares.Fit(train.Select(QcjFeatures).ToList(), trainLogTargets);
        double QcjBaseLogForecast(VrpConditioningRawRow r) => OrdinaryLeastSquares.Predict(qcjBaseCoefficients, QcjFeatures(r));

        var qcjBaseLogByDate = all.ToDictionary(r => r.Date, QcjBaseLogForecast);
        var qcjResidualByDate = all.ToDictionary(
            r => r.Date, r => Math.Log(r.LabelCumulativeVariance) - qcjBaseLogByDate[r.Date]);
        var qcjMeanLast5ResidualByDate = LaggedResidualMeans(all, qcjResidualByDate);

        double[] QcjCandidateFeatures(VrpConditioningRawRow r) =>
        [
            r.LogRv, r.MeanLogRv5, r.MeanLogRv22,
            r.DayOfWeekDummies[0], r.DayOfWeekDummies[1], r.DayOfWeekDummies[2], r.DayOfWeekDummies[3],
            r.DaysToMonthlyOpex,
            qcjMeanLast5ResidualByDate[r.Date],
            r.LogImpliedVariance, r.Vix5DayChange, Divergence(r),
            r.SpxDrawdown22,
        ];

        var qcjModel = ElasticNet.FitWithCrossValidation(
            train.Select(QcjCandidateFeatures).ToList(),
            train.Select(r => qcjResidualByDate[r.Date]).ToList());

        double QcjRawLogForecast(VrpConditioningRawRow r) =>
            qcjBaseLogByDate[r.Date] + qcjModel.Predict(QcjCandidateFeatures(r));

        var qcjFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(QcjRawLogForecast(r))).ToList());
        double QcjForecast(VrpConditioningRawRow r) => qcjFactor * Math.Exp(QcjRawLogForecast(r));

        Dictionary<string, double> ForecastsFor(VrpConditioningRawRow r) => new()
        {
            [VrpConditioningArms.Unconditional] = unconditionalForecast,
            [VrpConditioningArms.CalibratedVix] = CalibratedVixForecast(r),
            [VrpConditioningArms.HarX] = HarxForecast(r),
            [VrpConditioningArms.Corrected] = CorrectedForecast(r),
            [VrpConditioningArms.QcjCorrected] = QcjForecast(r),
        };

        // ---- Quintile breakpoints, from the TRAINING window's spreads ----
        // A bucket edge derived from the evaluation sample is a quiet leak: it lets the boundary
        // move to wherever the test data happens to sit. The breakpoints are frozen here, where only
        // the training block is in scope, and applied unchanged to the test rows below.
        var trainSpreads = new Dictionary<string, List<double>>();
        foreach (var arm in VrpConditioningArms.All) trainSpreads[arm] = new List<double>(train.Count);

        foreach (var row in train)
        {
            var forecasts = ForecastsFor(row);
            foreach (var arm in VrpConditioningArms.All)
                trainSpreads[arm].Add(row.ImpliedVariance - forecasts[arm]);
        }

        var breakpoints = trainSpreads.ToDictionary(
            kvp => kvp.Key, kvp => VrpConditioningQuintiles.Breakpoints(kvp.Value));

        // ---- Score the test block ----
        var dailyResults = new List<VrpConditioningDailyResult>(test.Count);

        foreach (var row in test)
        {
            var forecasts = ForecastsFor(row);

            var qlike = forecasts.ToDictionary(
                kvp => kvp.Key, kvp => QlikeRetransformation.Loss(row.LabelCumulativeVariance, kvp.Value));

            var spread = forecasts.ToDictionary(kvp => kvp.Key, kvp => row.ImpliedVariance - kvp.Value);
            var bucket = spread.ToDictionary(
                kvp => kvp.Key, kvp => VrpConditioningQuintiles.BucketOf(kvp.Value, breakpoints[kvp.Key]));

            var strikeVol = row.VixLevel / 100.0;
            var realizedVol = VrpConditioningHorizon.AnnualizedVolatilityFromLabel(row.LabelCumulativeVariance);

            dailyResults.Add(new VrpConditioningDailyResult(
                row.Date,
                row.LabelFrom,
                row.LabelTo,
                split.Fold.Name,
                row.LabelCumulativeVariance,
                row.ImpliedVariance,
                row.VixLevel,
                100.0 * realizedVol,
                forecasts,
                qlike,
                spread,
                bucket,
                row.ImpliedVariance - row.LabelCumulativeVariance,
                VrpConditioningHorizon.ShortVarianceSwapPayoffPerVegaNotional(strikeVol, realizedVol)));
        }

        return new VrpConditioningFoldResult(
            split.Fold.Name,
            train[0].Date,
            train[^1].Date,
            test[0].Date,
            test[^1].Date,
            train.Count,
            breakpoints,
            dailyResults,
            DescribeCorrection(split.Fold.Name, candidateModel));
    }

    /// <summary>
    /// Records what the residual model actually selected, and says so out loud when it selected
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <b>This exists because of docs/LESSONS.md #3 — absence renders as health.</b> When the inner
    /// blocked CV picks the intercept-only model, the corrected arm's forecast is not merely CLOSE to
    /// the gate's, it is EXACTLY equal to it, and the two rows in the arms table become
    /// indistinguishable. Without this record that reads as "the correction agreed with the gate",
    /// which is a completely different finding from "the correction does not exist". The identity is
    /// exact and worth spelling out: with an intercept-only model the raw log forecast is
    /// <c>harxLog + c</c>, so the raw level forecast is <c>e^c * x</c>; the model-specific QLIKE
    /// retransformation factor is then <c>mean(y/(e^c * x)) = harxFactor / e^c</c>, and the product
    /// is <c>harxFactor * x</c> — the gate's own forecast, with <c>c</c> cancelled exactly. This is
    /// the retransformation discipline working as designed, not a defect.
    /// </remarks>
    private static VrpConditioningCorrectionFit DescribeCorrection(string foldName, ElasticNetModel model)
    {
        var nonZero = model.Coefficients.Count(c => Math.Abs(c) > 1e-14);

        var note = nonZero == 0
            ? "NULL MODEL. The inner blocked 5-fold CV found no lambda at which any registered feature " +
              "improved held-out error on the HAR-X residual, so every slope was shrunk to zero and only " +
              "an intercept remains. An intercept-only correction is a constant multiplicative shift in " +
              "level space, which this model's own QLIKE retransformation factor absorbs exactly — so " +
              "the corrected arm's forecasts are IDENTICAL to the gate's, day for day, to floating-point " +
              "round-off — not merely close to them. Read the two identical rows as 'the correction adds nothing at this horizon', " +
              "never as 'the two models agree'."
            : $"{nonZero} of {model.Coefficients.Length} registered features retained a non-zero " +
              "coefficient after the inner blocked 5-fold CV.";

        return new VrpConditioningCorrectionFit(
            foldName, model.Alpha, model.Lambda, model.Intercept, nonZero, model.Coefficients.Length,
            nonZero == 0, note);
    }

    /// <summary>
    /// Mean of the last five signed HAR-X residuals that a decision at each row's date could
    /// actually have observed.
    /// </summary>
    /// <remarks>
    /// A residual for decision date <c>s</c> is knowable only once <c>s</c>'s label window has
    /// closed, i.e. from <see cref="VrpConditioningRawRow.LabelTo"/> onward. So the residual of
    /// <c>s</c> is admissible at <c>t</c> exactly when <c>LabelTo(s) &lt;= Date(t)</c>. Both
    /// <c>Date</c> and <c>LabelTo</c> increase together along the date-ordered series, so a single
    /// forward pointer is enough — no scan, and no possibility of the pointer running ahead of the
    /// date it is being read at. Rows with fewer than five admissible residuals get the mean of what
    /// exists, and zero when nothing does, matching the parent study's treatment of its own warm-up.
    /// </remarks>
    internal static Dictionary<DateOnly, double> LaggedResidualMeans(
        IReadOnlyList<VrpConditioningRawRow> dateOrdered, IReadOnlyDictionary<DateOnly, double> residualByDate)
    {
        var means = new Dictionary<DateOnly, double>(dateOrdered.Count);
        var admissible = new Queue<double>();
        var next = 0;

        foreach (var row in dateOrdered)
        {
            while (next < dateOrdered.Count && dateOrdered[next].LabelTo <= row.Date)
            {
                admissible.Enqueue(residualByDate[dateOrdered[next].Date]);
                if (admissible.Count > VrpConditioningHorizon.WeeklyWindow) admissible.Dequeue();
                next++;
            }

            means[row.Date] = admissible.Count == 0 ? 0.0 : admissible.Average();
        }

        return means;
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
