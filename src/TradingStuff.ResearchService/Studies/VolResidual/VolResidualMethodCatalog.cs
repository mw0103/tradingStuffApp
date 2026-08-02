using TradingStuff.Volatility.Baselines;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// The methods the fold runner fits, in the order it fits them.
/// </summary>
/// <remarks>
/// Order is load-bearing in exactly one place — CORRECTED reads HAR-X's log forecast, so HAR-X
/// must precede it — and <see cref="VolResidualFoldContext.Require"/> turns a violation into a
/// named error rather than a wrong number. Everything else is order-independent, and kept in
/// the historical order so the results are comparable line-by-line with earlier runs.
/// </remarks>
public static class VolResidualMethodCatalog
{
    /// <summary>The registered ladder, as run on every fold.</summary>
    public static IReadOnlyList<VolResidualMethod> Registered { get; } =
    [
        new HarMethod(),
        new HarxMethod(),
        new CalibratedVixMethod(),
        new CorrectedMethod(),
    ];

    /// <summary>
    /// Exploratory methods, fitted only when a run opts in. Nothing here may be adjudicated:
    /// rung 4 runs only if rung 3 passes the H1 gate, and it has not.
    /// </summary>
    public static IReadOnlyList<VolResidualMethod> Exploratory { get; } =
    [
        new GbtMethod(),
    ];
}

/// <summary>HAR-RV in log space (Corsi), the reference the whole ladder is read against.</summary>
public sealed class HarMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.Har;
    public override bool Registered => true;

    internal static double[] Features(VolResidualRawRow r) => [r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22];

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var coefficients = OrdinaryLeastSquares.Fit(
            context.Train.Select(Features).ToList(), context.TrainLogTargets.ToList());

        double LogForecast(VolResidualRawRow r) => OrdinaryLeastSquares.Predict(coefficients, Features(r));

        var factor = QlikeRetransformation.FitFactor(
            context.TrainActuals.ToList(),
            context.Train.Select(r => Math.Exp(LogForecast(r))).ToList());

        return new VolResidualFittedMethod(
            Forecast: r => factor * Math.Exp(LogForecast(r)),
            LogForecast: LogForecast);
    }
}

/// <summary>
/// HAR-X — the HAR triplet plus the Tier-1 VIX block — under non-negative least squares. The
/// primary gate: H1 is measured against this model, never plain HAR.
/// </summary>
public sealed class HarxMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.HarX;
    public override bool Registered => true;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        double[] Features(VolResidualRawRow r) =>
            [r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22, r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r)];

        var coefficients = NonNegativeLeastSquares.Fit(
            context.Train.Select(Features).ToList(), context.TrainLogTargets.ToList());

        double LogForecast(VolResidualRawRow r) => NonNegativeLeastSquares.Predict(coefficients, Features(r));

        var factor = QlikeRetransformation.FitFactor(
            context.TrainActuals.ToList(),
            context.Train.Select(r => Math.Exp(LogForecast(r))).ToList());

        return new VolResidualFittedMethod(
            Forecast: r => factor * Math.Exp(LogForecast(r)),
            LogForecast: LogForecast);
    }
}

/// <summary>
/// B1: VIX-squared, calibrated. Its own fit already minimizes training QLIKE directly
/// (<see cref="CalibratedVixFit"/>), so no retransformation is layered on top — the one
/// registered model whose forecast is its fit.
/// </summary>
public sealed class CalibratedVixMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.Vix;
    public override bool Registered => true;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var b1 = CalibratedVixFit.Fit(
            context.Train.Select(r => r.LogPriorVix2).ToList(), context.TrainActuals.ToList());

        return new VolResidualFittedMethod(Forecast: r => b1.PredictVariance(r.LogPriorVix2));
    }
}

/// <summary>
/// CORRECTED — the registered rung-3 candidate: an elastic net on HAR-X's own residual target,
/// its correction added back onto HAR-X's log forecast.
/// </summary>
/// <remarks>
/// Depends on <see cref="HarxMethod"/> having been fitted first, and on its RAW log forecast:
/// correcting the retransformed forecast would bake HAR-X's correction factor into the residual
/// definition. The lagged-residual feature is walked causally over train and test together —
/// the model producing the residuals was fitted on train only, so evaluating it on later rows
/// is ordinary prediction, and a day's feature reads only strictly prior days' realized
/// residuals.
/// </remarks>
public sealed class CorrectedMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.Corrected;
    public override bool Registered => true;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var harxLog = context.Require(VolResidualModelKeys.HarX).LogForecast
            ?? throw new InvalidOperationException("HAR-X exposes no log forecast to correct.");

        var harxLogForecastByDate = context.AllRowsByDate.ToDictionary(r => r.Date, r => harxLog(r));
        var residualByDate = context.AllRowsByDate.ToDictionary(
            r => r.Date, r => Math.Log(r.ActualVariance) - harxLogForecastByDate[r.Date]);

        // Mean of the last 5 signed HAR-X residuals, strictly prior days only.
        var meanLast5ResidualByDate = new Dictionary<DateOnly, double>();
        var recentResiduals = new Queue<double>();
        foreach (var row in context.AllRowsByDate)
        {
            meanLast5ResidualByDate[row.Date] = recentResiduals.Count == 0 ? 0.0 : recentResiduals.Average();
            recentResiduals.Enqueue(residualByDate[row.Date]);
            if (recentResiduals.Count > 5) recentResiduals.Dequeue();
        }

        double[] Features(VolResidualRawRow r) => CandidateFeatures(context, meanLast5ResidualByDate, r);

        var model = ElasticNet.FitWithCrossValidation(
            context.Train.Select(Features).ToList(),
            context.Train.Select(r => residualByDate[r.Date]).ToList());

        double RawLogForecast(VolResidualRawRow r) => harxLogForecastByDate[r.Date] + model.Predict(Features(r));

        var factor = QlikeRetransformation.FitFactor(
            context.TrainActuals.ToList(),
            context.Train.Select(r => Math.Exp(RawLogForecast(r))).ToList());

        return new VolResidualFittedMethod(
            Forecast: r => factor * Math.Exp(RawLogForecast(r)),
            LogForecast: RawLogForecast);
    }

    /// <summary>The candidate feature vector, shared with the exploratory GBT so the two are comparable.</summary>
    internal static double[] CandidateFeatures(
        VolResidualFoldContext context, IReadOnlyDictionary<DateOnly, double> meanLast5ResidualByDate, VolResidualRawRow r) =>
    [
        r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22,
        r.DayOfWeekDummies[0], r.DayOfWeekDummies[1], r.DayOfWeekDummies[2], r.DayOfWeekDummies[3],
        r.DaysToMonthlyOpex,
        meanLast5ResidualByDate[r.Date],
        r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r),
    ];
}

/// <summary>
/// Ladder rung 4, gradient-boosted trees — EXPLORATORY. Fitted on the level-scale variance
/// target under squared error, so unlike every log-space method it produces a conditional-mean
/// forecast directly and is deliberately not retransformed.
/// </summary>
/// <remarks>
/// Rebuilds the same feature vector as <see cref="CorrectedMethod"/> — including the causal
/// residual walk — so the two rungs see identical inputs and any difference between them is the
/// model family, not the features.
/// </remarks>
public sealed class GbtMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.Gbt;
    public override bool Registered => false;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var harxLog = context.Require(VolResidualModelKeys.HarX).LogForecast
            ?? throw new InvalidOperationException("HAR-X exposes no log forecast to build residual features from.");

        var residualByDate = context.AllRowsByDate.ToDictionary(
            r => r.Date, r => Math.Log(r.ActualVariance) - harxLog(r));

        var meanLast5ResidualByDate = new Dictionary<DateOnly, double>();
        var recentResiduals = new Queue<double>();
        foreach (var row in context.AllRowsByDate)
        {
            meanLast5ResidualByDate[row.Date] = recentResiduals.Count == 0 ? 0.0 : recentResiduals.Average();
            recentResiduals.Enqueue(residualByDate[row.Date]);
            if (recentResiduals.Count > 5) recentResiduals.Dequeue();
        }

        double[] Features(VolResidualRawRow r) =>
            CorrectedMethod.CandidateFeatures(context, meanLast5ResidualByDate, r);

        var gbt = GradientBoostedTrees.Fit(
            context.Train.Select(Features).ToList(), context.TrainActuals.ToList());

        return new VolResidualFittedMethod(
            Forecast: r => gbt.Predict(Features(r)),
            FloorBinds: r => gbt.FloorBinds(Features(r)));
    }
}
