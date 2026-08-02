using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Tests.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Pins every model's forecasts on a fixed synthetic fold, to the digit.
/// </summary>
/// <remarks>
/// <para>
/// A characterization test, not a correctness test: it asserts what the runner currently computes,
/// not what it ought to. The other suites in this folder cover ought — no look-ahead, train-only
/// retransformation, the QLIKE identities. This one exists so that the fold runner can be
/// restructured to accept new prediction methods without silently moving the numbers a registered
/// result was adjudicated on.
/// </para>
/// <para>
/// The values below were captured from the implementation as it stood when the model set was
/// HAR / VIX / HAR-X / CORRECTED / GBT. If a change to how methods are wired alters any of them,
/// that is the test doing its job and the change needs justifying — not re-baselining. A genuine
/// change of method, by contrast, adds a key here rather than moving one.
/// </para>
/// <para>
/// Fifteen decimal places on values near 4e-3 is roughly twelve significant figures: tight enough
/// that a reordered sum shows up, loose enough to survive the last bit.
/// </para>
/// </remarks>
public class VolResidualFoldRunnerCharacterizationTests
{
    private const string Calendar = SessionBars.CboeIndex;

    private static VolResidualFoldResult RunFixedFold()
    {
        var dates = SessionBars.TradingDates(120, from: new DateOnly(2015, 1, 5), calendar: Calendar);

        var spx = VolatilityPresets.BuildSpxStudyTarget(
            SessionBars.Clock,
            dates.SelectMany((d, i) => SessionBars.Wiggly(d, baseline: 100.0 + i * 0.1, calendar: Calendar)));

        var vix = dates
            .Select((d, i) => (Date: d, Value: 15.0 + Math.Sin(i * 0.3) * 3.0 + i * 0.02))
            .ToDictionary(x => x.Date, x => x.Value);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spx, vix);

        return VolResidualFoldRunner.Run(
            new VolResidualFoldSplit(new WalkForwardFold { Name = "characterization" },
                rows.Take(60).ToList(), rows.Skip(60).Take(10).ToList()),
            includeExploratoryGbt: true);
    }

    public static TheoryData<string, double, double, double> Golden() => new()
    {
        // key,                              first,                  last,                   mean
        { VolResidualModelKeys.Har,       0.004020769571247299,  0.0039551633219449154, 0.0039878463273596496 },
        { VolResidualModelKeys.Vix,       0.0042522100696300025, 0.0042733644728873334, 0.004265793788612424  },
        { VolResidualModelKeys.HarX,      0.0042547836721217713, 0.0042748569223205451, 0.0042855012728469652 },
        { VolResidualModelKeys.Corrected, 0.0040207778367011586, 0.0039551399839705932, 0.0039878580000450945 },
        { VolResidualModelKeys.Gbt,       0.0042591212498039018, 0.0042591212498039018, 0.0042591212498039027 },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void EachModelReproducesItsRecordedForecasts(string key, double first, double last, double mean)
    {
        var daily = RunFixedFold().DailyResults;

        Assert.Equal(first, daily[0].Forecasts[key], 15);
        Assert.Equal(last, daily[^1].Forecasts[key], 15);
        Assert.Equal(mean, daily.Average(d => d.Forecasts[key]), 15);
    }

    [Fact]
    public void TheFoldProducesExactlyTheExpectedModelSet()
    {
        var daily = RunFixedFold().DailyResults;

        Assert.Equal(10, daily.Count);

        // A new prediction method must show up here deliberately. Adding one silently — or losing
        // one to a wiring change — is the failure this asserts against.
        Assert.Equal(
            [VolResidualModelKeys.Corrected, VolResidualModelKeys.Gbt, VolResidualModelKeys.Har,
             VolResidualModelKeys.HarX, VolResidualModelKeys.Vix],
            daily[0].Forecasts.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ARegisteredRunFitsNoExploratoryModel()
    {
        var dates = SessionBars.TradingDates(120, from: new DateOnly(2015, 1, 5), calendar: Calendar);
        var spx = VolatilityPresets.BuildSpxStudyTarget(
            SessionBars.Clock,
            dates.SelectMany((d, i) => SessionBars.Wiggly(d, baseline: 100.0 + i * 0.1, calendar: Calendar)));
        var vix = dates
            .Select((d, i) => (Date: d, Value: 15.0 + Math.Sin(i * 0.3) * 3.0 + i * 0.02))
            .ToDictionary(x => x.Date, x => x.Value);
        var rows = VolResidualFeatureBuilder.BuildRawRows(spx, vix);

        var registered = VolResidualFoldRunner.Run(
            new VolResidualFoldSplit(new WalkForwardFold { Name = "registered" },
                rows.Take(60).ToList(), rows.Skip(60).Take(10).ToList()));

        // Rung 4 runs only if rung 3 passes the gate, and it has not. The default run must not
        // even fit it, let alone score it.
        Assert.DoesNotContain(VolResidualModelKeys.Gbt, registered.DailyResults[0].Forecasts.Keys);
        Assert.Equal(0, registered.GbtFloorHits);

        // And the registered models are bit-identical whether or not the exploratory rung ran
        // alongside them — fitting GBT must not perturb anything it sits next to.
        var withGbt = RunFixedFold();
        foreach (var key in registered.DailyResults[0].Forecasts.Keys)
        {
            for (var i = 0; i < registered.DailyResults.Count; i++)
            {
                Assert.Equal(
                    registered.DailyResults[i].Forecasts[key],
                    withGbt.DailyResults[i].Forecasts[key], 15);
            }
        }
    }
}
