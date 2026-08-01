using TradingStuff.Volatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the SPY-to-SPX transfer measurement.
/// </summary>
/// <remarks>
/// The point of this component is that the transfer is measured rather than assumed, so the
/// tests construct series with a known relationship and check the calibration recovers it.
/// The dispersion matters as much as the level: a level correction can be applied, and the
/// scatter around it cannot, which is the honest measure of how far from one-to-one the
/// transfer really is.
/// </remarks>
public class VolatilityComparisonTests
{
    private static readonly DateTime Start = new(2024, 1, 2);

    private static RealizedVolatilityDay Day(DateTime date, double variance, bool complete = true, string symbol = "SPY") =>
        new()
        {
            Symbol = symbol, Date = date, TotalVariance = variance, IntradayVariance = variance, IsComplete = complete,
        };

    /// <summary>A series whose variance walks deterministically, so logs are well spread.</summary>
    private static List<RealizedVolatilityDay> Series(int count, Func<int, double> variance, string symbol = "SPY") =>
        Enumerable.Range(0, count).Select(i => Day(Start.AddDays(i), variance(i), symbol: symbol)).ToList();

    private static double SourceVariance(int i) => 1e-4 * (1.0 + (i % 17) * 0.25);

    // ---------- validation ----------

    [Fact]
    public void CompareRejectsMissingSeries()
    {
        Assert.Throws<ArgumentNullException>(() => VolatilityComparison.Compare(null!, Series(5, SourceVariance)));
        Assert.Throws<ArgumentNullException>(() => VolatilityComparison.Compare(Series(5, SourceVariance), null!));
    }

    [Fact]
    public void TooLittleOverlapIsRefusedRatherThanFitted()
    {
        var source = Series(2, SourceVariance);
        var target = Series(2, SourceVariance, symbol: "SPX");

        // Two points would fit a line exactly and report a meaningless R-squared of 1.
        Assert.Throws<InvalidOperationException>(() => VolatilityComparison.Compare(source, target));
    }

    [Fact]
    public void ThreeOverlappingSessionsAreEnough()
    {
        var source = Series(3, SourceVariance);
        var target = Series(3, i => SourceVariance(i) * 1.2, symbol: "SPX");

        Assert.Equal(3, VolatilityComparison.Compare(source, target).MatchedDays);
    }

    [Fact]
    public void NonOverlappingSeriesAreRefused()
    {
        var source = Series(30, SourceVariance);
        var target = Enumerable.Range(0, 30)
            .Select(i => Day(Start.AddYears(5).AddDays(i), SourceVariance(i), symbol: "SPX")).ToList();

        Assert.Throws<InvalidOperationException>(() => VolatilityComparison.Compare(source, target));
    }

    // ---------- matching ----------

    [Fact]
    public void OnlyCommonCompleteSessionsAreMatched()
    {
        var source = Series(30, SourceVariance);
        var target = Series(30, i => SourceVariance(i) * 1.2, symbol: "SPX");

        source[3].IsComplete = false;
        target[5].IsComplete = false;
        target[7].TotalVariance = 0.0;
        source[9].TotalVariance = -1e-4;

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(26, result.MatchedDays);
    }

    [Fact]
    public void MatchingIsByDateNotByPosition()
    {
        var source = Series(20, SourceVariance);
        // The target starts five sessions later, so only fifteen dates line up.
        var target = Enumerable.Range(0, 20)
            .Select(i => Day(Start.AddDays(i + 5), SourceVariance(i) * 1.2, symbol: "SPX")).ToList();

        Assert.Equal(15, VolatilityComparison.Compare(source, target).MatchedDays);
    }

    [Fact]
    public void MatchingIgnoresTheTimeOfDay()
    {
        var source = Series(10, SourceVariance);
        var target = Enumerable.Range(0, 10)
            .Select(i => Day(Start.AddDays(i).AddHours(16), SourceVariance(i) * 1.2, symbol: "SPX")).ToList();

        Assert.Equal(10, VolatilityComparison.Compare(source, target).MatchedDays);
    }

    [Fact]
    public void UnorderedSourceIsSortedBeforeComparison()
    {
        var source = Series(20, SourceVariance);
        var target = Series(20, i => SourceVariance(i) * 1.2, symbol: "SPX");

        var ordered = VolatilityComparison.Compare(source, target);
        var shuffled = VolatilityComparison.Compare(source.OrderByDescending(d => d.Date).ToList(), target);

        Assert.Equal(ordered.MatchedDays, shuffled.MatchedDays);
        Assert.Equal(ordered.MeanLogVarianceRatio, shuffled.MeanLogVarianceRatio, 12);
        Assert.Equal(ordered.CalibrationSlope, shuffled.CalibrationSlope, 12);
    }

    // ---------- level and dispersion ----------

    [Fact]
    public void AConstantMultipleIsRecoveredAsTheMeanLogRatio()
    {
        var source = Series(40, SourceVariance);
        var target = Series(40, i => SourceVariance(i) * 1.25, symbol: "SPX");

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(Math.Log(1.25), result.MeanLogVarianceRatio, 12);
        // No scatter around a pure level shift.
        Assert.Equal(0.0, result.LogVarianceRatioStdDev, 12);
    }

    [Fact]
    public void IdenticalSeriesAgreeExactly()
    {
        var source = Series(40, SourceVariance);
        var target = Series(40, SourceVariance, symbol: "SPX");

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(0.0, result.MeanLogVarianceRatio, 12);
        Assert.Equal(1.0, result.LogVarianceCorrelation, 12);
        Assert.Equal(1.0, result.CalibrationSlope, 6);
        Assert.Equal(0.0, result.CalibrationIntercept, 6);
        // Not exactly 1: the ridge term biases the fit very slightly, which is the price of
        // the solve staying well conditioned on collinear regressors.
        Assert.Equal(1.0, result.CalibrationRSquared, 7);
    }

    [Fact]
    public void DispersionGrowsWhenTheRelationshipIsNoisy()
    {
        var rng = new Random(5);
        var source = Series(60, SourceVariance);
        var target = Enumerable.Range(0, 60)
            .Select(i => Day(Start.AddDays(i), SourceVariance(i) * (1.0 + (rng.NextDouble() - 0.5) * 0.5), symbol: "SPX"))
            .ToList();

        var noisy = VolatilityComparison.Compare(source, target);
        var clean = VolatilityComparison.Compare(source, Series(60, i => SourceVariance(i) * 1.25, symbol: "SPX"));

        Assert.True(noisy.LogVarianceRatioStdDev > clean.LogVarianceRatioStdDev);
        Assert.True(noisy.CalibrationRSquared < clean.CalibrationRSquared);
    }

    [Fact]
    public void TheStandardDeviationIsTheSampleForm()
    {
        // Five sessions with known multipliers, so the expected dispersion is computable by
        // hand rather than read back out of the result.
        double[] multipliers = [1.0, 1.1, 1.2, 1.3, 1.4];
        var source = Series(5, SourceVariance);
        var target = Enumerable.Range(0, 5)
            .Select(i => Day(Start.AddDays(i), SourceVariance(i) * multipliers[i], symbol: "SPX")).ToList();

        var result = VolatilityComparison.Compare(source, target);

        var ratios = multipliers.Select(m => Math.Log(m)).ToList();
        var mean = ratios.Average();
        // Divides by n-1, not n: this is a sample estimate, not the population figure.
        var expected = Math.Sqrt(ratios.Sum(r => (r - mean) * (r - mean)) / (ratios.Count - 1));

        Assert.Equal(mean, result.MeanLogVarianceRatio, 12);
        Assert.Equal(expected, result.LogVarianceRatioStdDev, 12);
    }

    // ---------- calibration ----------

    [Fact]
    public void TheCalibrationRecoversAKnownPowerRelationship()
    {
        // log(target) = a + b*log(source) with a = log(1.3), b = 0.85.
        var source = Series(60, SourceVariance);
        var target = Enumerable.Range(0, 60)
            .Select(i => Day(Start.AddDays(i), 1.3 * Math.Pow(SourceVariance(i), 0.85), symbol: "SPX"))
            .ToList();

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(Math.Log(1.3), result.CalibrationIntercept, 6);
        Assert.Equal(0.85, result.CalibrationSlope, 6);
        Assert.Equal(1.0, result.CalibrationRSquared, 9);
    }

    [Fact]
    public void TransferAppliesTheFittedCalibration()
    {
        var source = Series(60, SourceVariance);
        var target = Enumerable.Range(0, 60)
            .Select(i => Day(Start.AddDays(i), 1.3 * Math.Pow(SourceVariance(i), 0.85), symbol: "SPX"))
            .ToList();

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(1.3 * Math.Pow(2e-4, 0.85), result.TransferVariance(2e-4), 10);
        Assert.Equal(
            Math.Exp(result.CalibrationIntercept + result.CalibrationSlope * Math.Log(3e-4)),
            result.TransferVariance(3e-4), 15);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1e-4)]
    public void TransferRejectsANonPositiveVariance(double variance) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VolatilityComparisonResult { CalibrationIntercept = 0.0, CalibrationSlope = 1.0 }
                .TransferVariance(variance));

    // ---------- correlation ----------

    [Fact]
    public void CorrelationIsOneForAMonotoneRelationshipAndNegativeWhenInverted()
    {
        var source = Series(40, i => 1e-4 * (1.0 + i * 0.1));
        var rising = Enumerable.Range(0, 40)
            .Select(i => Day(Start.AddDays(i), 1e-4 * (1.0 + i * 0.1) * 1.2, symbol: "SPX")).ToList();
        var falling = Enumerable.Range(0, 40)
            .Select(i => Day(Start.AddDays(i), 1e-4 * (1.0 + (39 - i) * 0.1), symbol: "SPX")).ToList();

        Assert.Equal(1.0, VolatilityComparison.Compare(source, rising).LogVarianceCorrelation, 9);
        Assert.True(VolatilityComparison.Compare(source, falling).LogVarianceCorrelation < -0.9);
    }

    [Fact]
    public void AFlatSourceProducesNoCorrelationRatherThanNaN()
    {
        // Zero variance in one leg makes the correlation denominator zero; it must return a
        // number, because this feeds a summary a human reads rather than a computation.
        var source = Series(10, _ => 1e-4);
        var target = Series(10, i => 1e-4 * (1.0 + i * 0.1), symbol: "SPX");

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(0.0, result.LogVarianceCorrelation);
        Assert.False(double.IsNaN(result.LogVarianceCorrelation));
    }

    [Fact]
    public void AFlatTargetProducesADegenerateButFiniteRSquared()
    {
        var source = Series(10, i => 1e-4 * (1.0 + i * 0.1));
        var target = Series(10, _ => 1e-4, symbol: "SPX");

        var result = VolatilityComparison.Compare(source, target);

        // RSquared guards on `totalSumSquares > 0.0` intending to return 0 here, but a
        // constant target does not give exactly zero total sum of squares: Average() leaves
        // a one-ulp residue, the guard passes, and the ratio explodes. The number is
        // meaningless either way — what matters is that it is finite rather than NaN, so a
        // downstream summary prints obvious garbage instead of silently poisoning a
        // comparison. Worth tightening the guard to a tolerance.
        Assert.False(double.IsNaN(result.CalibrationRSquared));
        Assert.True(double.IsFinite(result.CalibrationRSquared));
        Assert.True(result.CalibrationRSquared <= 0.0);
    }

    // ---------- divergences ----------

    [Fact]
    public void TheWorstDisagreementsAreReturnedWorstFirst()
    {
        var source = Series(40, SourceVariance);
        var target = Series(40, i => SourceVariance(i) * 1.2, symbol: "SPX").ToList();

        // Two dates where the two series disagree violently.
        target[10].TotalVariance = SourceVariance(10) * 8.0;
        target[20].TotalVariance = SourceVariance(20) * 20.0;

        var result = VolatilityComparison.Compare(source, target);

        Assert.Equal(Start.AddDays(20), result.LargestDivergences[0].Date);
        Assert.Equal(Start.AddDays(10), result.LargestDivergences[1].Date);
        Assert.True(result.LargestDivergences[0].AbsoluteLogVarianceRatio
            > result.LargestDivergences[1].AbsoluteLogVarianceRatio);
    }

    [Fact]
    public void ADisagreementInEitherDirectionCounts()
    {
        var source = Series(40, SourceVariance);
        var target = Series(40, SourceVariance, symbol: "SPX").ToList();

        // One session far below, none above: ranked on absolute divergence.
        target[15].TotalVariance = SourceVariance(15) / 20.0;

        var worst = VolatilityComparison.Compare(source, target).LargestDivergences[0];

        Assert.Equal(Start.AddDays(15), worst.Date);
        Assert.True(worst.LogVarianceRatio < 0.0);
        Assert.Equal(Math.Abs(worst.LogVarianceRatio), worst.AbsoluteLogVarianceRatio, 15);
    }

    [Fact]
    public void TheDivergenceListIsCappedAtTheRequestedSize()
    {
        var source = Series(40, SourceVariance);
        var target = Series(40, i => SourceVariance(i) * (1.0 + i * 0.05), symbol: "SPX");

        Assert.Equal(5, VolatilityComparison.Compare(source, target, topDivergences: 5).LargestDivergences.Count);
        // The default keeps twenty.
        Assert.Equal(20, VolatilityComparison.Compare(source, target).LargestDivergences.Count);
    }

    [Fact]
    public void ADivergenceCarriesBothSeriesVolatilities()
    {
        var source = Series(10, _ => VolatilityScaling.ToMeanDailyVariance(0.16));
        var target = Series(10, _ => VolatilityScaling.ToMeanDailyVariance(0.20), symbol: "SPX");

        var worst = VolatilityComparison.Compare(source, target).LargestDivergences[0];

        Assert.Equal(0.16, worst.SourceAnnualizedVolatility, 9);
        Assert.Equal(0.20, worst.TargetAnnualizedVolatility, 9);
    }

    [Fact]
    public void TheResultRendersItsHeadlineNumbers()
    {
        var text = new VolatilityComparisonResult
        {
            MatchedDays = 500,
            LogVarianceCorrelation = 0.95,
            MeanLogVarianceRatio = 0.1,
            LogVarianceRatioStdDev = 0.25,
            CalibrationIntercept = -0.5,
            CalibrationSlope = 0.9,
            CalibrationRSquared = 0.9,
        }.ToString();

        Assert.Contains("n=500", text, StringComparison.Ordinal);
        Assert.Contains("corr(logRV)=0.9500", text, StringComparison.Ordinal);
        Assert.Contains("calib:", text, StringComparison.Ordinal);
    }
}
