using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Drives a real vol-residual development run against the live research database and prints the
/// candidate comparison. Opt-in: set <c>VOLRESIDUAL_DEV_DB</c> to the connection string.
/// </summary>
/// <remarks>
/// This is a research harness, not a test of anything — it asserts only that the run produced
/// scoreable days, because its output is numbers a human reads. It exists so a dev exploration can
/// be run without booting the full app host, and so the exact window and model set behind a
/// reported number is in source control rather than in a shell history.
/// </remarks>
public class LiveDevRunHarness
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("VOLRESIDUAL_DEV_DB");

    [Fact]
    public async Task RunTheExploratoryCatalogAgainstRealData()
    {
        // No database configured: this harness is inert in an ordinary suite run, deliberately.
        // It reports numbers rather than asserting them, so silently doing nothing is the correct
        // behaviour when there is nothing to report on.
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = ConnectionString,
            })
            .Build();

        var runner = new VolResidualStudyRunner(
            new VolResidualBarLoader(configuration),
            TradingStuff.Tests.Volatility.SessionBars.Clock,
            NullLogger<VolResidualStudyRunner>.Instance);

        // The registered adjudication, per candidate, needs the fold results the runner keeps
        // internally. Rebuilt here from the same loader and the same registered folds so the
        // verdicts below are the study's own, not a re-derivation.
        var response = await runner.RunAsync(
            new DateOnly(2010, 1, 1), new DateOnly(2023, 12, 31),
            includeExploratoryGbt: true, CancellationToken.None);

        Console.WriteLine($"### status={response.Status} window={response.DataWindow.From}..{response.DataWindow.To} " +
                          $"available={response.DataWindow.SessionsAvailable} used={response.DataWindow.SessionsUsed} " +
                          $"exploratory={response.IsExploratory} registrable={response.Registrable}");

        if (response.Status != VolResidualRunStatus.Ok)
        {
            Console.WriteLine($"### insufficient: {response.InsufficientReason}");
            Assert.Fail("The run produced no scoreable days; see the reason above.");
        }

        var daily = response.Daily;
        var gate = VolResidualModelKeys.Gate;
        var keys = response.Models.Select(m => m.Key).ToList();

        Console.WriteLine($"### days={daily.Count} folds={daily.Select(d => d.Fold).Distinct().Count()}");
        Console.WriteLine("### key | pooledQLIKE | margin% | DM stat | DM p(1s) | low-VIX% | high-VIX% | worst fold%");

        foreach (var key in keys)
        {
            var summary = response.Models.First(m => m.Key == key);
            var candidate = daily.Select(d => d.Qlike[key]).ToList();
            var baseline = daily.Select(d => d.Qlike[gate]).ToList();

            var dm = key == gate ? null : DieboldMariano.Compare(candidate, baseline, hacLag: 5);

            var low = HalfMargin(daily, key, gate, VolResidualVixRegimes.Low);
            var high = HalfMargin(daily, key, gate, VolResidualVixRegimes.High);

            var worstFold = daily
                .GroupBy(d => d.Fold)
                .Select(g => 100.0 * (1.0 - g.Average(d => d.Qlike[key]) / g.Average(d => d.Qlike[gate])))
                .DefaultIfEmpty(0.0)
                .Min();

            Console.WriteLine(
                $"### {key,-12} | {summary.PooledQlike,12:F6} | {summary.ImprovementVsGatePct,8:F3} | " +
                $"{dm?.Statistic,8:F3} | {dm?.PValue,8:F4} | {low,8:F3} | {high,8:F3} | {worstFold,8:F3}");
        }

        // ---- The registered verdict, applied to every candidate ----
        // Same adjudicator the registered run uses: margin-adjusted DM at tau = 2%, fold signs,
        // VIX halves, block-bootstrap lower bound. Running it on an exploratory candidate does not
        // register anything — it answers "would this have cleared, had it been the variant?".
        var loader = new VolResidualBarLoader(configuration);
        var spxBars = await loader.LoadSpxOneMinuteBarsAsync(
            new DateOnly(2010, 1, 1), new DateOnly(2023, 12, 31), CancellationToken.None);
        var vix = await loader.LoadVixDailyClosesAsync(
            new DateOnly(2010, 1, 1), new DateOnly(2023, 12, 31), CancellationToken.None);

        var spxDays = VolatilityPresets.BuildSpxStudyTarget(
            TradingStuff.Tests.Volatility.SessionBars.Clock,
            TradingStuff.ResearchService.Volatility.HistoricalBarAdapter.ToIntradayBars(spxBars).ToList());
        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix);
        var foldResults = VolResidualSplitter.Split(rows, WalkForwardFold.Registered())
            .Where(VolResidualFoldRunner.CanScore)
            .Select(s => VolResidualFoldRunner.Run(s, includeExploratoryGbt: true))
            .ToList();

        Console.WriteLine("### --- registered adjudication (tau=2%, one-sided margin-adjusted p) ---");
        Console.WriteLine("### key | margin% | pass | DM stat | DM p | pass | folds+ | boot lower | halves | verdict");

        foreach (var key in keys.Where(k => k != gate))
        {
            var v = VolResidualAdjudication.Adjudicate(foldResults, key);
            if (v is null) { Console.WriteLine($"### {key,-12} | no verdict"); continue; }

            Console.WriteLine(
                $"### {key,-12} | {v.MarginPct,7:F3} | {(v.MarginPasses ? "Y" : "n"),4} | {v.DmStatistic,7:F3} | " +
                $"{v.DmPValue,6:F4} | {(v.DmPasses ? "Y" : "n"),4} | {v.FoldsPositive}/{v.FoldsTotal} | " +
                $"{v.BootstrapLower,11:E3} | {(v.VixHalvesPositive ? "Y" : "n"),6} | {v.Verdict}");
        }

        Assert.NotEmpty(daily);
    }

    private static double HalfMargin(
        IReadOnlyList<VolResidualDailyRow> daily, string key, string gate, string regime)
    {
        var half = daily.Where(d => d.VixRegime == regime).ToList();
        if (half.Count == 0) return double.NaN;
        return 100.0 * (1.0 - half.Average(d => d.Qlike[key]) / half.Average(d => d.Qlike[gate]));
    }
}
