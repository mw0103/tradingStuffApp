using Microsoft.Extensions.Configuration;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// Drives the VRP decision layer against the live research database: five forecast arms, four
/// declared decision rules, 2010–2023, holdout untouched. Reports which forecast makes the better
/// DECISION, which is the study's actual question. Inert unless <c>VOLRESIDUAL_DEV_DB</c> is set.
/// </summary>
public class LiveDecisionLayerHarness
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("VOLRESIDUAL_DEV_DB");

    [Fact]
    public async Task RunTheDecisionLayerAgainstRealData()
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
        Console.WriteLine($"### rows={rows.Count} {rows.FirstOrDefault()?.Date}..{rows.LastOrDefault()?.Date}");

        var folds = VolResidualSplitter
            .Split(rows, r => r.Date, WalkForwardFold.Registered(), VrpConditioningHorizon.PurgeRows)
            .Where(VrpConditioningFoldRunner.CanScore)
            .Select(VrpConditioningFoldRunner.Run)
            .ToList();

        var days = folds.SelectMany(f => f.DailyResults).OrderBy(d => d.Date).ToList();
        Console.WriteLine($"### folds={folds.Count} days={days.Count}");

        // ---- Forecast quality at the 21-day horizon, for context ----
        var gate = VrpConditioningArms.Gate;
        Console.WriteLine("### arm QLIKE (21-day): arm | pooled | margin% vs HARX");
        foreach (var arm in VrpConditioningArms.All)
        {
            var pooled = days.Average(d => d.Qlike[arm]);
            var gatePooled = days.Average(d => d.Qlike[gate]);
            Console.WriteLine($"### {arm,-14} | {pooled,10:F6} | {100.0 * (1.0 - pooled / gatePooled),8:F3}");
        }

        // ---- The decision layer ----
        var strategies = VrpDecisionRules.Evaluate(days);

        Console.WriteLine("### strategy: arm | rule | meanPnl/day | thinnedMean | thinnedSharpe | nThin | particip% | worstDay | maxDD | calmar");
        foreach (var s in strategies.OrderBy(x => x.Rule, StringComparer.Ordinal).ThenBy(x => x.Arm, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"### {s.Arm,-14} | {s.Rule,-18} | {s.MeanPnlPerDay,10:F4} | {s.ThinnedMeanPnl,10:F4} | " +
                $"{s.ThinnedSharpe,7:F3} | {s.ThinnedObservations,5} | {100.0 * s.Participation,6:F1} | " +
                $"{s.WorstDay,8:F3} | {s.MaxDrawdown,8:F3} | {s.Calmar,7:F3}");
        }

        Assert.NotEmpty(days);
    }
}
