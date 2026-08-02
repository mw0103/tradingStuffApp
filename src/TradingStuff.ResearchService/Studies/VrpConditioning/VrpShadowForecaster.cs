using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>One decision date's shadow quantities, computed from data available at its close.</summary>
public sealed record VrpShadowMark(
    DateOnly MarkDate,
    DateOnly TrainFrom,
    DateOnly TrainTo,
    int TrainRows,
    double VixClose,
    double ImpliedVariance,
    double QcjForecast,
    double HarxForecast,
    double QcjSpread,
    double HarxSpread,
    double VixSpread,
    int QcjBucket,
    int HarxBucket,
    int VixBucket,
    double ShadowAllocQcj,
    double ShadowAllocHarx,
    double ShadowAllocVix);

/// <summary>
/// Computes the paper-run protocol's shadow quantities for ONE decision date: the QCJ and HAR-X
/// 21-day forecasts, spreads against implied, train-frozen buckets, and the hypothetical
/// scale-down allocations. Nothing here trades; the traded path is constant one vega by protocol.
/// </summary>
/// <remarks>
/// <para>
/// <b>The causality rule is the whole point of this class existing separately from the fold
/// runner.</b> A live decision at date <c>t</c> may train only on rows whose 21-day label window
/// has CLOSED by <c>t</c> — the most recent ~21 sessions of feature rows have unfinished labels
/// and are not trainable, exactly the purge the study applies between folds. The decision row
/// itself is feature-only: its label does not exist yet, which is the definition of live.
/// </para>
/// <para>
/// The arm fits replicate the fold runner's exactly (HAR-X under NNLS with the retransformation;
/// QCJ base under OLS with the elastic-net residual correction, lagged residuals admissible only
/// after their own label closes). The one structural difference: the residual walk ends at the
/// decision date with the decision row never enqueued — its residual cannot exist.
/// </para>
/// </remarks>
public static class VrpShadowForecaster
{
    /// <summary>
    /// The frozen scale-down mapping, kept HERE as a shadow calculation only. Its confirmatory
    /// test failed (docs/research/confirmatory-scale-down-result.md); it is logged so its
    /// prospective behaviour accumulates in genuinely new observations, and it drives nothing.
    /// </summary>
    public static double ShadowAllocation(int bucket) => bucket switch { 1 => 0.25, 2 => 0.5, _ => 1.0 };

    /// <summary>
    /// Builds the shadow mark for the LAST complete session in <paramref name="spxDays"/>, or
    /// explains why it cannot. Row construction mirrors the study dataset; the label windows the
    /// study requires are exactly what this class must NOT require for the decision date.
    /// </summary>
    public static (VrpShadowMark? Mark, string? Refusal) Compute(
        IReadOnlyList<RealizedVolatilityDay> spxDays,
        IReadOnlyDictionary<DateOnly, double> vixDailyClose)
    {
        ArgumentNullException.ThrowIfNull(spxDays);
        ArgumentNullException.ThrowIfNull(vixDailyClose);

        // Labeled history: the study's own builder, which only emits rows with complete labels.
        var labeled = VrpConditioningFeatureBuilder.BuildRawRows(spxDays, vixDailyClose);

        if (labeled.Count < VrpConditioningFoldRunner.MinimumTrainRows)
        {
            return (null,
                $"Only {labeled.Count} labeled training rows are available; the fold runner's floor is " +
                $"{VrpConditioningFoldRunner.MinimumTrainRows}. Not enough history to fit a shadow forecast.");
        }

        // The decision row: features for the last complete session, no label required.
        var ordered = spxDays
            .Where(d => d.IsComplete && d.TotalVariance > 0.0 && d.SessionClose > 0.0)
            .OrderBy(d => d.Date)
            .ToList();

        var decision = BuildDecisionRow(ordered, vixDailyClose);

        if (decision.Row is null)
        {
            return (null, decision.Refusal);
        }

        var row = decision.Row;

        // Train = every labeled row whose label window closed by the decision date. The builder
        // guarantees LabelTo <= last labeled date <= decision date, but the guard is explicit so
        // a future change to the builder cannot silently leak an open window into training.
        var train = labeled.Where(r => r.LabelTo <= row.Date).ToList();

        // ---- Shared train-frozen statistics (the fold runner's, verbatim) ----
        var trainActuals = train.Select(r => r.LabelCumulativeVariance).ToList();
        var trainLogTargets = train.Select(r => Math.Log(r.LabelCumulativeVariance)).ToList();

        var (vixChangeMean, vixChangeStd) = MeanAndPopulationStd(train.Select(r => r.Vix5DayChange));
        var (spxReturnMean, spxReturnStd) = MeanAndPopulationStd(train.Select(r => r.Spx1DayLogReturn));

        double Divergence(VrpConditioningRawRow r) =>
            ZScore(r.Vix5DayChange, vixChangeMean, vixChangeStd) * ZScore(r.Spx1DayLogReturn, spxReturnMean, spxReturnStd);

        // ---- HAR-X arm ----
        double[] HarxFeatures(VrpConditioningRawRow r) =>
            [r.LogRv, r.MeanLogRv5, r.MeanLogRv22, r.LogImpliedVariance, r.Vix5DayChange, Divergence(r)];

        var harxCoefficients = NonNegativeLeastSquares.Fit(train.Select(HarxFeatures).ToList(), trainLogTargets);
        double HarxLog(VrpConditioningRawRow r) => NonNegativeLeastSquares.Predict(harxCoefficients, HarxFeatures(r));
        var harxFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(HarxLog(r))).ToList());
        double HarxForecast(VrpConditioningRawRow r) => harxFactor * Math.Exp(HarxLog(r));

        // ---- QCJ arm (base + elastic-net correction, causal residual walk) ----
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

        var qcjBase = OrdinaryLeastSquares.Fit(train.Select(QcjFeatures).ToList(), trainLogTargets);
        double QcjBaseLog(VrpConditioningRawRow r) => OrdinaryLeastSquares.Predict(qcjBase, QcjFeatures(r));

        // Residuals exist for TRAINED rows only; the decision row is present in the walk so it can
        // READ the admissible queue, and is never enqueued - its label has not happened.
        var walk = train.Append(row).OrderBy(r => r.Date).ToList();
        var residualByDate = train.ToDictionary(
            r => r.Date, r => Math.Log(r.LabelCumulativeVariance) - QcjBaseLog(r));
        var meanLast5 = LaggedResidualMeansForShadow(walk, residualByDate);

        double[] QcjCandidateFeatures(VrpConditioningRawRow r) =>
        [
            r.LogRv, r.MeanLogRv5, r.MeanLogRv22,
            r.DayOfWeekDummies[0], r.DayOfWeekDummies[1], r.DayOfWeekDummies[2], r.DayOfWeekDummies[3],
            r.DaysToMonthlyOpex,
            meanLast5[r.Date],
            r.LogImpliedVariance, r.Vix5DayChange, Divergence(r),
            r.SpxDrawdown22,
        ];

        var qcjModel = ElasticNet.FitWithCrossValidation(
            train.Select(QcjCandidateFeatures).ToList(),
            train.Select(r => residualByDate[r.Date]).ToList());

        double QcjRawLog(VrpConditioningRawRow r) => QcjBaseLog(r) + qcjModel.Predict(QcjCandidateFeatures(r));
        var qcjFactor = QlikeRetransformation.FitFactor(
            trainActuals, train.Select(r => Math.Exp(QcjRawLog(r))).ToList());
        double QcjForecast(VrpConditioningRawRow r) => qcjFactor * Math.Exp(QcjRawLog(r));

        // ---- Spreads and train-frozen buckets ----
        var unconditional = Math.Exp(trainLogTargets.Average()) *
            QlikeRetransformation.FitFactor(
                trainActuals,
                [.. Enumerable.Repeat(Math.Exp(trainLogTargets.Average()), train.Count)]);

        var qcjBreaks = VrpConditioningQuintiles.Breakpoints(
            train.Select(r => r.ImpliedVariance - QcjForecast(r)).ToList());
        var harxBreaks = VrpConditioningQuintiles.Breakpoints(
            train.Select(r => r.ImpliedVariance - HarxForecast(r)).ToList());
        var vixBreaks = VrpConditioningQuintiles.Breakpoints(
            train.Select(r => r.ImpliedVariance - unconditional).ToList());

        var qcjForecast = QcjForecast(row);
        var harxForecast = HarxForecast(row);

        var qcjSpread = row.ImpliedVariance - qcjForecast;
        var harxSpread = row.ImpliedVariance - harxForecast;
        var vixSpread = row.ImpliedVariance - unconditional;

        var qcjBucket = VrpConditioningQuintiles.BucketOf(qcjSpread, qcjBreaks);
        var harxBucket = VrpConditioningQuintiles.BucketOf(harxSpread, harxBreaks);
        var vixBucket = VrpConditioningQuintiles.BucketOf(vixSpread, vixBreaks);

        return (new VrpShadowMark(
            row.Date,
            train[0].Date,
            train[^1].Date,
            train.Count,
            row.VixLevel,
            row.ImpliedVariance,
            qcjForecast,
            harxForecast,
            qcjSpread,
            harxSpread,
            vixSpread,
            qcjBucket,
            harxBucket,
            vixBucket,
            ShadowAllocation(qcjBucket),
            ShadowAllocation(harxBucket),
            ShadowAllocation(vixBucket)), null);
    }

    /// <summary>
    /// The decision row: features for the last complete session, label fields sentinelled. The
    /// label window is OPEN by definition of "live", so <c>LabelTo</c> is set beyond any date the
    /// residual walk can reach — the admissibility rule then guarantees this row's (nonexistent)
    /// residual can never be read.
    /// </summary>
    internal static (VrpConditioningRawRow? Row, string? Refusal) BuildDecisionRow(
        IReadOnlyList<RealizedVolatilityDay> ordered,
        IReadOnlyDictionary<DateOnly, double> vixDailyClose)
    {
        const int weekly = VrpConditioningHorizon.WeeklyWindow;
        const int monthly = VrpConditioningHorizon.MonthlyWindow;

        if (ordered.Count < monthly)
        {
            return (null, $"Only {ordered.Count} complete SPX sessions; the feature warm-up needs {monthly}.");
        }

        var t = ordered.Count - 1;
        var day = ordered[t];
        var decisionDate = DateOnly.FromDateTime(day.Date);

        if (!vixDailyClose.TryGetValue(decisionDate, out var vix) || vix <= 0.0)
        {
            return (null, $"No VIX close for the decision date {decisionDate:yyyy-MM-dd}; the implied leg cannot be built.");
        }

        var dateMinus5 = DateOnly.FromDateTime(ordered[t - weekly].Date);
        if (!vixDailyClose.TryGetValue(dateMinus5, out var vixMinus5) || vixMinus5 <= 0.0)
        {
            return (null, $"No VIX close five sessions back ({dateMinus5:yyyy-MM-dd}); Vix5DayChange cannot be built.");
        }

        var window5 = ordered.Skip(t - weekly + 1).Take(weekly).ToList();
        var window22 = ordered.Skip(t - monthly + 1).Take(monthly).ToList();

        var impliedVariance = VrpConditioningHorizon.ImpliedVarianceOverLabelHorizon(vix);

        var peak = window22.Max(d => d.SessionClose);

        return (new VrpConditioningRawRow(
            decisionDate,
            LabelFrom: DateOnly.MaxValue,
            LabelTo: DateOnly.MaxValue,
            LabelSessions: 0,
            // Sentinel: any read of the decision row's label is a bug, and 1.0 makes log() zero
            // rather than a crash - the admissibility guard is what prevents the read.
            LabelCumulativeVariance: 1.0,
            VixLevel: vix,
            ImpliedVariance: impliedVariance,
            LogImpliedVariance: Math.Log(impliedVariance),
            LogRv: Math.Log(day.TotalVariance),
            MeanLogRv5: window5.Average(d => Math.Log(d.TotalVariance)),
            MeanLogRv22: window22.Average(d => Math.Log(d.TotalVariance)),
            DayOfWeekDummies: DayOfWeekDummies(decisionDate.DayOfWeek),
            DaysToMonthlyOpex: VolResidualFeatureBuilder.DaysToNextThirdFriday(decisionDate),
            Vix5DayChange: vix - vixMinus5,
            Spx1DayLogReturn: Math.Log(day.SessionClose / ordered[t - 1].SessionClose),
            SpxDrawdown22: peak > 0.0 ? Math.Log(day.SessionClose / peak) : 0.0,
            RealizedQuarticity: day.RealizedQuarticity,
            BipowerVariation: day.BipowerVariation,
            JumpVariation: day.JumpVariation), null);
    }

    /// <summary>
    /// The fold runner's admissibility rule over the shadow walk: a residual is readable at date
    /// <c>t</c> only when its own label closed by <c>t</c>, and rows without residuals (the
    /// decision row) are skipped by lookup rather than defaulted.
    /// </summary>
    private static Dictionary<DateOnly, double> LaggedResidualMeansForShadow(
        IReadOnlyList<VrpConditioningRawRow> dateOrdered,
        IReadOnlyDictionary<DateOnly, double> residualByDate)
    {
        var means = new Dictionary<DateOnly, double>(dateOrdered.Count);
        var admissible = new Queue<double>();
        var next = 0;

        foreach (var row in dateOrdered)
        {
            while (next < dateOrdered.Count && dateOrdered[next].LabelTo <= row.Date)
            {
                if (residualByDate.TryGetValue(dateOrdered[next].Date, out var residual))
                {
                    admissible.Enqueue(residual);
                    if (admissible.Count > VrpConditioningHorizon.WeeklyWindow) admissible.Dequeue();
                }

                next++;
            }

            means[row.Date] = admissible.Count == 0 ? 0.0 : admissible.Average();
        }

        return means;
    }

    private static double[] DayOfWeekDummies(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Tuesday => [1, 0, 0, 0],
        DayOfWeek.Wednesday => [0, 1, 0, 0],
        DayOfWeek.Thursday => [0, 0, 1, 0],
        DayOfWeek.Friday => [0, 0, 0, 1],
        _ => [0, 0, 0, 0],
    };

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
