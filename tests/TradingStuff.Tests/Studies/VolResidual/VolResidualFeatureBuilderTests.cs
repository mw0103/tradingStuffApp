using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Tests.Volatility;
using TradingStuff.Volatility;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Pins the registered Tier-0/Tier-1 feature definitions and, most importantly, the property the
/// entire study depends on: nothing computed for day D can change when bars for days AFTER D are
/// appended to the input.
/// </summary>
public class VolResidualFeatureBuilderTests
{
    private const string Calendar = SessionBars.CboeIndex;

    private static Dictionary<DateOnly, double> BuildVixDict(IReadOnlyList<DateOnly> dates)
    {
        var dict = new Dictionary<DateOnly, double>();
        for (var i = 0; i < dates.Count; i++)
        {
            // A deterministic, slowly-varying series - never zero, never constant (a constant VIX
            // would make the z-scored divergence feature degenerate).
            dict[dates[i]] = 15.0 + Math.Sin(i * 0.3) * 3.0 + i * 0.02;
        }

        return dict;
    }

    private static List<RealizedVolatilityDay> BuildSpxDays(IReadOnlyList<DateOnly> dates) =>
        VolatilityPresets.BuildSpxStudyTarget(
            SessionBars.Clock,
            dates.SelectMany((d, i) => SessionBars.Wiggly(d, baseline: 100.0 + i * 0.1, calendar: Calendar)));

    // ---------- day-of-week and opex calendar ----------

    [Fact]
    public void DayOfWeekDummiesMatchTheRegisteredDaysAcrossARealTradingWeek()
    {
        // Six full trading weeks (Mon-Fri) starting 2016-01-04 (a Monday), plus enough lead-in
        // days already covered by the 22-day warmup — pick a window comfortably past the warmup
        // and read off every weekday's dummy vector in one pass.
        var dates = SessionBars.TradingDates(50, from: new DateOnly(2016, 1, 4), calendar: Calendar);
        var spxDays = BuildSpxDays(dates);
        var vix = BuildVixDict(dates);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix).ToDictionary(r => r.Date);

        foreach (var date in dates.Where(d => rows.ContainsKey(d)))
        {
            var dummies = rows[date].DayOfWeekDummies;
            Assert.Equal(4, dummies.Length);

            double[] expected = date.DayOfWeek switch
            {
                DayOfWeek.Monday => [0, 0, 0, 0],
                DayOfWeek.Tuesday => [1, 0, 0, 0],
                DayOfWeek.Wednesday => [0, 1, 0, 0],
                DayOfWeek.Thursday => [0, 0, 1, 0],
                DayOfWeek.Friday => [0, 0, 0, 1],
                _ => throw new InvalidOperationException($"Unexpected trading-day weekday: {date.DayOfWeek}"),
            };

            Assert.Equal(expected, dummies);
        }

        // Sanity: this window actually contains at least one of each weekday, or the assertions
        // above would all pass vacuously against a degenerate fixture.
        Assert.True(dates.Select(d => d.DayOfWeek).Distinct().Count() == 5);
    }

    [Theory]
    [InlineData("2024-01-01", 18)]  // Monday; next 3rd Friday is 2024-01-19
    [InlineData("2024-01-19", 0)]   // ON the 3rd Friday itself
    [InlineData("2024-01-20", 27)]  // day after; rolls to Feb's 3rd Friday (2024-02-16)
    [InlineData("2024-02-16", 0)]
    public void DaysToNextThirdFridayMatchesHandComputedExpirationDates(string date, int expectedDays) =>
        Assert.Equal(expectedDays, VolResidualFeatureBuilder.DaysToNextThirdFriday(DateOnly.Parse(date)));

    // ---------- warmup / coverage ----------

    [Fact]
    public void ProducesNoRowsUntilTwentyTwoPriorCompleteSessionsExist()
    {
        var dates = SessionBars.TradingDates(21, from: new DateOnly(2016, 1, 4), calendar: Calendar);
        var spxDays = BuildSpxDays(dates);
        var vix = BuildVixDict(dates);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix);

        Assert.Empty(rows);
    }

    [Fact]
    public void ProducesOneRowPerDayOnceTheMonthlyWindowAndVixAreBothAvailable()
    {
        const int totalDays = 40;
        var dates = SessionBars.TradingDates(totalDays, from: new DateOnly(2016, 1, 4), calendar: Calendar);
        var spxDays = BuildSpxDays(dates);
        var vix = BuildVixDict(dates);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix);

        Assert.Equal(totalDays - 22, rows.Count);
        Assert.Equal(dates[22], rows[0].Date);
        Assert.Equal(dates[^1], rows[^1].Date);
    }

    [Fact]
    public void MissingVixCoverageForAnOtherwiseUsableDaySkipsOnlyThatDay()
    {
        const int totalDays = 30;
        var dates = SessionBars.TradingDates(totalDays, from: new DateOnly(2016, 1, 4), calendar: Calendar);
        var spxDays = BuildSpxDays(dates);
        var vix = BuildVixDict(dates);

        // Remove VIX coverage for the date one day before the first candidate row, so that row's
        // "prior close VIX" lookup misses.
        vix.Remove(dates[21]);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix);

        Assert.DoesNotContain(rows, r => r.Date == dates[22]);
        Assert.Contains(rows, r => r.Date == dates[23]); // the next day is unaffected
    }

    // ---------- THE property test: no look-ahead ----------

    [Fact]
    public void AppendingFutureBarsNeverChangesAnAlreadyComputedRow()
    {
        const int baseDays = 40;
        const int extraFutureDays = 10;

        var allDates = SessionBars.TradingDates(
            baseDays + extraFutureDays, from: new DateOnly(2015, 1, 5), calendar: Calendar);
        var baseDates = allDates.Take(baseDays).ToList();

        // Same VIX dictionary for both runs — extra future entries an early row could never look
        // up are harmless; this isolates the test to what the SPX bar history affects.
        var vix = BuildVixDict(allDates);

        var spxDaysBefore = BuildSpxDays(baseDates);
        var spxDaysAfter = BuildSpxDays(allDates); // the SAME baseDates' bars, plus 10 more future days

        var rowsBefore = VolResidualFeatureBuilder.BuildRawRows(spxDaysBefore, vix);
        var rowsAfter = VolResidualFeatureBuilder.BuildRawRows(spxDaysAfter, vix);

        Assert.NotEmpty(rowsBefore);
        Assert.True(rowsAfter.Count > rowsBefore.Count, "appending future days should add NEW rows, or this test proves nothing");

        var afterByDate = rowsAfter.ToDictionary(r => r.Date);

        foreach (var before in rowsBefore)
        {
            var after = afterByDate[before.Date];

            Assert.Equal(before.ActualVariance, after.ActualVariance);
            Assert.Equal(before.LogRvDMinus1, after.LogRvDMinus1);
            Assert.Equal(before.MeanLogRv5, after.MeanLogRv5);
            Assert.Equal(before.MeanLogRv22, after.MeanLogRv22);
            Assert.Equal(before.DayOfWeekDummies, after.DayOfWeekDummies);
            Assert.Equal(before.DaysToMonthlyOpex, after.DaysToMonthlyOpex);
            Assert.Equal(before.LogPriorVix2, after.LogPriorVix2);
            Assert.Equal(before.Vix5DayChange, after.Vix5DayChange);
            Assert.Equal(before.Spx1DayLogReturn, after.Spx1DayLogReturn);
        }
    }

    [Fact]
    public void AppendingFutureTestSamplesNeverChangesAnAlreadyComputedForecast()
    {
        // The same property, one level up: through the fold runner's model fitting and the
        // causal rolling HAR-X-residual feature, not just raw feature construction.
        const int totalDays = 90;
        var dates = SessionBars.TradingDates(totalDays, from: new DateOnly(2015, 1, 5), calendar: Calendar);
        var spxDays = BuildSpxDays(dates);
        var vix = BuildVixDict(dates);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spxDays, vix);
        Assert.True(rows.Count >= 50, "need enough rows for a 40-train/10-test split");

        var train = rows.Take(40).ToList();
        var testShort = rows.Skip(40).Take(5).ToList();
        var testExtended = rows.Skip(40).Take(10).ToList(); // same first 5 days, plus 5 MORE FUTURE test days

        var dummyFold = new TradingStuff.Volatility.Forecasting.WalkForwardFold
        {
            Name = "F-test",
            TrainStart = DateTime.MinValue, TrainEnd = DateTime.MinValue,
            ValidationStart = DateTime.MinValue, ValidationEnd = DateTime.MinValue,
            TestStart = DateTime.MinValue, TestEnd = DateTime.MinValue,
        };

        var resultShort = VolResidualFoldRunner.Run(new VolResidualFoldSplit(dummyFold, train, testShort));
        var resultExtended = VolResidualFoldRunner.Run(new VolResidualFoldSplit(dummyFold, train, testExtended));

        var extendedByDate = resultExtended.DailyResults.ToDictionary(d => d.Date);

        foreach (var shortDay in resultShort.DailyResults)
        {
            var extendedDay = extendedByDate[shortDay.Date];

            foreach (var modelKey in shortDay.Forecasts.Keys)
            {
                Assert.Equal(shortDay.Forecasts[modelKey], extendedDay.Forecasts[modelKey]);
                Assert.Equal(shortDay.Qlike[modelKey], extendedDay.Qlike[modelKey]);
            }
        }
    }
}
