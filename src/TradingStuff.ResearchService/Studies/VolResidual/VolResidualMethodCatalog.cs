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
        new EqualWeightMethod(),
        new HarqxMethod(),
        new SharxMethod(),
        new HarCjxMethod(),
        new GrNnlsMethod(),
        new GrRegimeMethod(),
        new DiscountedQlikeMethod(),
        new HarqCjxMethod(),
        new CorrectedOverHarqCjMethod(),
    ];
}

/// <summary>HAR-RV in log space (Corsi), the reference the whole ladder is read against.</summary>
public sealed class HarMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.Har;
    public override bool Registered => true;

    public override string Label => "HAR-RV";
    public override string Role => VolResidualModelRoles.Reference;

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

    public override string Label => "HAR-X (primary gate)";
    public override string Role => VolResidualModelRoles.Gate;

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

    public override string Label => "B1: calibrated VIX";
    public override string Role => VolResidualModelRoles.Baseline;

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
public class CorrectedMethod : VolResidualMethod
{
    /// <summary>The model whose log forecast is corrected. HAR-X for the registered candidate.</summary>
    protected virtual string BaseModelKey => VolResidualModelKeys.HarX;

    public override string Key => VolResidualModelKeys.Corrected;
    public override bool Registered => true;

    public override string Label => "Corrected (elastic net on HAR-X residual)";
    public override string Role => VolResidualModelRoles.Candidate;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var harxLog = context.Require(BaseModelKey).LogForecast
            ?? throw new InvalidOperationException($"{BaseModelKey} exposes no log forecast to correct.");

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

    public override string Label => "Rung 4: gradient-boosted trees (EXPLORATORY — not eligible for any claim)";

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


/// <summary>
/// Candidate B1: the equal-weight average of the fitted HAR-X and calibrated-VIX forecasts.
/// </summary>
/// <remarks>
/// <para>
/// The null hypothesis of the combination tier (<c>docs/research/model-candidates.md</c> §3).
/// Zero parameters beyond the members' own fits, so there is nothing here to overfit and nothing
/// to tune — which is exactly why it runs first: if an unweighted average of two forecasts that
/// disagree by construction (history-only versus option-market-only information) does not beat
/// the gate on DM, estimated weighting schemes are unlikely to, and the tier can be closed
/// cheaply.
/// </para>
/// <para>
/// The average is taken over the members' REPORTED forecasts — after each member's own
/// retransformation — because those are the forecasts each member would stand behind alone. No
/// further retransformation is layered on the average: its members are already calibrated, and a
/// combination that recalibrates its inputs is a different, parameterized candidate.
/// </para>
/// </remarks>
public sealed class EqualWeightMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.EqualWeight;
    public override bool Registered => false;

    public override string Label => "B1: equal-weight HAR-X + calibrated VIX";

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var harx = context.Require(VolResidualModelKeys.HarX);
        var vix = context.Require(VolResidualModelKeys.Vix);

        return new VolResidualFittedMethod(
            Forecast: r => 0.5 * (harx.Forecast(r) + vix.Forecast(r)));
    }
}

/// <summary>
/// Candidate A1: HARQ-X — HAR-X with the daily lag attenuated by realized quarticity
/// (Bollerslev–Patton–Quaedvlieg 2016, adapted to this study's log-space HAR-X).
/// </summary>
/// <remarks>
/// <para>
/// The premise: sqrt(RQ) proxies the sampling error of a day's realized variance, so the daily
/// lag should be trusted less on noisily measured days. The interaction term is
/// <c>(sqrt(RQ_{d-1}) − trainMean(sqrt(RQ))) · LogRvDMinus1</c> — demeaned on the TRAINING
/// window, as BPQ demean, so the base daily coefficient keeps its interpretation and the model
/// collapses to exactly HAR-X-shaped behaviour at average measurement quality.
/// </para>
/// <para>
/// Fitted by OLS, not the NNLS the gate uses, and that is a considered exception: the whole
/// point of the attenuation coefficient is that it can move the daily weight in either
/// direction, and a non-negativity constraint would clamp away the very effect being tested.
/// BPQ themselves estimate by OLS.
/// </para>
/// </remarks>
public sealed class HarqxMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.HarqX;
    public override bool Registered => false;

    public override string Label => "A1: HARQ-X (quarticity-attenuated daily lag)";

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        // Train-frozen centre for the quarticity proxy; never re-estimated on evaluation rows.
        var trainMeanSqrtRq = context.Train.Average(r => Math.Sqrt(Math.Max(r.RqDMinus1, 0.0)));

        double[] Features(VolResidualRawRow r) =>
        [
            r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22,
            r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r),
            (Math.Sqrt(Math.Max(r.RqDMinus1, 0.0)) - trainMeanSqrtRq) * r.LogRvDMinus1,
        ];

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
/// Candidate A2: SHAR-X — HAR-X with the daily lag split into signed semivariances
/// (Patton–Sheppard 2015, adapted to this study's log-space HAR-X).
/// </summary>
/// <remarks>
/// <para>
/// The daily term <c>log RV_{d-1}</c> is replaced by <c>log RS⁻_{d-1}</c> and <c>log RS⁺_{d-1}</c>,
/// which is the literature form: the two semivariances partition RV exactly, and the finding
/// being tested is that the downside half carries most of the predictive content — a claim about
/// the two coefficients differing, which only separate columns can express.
/// </para>
/// <para>
/// Each semivariance is floored at a small fraction of that day's RV before the log. A full RTH
/// session has hundreds of returns in both directions so the floor is not expected to bind on
/// real data; it exists so a degenerate session cannot put a negative infinity into the design
/// matrix. When a day carries no semivariance information at all (both zero — a source day from
/// before the estimator computed them), the split falls back to RV/2 each, the honest
/// no-information value, which collapses the pair back to HAR-X-shaped behaviour for that row.
/// </para>
/// </remarks>
public sealed class SharxMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.SharX;
    public override bool Registered => false;

    public override string Label => "A2: SHAR-X (signed semivariance daily lag)";

    /// <summary>Floor as a fraction of the day's RV — keeps the log finite without inventing a scale.</summary>
    internal const double SemivarianceFloorFraction = 1e-6;

    internal static (double Downside, double Upside) Semivariances(VolResidualRawRow r)
    {
        var rv = Math.Exp(r.LogRvDMinus1);
        var down = r.DownsideVarianceDMinus1;
        var up = r.UpsideVarianceDMinus1;

        // Neither half known: the no-information split, exactly reconstructing RV.
        if (down <= 0.0 && up <= 0.0) return (0.5 * rv, 0.5 * rv);

        var floor = SemivarianceFloorFraction * rv;
        return (Math.Max(down, floor), Math.Max(up, floor));
    }

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        double[] Features(VolResidualRawRow r)
        {
            var (down, up) = Semivariances(r);
            return
            [
                Math.Log(down), Math.Log(up), r.MeanLogRv5, r.MeanLogRv22,
                r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r),
            ];
        }

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
/// Candidate A3: HAR-CJ-X — the daily lag split into its continuous and jump parts
/// (Andersen–Bollerslev–Diebold 2007, adapted to this study's log-space HAR-X).
/// </summary>
/// <remarks>
/// The daily term becomes <c>log BV_{d-1}</c> (the continuous part) plus the jump SHARE
/// <c>J/(J+BV)</c> bounded to [0,1]. The share rather than the jump level is deliberate: it is
/// scale-free, needs no separate normalization against RV's five orders of magnitude, and is
/// exactly zero on a day the jump test found nothing — so the model reduces to a bipower HAR-X
/// rather than to a discontinuity. Bipower is floored like SHAR-X's semivariances, and a day with
/// no decomposition available falls back to BV = RV and share = 0.
/// </remarks>
public sealed class HarCjxMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.HarCjX;
    public override bool Registered => false;

    public override string Label => "A3: HAR-CJ-X (continuous/jump decomposition)";

    internal static (double Bipower, double JumpShare) Decomposition(VolResidualRawRow r)
    {
        var rv = Math.Exp(r.LogRvDMinus1);
        var bv = r.BipowerDMinus1;
        if (bv <= 0.0) return (rv, 0.0);

        var jump = Math.Max(r.JumpDMinus1, 0.0);
        var total = bv + jump;
        var share = total <= 0.0 ? 0.0 : Math.Clamp(jump / total, 0.0, 1.0);
        return (Math.Max(bv, SharxMethod.SemivarianceFloorFraction * rv), share);
    }

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        double[] Features(VolResidualRawRow r)
        {
            var (bipower, jumpShare) = Decomposition(r);
            return
            [
                Math.Log(bipower), jumpShare, r.MeanLogRv5, r.MeanLogRv22,
                r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r),
            ];
        }

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
/// The member pool every combination candidate (B2–B4) weights: HAR-X, calibrated VIX, and plain
/// HAR, in that fixed order.
/// </summary>
/// <remarks>
/// Fixed and shared so the three combination candidates differ ONLY in how they weight — which is
/// the comparison the tier is meant to make. All three members are already-fitted registered
/// methods, so a combination reads their reported LEVEL forecasts and never refits them.
/// </remarks>
internal static class CombinationMembers
{
    internal static readonly string[] Keys =
        [VolResidualModelKeys.HarX, VolResidualModelKeys.Vix, VolResidualModelKeys.Har];

    internal static Func<VolResidualRawRow, double>[] Resolve(VolResidualFoldContext context) =>
        Keys.Select(k => context.Require(k).Forecast).ToArray();

    internal static double[] Forecasts(Func<VolResidualRawRow, double>[] members, VolResidualRawRow r) =>
        members.Select(m => m(r)).ToArray();
}

/// <summary>
/// Candidate B2: Granger–Ramanathan combination under non-negative least squares.
/// </summary>
/// <remarks>
/// <para>
/// Regresses the training window's realized variance on the member forecasts, weights constrained
/// non-negative, and applies those weights out of fold. One estimated layer above B1 — still
/// convex, still interpretable: the weights say which member the data trusts.
/// </para>
/// <para>
/// The intercept is free (<see cref="NonNegativeLeastSquares"/> leaves it unconstrained), which is
/// Granger–Ramanathan's own preferred form — the free constant is what makes the combination
/// unbiased in-sample without forcing the weights to sum to one. Fitted on the LEVEL scale
/// against level forecasts, so no retransformation is layered on top: the members are already
/// calibrated and the fit is already in the space it is scored in.
/// </para>
/// </remarks>
public sealed class GrNnlsMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.GrNnls;
    public override bool Registered => false;

    public override string Label => "B2: Granger-Ramanathan combination (NNLS)";

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var members = CombinationMembers.Resolve(context);
        var coefficients = FitWeights(context, members, context.Train);

        return new VolResidualFittedMethod(
            Forecast: r => Math.Max(
                NonNegativeLeastSquares.Predict(coefficients, CombinationMembers.Forecasts(members, r)), 1e-12));
    }

    /// <summary>Weights from one block of training rows. Shared with B3, which fits two blocks.</summary>
    internal static double[] FitWeights(
        VolResidualFoldContext context,
        Func<VolResidualRawRow, double>[] members,
        IReadOnlyList<VolResidualRawRow> rows) =>
        NonNegativeLeastSquares.Fit(
            rows.Select(r => CombinationMembers.Forecasts(members, r)).ToList(),
            rows.Select(r => r.ActualVariance).ToList());
}

/// <summary>
/// Candidate B3: B2's weights, estimated separately either side of the training window's median
/// prior VIX.
/// </summary>
/// <remarks>
/// <para>
/// The candidate the H1 result's +7.07% / +1.45% asymmetry argues for: nothing in the current
/// ladder can shift trust between members as the regime changes, and separate weights per half is
/// the smallest mechanism that can.
/// </para>
/// <para>
/// The threshold is the TRAINING window's median <c>LogPriorVix2</c> — the same train-frozen rule
/// the fold runner uses to report VIX halves, computed here from the training block only. A test
/// row is routed by its own prior VIX against that frozen threshold, so routing is a prediction,
/// not a look. A half with fewer than <see cref="MinimumRowsPerRegime"/> training rows falls back
/// to pooled weights rather than fitting a regime on a handful of days.
/// </para>
/// </remarks>
public sealed class GrRegimeMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.GrRegime;
    public override bool Registered => false;

    public override string Label => "B3: regime-split combination weights";

    /// <summary>Below this, a regime borrows the pooled weights. Declared, not tuned.</summary>
    public const int MinimumRowsPerRegime = 60;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var members = CombinationMembers.Resolve(context);

        var sortedLogVix2 = context.Train.Select(r => r.LogPriorVix2).OrderBy(v => v).ToList();
        var middle = sortedLogVix2.Count / 2;
        var threshold = sortedLogVix2.Count % 2 == 1
            ? sortedLogVix2[middle]
            : 0.5 * (sortedLogVix2[middle - 1] + sortedLogVix2[middle]);

        var pooled = GrNnlsMethod.FitWeights(context, members, context.Train);

        var lowRows = context.Train.Where(r => r.LogPriorVix2 <= threshold).ToList();
        var highRows = context.Train.Where(r => r.LogPriorVix2 > threshold).ToList();

        var low = lowRows.Count >= MinimumRowsPerRegime
            ? GrNnlsMethod.FitWeights(context, members, lowRows) : pooled;
        var high = highRows.Count >= MinimumRowsPerRegime
            ? GrNnlsMethod.FitWeights(context, members, highRows) : pooled;

        return new VolResidualFittedMethod(
            Forecast: r => Math.Max(
                NonNegativeLeastSquares.Predict(
                    r.LogPriorVix2 <= threshold ? low : high,
                    CombinationMembers.Forecasts(members, r)),
                1e-12));
    }
}

/// <summary>
/// Candidate B4: weights proportional to the inverse of each member's exponentially discounted
/// training QLIKE.
/// </summary>
/// <remarks>
/// <para>
/// Adapts after a regime break faster than B2 can — recent training days dominate the weighting —
/// without estimating a regression at all. The discount is declared, not searched: one constant,
/// stated here and in any registration this candidate is promoted into.
/// </para>
/// <para>
/// Weights are a convex combination (non-negative, summing to one) of the members' own level
/// forecasts, so like B1 nothing is retransformed on top. Inverse-loss weighting rather than
/// softmax-of-loss keeps the scheme scale-free in QLIKE units, which have no natural temperature.
/// </para>
/// </remarks>
public sealed class DiscountedQlikeMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.DiscountedQlike;
    public override bool Registered => false;

    public override string Label => "B4: discounted-QLIKE adaptive weights";

    /// <summary>Per-day discount on training QLIKE. Declared, not tuned; ~69-day half-life.</summary>
    public const double Discount = 0.99;

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var members = CombinationMembers.Resolve(context);

        // Discounted mean QLIKE per member: the LAST training day carries weight 1, each earlier
        // day one further factor of Discount. Training rows are ascending by date.
        var n = context.Train.Count;
        var discountedLoss = new double[members.Length];
        var weightSum = 0.0;

        for (var i = 0; i < n; i++)
        {
            var age = n - 1 - i;
            var w = Math.Pow(Discount, age);
            weightSum += w;
            for (var m = 0; m < members.Length; m++)
            {
                discountedLoss[m] += w * QlikeRetransformation.Loss(
                    context.Train[i].ActualVariance, members[m](context.Train[i]));
            }
        }

        var inverse = discountedLoss
            .Select(loss => 1.0 / Math.Max(loss / weightSum, 1e-12))
            .ToArray();
        var total = inverse.Sum();
        var weights = inverse.Select(v => v / total).ToArray();

        return new VolResidualFittedMethod(
            Forecast: r =>
            {
                var forecasts = CombinationMembers.Forecasts(members, r);
                var combined = 0.0;
                for (var m = 0; m < weights.Length; m++) combined += weights[m] * forecasts[m];
                return combined;
            });
    }
}


/// <summary>
/// Candidate A5: HARQ-CJ-X — the continuous/jump decomposition of A3 with A1's quarticity
/// attenuation applied to the continuous term.
/// </summary>
/// <remarks>
/// <para>
/// Suggested by the first dev run rather than by the literature: A1 and A3 each improved on the
/// gate by a similar amount (+1.7% and +1.9%) with all folds and both VIX halves positive, and
/// they do it through different mechanisms — A1 discounts a noisily MEASURED daily lag, A3
/// separates a fast-mean-reverting jump from diffusive variance. Neither mechanism subsumes the
/// other, so the question this candidate asks is whether their gains are additive.
/// </para>
/// <para>
/// The attenuation is applied to log BV rather than to log RV: once the jump is separated out,
/// the quantity whose measurement error quarticity describes is the continuous part. Fitted by
/// OLS for the same reason A1 is — the attenuation coefficient must be free to take either sign.
/// </para>
/// </remarks>
public sealed class HarqCjxMethod : VolResidualMethod
{
    public override string Key => VolResidualModelKeys.HarqCjX;
    public override bool Registered => false;
    public override string Label => "A5: HARQ-CJ-X (jump split + quarticity attenuation)";

    public override VolResidualFittedMethod Fit(VolResidualFoldContext context)
    {
        var trainMeanSqrtRq = context.Train.Average(r => Math.Sqrt(Math.Max(r.RqDMinus1, 0.0)));

        double[] Features(VolResidualRawRow r)
        {
            var (bipower, jumpShare) = HarCjxMethod.Decomposition(r);
            var logBipower = Math.Log(bipower);
            var attenuation = (Math.Sqrt(Math.Max(r.RqDMinus1, 0.0)) - trainMeanSqrtRq) * logBipower;
            return
            [
                logBipower, jumpShare, r.MeanLogRv5, r.MeanLogRv22,
                r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r), attenuation,
            ];
        }

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
/// Candidate A6: the registered elastic-net residual correction, applied to A5's log forecast
/// instead of HAR-X's.
/// </summary>
/// <remarks>
/// <para>
/// The first dev run separated two kinds of gain. The registered corrector (CORRECTED) improved
/// on the gate by ~3% but unevenly — 2 of 3 folds, +7.1% in calm markets against +1.4% in stressed
/// ones. The decomposition family (A1/A3/A5) improved by less, ~1.7–2.0%, but on every fold and in
/// both VIX halves. Those are different mechanisms: the decompositions fix what the daily lag
/// MEASURES, the corrector fixes what the residual still contains afterwards.
/// </para>
/// <para>
/// This candidate asks whether they compose — the corrector run over the better-behaved base
/// rather than over HAR-X. It is the natural next question after the first dev run, and it is the
/// one candidate on the list with a route to clearing BOTH failing conditions at once: enough
/// margin to pass the 2% bar, and enough consistency for the fold and half tests.
/// </para>
/// <para>
/// Everything about the correction itself is unchanged from
/// <see cref="CorrectedMethod"/> — same features, same causal residual walk, same elastic-net CV
/// grid — so a difference between the two is attributable to the base model alone.
/// </para>
/// </remarks>
public sealed class CorrectedOverHarqCjMethod : CorrectedMethod
{
    protected override string BaseModelKey => VolResidualModelKeys.HarqCjX;

    public override string Key => VolResidualModelKeys.CorrectedOverHarqCj;
    public override bool Registered => false;
    public override string Label => "A6: elastic-net correction over HARQ-CJ-X";
    public override string Role => VolResidualModelRoles.Exploratory;
}
