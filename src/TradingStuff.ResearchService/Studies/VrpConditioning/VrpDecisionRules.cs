namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// The decision layer: turns each arm's forecast spread into a short-vol position and scores the
/// resulting strategy. This is where "does a better estimate improve the decision?" is actually
/// asked — everything upstream of here is loss tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules are deliberately dumb.</b> Each is a fixed mapping from the day's train-frozen
/// spread bucket to a vega position, declared here, with nothing fitted: a rule with estimated
/// parameters would reopen the model-selection problem one level up, on far fewer effective
/// observations (the 21-day windows overlap 21:1). What varies between strategies is ONLY which
/// arm's forecast feeds the rule — so a difference between two strategies under the same rule is
/// attributable to the forecast, which is the comparison the study exists to make.
/// </para>
/// <para>
/// <b>Two honesty constraints carried from the study design.</b> First, all summary statistics are
/// computed on BOTH the overlapping daily series (every decision date; HAC-lagged where a test is
/// involved) and the stride-thinned non-overlapping series (every 21st row; ~12 independent
/// observations per year) — the thinned Sharpe is the honest one, the overlapping series is for
/// resolution. Second, the payoff is the idealized variance-swap short of
/// <see cref="VrpConditioningHorizon.ShortVarianceSwapPayoffPerVegaNotional"/>: no spreads, no
/// margin, no early unwind, no crush of any specific option structure. Numbers here rank rules and
/// arms; they do not estimate live P&amp;L.
/// </para>
/// </remarks>
public static class VrpDecisionRules
{
    /// <summary>Always short 1 vega: the unconditional VRP harvest every strategy must beat.</summary>
    public const string AlwaysSell = "always-sell";

    /// <summary>Short 1 vega when the spread is positive (implied above forecast), else flat.</summary>
    public const string SellWhenPositive = "sell-when-positive";

    /// <summary>Short 1 vega in the top two train-frozen spread quintiles, else flat.</summary>
    public const string SellTopQuintiles = "sell-top-quintiles";

    /// <summary>
    /// Scale into the premium: flat below bucket 3, half vega at 3, full at 4, one-and-a-half at 5.
    /// The sizing question in its simplest declared form.
    /// </summary>
    public const string Sized = "sized";

    public static readonly IReadOnlyList<string> All =
        [AlwaysSell, SellWhenPositive, SellTopQuintiles, Sized];

    /// <summary>Vega position for one day under one rule. Positive = short volatility.</summary>
    public static double Position(string rule, double spread, int bucket) => rule switch
    {
        AlwaysSell => 1.0,
        SellWhenPositive => spread > 0.0 ? 1.0 : 0.0,
        SellTopQuintiles => bucket >= 4 ? 1.0 : 0.0,
        Sized => bucket switch { 5 => 1.5, 4 => 1.0, 3 => 0.5, _ => 0.0 },
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown decision rule."),
    };

    /// <summary>One strategy's scorecard: an arm's forecasts driving one rule.</summary>
    /// <param name="MeanPnlPerDay">Mean daily P&amp;L per unit MAX vega, overlapping series.</param>
    /// <param name="ThinnedSharpe">
    /// Annualized Sharpe on the stride-thinned non-overlapping series — the honest risk-adjusted
    /// number, at the price of only ~12 observations per year.
    /// </param>
    /// <param name="ThinnedObservations">How few observations that Sharpe rests on. Displayed, always.</param>
    /// <param name="Participation">Fraction of days with a nonzero position.</param>
    /// <param name="WorstDay">Worst single overlapping-window payoff taken while positioned.</param>
    /// <param name="MaxDrawdown">Max peak-to-trough on the thinned cumulative P&amp;L.</param>
    public sealed record StrategyResult(
        string Arm,
        string Rule,
        double MeanPnlPerDay,
        double ThinnedMeanPnl,
        double ThinnedSharpe,
        int ThinnedObservations,
        double Participation,
        double WorstDay,
        double MaxDrawdown);

    /// <summary>
    /// Scores every arm under every rule over a set of scored days (typically all folds' test
    /// blocks pooled).
    /// </summary>
    public static List<StrategyResult> Evaluate(IReadOnlyList<VrpConditioningDailyResult> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        var ordered = days.OrderBy(d => d.Date).ToList();
        var results = new List<StrategyResult>();

        var arms = ordered.Count == 0
            ? []
            : ordered[0].Spread.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        foreach (var arm in arms)
        {
            foreach (var rule in All)
            {
                // Daily P&L: the day's short payoff scaled by the rule's position for that day.
                var pnl = ordered
                    .Select(d => Position(rule, d.Spread[arm], d.Bucket[arm]) * d.PnlPerVegaNotional)
                    .ToList();

                var positioned = ordered
                    .Where(d => Position(rule, d.Spread[arm], d.Bucket[arm]) > 0.0)
                    .ToList();

                // Thinned series: every LabelTradingDays-th decision date, so no two windows
                // overlap. The offset is fixed at zero rather than chosen - choosing the best
                // offset after seeing results would be one more quiet selection.
                var thinned = pnl.Where((_, i) => i % VrpConditioningHorizon.LabelTradingDays == 0).ToList();

                var thinnedMean = thinned.Count > 0 ? thinned.Average() : 0.0;
                var thinnedStd = PopulationStd(thinned);
                var periodsPerYear = VrpConditioningHorizon.TradingDaysPerYear / VrpConditioningHorizon.LabelTradingDays;
                var sharpe = thinnedStd > 1e-12
                    ? thinnedMean / thinnedStd * Math.Sqrt(periodsPerYear)
                    : 0.0;

                results.Add(new StrategyResult(
                    arm,
                    rule,
                    pnl.Count > 0 ? pnl.Average() : 0.0,
                    thinnedMean,
                    sharpe,
                    thinned.Count,
                    ordered.Count > 0 ? (double)positioned.Count / ordered.Count : 0.0,
                    positioned.Count > 0 ? positioned.Min(d => d.PnlPerVegaNotional) : 0.0,
                    MaxDrawdown(thinned)));
            }
        }

        return results;
    }

    private static double PopulationStd(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    /// <summary>Largest peak-to-trough fall of the cumulative sum, in the payoff's own units.</summary>
    internal static double MaxDrawdown(IReadOnlyList<double> pnl)
    {
        var cumulative = 0.0;
        var peak = 0.0;
        var worst = 0.0;

        foreach (var value in pnl)
        {
            cumulative += value;
            if (cumulative > peak) peak = cumulative;
            var drawdown = peak - cumulative;
            if (drawdown > worst) worst = drawdown;
        }

        return worst;
    }
}
