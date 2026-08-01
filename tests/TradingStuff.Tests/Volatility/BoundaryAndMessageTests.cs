using TradingStuff.Volatility;
using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Boundary conditions and diagnostic text that the broader suites step over.
/// </summary>
/// <remarks>
/// Mostly one-line predicates where the strictness of a comparison is the whole behaviour —
/// a crossed market accepted as two-sided, a grid extended one point too far, a tie in a
/// running maximum. None of these change a result enough to look wrong; they change it
/// enough to matter.
/// </remarks>
public class BoundaryAndMessageTests
{
    private static readonly DateTime Day = new(2024, 3, 4);
    private static DateTime At(int hour, int minute) => Day.AddHours(hour).AddMinutes(minute);

    // ---------- two-sided markets ----------

    [Theory]
    [InlineData(1.0, 1.2, true)]
    [InlineData(1.0, 1.0, true)]    // a locked market is still two-sided
    [InlineData(0.0, 1.2, false)]   // zero bid: excluded outright, not mid-priced
    [InlineData(1.0, 0.0, false)]
    [InlineData(-1.0, 1.2, false)]
    [InlineData(1.2, 1.0, false)]   // crossed: ask below bid is not a market
    public void ATwoSidedMarketNeedsBothSidesAndAnUncrossedSpread(double bid, double ask, bool expected) =>
        Assert.Equal(expected, new OptionQuote(100.0, OptionRight.Call, bid, ask).HasTwoSidedMarket);

    [Fact]
    public void TheMidIsTheAverageOfTheTwoSides() =>
        Assert.Equal(1.1, new OptionQuote(100.0, OptionRight.Call, 1.0, 1.2).Mid, 12);

    // ---------- day projections ----------

    [Fact]
    public void SessionMinutesSpanTheFirstAndLastSampledBar()
    {
        var day = new RealizedVolatilityDay
        {
            FirstBarTime = At(9, 31),
            LastBarTime = At(16, 0),
        };

        Assert.Equal(389.0, day.SessionMinutes, 9);
    }

    [Fact]
    public void SessionMinutesAreZeroForASingleInstant() =>
        Assert.Equal(0.0, new RealizedVolatilityDay { FirstBarTime = At(9, 31), LastBarTime = At(9, 31) }.SessionMinutes);

    // ---------- grid construction boundaries ----------

    [Fact]
    public void AGridEndingExactlyOnTheLastPrintIsNotExtended()
    {
        // Bars run to 09:45 and the scheduled end is 09:45: the effective end is that
        // instant either way, so no extra point is appended.
        var times = Enumerable.Range(0, 16).Select(i => At(9, 30 + i)).ToList();
        var prices = Enumerable.Range(0, 16).Select(i => 100.0 + i).ToList();

        var sampled = BarResampler.Sample(times, prices, At(9, 30), At(9, 45), 5, 0);

        Assert.Equal(At(9, 45), sampled.Times[^1]);
        Assert.Equal(4, sampled.Times.Count);
    }

    [Fact]
    public void AGridPointExactlyAtTheEndIsIncluded()
    {
        var times = Enumerable.Range(0, 11).Select(i => At(9, 30 + i)).ToList();
        var prices = Enumerable.Range(0, 11).Select(i => 100.0 + i).ToList();

        var sampled = BarResampler.Sample(times, prices, At(9, 30), At(9, 40), 5, 0);

        // 09:30, 09:35, 09:40 — the bound is inclusive, so the last point is kept.
        Assert.Equal([At(9, 30), At(9, 35), At(9, 40)], sampled.Times);
    }

    [Fact]
    public void NothingIsAppendedWhenNoGridPointExists()
    {
        // The only bar prints before the grid's first offset, so the grid is empty and there
        // is no final point to extend.
        var sampled = BarResampler.Sample([At(9, 31)], [100.0], At(9, 30), At(9, 30), 5, offsetMinutes: 10);

        Assert.Empty(sampled.Times);
    }

    [Fact]
    public void AZeroOffsetStartsExactlyAtTheGridStart()
    {
        var times = Enumerable.Range(0, 11).Select(i => At(9, 30 + i)).ToList();
        var prices = Enumerable.Range(0, 11).Select(i => 100.0 + i).ToList();

        Assert.Equal(At(9, 30), BarResampler.Sample(times, prices, At(9, 30), At(9, 40), 5, 0).Times[0]);
    }

    [Fact]
    public void SamplingBlamesTheParameterAtFault()
    {
        Assert.Equal("closeTimes",
            Assert.Throws<ArgumentNullException>(() =>
                BarResampler.Sample(null!, [1.0], At(9, 30), At(16, 0), 5, 0)).ParamName);
        Assert.Equal("closePrices",
            Assert.Throws<ArgumentNullException>(() =>
                BarResampler.Sample([At(9, 30)], null!, At(9, 30), At(16, 0), 5, 0)).ParamName);
        Assert.Equal("intervalMinutes",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BarResampler.Sample([At(9, 30)], [1.0], At(9, 30), At(16, 0), 0, 0)).ParamName);

        Assert.Contains("same length",
            Assert.Throws<ArgumentException>(() =>
                BarResampler.Sample([At(9, 30)], [1.0, 2.0], At(9, 30), At(16, 0), 5, 0)).Message,
            StringComparison.Ordinal);
        Assert.Contains("must be positive",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BarResampler.Sample([At(9, 30)], [1.0], At(9, 30), At(16, 0), 0, 0)).Message,
            StringComparison.Ordinal);
    }

    // ---------- largest gap ----------

    [Fact]
    public void TheLargestGapKeepsTheFirstOfEqualCandidates()
    {
        // Two equal gaps: the running maximum must not be replaced on a tie, or the reported
        // figure would silently depend on ordering.
        var days = new[] { 0, 5, 10 }.Select(offset => new RealizedVolatilityDay
        {
            Symbol = "SPY", Date = Day.AddDays(offset), TotalVariance = 1e-4, IsComplete = true,
        }).ToList();

        Assert.Equal(5, SeriesDiagnostics.Summarize(days).LargestGapDays);
    }

    // ---------- option validation messages ----------

    [Fact]
    public void OptionValidationExplainsWhichSettingIsWrong()
    {
        Assert.Contains("SourceBarMinutes must be positive",
            Assert.Throws<InvalidOperationException>(() =>
                new RealizedVolatilityOptions { SourceBarMinutes = 0 }.Validate()).Message,
            StringComparison.Ordinal);

        Assert.Contains("finer than the source bars",
            Assert.Throws<InvalidOperationException>(() =>
                new RealizedVolatilityOptions { SourceBarMinutes = 5, SamplingMinutes = 1 }.Validate()).Message,
            StringComparison.Ordinal);

        Assert.Contains("OvernightScalingWindow must be positive",
            Assert.Throws<InvalidOperationException>(() =>
                new RealizedVolatilityOptions { OvernightScalingWindow = 0 }.Validate()).Message,
            StringComparison.Ordinal);
    }

    // ---------- csv parser messages ----------

    [Fact]
    public void CsvFailuresExplainWhatIsWrong()
    {
        Assert.Contains("empty",
            Assert.Throws<ArgumentException>(() => CsvTable.Parse("")).Message, StringComparison.Ordinal);

        var table = CsvTable.Parse("bid,ask\n1,2\n");
        Assert.Contains("schema changed",
            Assert.Throws<InvalidOperationException>(() => table.RequireColumn("strike")).Message,
            StringComparison.Ordinal);

        Assert.Contains("fewer fields than the header",
            Assert.Throws<InvalidOperationException>(() => CsvTable.GetDouble(["1.0"], 5)).Message,
            StringComparison.Ordinal);
        Assert.Contains("fewer fields than the header",
            Assert.Throws<InvalidOperationException>(() => CsvTable.GetInt64(["1"], 5)).Message,
            StringComparison.Ordinal);

        Assert.Contains("as a number",
            Assert.Throws<InvalidOperationException>(() => CsvTable.GetDouble(["x"], 0)).Message,
            StringComparison.Ordinal);
        Assert.Contains("as an integer",
            Assert.Throws<InvalidOperationException>(() => CsvTable.GetInt64(["x"], 0)).Message,
            StringComparison.Ordinal);
    }

    // ---------- presets are configured, not defaulted ----------

    [Fact]
    public void ThePresetsSetEverySettingExplicitly()
    {
        // The presets must not silently inherit a future change to the defaults: each field
        // is stated, so a changed default cannot move SPY or SPX behind their backs.
        VolatilityPresets.Spy(out _, out var spy);
        VolatilityPresets.Spx(out _, out var spx);

        foreach (var o in new[] { spy, spx })
        {
            Assert.Equal(1, o.SourceBarMinutes);
            Assert.Equal(5, o.SamplingMinutes);
            Assert.Equal(5, o.SubsampleGridCount);
            Assert.True(o.UseSubsampling);
            Assert.Equal(BarTimestampConvention.BarStart, o.TimestampConvention);
            Assert.Equal(OvernightPolicy.HansenLundeScaling, o.OvernightPolicy);
            Assert.Equal(252, o.OvernightScalingWindow);
        }

        // And they are distinct instances, so mutating one cannot affect the other.
        Assert.NotSame(spy, spx);
        spy.SamplingMinutes = 15;
        VolatilityPresets.Spy(out _, out var fresh);
        Assert.Equal(5, fresh.SamplingMinutes);
    }
}
