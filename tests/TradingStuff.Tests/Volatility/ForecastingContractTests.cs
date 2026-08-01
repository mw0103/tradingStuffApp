using TradingStuff.Volatility;
using TradingStuff.Volatility.Baselines;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Diagnostic text and edge conditions in the forecasting layer and the comparison
/// regressions, which the behavioural suites step over.
/// </summary>
public class ForecastingContractTests
{
    private static readonly DateTime Origin = new(2010, 1, 1);

    private static HarSample Sample(int day, double v) => new()
    {
        Date = Origin.AddDays(day),
        Features = [v, v, v],
        Target = v,
        ForwardVariance = Math.Exp(v),
        RandomWalkForecast = Math.Exp(v),
    };

    // ---------- forecasting messages ----------

    [Fact]
    public void ForecastingFailuresExplainWhatIsWrong()
    {
        Assert.Contains("empty training block",
            Assert.Throws<ArgumentException>(() => new MeanLogVarianceModel().Fit([])).Message,
            StringComparison.Ordinal);

        Assert.Contains("ascending by date",
            Assert.Throws<ArgumentException>(() =>
            {
                var m = new MeanLogVarianceModel();
                m.Fit([Sample(0, -9.0)]);
                m.PredictLogVariance([Sample(5, -9.0), Sample(1, -9.0)]);
            }).Message, StringComparison.Ordinal);

        Assert.Contains("must be fitted",
            Assert.Throws<InvalidOperationException>(() => new MeanLogVarianceModel().Mean).Message,
            StringComparison.Ordinal);
        Assert.Contains("must be fitted",
            Assert.Throws<InvalidOperationException>(() =>
                new RollingMeanLogVarianceModel().PredictLogVariance([])).Message,
            StringComparison.Ordinal);
        Assert.Contains("must be fitted",
            Assert.Throws<InvalidOperationException>(() =>
                new EwmaLogVarianceModel().PredictLogVariance([])).Message,
            StringComparison.Ordinal);

        Assert.Contains("window must be positive",
            Assert.Throws<ArgumentOutOfRangeException>(() => new RollingMeanLogVarianceModel(0)).Message,
            StringComparison.Ordinal);
        Assert.Contains("strictly between 0 and 1",
            Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaLogVarianceModel(1.0)).Message,
            StringComparison.Ordinal);
        Assert.Contains("truncation lag cannot be negative",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DieboldMariano.Compare([1.0, 2.0], [1.0, 2.0], -1)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ModelNamesCarryTheirConfiguration()
    {
        Assert.Equal("rung1-rolling5", new RollingMeanLogVarianceModel(5).Name);
        Assert.Equal("rung1-ewma0.50", new EwmaLogVarianceModel(0.5).Name);
    }

    // ---------- erfc sign branch ----------

    [Fact]
    public void TheErrorFunctionHandlesBothSignsOfItsArgument()
    {
        // The approximation is evaluated on |x| and reflected for negative arguments; the
        // reflection is what makes the two-sided p-value symmetric.
        Assert.Equal(1.0, DieboldMariano.TwoSidedNormalPValue(0.0), 9);
        Assert.True(DieboldMariano.TwoSidedNormalPValue(0.5) > DieboldMariano.TwoSidedNormalPValue(1.5));
        Assert.Equal(DieboldMariano.TwoSidedNormalPValue(2.0), DieboldMariano.TwoSidedNormalPValue(-2.0), 15);
    }

    // ---------- grading edges ----------

    private static FoldScore Score(string fold, IReadOnlyList<double> daily, IReadOnlyList<DateTime> dates) => new()
    {
        FoldName = fold,
        ModelName = "m",
        Observations = daily.Count,
        DailyQuasiLikelihood = daily,
        Dates = dates,
        QuasiLikelihoodLoss = daily.Average(),
        LogMeanSquaredError = 0.0,
    };

    [Fact]
    public void APerfectBaselineLeavesNoRelativeGainToReport()
    {
        // QLIKE is zero only for an exact forecast. Dividing by it would be undefined, so the
        // gain is reported as zero rather than as infinity.
        var dates = Enumerable.Range(0, 10).Select(i => Origin.AddDays(i)).ToList();
        var perfect = Enumerable.Repeat(0.0, 10).ToList();

        var evaluation = WalkForwardEvaluation.Grade(
            "candidate", [Score("A", perfect, dates)], [Score("A", perfect, dates)]);

        Assert.Equal(0.0, evaluation.PooledQlikeGain);
        Assert.False(double.IsNaN(evaluation.PooledQlikeGain));
        Assert.False(double.IsInfinity(evaluation.PooledQlikeGain));
    }

    [Fact]
    public void TwoYearsSharingTheGainEquallyReportHalfEach()
    {
        // The running maximum must not be replaced on a tie, or the reported concentration
        // would depend on which year happened to be visited last.
        var dates = Enumerable.Range(0, 4)
            .Select(i => i < 2 ? new DateTime(2020, 6, 1).AddDays(i) : new DateTime(2021, 6, 1).AddDays(i))
            .ToList();
        var baseline = Enumerable.Repeat(0.5, 4).ToList();
        var candidate = Enumerable.Repeat(0.4, 4).ToList();

        var evaluation = WalkForwardEvaluation.Grade(
            "even", [Score("A", candidate, dates)], [Score("A", baseline, dates)]);

        Assert.Equal(0.5, evaluation.LargestYearShareOfGain, 12);
    }

    [Fact]
    public void ALossMakingYearStaysInTheConcentrationDenominator()
    {
        // Netting a bad year out first would let one catastrophic year mask a gain that is
        // otherwise entirely from a single other year.
        var dates = new List<DateTime>
        {
            new(2020, 6, 1), new(2020, 6, 2), new(2021, 6, 1), new(2021, 6, 2),
        };
        var baseline = new List<double> { 0.5, 0.5, 0.5, 0.5 };
        // 2020 gains 0.2 total; 2021 loses 0.1 total. Net 0.1, so 2020's share exceeds one.
        var candidate = new List<double> { 0.4, 0.4, 0.55, 0.55 };

        var evaluation = WalkForwardEvaluation.Grade(
            "mixed", [Score("A", candidate, dates)], [Score("A", baseline, dates)]);

        Assert.Equal(2.0, evaluation.LargestYearShareOfGain, 9);
    }

    [Fact]
    public void AModelThatIsUniformlyWorseReportsNoConcentration()
    {
        var dates = Enumerable.Range(0, 4).Select(i => Origin.AddDays(i)).ToList();

        var evaluation = WalkForwardEvaluation.Grade(
            "worse",
            [Score("A", Enumerable.Repeat(0.6, 4).ToList(), dates)],
            [Score("A", Enumerable.Repeat(0.5, 4).ToList(), dates)]);

        // There is no gain to concentrate.
        Assert.Equal(0.0, evaluation.LargestYearShareOfGain);
        Assert.True(evaluation.PooledQlikeGain < 0.0);
    }

    // ---------- comparison regressions ----------

    private static RealizedVolatilityDay Day(DateTime date, double variance, string symbol = "SPY") =>
        new() { Symbol = symbol, Date = date, TotalVariance = variance, IsComplete = true };

    [Fact]
    public void AZeroVarianceSourceSessionIsExcludedFromTheComparison()
    {
        var source = Enumerable.Range(0, 20).Select(i => Day(Origin.AddDays(i), 1e-4)).ToList();
        var target = Enumerable.Range(0, 20).Select(i => Day(Origin.AddDays(i), 1.2e-4, "SPX")).ToList();

        source[3].TotalVariance = 0.0;

        // Exactly zero is excluded, not merely negative: log of it is undefined.
        Assert.Equal(19, VolatilityComparison.Compare(source, target).MatchedDays);
    }

    [Fact]
    public void TheCalibrationRSquaredIsOneMinusTheVarianceRatio()
    {
        // A noisy relationship, so residual and total sums of squares both matter and the
        // formula's shape is pinned rather than collapsing to 1.
        var rng = new Random(19);
        var source = Enumerable.Range(0, 60).Select(i => Day(Origin.AddDays(i), 1e-4 * (1.0 + (i % 13) * 0.2))).ToList();
        var target = source
            .Select(d => Day(d.Date, d.TotalVariance * (1.2 + (rng.NextDouble() - 0.5) * 0.6), "SPX"))
            .ToList();

        var result = VolatilityComparison.Compare(source, target);

        var x = source.Select(d => Math.Log(d.TotalVariance)).ToList();
        var y = target.Select(d => Math.Log(d.TotalVariance)).ToList();
        var mean = y.Average();

        double residual = 0.0, total = 0.0;
        for (int i = 0; i < x.Count; i++)
        {
            var fitted = result.CalibrationIntercept + result.CalibrationSlope * x[i];
            residual += (y[i] - fitted) * (y[i] - fitted);
            total += (y[i] - mean) * (y[i] - mean);
        }

        Assert.Equal(1.0 - residual / total, result.CalibrationRSquared, 10);

        // With real scatter the figure sits strictly inside the unit interval, so neither a
        // dropped subtraction nor a flipped ratio could reproduce it.
        Assert.InRange(result.CalibrationRSquared, 0.05, 0.95);
    }

    [Fact]
    public void TheCorrelationIsTheStandardisedCovariance()
    {
        var rng = new Random(23);
        var source = Enumerable.Range(0, 60).Select(i => Day(Origin.AddDays(i), 1e-4 * (1.0 + (i % 11) * 0.3))).ToList();
        var target = source
            .Select(d => Day(d.Date, d.TotalVariance * (1.1 + (rng.NextDouble() - 0.5) * 0.5), "SPX"))
            .ToList();

        var result = VolatilityComparison.Compare(source, target);

        var x = source.Select(d => Math.Log(d.TotalVariance)).ToList();
        var y = target.Select(d => Math.Log(d.TotalVariance)).ToList();
        double mx = x.Average(), my = y.Average();

        double cov = 0.0, vx = 0.0, vy = 0.0;
        for (int i = 0; i < x.Count; i++)
        {
            cov += (x[i] - mx) * (y[i] - my);
            vx += (x[i] - mx) * (x[i] - mx);
            vy += (y[i] - my) * (y[i] - my);
        }

        Assert.Equal(cov / Math.Sqrt(vx * vy), result.LogVarianceCorrelation, 10);
        Assert.InRange(result.LogVarianceCorrelation, 0.05, 0.99);
    }
}
