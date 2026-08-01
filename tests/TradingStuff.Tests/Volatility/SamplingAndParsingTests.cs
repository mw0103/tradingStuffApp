using TradingStuff.Volatility;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins previous-tick sampling, the realized-moment estimators, and the name-driven CSV parser.
/// </summary>
/// <remarks>
/// Two of these are silent-failure surfaces. Sampling past the last real print manufactures
/// zero returns, which understates variance while inflating the return count so a half-empty
/// session looks complete; and a positional CSV parser that reads the ask out of the bid
/// column produces entirely plausible numbers. Both are asserted directly.
/// </remarks>
public class SamplingAndParsingTests
{
    private static readonly DateTime Day = new(2024, 3, 4);
    private static DateTime At(int hour, int minute) => Day.AddHours(hour).AddMinutes(minute);

    // ---------- sampling: validation ----------

    [Fact]
    public void SampleRejectsMismatchedOrMissingInput()
    {
        List<DateTime> times = [At(9, 31)];
        List<double> prices = [100.0];

        Assert.Throws<ArgumentNullException>(() => BarResampler.Sample(null!, prices, At(9, 30), At(16, 0), 5, 0));
        Assert.Throws<ArgumentNullException>(() => BarResampler.Sample(times, null!, At(9, 30), At(16, 0), 5, 0));
        Assert.Throws<ArgumentException>(() =>
            BarResampler.Sample(times, [1.0, 2.0], At(9, 30), At(16, 0), 5, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SampleRejectsANonPositiveInterval(int interval) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BarResampler.Sample([At(9, 31)], [100.0], At(9, 30), At(16, 0), interval, 0));

    [Fact]
    public void SamplingNothingYieldsNothing()
    {
        var sampled = BarResampler.Sample([], [], At(9, 30), At(16, 0), 5, 0);

        Assert.Empty(sampled.Prices);
        Assert.Empty(sampled.Times);
        Assert.Equal(0, sampled.StaleSamples);
    }

    // ---------- sampling: previous tick ----------

    [Fact]
    public void EachGridPointTakesTheLastBarClosedAtOrBeforeIt()
    {
        // Bars closing every minute 09:31..09:46 at 100, 101, ...
        var times = Enumerable.Range(0, 16).Select(i => At(9, 31 + i)).ToList();
        var prices = Enumerable.Range(0, 16).Select(i => 100.0 + i).ToList();

        var sampled = BarResampler.Sample(times, prices, At(9, 30), At(9, 45), 5, 0);

        // Grid 09:30 (no bar yet), 09:35, 09:40, 09:45.
        Assert.Equal(3, sampled.Prices.Count);
        Assert.Equal([104.0, 109.0, 114.0], sampled.Prices);
        Assert.Equal([At(9, 35), At(9, 40), At(9, 45)], sampled.Times);
        Assert.Equal(0, sampled.StaleSamples);
    }

    [Fact]
    public void TheOffsetShiftsTheWholeGrid()
    {
        var times = Enumerable.Range(0, 16).Select(i => At(9, 31 + i)).ToList();
        var prices = Enumerable.Range(0, 16).Select(i => 100.0 + i).ToList();

        var sampled = BarResampler.Sample(times, prices, At(9, 30), At(9, 45), 5, offsetMinutes: 2);

        // Grid 09:32, 09:37, 09:42, then the appended real end at 09:45.
        Assert.Equal([101.0, 106.0, 111.0, 114.0], sampled.Prices);
    }

    [Fact]
    public void AGapReusesThePreviousPriceAndIsCounted()
    {
        // Nothing prints between 09:35 and 09:46.
        var sampled = BarResampler.Sample(
            [At(9, 35), At(9, 46)], [100.0, 101.0], At(9, 30), At(9, 45), 5, 0);

        Assert.True(sampled.StaleSamples > 0);
        // The reused points all carry the last known price.
        Assert.All(sampled.Prices, p => Assert.Equal(100.0, p));
    }

    [Fact]
    public void AStaleGridPointStillProducesAZeroReturn()
    {
        var sampled = BarResampler.Sample(
            [At(9, 35), At(9, 46)], [100.0, 101.0], At(9, 30), At(9, 45), 5, 0);

        // This is exactly why StaleSamples is reported: the zero returns look like calm.
        Assert.All(sampled.LogReturns(), r => Assert.Equal(0.0, r));
    }

    [Fact]
    public void TheGridStopsAtTheLastRealPrint()
    {
        // Bars stop at 11:00 but the scheduled close is 16:00.
        var times = Enumerable.Range(0, 91).Select(i => At(9, 30 + i)).ToList();
        var prices = Enumerable.Range(0, 91).Select(i => 100.0 + (i % 5) * 0.25).ToList();

        var sampled = BarResampler.Sample(times, prices, At(9, 30), At(16, 0), 5, 0);

        // Extending to 16:00 would repeat the 11:00 price for five more hours, padding the
        // return count and burying the session's real variance in manufactured zeros.
        Assert.Equal(At(11, 0), sampled.Times[^1]);
        Assert.Equal(0, sampled.StaleSamples);
    }

    [Fact]
    public void TheFinalPrintIsAppendedWhenTheGridFallsShort()
    {
        // Bars run to 09:47, which the 5-minute grid from 09:30 does not land on.
        var times = Enumerable.Range(0, 18).Select(i => At(9, 30 + i)).ToList();
        var prices = Enumerable.Range(0, 18).Select(i => 100.0 + i).ToList();

        var sampled = BarResampler.Sample(times, prices, At(9, 30), At(16, 0), 5, 0);

        // The closing move is a material share of daily variance and must not be dropped.
        Assert.Equal(At(9, 47), sampled.Times[^1]);
        Assert.Equal(117.0, sampled.Prices[^1]);
    }

    [Fact]
    public void GridPointsBeforeTheFirstPrintAreSkipped()
    {
        var sampled = BarResampler.Sample([At(10, 0)], [100.0], At(9, 30), At(10, 0), 5, 0);

        Assert.Single(sampled.Prices);
        Assert.Equal(At(10, 0), sampled.Times[0]);
    }

    [Fact]
    public void LogReturnsAreDifferencesOfConsecutiveSampledPrices()
    {
        var session = new SampledSession();
        session.Prices.AddRange([100.0, 101.0, 99.0]);

        Assert.Equal([Math.Log(101.0 / 100.0), Math.Log(99.0 / 101.0)], session.LogReturns());
    }

    [Fact]
    public void ASinglePriceHasNoReturns()
    {
        var session = new SampledSession();
        session.Prices.Add(100.0);

        Assert.Empty(session.LogReturns());
    }

    // ---------- timestamp convention ----------

    [Fact]
    public void BarStartTimestampsAreShiftedToTheirClose()
    {
        List<IntradayBar> bars = [new(At(9, 30), 100, 100, 100, 100.5), new(At(9, 31), 100, 100, 100, 101.5)];

        BarResampler.ToCloseSeries(bars, BarTimestampConvention.BarStart, 1, out var times, out var prices);

        // A bar stamped 09:30 on a one-minute grid closed at 09:31. Getting this wrong
        // shifts every sampled price by one bar.
        Assert.Equal([At(9, 31), At(9, 32)], times);
        Assert.Equal([100.5, 101.5], prices);
    }

    [Fact]
    public void BarEndTimestampsArePassedThrough()
    {
        List<IntradayBar> bars = [new(At(9, 31), 100, 100, 100, 100.5)];

        BarResampler.ToCloseSeries(bars, BarTimestampConvention.BarEnd, 1, out var times, out _);

        Assert.Equal([At(9, 31)], times);
    }

    [Fact]
    public void TheShiftFollowsTheBarInterval()
    {
        List<IntradayBar> bars = [new(At(9, 30), 100, 100, 100, 100.5)];

        BarResampler.ToCloseSeries(bars, BarTimestampConvention.BarStart, 5, out var times, out _);

        Assert.Equal([At(9, 35)], times);
    }

    // ---------- realized moments ----------

    [Fact]
    public void EstimatorRejectsMissingReturns() =>
        Assert.Throws<ArgumentNullException>(() => RealizedVolatilityEstimator.FromReturns(null!));

    [Fact]
    public void QuarticityUsesTheStandardScaling()
    {
        List<double> returns = [0.01, -0.02];
        var sumFourth = Math.Pow(0.01, 4) + Math.Pow(0.02, 4);

        Assert.Equal((2 / 3.0) * sumFourth, RealizedVolatilityEstimator.FromReturns(returns).RealizedQuarticity, 15);
    }

    [Fact]
    public void ZeroReturnsContributeToNeitherSemivariance()
    {
        // The split is on strict sign, so a flat interval belongs to neither side.
        var m = RealizedVolatilityEstimator.FromReturns([0.0, 0.0, 0.01]);

        Assert.Equal(1e-4, m.UpsideVariance, 15);
        Assert.Equal(0.0, m.DownsideVariance);
        Assert.Equal(1e-4, m.RealizedVariance, 15);
    }

    [Fact]
    public void ContinuousVarianceIsTheLesserOfTheTwoMeasures()
    {
        Assert.Equal(2.0, new RealizedMoments { RealizedVariance = 3.0, BipowerVariation = 2.0 }.ContinuousVariance);
        Assert.Equal(3.0, new RealizedMoments { RealizedVariance = 3.0, BipowerVariation = 5.0 }.ContinuousVariance);
    }

    [Fact]
    public void TheJumpComponentFloorsAtZero()
    {
        Assert.Equal(1.0, new RealizedMoments { RealizedVariance = 3.0, BipowerVariation = 2.0 }.JumpVariation);
        // Bipower can exceed RV from sampling noise alone; a negative jump is not meaningful.
        Assert.Equal(0.0, new RealizedMoments { RealizedVariance = 3.0, BipowerVariation = 5.0 }.JumpVariation);
    }

    [Fact]
    public void SignedAsymmetryIsPositiveWhenDownsideDominates()
    {
        Assert.Equal(1.0, new RealizedMoments { DownsideVariance = 3.0, UpsideVariance = 2.0 }.SignedVarianceAsymmetry);
        Assert.Equal(-1.0, new RealizedMoments { DownsideVariance = 2.0, UpsideVariance = 3.0 }.SignedVarianceAsymmetry);
    }

    [Fact]
    public void AveragingAcrossGridsMeansEveryMeasure()
    {
        List<RealizedMoments> grids =
        [
            new() { RealizedVariance = 1.0, BipowerVariation = 2.0, UpsideVariance = 3.0, DownsideVariance = 4.0, RealizedQuarticity = 5.0, ReturnCount = 10 },
            new() { RealizedVariance = 3.0, BipowerVariation = 4.0, UpsideVariance = 5.0, DownsideVariance = 6.0, RealizedQuarticity = 7.0, ReturnCount = 20 },
        ];

        var averaged = RealizedVolatilityEstimator.Average(grids);

        Assert.Equal(2.0, averaged.RealizedVariance, 15);
        Assert.Equal(3.0, averaged.BipowerVariation, 15);
        Assert.Equal(4.0, averaged.UpsideVariance, 15);
        Assert.Equal(5.0, averaged.DownsideVariance, 15);
        Assert.Equal(6.0, averaged.RealizedQuarticity, 15);
        Assert.Equal(15, averaged.ReturnCount);
    }

    [Fact]
    public void AveragingRoundsTheReturnCount()
    {
        List<RealizedMoments> grids = [new() { ReturnCount = 10 }, new() { ReturnCount = 11 }];

        // 10.5 rounds to even under banker's rounding, which is what Math.Round does here.
        Assert.Equal(10, RealizedVolatilityEstimator.Average(grids).ReturnCount);
    }

    [Fact]
    public void AveragingRejectsMissingGridsAndHandlesNone()
    {
        Assert.Throws<ArgumentNullException>(() => RealizedVolatilityEstimator.Average(null!));

        var empty = RealizedVolatilityEstimator.Average([]);
        Assert.Equal(0.0, empty.RealizedVariance);
        Assert.Equal(0, empty.ReturnCount);
    }

    // ---------- csv parsing ----------

    [Fact]
    public void ParsingRejectsAnEmptyBody()
    {
        Assert.Throws<ArgumentException>(() => CsvTable.Parse(null!));
        Assert.Throws<ArgumentException>(() => CsvTable.Parse(""));
        Assert.Throws<ArgumentException>(() => CsvTable.Parse("   \n  "));
    }

    [Fact]
    public void HeaderOnlyBodiesParseToNoRows()
    {
        var table = CsvTable.Parse("bid,ask\n");

        Assert.Equal(0, table.Count);
        Assert.Equal(["bid", "ask"], table.ColumnNames);
    }

    [Fact]
    public void RowsAreSplitOnCommasAndBlankLinesIgnored()
    {
        var table = CsvTable.Parse("bid,ask\n1,2\n\n3,4\n");

        Assert.Equal(2, table.Count);
        Assert.Equal(["3", "4"], table.Rows[1]);
    }

    [Fact]
    public void BothLineEndingsAreAccepted()
    {
        Assert.Equal(2, CsvTable.Parse("bid,ask\r\n1,2\r\n3,4\r\n").Count);
    }

    [Fact]
    public void ColumnLookupIsCaseInsensitiveAndTrimmed()
    {
        var table = CsvTable.Parse(" Bid , ASK \n1,2\n");

        Assert.True(table.HasColumn("bid"));
        Assert.True(table.HasColumn("BID"));
        Assert.True(table.HasColumn("ask"));
        Assert.False(table.HasColumn("mid"));
        Assert.Equal(0, table.RequireColumn("bid"));
        Assert.Equal(1, table.RequireColumn("ask"));
    }

    [Fact]
    public void RequireColumnAcceptsSeveralSpellings()
    {
        var table = CsvTable.Parse("ms_of_day,bid\n1,2\n");

        // Version-to-version renames are resolved here rather than at every call site.
        Assert.Equal(0, table.RequireColumn("ms_of_day", "ms_of_day2"));
        Assert.Equal(0, table.RequireColumn("timestamp", "ms_of_day"));
    }

    [Fact]
    public void AMissingColumnReportsWhatWasActuallyReceived()
    {
        var table = CsvTable.Parse("bid,ask\n1,2\n");

        var ex = Assert.Throws<InvalidOperationException>(() => table.RequireColumn("strike", "strike_price"));

        // A schema change must say what it got, not just that something was missing.
        Assert.Contains("strike", ex.Message, StringComparison.Ordinal);
        Assert.Contains("strike_price", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ask", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedAccessorsParseInvariantly()
    {
        var row = new[] { "1.25", "-3", " SPXW " };

        Assert.Equal(1.25, CsvTable.GetDouble(row, 0), 15);
        Assert.Equal(-3L, CsvTable.GetInt64(row, 1));
        Assert.Equal("SPXW", CsvTable.GetString(row, 2));
    }

    [Fact]
    public void ScientificNotationParsesAsANumber() =>
        Assert.Equal(1.5e-3, CsvTable.GetDouble(["1.5e-3"], 0), 15);

    [Fact]
    public void AShortRowIsReportedRatherThanReadPastTheEnd()
    {
        var row = new[] { "1.25" };

        Assert.Throws<InvalidOperationException>(() => CsvTable.GetDouble(row, 1));
        Assert.Throws<InvalidOperationException>(() => CsvTable.GetInt64(row, 1));
        // The string accessor is lenient by design: a missing trailing field is empty.
        Assert.Equal(string.Empty, CsvTable.GetString(row, 1));
    }

    [Fact]
    public void UnparseableValuesNameTheOffendingText()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CsvTable.GetDouble(["not-a-number"], 0));
        Assert.Contains("not-a-number", ex.Message, StringComparison.Ordinal);

        var intEx = Assert.Throws<InvalidOperationException>(() => CsvTable.GetInt64(["1.5"], 0));
        Assert.Contains("1.5", intEx.Message, StringComparison.Ordinal);
    }
}
