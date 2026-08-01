using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Tests.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Pins constraint D: every model's retransformation (and, more broadly, every part of its fit) is
/// estimated on the TRAINING window only, per model, never touching evaluation data.
/// </summary>
/// <remarks>
/// The proof technique: fit one fold twice, with the SAME training block both times but two
/// DIFFERENT test blocks whose realized outcomes disagree wildly. If any model's fit — its
/// coefficients or its retransformation factor — depended on so much as one evaluation-window
/// actual, changing those actuals would change the forecast for days that did not change. Nothing
/// in a correct implementation may respond to that.
/// </remarks>
public class VolResidualFoldRunnerRetransformationTests
{
    private const string Calendar = SessionBars.CboeIndex;

    private static Dictionary<DateOnly, double> BuildVixDict(IReadOnlyList<DateOnly> dates)
    {
        var dict = new Dictionary<DateOnly, double>();
        for (var i = 0; i < dates.Count; i++)
        {
            dict[dates[i]] = 15.0 + Math.Sin(i * 0.3) * 3.0 + i * 0.02;
        }

        return dict;
    }

    [Fact]
    public void ForecastsForAnUnchangedTestDayAreUnaffectedByCorruptingAnotherTestDaysActual()
    {
        const int totalDays = 90;
        var dates = SessionBars.TradingDates(totalDays, from: new DateOnly(2015, 1, 5), calendar: Calendar);

        var spxDays = VolatilityPresets.BuildSpxStudyTarget(
            SessionBars.Clock,
            dates.SelectMany((d, i) => SessionBars.Wiggly(d, baseline: 100.0 + i * 0.1, calendar: Calendar)));
        var vix = BuildVixDict(dates);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix);
        Assert.True(rows.Count >= 45, "need enough rows for a 40-train/5-test split");

        var train = rows.Take(40).ToList();
        var testOriginal = rows.Skip(40).Take(5).ToList();

        // Corrupt every test-block actual except the first day's, by two full orders of magnitude.
        // If any model's coefficients or retransformation factor were fit on (or otherwise reacted
        // to) test-block actuals, the first day's forecast would move.
        var testCorrupted = testOriginal.Select((r, i) => i == 0
                ? r
                : r with { ActualVariance = r.ActualVariance * 100.0 })
            .ToList();

        var dummyFold = new WalkForwardFold
        {
            Name = "F-retransform-test",
            TrainStart = DateTime.MinValue, TrainEnd = DateTime.MinValue,
            ValidationStart = DateTime.MinValue, ValidationEnd = DateTime.MinValue,
            TestStart = DateTime.MinValue, TestEnd = DateTime.MinValue,
        };

        var resultOriginal = VolResidualFoldRunner.Run(new VolResidualFoldSplit(dummyFold, train, testOriginal));
        var resultCorrupted = VolResidualFoldRunner.Run(new VolResidualFoldSplit(dummyFold, train, testCorrupted));

        var firstDayOriginal = resultOriginal.DailyResults[0];
        var firstDayCorrupted = resultCorrupted.DailyResults[0];

        Assert.Equal(firstDayOriginal.Date, firstDayCorrupted.Date);

        foreach (var modelKey in firstDayOriginal.Forecasts.Keys)
        {
            Assert.Equal(firstDayOriginal.Forecasts[modelKey], firstDayCorrupted.Forecasts[modelKey]);
        }
    }

    [Fact]
    public void TheHarRetransformationFactorIsFitOnlyFromTrainingActualsNotFromRawTrainingForecasts()
    {
        // A direct pin of QlikeRetransformation.FitFactor's contract as VolResidualFoldRunner uses
        // it: the factor for one model must depend on THAT model's own training actuals and THAT
        // model's own raw training forecasts — swapping in a different (but equally valid-looking)
        // set of training actuals must change the factor, proving it is not a constant or a value
        // baked in from somewhere else.
        var rawForecasts = new List<double> { 0.0001, 0.0002, 0.00015, 0.0003 };
        var actualsA = new List<double> { 0.0002, 0.0004, 0.0003, 0.0006 }; // exactly 2x raw
        var actualsB = new List<double> { 0.0003, 0.0006, 0.00045, 0.0009 }; // exactly 3x raw

        var factorA = QlikeRetransformation.FitFactor(actualsA, rawForecasts);
        var factorB = QlikeRetransformation.FitFactor(actualsB, rawForecasts);

        Assert.Equal(2.0, factorA, precision: 9);
        Assert.Equal(3.0, factorB, precision: 9);
        Assert.NotEqual(factorA, factorB);
    }
}
