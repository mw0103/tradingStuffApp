using Microsoft.Extensions.Configuration;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// THE confirmatory run of <c>docs/research/confirmatory-scale-down-protocol.md</c>, frozen at
/// commit a036f14 before this harness existed. Prints exactly the frozen comparison set, the
/// frozen metrics, the monotonicity table, and the dependence-adjusted QLIKE differential.
/// Inert unless <c>VOLRESIDUAL_DEV_DB</c> is set.
/// </summary>
public class ConfirmatoryScaleDownHarness
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("VOLRESIDUAL_DEV_DB");

    [Fact]
    public async Task RunTheFrozenProtocol()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = ConnectionString,
            })
            .Build();

        var loader = new VolResidualBarLoader(configuration);
        var from = new DateOnly(2010, 1, 1);
        var to = new DateOnly(2023, 12, 31);

        var spxBars = await loader.LoadSpxOneMinuteBarsAsync(from, to, CancellationToken.None);
        var vix = await loader.LoadVixDailyClosesAsync(from, to, CancellationToken.None);

        var spxDays = VolatilityPresets.BuildSpxStudyTarget(
            TradingStuff.Tests.Volatility.SessionBars.Clock,
            HistoricalBarAdapter.ToIntradayBars(spxBars).ToList());

        var rows = VrpConditioningFeatureBuilder.BuildRawRows(spxDays, vix);
        var foldResults = VolResidualSplitter
            .Split(rows, r => r.Date, WalkForwardFold.Registered(), VrpConditioningHorizon.PurgeRows)
            .Where(VrpConditioningFoldRunner.CanScore)
            .Select(VrpConditioningFoldRunner.Run)
            .ToList();

        var days = foldResults.SelectMany(f => f.DailyResults).OrderBy(d => d.Date).ToList();
        Console.WriteLine($"### days={days.Count} folds={foldResults.Count}");

        // ---- §4: forecast-tier dependence-adjusted uncertainty, QCJ vs HAR-X ----
        var comparisons = VrpConditioningAdjudication.Compare(foldResults, VrpConditioningArms.Gate);
        var qcj = comparisons.First(c => c.Arm == VrpConditioningArms.QcjCorrected);

        Console.WriteLine("### QLIKE differential QCJ vs HARX (advantage = gate loss - arm loss, positive favours QCJ):");
        Console.WriteLine(
            $"###   overlapping (lag {VrpConditioningHorizon.OverlappingHacLag}, NOT honest): " +
            $"adv={qcj.Overlapping.MeanLossAdvantage:G6} stat={qcj.Overlapping.Statistic:F3} p={qcj.Overlapping.PValueOneSided:F4}");
        Console.WriteLine(
            $"###   thinned     (lag {VrpConditioningHorizon.NonOverlappingHacLag}, HONEST):   " +
            $"adv={qcj.NonOverlapping.MeanLossAdvantage:G6} stat={qcj.NonOverlapping.Statistic:F3} p={qcj.NonOverlapping.PValueOneSided:F4} n={qcj.NonOverlapping.Observations}");
        Console.WriteLine(
            $"###   bootstrap mean-advantage interval: [{qcj.MeanAdvantageInterval.Lower:G6}, {qcj.MeanAdvantageInterval.Upper:G6}] " +
            $"alpha={qcj.MeanAdvantageInterval.Alpha} disagree={qcj.SamplingsDisagree}");

        // ---- §3: the frozen four-strategy comparison, frozen metrics only ----
        var strategies = VrpDecisionRules.Evaluate(days);

        var frozen = new (string Label, string Arm, string Rule)[]
        {
            ("constant-1-vega", VrpConditioningArms.HarX, VrpDecisionRules.AlwaysSell),
            ("vix-only-scale-down", VrpConditioningArms.Unconditional, VrpDecisionRules.ScaleDown),
            ("harx-scale-down", VrpConditioningArms.HarX, VrpDecisionRules.ScaleDown),
            ("qcj-scale-down", VrpConditioningArms.QcjCorrected, VrpDecisionRules.ScaleDown),
        };

        Console.WriteLine("### strategy | avgVega | thinnedMean/vega | PRIMARY downsideDev/vega | sharpe | maxDD | calmar | worst");
        foreach (var (label, arm, rule) in frozen)
        {
            var s = strategies.First(x => x.Arm == arm && x.Rule == rule);
            Console.WriteLine(
                $"### {label,-20} | {s.AverageVega,7:F4} | {s.ThinnedMeanPerUnitVega,10:F4} | {s.DownsideDeviationPerUnitVega,12:F4} | " +
                $"{s.ThinnedSharpe,6:F3} | {s.MaxDrawdown,7:F2} | {s.Calmar,6:F3} | {s.WorstDay,8:F3}");
        }

        // ---- §4: monotonicity of the QCJ spread ranking ----
        Console.WriteLine("### QCJ monotonicity: test bucket | n | mean short payoff");
        foreach (var group in days
            .GroupBy(d => d.Bucket[VrpConditioningArms.QcjCorrected])
            .OrderBy(g => g.Key))
        {
            Console.WriteLine($"###   bucket {group.Key} | {group.Count(),4} | {group.Average(d => d.PnlPerVegaNotional),8:F3}");
        }

        Assert.NotEmpty(days);
    }
}
