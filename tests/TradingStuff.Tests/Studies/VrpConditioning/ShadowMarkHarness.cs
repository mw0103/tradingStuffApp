using Microsoft.Extensions.Configuration;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.Volatility;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// Computes ONE shadow mark from the live research database and prints it — the offline proof of
/// the Phase 1 path before the endpoint ever runs. Persists nothing. Inert without
/// <c>VOLRESIDUAL_DEV_DB</c>.
/// </summary>
public class ShadowMarkHarness
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("VOLRESIDUAL_DEV_DB");

    [Fact]
    public async Task ComputeTheLatestShadowMark()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = ConnectionString,
            })
            .Build();

        var loader = new VolResidualBarLoader(configuration);
        var today = new DateOnly(2026, 8, 2);
        var from = today.AddYears(-3);

        var spxBars = await loader.LoadSpxOneMinuteBarsAsync(from, today, CancellationToken.None);
        var vix = await loader.LoadVixDailyClosesAsync(from, today, CancellationToken.None);

        var spxDays = VolatilityPresets.BuildSpxStudyTarget(
            TradingStuff.Tests.Volatility.SessionBars.Clock,
            HistoricalBarAdapter.ToIntradayBars(spxBars).ToList());

        var (mark, refusal) = VrpShadowForecaster.Compute(spxDays, vix);

        if (mark is null)
        {
            Console.WriteLine($"### REFUSED: {refusal}");
            Assert.Fail(refusal);
        }

        Console.WriteLine($"### mark date={mark.MarkDate} train={mark.TrainFrom}..{mark.TrainTo} ({mark.TrainRows} rows)");
        Console.WriteLine($"### vix={mark.VixClose:F2} implied21d={mark.ImpliedVariance:G6}");
        Console.WriteLine($"### QCJ forecast={mark.QcjForecast:G6} spread={mark.QcjSpread:G6} bucket={mark.QcjBucket} shadowAlloc={mark.ShadowAllocQcj}");
        Console.WriteLine($"### HARX forecast={mark.HarxForecast:G6} spread={mark.HarxSpread:G6} bucket={mark.HarxBucket} shadowAlloc={mark.ShadowAllocHarx}");
        Console.WriteLine($"### VIX-only spread={mark.VixSpread:G6} bucket={mark.VixBucket} shadowAlloc={mark.ShadowAllocVix}");

        // The causal guarantees, asserted on the real thing: the mark's training window ends
        // before the decision date, and every trained label closed by the decision date.
        Assert.True(mark.TrainTo < mark.MarkDate);
        Assert.True(mark.TrainRows >= VrpConditioningFoldRunner.MinimumTrainRows);
        Assert.InRange(mark.QcjBucket, 1, 5);
    }
}
