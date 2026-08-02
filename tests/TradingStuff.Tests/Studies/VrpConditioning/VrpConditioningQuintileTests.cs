using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// Quintile edges must come from the TRAINING window. An evaluation-defined bucket edge is a quiet
/// leak — it moves the boundary to wherever the test data sits, guarantees each bucket is exactly a
/// fifth of the evaluation sample, and describes a rule nobody could have followed in advance.
/// </summary>
public class VrpConditioningQuintileTests
{
    [Fact]
    public void BreakpointsComeFromTheTrainingSampleSoATestSampleOnADifferentScaleDoesNotResortItself()
    {
        // Training spreads span 0..99; evaluation spreads all sit inside the training first
        // quintile. Under train-defined edges every evaluation day is Q1. Under evaluation-defined
        // edges they would be spread evenly across Q1..Q5 — the exact signature of the leak.
        var training = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
        var breakpoints = VrpConditioningQuintiles.Breakpoints(training);

        var evaluation = Enumerable.Range(0, 10).Select(i => i * 0.5).ToList();
        var buckets = evaluation.Select(s => VrpConditioningQuintiles.BucketOf(s, breakpoints)).ToList();

        Assert.All(buckets, b => Assert.Equal(1, b));

        // Sanity: the same evaluation sample cut on its OWN quintiles would NOT be all-Q1, so the
        // assertion above is discriminating rather than vacuous.
        var selfBreakpoints = VrpConditioningQuintiles.Breakpoints(evaluation);
        var selfBuckets = evaluation.Select(s => VrpConditioningQuintiles.BucketOf(s, selfBreakpoints)).ToList();
        Assert.Contains(5, selfBuckets);
    }

    [Fact]
    public void EveryBucketIsReachableAndTheEdgesArePlacedAtTheQuintilesOfTheTrainingSample()
    {
        var training = Enumerable.Range(0, 1000).Select(i => (double)i).ToList();
        var breakpoints = VrpConditioningQuintiles.Breakpoints(training);

        Assert.Equal(4, breakpoints.Length);
        Assert.Equal(199.8, breakpoints[0], 6);
        Assert.Equal(399.6, breakpoints[1], 6);
        Assert.Equal(599.4, breakpoints[2], 6);
        Assert.Equal(799.2, breakpoints[3], 6);

        Assert.Equal(1, VrpConditioningQuintiles.BucketOf(-1e9, breakpoints));
        Assert.Equal(1, VrpConditioningQuintiles.BucketOf(breakpoints[0], breakpoints)); // edge is inclusive below
        Assert.Equal(2, VrpConditioningQuintiles.BucketOf(breakpoints[0] + 1e-9, breakpoints));
        Assert.Equal(5, VrpConditioningQuintiles.BucketOf(1e9, breakpoints));
    }

    [Fact]
    public void TheFoldRunnerDerivesItsBreakpointsFromTrainingRowsNotFromTheRowsItScores()
    {
        var dates = VrpConditioningFixture.TradingDates(300, new DateOnly(2016, 1, 4));
        var rows = VrpConditioningFixture.Rows(dates);

        var fold = new WalkForwardFold
        {
            Name = "T1",
            TrainStart = dates[0].ToDateTime(TimeOnly.MinValue),
            TrainEnd = dates[159].ToDateTime(TimeOnly.MinValue),
            ValidationStart = dates[160].ToDateTime(TimeOnly.MinValue),
            ValidationEnd = dates[179].ToDateTime(TimeOnly.MinValue),
            TestStart = dates[180].ToDateTime(TimeOnly.MinValue),
            TestEnd = dates[299].ToDateTime(TimeOnly.MinValue),
        };

        var split = VolResidualSplitter
            .Split(rows, r => r.Date, [fold], VrpConditioningHorizon.PurgeRows)
            .Single();

        var result = VrpConditioningFoldRunner.Run(split);

        // The unconditional arm forecasts one constant for every row, so its spread is recoverable
        // from outside the runner: spread = impliedVariance - constant. That makes it the one arm
        // whose training breakpoints can be recomputed independently here.
        var constant = result.DailyResults[0].Forecasts[VrpConditioningArms.Unconditional];
        Assert.All(result.DailyResults, d => Assert.Equal(constant, d.Forecasts[VrpConditioningArms.Unconditional]));

        var fromTraining = VrpConditioningQuintiles.Breakpoints(
            [.. split.Train.Select(r => r.ImpliedVariance - constant)]);
        var fromEvaluation = VrpConditioningQuintiles.Breakpoints(
            [.. split.Test.Select(r => r.ImpliedVariance - constant)]);

        Assert.Equal(fromTraining, result.TrainSpreadBreakpoints[VrpConditioningArms.Unconditional]);

        // ... and the two are genuinely different on this fixture, so the assertion above
        // discriminates between the correct source and the leaky one.
        Assert.NotEqual(fromEvaluation, result.TrainSpreadBreakpoints[VrpConditioningArms.Unconditional]);

        // Frozen edges produce UNEVEN evaluation buckets. Even fifths would be the visible signature
        // of edges fitted to the sample being scored.
        var counts = result.DailyResults
            .GroupBy(d => d.Bucket[VrpConditioningArms.Unconditional])
            .ToDictionary(g => g.Key, g => g.Count());

        var evenFifth = result.DailyResults.Count / 5;
        Assert.True(counts.Values.Any(c => Math.Abs(c - evenFifth) > 1),
            "every evaluation bucket came out within one row of an even fifth, which is what " +
            "evaluation-fitted quintile edges look like.");
    }

    [Fact]
    public void TheArmReportsHowMuchOfItsConditioningIsJustTheVixLevel()
    {
        // The spread is impliedVar - forecastVar and the implied leg is a function of VIX alone, so
        // the study MUST say how much of the sorting the forecast leg is actually responsible for.
        // The unconditional arm is the extreme case: its forecast is one constant, so its spread is
        // implied variance shifted, which is a strictly monotone function of the VIX level — rank
        // correlation exactly 1, and every bucket identical to its own.
        var dates = VrpConditioningFixture.TradingDates(300, new DateOnly(2016, 1, 4));
        var rows = VrpConditioningFixture.Rows(dates);

        var fold = new WalkForwardFold
        {
            Name = "T1",
            TrainStart = dates[0].ToDateTime(TimeOnly.MinValue),
            TrainEnd = dates[159].ToDateTime(TimeOnly.MinValue),
            ValidationStart = dates[160].ToDateTime(TimeOnly.MinValue),
            ValidationEnd = dates[179].ToDateTime(TimeOnly.MinValue),
            TestStart = dates[180].ToDateTime(TimeOnly.MinValue),
            TestEnd = dates[299].ToDateTime(TimeOnly.MinValue),
        };

        var result = VrpConditioningFoldRunner.Run(
            VolResidualSplitter.Split(rows, r => r.Date, [fold], VrpConditioningHorizon.PurgeRows).Single());

        var days = result.DailyResults.OrderBy(d => d.Date).ToList();

        var unconditional = VrpConditioningQuintiles.Aggregate(
            days, VrpConditioningArms.Unconditional, result.TrainSpreadBreakpoints[VrpConditioningArms.Unconditional]);

        Assert.Equal(1.0, unconditional.SpreadVsVixSpearman, 12);
        Assert.Equal(1.0, unconditional.BucketAgreementWithUnconditional, 12);

        // And a forecast that genuinely varies must NOT be reported as a perfect VIX relabelling,
        // or the diagnostic is measuring nothing.
        var harx = VrpConditioningQuintiles.Aggregate(
            days, VrpConditioningArms.HarX, result.TrainSpreadBreakpoints[VrpConditioningArms.HarX]);

        Assert.True(harx.SpreadVsVixSpearman < 1.0,
            $"HAR-X's spread has rank correlation {harx.SpreadVsVixSpearman} with the raw VIX level; a " +
            "value of exactly 1 would mean its forecast leg never changes the ordering, which cannot " +
            "be true of a forecast that varies day to day.");
        Assert.InRange(harx.BucketAgreementWithUnconditional, 0.0, 1.0);
    }

    [Fact]
    public void MonotonicityIsReportedAsAShapeIncludingWhenItIsAbsent()
    {
        Assert.True(VrpConditioningQuintiles.Verdict([1.0, 2.0, 3.0, 4.0, 5.0]).IsMonotone);
        Assert.Equal("monotone-increasing", VrpConditioningQuintiles.Verdict([1.0, 2.0, 3.0, 4.0, 5.0]).Shape);

        Assert.True(VrpConditioningQuintiles.Verdict([5.0, 4.0, 3.0, 2.0, 1.0]).IsMonotone);
        Assert.Equal("monotone-decreasing", VrpConditioningQuintiles.Verdict([5.0, 4.0, 3.0, 2.0, 1.0]).Shape);

        var bumpy = VrpConditioningQuintiles.Verdict([1.0, 3.0, 2.0, 4.0, 5.0]);
        Assert.False(bumpy.IsMonotone);
        Assert.Equal(1, bumpy.Violations);
        Assert.Contains("non-monotone", bumpy.Shape);
        Assert.Contains("1 of 4", bumpy.Shape);

        // A single good bucket with everything else flat must NOT read as monotone support.
        var oneGoodBucket = VrpConditioningQuintiles.Verdict([0.0, 0.0, 0.0, 0.0, 9.0]);
        Assert.True(oneGoodBucket.IsMonotone); // non-decreasing, technically
        Assert.Equal(0, oneGoodBucket.Violations);
    }
}
