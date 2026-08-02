using TradingStuff.Volatility.Baselines;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// Everything a prediction method may read when fitting on one fold: the split, the shared
/// train-frozen statistics, and the methods already fitted before it in catalog order.
/// </summary>
/// <remarks>
/// <para>
/// The context is built once per fold, from the TRAINING block only, before any method runs.
/// A method must treat everything here as read-only except <see cref="Fitted"/>, which the
/// runner appends each method's result to as the catalog executes — that is how a later method
/// depends on an earlier one (CORRECTED reads HAR-X's log forecast) without either of them
/// reaching into the other's internals.
/// </para>
/// <para>
/// The shared statistics live here rather than in the methods that use them because they are
/// train-frozen by construction: the divergence z-score moments are estimated once from the
/// training window and applied to every row with those same frozen values. A method that
/// recomputed them per-row or per-block would silently leak evaluation data into a feature.
/// </para>
/// </remarks>
public sealed class VolResidualFoldContext
{
    public required VolResidualFoldSplit Split { get; init; }

    public IReadOnlyList<VolResidualRawRow> Train => Split.Train;
    public IReadOnlyList<VolResidualRawRow> Test => Split.Test;

    /// <summary>Train and test rows together, ascending by date. The causal-walk order.</summary>
    public required IReadOnlyList<VolResidualRawRow> AllRowsByDate { get; init; }

    public required IReadOnlyList<double> TrainActuals { get; init; }
    public required IReadOnlyList<double> TrainLogTargets { get; init; }

    /// <summary>Train-window moments for the Tier-1 VIX divergence z-scores. Frozen; never re-estimated.</summary>
    public required double VixChangeMean { get; init; }
    public required double VixChangeStd { get; init; }
    public required double SpxReturnMean { get; init; }
    public required double SpxReturnStd { get; init; }

    /// <summary>
    /// Methods already fitted on this fold, keyed by <see cref="VolResidualMethod.Key"/>, in
    /// catalog order. How a dependent method reads its prerequisite.
    /// </summary>
    public Dictionary<string, VolResidualFittedMethod> Fitted { get; } = [];

    /// <summary>The prerequisite lookup, failing with the dependency named rather than a KeyNotFound.</summary>
    public VolResidualFittedMethod Require(string key) =>
        Fitted.TryGetValue(key, out var fitted)
            ? fitted
            : throw new InvalidOperationException(
                $"Method '{key}' has not been fitted on this fold yet. Catalog order is load-bearing: " +
                "a method that depends on another must appear after it in VolResidualMethodCatalog.");

    /// <summary>The Tier-1 divergence interaction, from the frozen training moments.</summary>
    public double Divergence(VolResidualRawRow r) =>
        ZScore(r.Vix5DayChange, VixChangeMean, VixChangeStd) * ZScore(r.Spx1DayLogReturn, SpxReturnMean, SpxReturnStd);

    public static VolResidualFoldContext Build(VolResidualFoldSplit split)
    {
        var (vixChangeMean, vixChangeStd) = MeanAndPopulationStd(split.Train.Select(r => r.Vix5DayChange));
        var (spxReturnMean, spxReturnStd) = MeanAndPopulationStd(split.Train.Select(r => r.Spx1DayLogReturn));

        return new VolResidualFoldContext
        {
            Split = split,
            AllRowsByDate = split.Train.Concat(split.Test).OrderBy(r => r.Date).ToList(),
            TrainActuals = split.Train.Select(r => r.ActualVariance).ToList(),
            TrainLogTargets = split.Train.Select(r => Math.Log(r.ActualVariance)).ToList(),
            VixChangeMean = vixChangeMean,
            VixChangeStd = vixChangeStd,
            SpxReturnMean = spxReturnMean,
            SpxReturnStd = spxReturnStd,
        };
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

/// <summary>
/// One method, fitted on one fold's training block: its level-scale variance forecast, and
/// whatever intermediate a dependent method is allowed to build on.
/// </summary>
/// <param name="Forecast">
/// The forecast the method reports and is scored on: LEVEL-scale variance, after whatever
/// retransformation the method applies. This is the only member scoring reads.
/// </param>
/// <param name="LogForecast">
/// The raw log-space fit BEFORE retransformation, when the method has one. This is what a
/// residual-corrector builds on — correcting the retransformed forecast would bake the
/// correction factor into the residual definition. Null for level-space methods (calibrated
/// VIX, trees), which is a fact about them, not an omission: there is no log fit to correct.
/// </param>
/// <param name="FloorBinds">
/// Whether the method's positivity floor raised the forecast for this row. Null for methods
/// without a floor. Reported per fold because a floor that binds often means the fit, not the
/// floor, is doing the forecasting.
/// </param>
public sealed record VolResidualFittedMethod(
    Func<VolResidualRawRow, double> Forecast,
    Func<VolResidualRawRow, double>? LogForecast = null,
    Func<VolResidualRawRow, bool>? FloorBinds = null);

/// <summary>
/// A prediction method the fold runner can fit and score.
/// </summary>
/// <remarks>
/// <para>
/// The methods are deliberately NOT uniform, and this interface preserves rather than flattens
/// the differences. Whether a method fits in log space and retransforms (HAR, HAR-X, the
/// corrector) or fits the level directly (calibrated VIX, trees) is the method's own business,
/// expressed in what its <see cref="VolResidualFittedMethod"/> exposes. What the interface fixes
/// is only the contract scoring relies on: a level-scale forecast per row, fitted from the
/// training block and the previously fitted methods, nothing else.
/// </para>
/// <para>
/// Adding a method is one class and one catalog entry. It does NOT register a study variant —
/// running a new method against real data goes through <c>research.registered_trials</c> first,
/// which is a decision, not a code change.
/// </para>
/// </remarks>
public abstract class VolResidualMethod
{
    /// <summary>The key forecasts and losses are reported under. One of <see cref="VolResidualModelKeys"/>.</summary>
    public abstract string Key { get; }

    /// <summary>
    /// Whether this method is part of the registered ladder. Exploratory methods are fitted
    /// only when a run opts in, and their presence must not perturb any registered method —
    /// a property the characterization suite asserts.
    /// </summary>
    public abstract bool Registered { get; }

    /// <summary>
    /// How this method is named in a run's reported model list. Defaults to the key, so a new
    /// method is reported the moment it is added to the catalog — the reporting layer enumerates
    /// the catalog rather than keeping a parallel list that can silently omit a model.
    /// </summary>
    public virtual string Label => Key;

    /// <summary>
    /// The role the run reports this method under. Anything unregistered is exploratory by
    /// definition and cannot be anything else, so only registered methods may override.
    /// </summary>
    public virtual string Role => VolResidualModelRoles.Exploratory;

    public abstract VolResidualFittedMethod Fit(VolResidualFoldContext context);
}
