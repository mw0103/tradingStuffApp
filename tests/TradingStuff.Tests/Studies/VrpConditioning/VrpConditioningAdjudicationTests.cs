using TradingStuff.ResearchService.Studies.VrpConditioning;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// The overlap problem, pinned. Twenty-one-day windows on daily data share up to twenty of their
/// twenty-one label days, so the overlapping series is enormously over-precise; the study is required
/// to report a non-overlapping subsample alongside it and to mark that one as the honest inference.
/// </summary>
public class VrpConditioningAdjudicationTests
{
    private static VrpConditioningDailyResult Day(DateOnly date, string fold, double armLoss, double gateLoss)
    {
        var forecasts = new Dictionary<string, double>
        {
            [VrpConditioningArms.Unconditional] = 0.01,
            [VrpConditioningArms.CalibratedVix] = 0.01,
            [VrpConditioningArms.HarX] = 0.01,
            [VrpConditioningArms.Corrected] = 0.01,
        };

        var qlike = new Dictionary<string, double>
        {
            [VrpConditioningArms.Unconditional] = armLoss,
            [VrpConditioningArms.CalibratedVix] = armLoss,
            [VrpConditioningArms.HarX] = gateLoss,
            [VrpConditioningArms.Corrected] = armLoss,
        };

        var spread = forecasts.ToDictionary(kvp => kvp.Key, _ => 0.001);
        var bucket = forecasts.ToDictionary(kvp => kvp.Key, _ => 3);

        return new VrpConditioningDailyResult(
            date, date.AddDays(1), date.AddDays(30), fold,
            0.01, 0.012, 18.0, 17.0, forecasts, qlike, spread, bucket, 0.002, 1.5);
    }

    /// <summary>
    /// A fold whose loss differential is strongly serially correlated — which is what 21-day
    /// overlapping labels actually produce — so thinning genuinely changes the answer.
    /// </summary>
    private static VrpConditioningFoldResult Fold(string name, DateOnly start, int days, int seed)
    {
        var results = new List<VrpConditioningDailyResult>(days);
        var state = seed;
        var level = 0.0;

        for (var i = 0; i < days; i++)
        {
            state = state * 1103515245 + 12345;
            var noise = (state >> 16 & 0x7FFF) / 32768.0 - 0.5;

            // A slow-moving level plus small noise: adjacent days are almost identical, distant
            // days are not — the dependence structure a 21-day label induces mechanically.
            level = 0.97 * level + 0.03 * noise;

            var gate = 0.50 + level;
            var arm = gate - 0.01 - 0.5 * level;

            results.Add(Day(start.AddDays(i), name, arm, gate));
        }

        var breakpoints = VrpConditioningArms.All.ToDictionary(a => a, _ => new[] { -1.0, 0.0, 1.0, 2.0 });

        return new VrpConditioningFoldResult(
            name, start.AddDays(-400), start.AddDays(-100), start, start.AddDays(days - 1), 500,
            breakpoints, results);
    }

    [Fact]
    public void TheNonOverlappingSubsampleTakesEveryTwentyFirstDayWithinEachFold()
    {
        var folds = new List<VrpConditioningFoldResult>
        {
            Fold("F1", new DateOnly(2018, 1, 1), 200, 7),
            Fold("F2", new DateOnly(2020, 1, 1), 150, 11),
        };

        var thinned = VrpConditioningAdjudication.NonOverlappingSubsample(folds);

        // Ceiling division within each fold, never across the seam between them.
        Assert.Equal(200 / 21 + 1 + (150 / 21 + 1), thinned.Count);

        foreach (var fold in folds)
        {
            var picked = thinned.Where(d => d.FoldName == fold.FoldName).OrderBy(d => d.Date).ToList();
            var source = fold.DailyResults.OrderBy(d => d.Date).ToList();

            for (var i = 0; i < picked.Count; i++)
            {
                Assert.Equal(source[i * VrpConditioningHorizon.LabelTradingDays].Date, picked[i].Date);
            }
        }

        // No two retained observations within a fold are closer than the label horizon, which is the
        // property "non-overlapping" actually means.
        foreach (var group in thinned.GroupBy(d => d.FoldName))
        {
            var ordered = group.OrderBy(d => d.Date).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var gap = ordered[i].Date.DayNumber - ordered[i - 1].Date.DayNumber;
                Assert.True(gap >= VrpConditioningHorizon.LabelTradingDays,
                    $"retained observations {ordered[i - 1].Date} and {ordered[i].Date} are {gap} apart, " +
                    $"closer than the {VrpConditioningHorizon.LabelTradingDays}-session label horizon.");
            }
        }
    }

    [Fact]
    public void TheOverlappingAndNonOverlappingComparisonsAreDifferentTestsAndTheHonestOneIsMarked()
    {
        var folds = new List<VrpConditioningFoldResult>
        {
            Fold("F1", new DateOnly(2018, 1, 1), 500, 7),
            Fold("F2", new DateOnly(2020, 1, 1), 500, 11),
            Fold("F3", new DateOnly(2022, 1, 1), 500, 13),
        };

        var comparisons = VrpConditioningAdjudication.Compare(folds);

        Assert.NotEmpty(comparisons);
        Assert.DoesNotContain(comparisons, c => c.Arm == VrpConditioningArms.Gate);

        foreach (var comparison in comparisons)
        {
            Assert.Equal(VrpConditioningAdjudication.Overlapping, comparison.Overlapping.Sampling);
            Assert.Equal(VrpConditioningAdjudication.NonOverlapping, comparison.NonOverlapping.Sampling);

            // Only ONE of the two is labelled the honest inference, and it is the thinned one.
            Assert.False(comparison.Overlapping.Honest);
            Assert.True(comparison.NonOverlapping.Honest);

            // The registered lags: >= horizon - 1 on the overlapping series, and the 1-observation
            // rule on the thinned one.
            Assert.Equal(VrpConditioningHorizon.OverlappingHacLag, comparison.Overlapping.HacLag);
            Assert.Equal(VrpConditioningHorizon.NonOverlappingHacLag, comparison.NonOverlapping.HacLag);

            // They are computed on genuinely different samples...
            Assert.Equal(1500, comparison.Overlapping.Observations);
            Assert.Equal(72, comparison.NonOverlapping.Observations);
            Assert.True(comparison.NonOverlapping.Observations < comparison.Overlapping.Observations / 20,
                "the thinned sample must be smaller by roughly the horizon, or the stride is not applied.");

            // ... and produce genuinely different statistics. The overlapping one is the
            // over-precise one, so its |statistic| is the larger.
            Assert.NotEqual(comparison.Overlapping.Statistic, comparison.NonOverlapping.Statistic);
            Assert.True(Math.Abs(comparison.Overlapping.Statistic) > Math.Abs(comparison.NonOverlapping.Statistic),
                $"the overlapping statistic ({comparison.Overlapping.Statistic:F3}) is not larger in " +
                $"magnitude than the non-overlapping one ({comparison.NonOverlapping.Statistic:F3}); " +
                "if thinning did not reduce apparent precision, the thinning is not happening.");

            // The bootstrap interval is reported, and no p-value rides on it.
            Assert.True(comparison.MeanAdvantageInterval.Draws > 0);
            Assert.True(comparison.MeanAdvantageInterval.Lower <= comparison.MeanAdvantageInterval.Upper);
        }
    }

    [Fact]
    public void EveryResponseCarriesTheNoSignificanceClaimsLimitationAsData()
    {
        var limitations = VrpConditioningLimitations.Registered;

        Assert.Contains("NO SIGNIFICANCE CLAIMS", limitations.Inference);
        Assert.Contains("bootstrap", limitations.Inference, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no bid-ask", limitations.PnlProxy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delta hedging", limitations.PnlProxy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("margin", limitations.PnlProxy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overlap", limitations.Overlap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONDITIONING KNOWLEDGE, NOT P&L CLAIMS", limitations.Headline);
    }
}
