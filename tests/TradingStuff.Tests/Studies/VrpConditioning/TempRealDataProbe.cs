using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;
using Xunit.Abstractions;

namespace TradingStuff.Tests.Studies.VrpConditioning;

// TEMPORARY probe against the live recorded database. Deleted before the change is reported.
[Trait("Category", "TempProbe")]
public sealed class TempRealDataProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task RunAgainstLiveBars()
    {
        var cs = Environment.GetEnvironmentVariable("TRADING_LIVE_POSTGRES");
        if (cs is null) return;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = cs })
            .Build();

        var runner = new VrpConditioningStudyRunner(
            new VolResidualBarLoader(config), new SessionClock(), NullLogger<VrpConditioningStudyRunner>.Instance);

        // --- probe the elastic net directly on real fold data ---
        var loader = new VolResidualBarLoader(config);
        var bars = await loader.LoadSpxOneMinuteBarsAsync(new DateOnly(2010, 1, 1), new DateOnly(2023, 12, 31), CancellationToken.None);
        var vix = await loader.LoadVixDailyClosesAsync(new DateOnly(2010, 1, 1), new DateOnly(2023, 12, 31), CancellationToken.None);
        var days = TradingStuff.Volatility.VolatilityPresets.BuildSpxStudyTarget(
            new SessionClock(), TradingStuff.ResearchService.Volatility.HistoricalBarAdapter.ToIntradayBars(bars));
        var rawRows = VrpConditioningFeatureBuilder.BuildRawRows(days, vix);

        foreach (var split in VolResidualSplitter.Split(
            rawRows, x => x.Date, TradingStuff.Volatility.Forecasting.WalkForwardFold.Registered(), VrpConditioningHorizon.PurgeRows))
        {
            var tr = split.Train;
            var logTargets = tr.Select(x => Math.Log(x.LabelCumulativeVariance)).ToList();
            double[] Feat(VrpConditioningRawRow x) => [x.LogRv, x.MeanLogRv5, x.MeanLogRv22, x.LogImpliedVariance, x.Vix5DayChange, 0.0];
            var coef = NonNegativeLeastSquares.Fit(tr.Select(Feat).ToList(), logTargets);
            var resid = tr.Select((x, i) => logTargets[i] - NonNegativeLeastSquares.Predict(coef, Feat(x))).ToList();

            var net = ElasticNet.FitWithCrossValidation(tr.Select(Feat).ToList(), resid);
            output.WriteLine($"PROBE {split.Fold.Name}: train={tr.Count} residSd={Sd(resid):F4} " +
                             $"alpha={net.Alpha} lambda={net.Lambda:E3} intercept={net.Intercept:E3} " +
                             $"nonZeroCoefs={net.Coefficients.Count(c => Math.Abs(c) > 1e-12)} " +
                             $"maxAbsCoef={net.Coefficients.Select(Math.Abs).DefaultIfEmpty(0).Max():E3}");
        }

        var started = DateTimeOffset.UtcNow;
        var r = await runner.RunAsync(null, null, CancellationToken.None);
        output.WriteLine($"status={r.Status} elapsed={(DateTimeOffset.UtcNow - started).TotalSeconds:F1}s");
        output.WriteLine($"reason={r.InsufficientReason}");
        if (r.Status != VrpConditioningRunStatus.Ok) return;

        output.WriteLine($"window {r.DataWindow.From}..{r.DataWindow.To}  sessions={r.DataWindow.SessionsAvailable} " +
                         $"decisionDates={r.DataWindow.DecisionDates} labels {r.DataWindow.FirstLabelFrom}..{r.DataWindow.LastLabelTo}");
        output.WriteLine($"effective: scored={r.EffectiveSample.ScoredDecisionDates} nonOverlapping={r.EffectiveSample.NonOverlappingWindows}");

        output.WriteLine("");
        output.WriteLine("ARMS (pooled QLIKE, improvement vs HARX gate):");
        foreach (var a in r.Arms)
        {
            output.WriteLine($"  {a.Key,-15} {a.Role,-10} pooled={a.PooledQlike:F6}  vsGate={a.ImprovementVsGatePct,7:F2}%  " +
                             string.Join(" ", a.Folds.Select(f => $"[{f.Fold} n={f.Days} q={f.Qlike:F4}]")));
        }

        output.WriteLine("");
        output.WriteLine("DIEBOLD-MARIANO (vs gate):");
        foreach (var c in r.DieboldMariano)
        {
            output.WriteLine($"  {c.Arm}  disagree={c.SamplingsDisagree}");
            output.WriteLine($"    overlapping    n={c.Overlapping.Observations,5} lag={c.Overlapping.HacLag,3} DM={c.Overlapping.Statistic,8:F3} p={c.Overlapping.PValueOneSided:F4} adv={c.Overlapping.MeanLossAdvantage: 0.000000;-0.000000}");
            output.WriteLine($"    NONoverlapping n={c.NonOverlapping.Observations,5} lag={c.NonOverlapping.HacLag,3} DM={c.NonOverlapping.Statistic,8:F3} p={c.NonOverlapping.PValueOneSided:F4} adv={c.NonOverlapping.MeanLossAdvantage: 0.000000;-0.000000}");
            output.WriteLine($"    bootstrap CI on mean advantage: [{c.MeanAdvantageInterval.Lower: 0.000000;-0.000000}, {c.MeanAdvantageInterval.Upper: 0.000000;-0.000000}]");
        }

        foreach (var arm in r.Conditioning)
        {
            output.WriteLine("");
            output.WriteLine($"=== QUINTILES: {arm.Arm} ===");
            output.WriteLine($"  pnl monotonicity      : {arm.PnlMonotonicity.Shape}");
            output.WriteLine($"  premium monotonicity  : {arm.PremiumMonotonicity.Shape}");
            output.WriteLine($"  realizedVar monotonic.: {arm.RealizedVarianceMonotonicity.Shape}");
            output.WriteLine($"  bootstrap monotone fraction: pnl={arm.BootstrapMonotoneFractionPnl:P1} premium={arm.BootstrapMonotoneFractionPremium:P1} (usable resamples={arm.UsableResamples})");
            output.WriteLine($"  Q5-Q1 pnl = {arm.Q5MinusQ1Pnl:F3} vol pts  CI[{arm.Q5MinusQ1PnlInterval.Lower:F3}, {arm.Q5MinusQ1PnlInterval.Upper:F3}]");
            output.WriteLine($"  {"bucket",-7} {"days",5} {"meanSpread",12} {"meanRealVar",12} {"realVol%",9} {"meanImplVar",12} {"premium",12} {"pnl/vega",9}  pnlCI");
            foreach (var b in arm.Buckets)
            {
                output.WriteLine($"  Q{b.Bucket,-6} {b.Days,5} {b.MeanSpread,12:F6} {b.MeanRealizedVariance,12:F6} {b.MeanRealizedAnnualizedVolPct,9:F2} {b.MeanImpliedVariance,12:F6} {b.MeanPremiumCollected,12:F6} {b.MeanPnlPerVegaNotional,9:F3}  [{b.PnlInterval.Lower:F3}, {b.PnlInterval.Upper:F3}]");
            }
        }
    }
}
