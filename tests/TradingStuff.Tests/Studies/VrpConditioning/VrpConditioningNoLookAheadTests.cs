using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// The property the whole study rests on: nothing a decision date sees can change when sessions
/// AFTER it are appended.
/// </summary>
/// <remarks>
/// Two levels, because they fail to different bugs. The feature-level test catches a window that
/// reaches forward. The forecast-level test additionally catches a fitted quantity — the elastic
/// net's lagged-residual input, the training-window quintile breakpoints, a retransformation factor —
/// that is computed over rows the decision could not have had. Neither catches a lagged-residual
/// feature that reads residuals not yet OBSERVABLE at t (they exist in both the short and long
/// series), which is why <see cref="ALaggedResidualIsOnlyAdmissibleOnceItsOwnLabelWindowHasClosed"/>
/// exists separately.
/// </remarks>
public class VrpConditioningNoLookAheadTests
{
    private static readonly DateOnly Start = new(2016, 1, 4);

    [Fact]
    public void AppendingFutureSessionsChangesNoFieldOfAnExistingDecisionDate()
    {
        var shortDates = VrpConditioningFixture.TradingDates(160, Start);
        var longDates = VrpConditioningFixture.TradingDates(230, Start);

        var shortRows = VrpConditioningFixture.Rows(shortDates).ToDictionary(r => r.Date);
        var longRows = VrpConditioningFixture.Rows(longDates).ToDictionary(r => r.Date);

        Assert.NotEmpty(shortRows);
        Assert.True(longRows.Count > shortRows.Count, "the longer series must produce strictly more decision dates.");

        foreach (var (date, before) in shortRows)
        {
            var after = longRows[date];

            // Record equality would compare DayOfWeekDummies by reference, so every field is named.
            Assert.Equal(before.LabelFrom, after.LabelFrom);
            Assert.Equal(before.LabelTo, after.LabelTo);
            Assert.Equal(before.LabelSessions, after.LabelSessions);
            Assert.Equal(before.LabelCumulativeVariance, after.LabelCumulativeVariance);
            Assert.Equal(before.VixLevel, after.VixLevel);
            Assert.Equal(before.ImpliedVariance, after.ImpliedVariance);
            Assert.Equal(before.LogImpliedVariance, after.LogImpliedVariance);
            Assert.Equal(before.LogRv, after.LogRv);
            Assert.Equal(before.MeanLogRv5, after.MeanLogRv5);
            Assert.Equal(before.MeanLogRv22, after.MeanLogRv22);
            Assert.Equal(before.DayOfWeekDummies, after.DayOfWeekDummies);
            Assert.Equal(before.DaysToMonthlyOpex, after.DaysToMonthlyOpex);
            Assert.Equal(before.Vix5DayChange, after.Vix5DayChange);
            Assert.Equal(before.Spx1DayLogReturn, after.Spx1DayLogReturn);
            Assert.Equal(before.SpxDrawdown22, after.SpxDrawdown22);
        }
    }

    [Fact]
    public void AppendingFutureSessionsChangesNoForecastSpreadOrBucketOfAnAlreadyScoredDay()
    {
        var shortDates = VrpConditioningFixture.TradingDates(250, Start);
        var longDates = VrpConditioningFixture.TradingDates(300, Start);

        // A fold whose TEST window extends past where the short series can produce labels, so the
        // longer series genuinely adds scored days INSIDE the test block rather than after it.
        var fold = new WalkForwardFold
        {
            Name = "T1",
            TrainStart = longDates[0].ToDateTime(TimeOnly.MinValue),
            TrainEnd = longDates[159].ToDateTime(TimeOnly.MinValue),
            ValidationStart = longDates[160].ToDateTime(TimeOnly.MinValue),
            ValidationEnd = longDates[179].ToDateTime(TimeOnly.MinValue),
            TestStart = longDates[180].ToDateTime(TimeOnly.MinValue),
            TestEnd = longDates[299].ToDateTime(TimeOnly.MinValue),
        };

        var before = RunFold(shortDates, fold);
        var after = RunFold(longDates, fold);

        Assert.True(after.DailyResults.Count > before.DailyResults.Count,
            "the longer series must add scored days inside the test block, or this test proves nothing.");

        // The training block is identical in both runs, so every fitted quantity must be too.
        foreach (var arm in VrpConditioningArms.All)
        {
            Assert.Equal(before.TrainSpreadBreakpoints[arm], after.TrainSpreadBreakpoints[arm]);
        }

        var afterByDate = after.DailyResults.ToDictionary(d => d.Date);

        foreach (var day in before.DailyResults)
        {
            var later = afterByDate[day.Date];

            foreach (var arm in VrpConditioningArms.All)
            {
                Assert.Equal(day.Forecasts[arm], later.Forecasts[arm]);
                Assert.Equal(day.Qlike[arm], later.Qlike[arm]);
                Assert.Equal(day.Spread[arm], later.Spread[arm]);
                Assert.Equal(day.Bucket[arm], later.Bucket[arm]);
            }

            Assert.Equal(day.RealizedVariance, later.RealizedVariance);
            Assert.Equal(day.PremiumCollected, later.PremiumCollected);
            Assert.Equal(day.PnlPerVegaNotional, later.PnlPerVegaNotional);
        }
    }

    [Fact]
    public void ALaggedResidualIsOnlyAdmissibleOnceItsOwnLabelWindowHasClosed()
    {
        // The trap this test exists for: the parent study walks residuals day by day, enqueueing
        // each row's residual immediately after reading the queue. That is causal for a 1-day label
        // and a leak for a 21-day one — the residual of decision date s is unknowable until s's
        // label finishes, 21 sessions later.
        var dates = VrpConditioningFixture.TradingDates(200, Start);
        var rows = VrpConditioningFixture.Rows(dates);

        // Residuals that make a leak unmissable: every row carries a large, distinct value, so the
        // mean at t identifies exactly which rows fed it.
        var residualByDate = rows.ToDictionary(r => r.Date, r => 1000.0 + rows.FindIndex(x => x.Date == r.Date));

        var means = VrpConditioningFoldRunner.LaggedResidualMeans(rows, residualByDate);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            var admissible = rows
                .Where(candidate => candidate.LabelTo <= row.Date)
                .Select(candidate => residualByDate[candidate.Date])
                .ToList();

            var expected = admissible.Count == 0
                ? 0.0
                : admissible.TakeLast(VrpConditioningHorizon.WeeklyWindow).Average();

            Assert.Equal(expected, means[row.Date], 12);

            // And, stated as the property rather than as arithmetic: no residual from a decision
            // date whose label is still running may have contributed.
            var stillRunning = rows.Where(c => c.Date < row.Date && c.LabelTo > row.Date).ToList();
            foreach (var c in stillRunning)
            {
                Assert.DoesNotContain(residualByDate[c.Date], admissible);
            }

            // The immediately preceding decision date is ALWAYS still running (its label has 20 of
            // 21 sessions left), so a parent-style queue would have included it here.
            if (i > 0)
            {
                Assert.True(rows[i - 1].LabelTo > row.Date);
                Assert.NotEqual(residualByDate[rows[i - 1].Date], means[row.Date]);
            }
        }
    }

    private static VrpConditioningFoldResult RunFold(IReadOnlyList<DateOnly> dates, WalkForwardFold fold)
    {
        var rows = VrpConditioningFixture.Rows(dates);
        var split = VolResidualSplitterFacade.Split(rows, fold);

        Assert.True(VrpConditioningFoldRunner.CanScore(split),
            $"fold {fold.Name} is not scoreable on this fixture: {split.Train.Count} train / {split.Test.Count} test.");

        return VrpConditioningFoldRunner.Run(split);
    }

    /// <summary>Keeps the splitter call — and its purge — in one place across these tests.</summary>
    private static class VolResidualSplitterFacade
    {
        public static (WalkForwardFold Fold, List<VrpConditioningRawRow> Train, List<VrpConditioningRawRow> Test) Split(
            IReadOnlyList<VrpConditioningRawRow> rows, WalkForwardFold fold) =>
            TradingStuff.ResearchService.Studies.VolResidual.VolResidualSplitter
                .Split(rows, r => r.Date, [fold], VrpConditioningHorizon.PurgeRows)
                .Single();
    }
}
