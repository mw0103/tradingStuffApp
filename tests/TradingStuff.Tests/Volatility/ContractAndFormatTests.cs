using System.Globalization;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Baselines;
using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the failure contracts — which parameter is blamed, and what a message must say — and
/// the exact numeric formatting of the CSV export.
/// </summary>
/// <remarks>
/// Diagnostics are part of the contract here. A guard that throws the wrong
/// <c>ParamName</c>, or a schema error that does not name what it actually received, costs
/// exactly the debugging session it was written to prevent.
/// <para>
/// The CSV assertions compare rendered text rather than parsed values on purpose. On modern
/// .NET the default double format is already shortest-round-trippable, so dropping the
/// explicit <c>G17</c> would still round-trip — the loss only shows up in the digits actually
/// written to the file.
/// </para>
/// </remarks>
public class ContractAndFormatTests
{
    private static readonly DateTime Start = new(2024, 1, 2);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------- OLS contracts ----------

    [Fact]
    public void OlsBlamesTheParameterAtFault()
    {
        Assert.Equal("design",
            Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Fit(null!, [1.0])).ParamName);
        Assert.Equal("targets",
            Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Fit([[1.0]], null!)).ParamName);
        Assert.Equal("coefficients",
            Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Predict(null!, [1.0])).ParamName);
        Assert.Equal("features",
            Assert.Throws<ArgumentNullException>(() => OrdinaryLeastSquares.Predict([1.0], null!)).ParamName);
    }

    [Fact]
    public void OlsFailuresExplainWhatIsWrong()
    {
        Assert.Contains("same number of rows",
            Assert.Throws<ArgumentException>(() => OrdinaryLeastSquares.Fit([[1.0], [2.0]], [1.0])).Message,
            StringComparison.Ordinal);

        Assert.Contains("no observations",
            Assert.Throws<ArgumentException>(() => OrdinaryLeastSquares.Fit([], [])).Message,
            StringComparison.Ordinal);

        Assert.Contains("underdetermined",
            Assert.Throws<ArgumentException>(() =>
                OrdinaryLeastSquares.Fit([[1.0, 2.0], [3.0, 4.0]], [1.0, 2.0])).Message,
            StringComparison.Ordinal);

        Assert.Contains("same width",
            Assert.Throws<ArgumentException>(() =>
                OrdinaryLeastSquares.Fit([[1.0], [2.0, 3.0], [4.0]], [1.0, 2.0, 3.0])).Message,
            StringComparison.Ordinal);

        Assert.Contains("one longer",
            Assert.Throws<ArgumentException>(() => OrdinaryLeastSquares.Predict([1.0, 2.0], [1.0, 2.0])).Message,
            StringComparison.Ordinal);

        var singular = Enumerable.Range(0, 20).Select(_ => new[] { 1.0 }).ToList();
        Assert.Contains("collinear",
            Assert.Throws<InvalidOperationException>(() =>
                OrdinaryLeastSquares.Fit(singular, singular.Select((_, i) => (double)i).ToList(), ridge: 0.0)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheRidgeIsAddedToTheDiagonalNotSubtracted()
    {
        // A large ridge shrinks the slope toward zero. Subtracting instead would inflate or
        // destabilise it, so the direction of the effect pins the sign.
        var design = Enumerable.Range(0, 40).Select(i => new[] { (double)i }).ToList();
        var targets = design.Select(r => 2.0 * r[0]).ToList();

        var light = OrdinaryLeastSquares.Fit(design, targets, ridge: 1e-8);
        var heavy = OrdinaryLeastSquares.Fit(design, targets, ridge: 1e6);

        Assert.Equal(2.0, light[1], 6);
        Assert.True(heavy[1] < light[1]);
        Assert.True(heavy[1] > 0.0);
    }

    [Fact]
    public void PivotSelectionFindsTheLargestMagnitudeIncludingTheFinalRow()
    {
        // The normal equations put the dominant entry in the last row, so a pivot search
        // that stopped one row early would pick a smaller pivot and lose precision.
        List<double[]> design =
        [
            [1e-9, 1.0], [2e-9, 2.0], [3e-9, 4.0], [4e-9, 8.0], [5e-9, 16.0], [6e-9, 32.0],
        ];
        var targets = design.Select(r => 1.0 + 3.0 * r[0] + 5.0 * r[1]).ToList();

        var beta = OrdinaryLeastSquares.Fit(design, targets, ridge: 0.0);

        Assert.Equal(1.0, beta[0], 6);
        Assert.Equal(5.0, beta[2], 6);
    }

    [Fact]
    public void ASingularSystemIsDetectedAtTheTolerance()
    {
        // Two identical columns: the second pivot collapses below 1e-14.
        var design = Enumerable.Range(1, 30).Select(i => new[] { (double)i, (double)i }).ToList();
        var targets = design.Select(r => r[0]).ToList();

        Assert.Throws<InvalidOperationException>(() => OrdinaryLeastSquares.Fit(design, targets, ridge: 0.0));
    }

    // ---------- HAR dataset contracts ----------

    [Fact]
    public void HarDatasetBlamesTheParameterAtFault()
    {
        Assert.Equal("days",
            Assert.Throws<ArgumentNullException>(() => HarDatasetBuilder.Build(null!, new HarDatasetOptions())).ParamName);
        Assert.Equal("options",
            Assert.Throws<ArgumentNullException>(() => HarDatasetBuilder.Build([], null!)).ParamName);
        Assert.Equal("options",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HarDatasetBuilder.Build([], new HarDatasetOptions { HorizonDays = 0 })).ParamName);
        Assert.Equal("samples",
            Assert.Throws<ArgumentNullException>(() => HarDatasetBuilder.Split(null!, 0.5, 0, out _, out _)).ParamName);
        Assert.Equal("trainRatio",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HarDatasetBuilder.Split([], 0.0, 0, out _, out _)).ParamName);
        Assert.Equal("embargoDays",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HarDatasetBuilder.Split([], 0.5, -1, out _, out _)).ParamName);
    }

    [Fact]
    public void HarDatasetFailuresExplainWhatIsWrong()
    {
        Assert.Contains("HorizonDays must be positive",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HarDatasetBuilder.Build([], new HarDatasetOptions { HorizonDays = 0 })).Message,
            StringComparison.Ordinal);

        Assert.Contains("MonthlyWindow must be at least as long",
            Assert.Throws<ArgumentException>(() => HarDatasetBuilder.Build([],
                new HarDatasetOptions { WeeklyWindow = 10, MonthlyWindow = 9 })).Message,
            StringComparison.Ordinal);

        Assert.Contains("strictly between 0 and 1",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HarDatasetBuilder.Split([], 1.0, 0, out _, out _)).Message,
            StringComparison.Ordinal);
    }

    private static List<RealizedVolatilityDay> Ramp(int count, double scale = 1.0) =>
        Enumerable.Range(0, count).Select(i => new RealizedVolatilityDay
        {
            Symbol = "SPX", Date = Start.AddDays(i), TotalVariance = (i + 1) * scale, IsComplete = true,
        }).ToList();

    [Fact]
    public void EachFeatureWindowIsGuardedIndependently()
    {
        // The guards are on the window means, not on individual sessions, so each case below
        // drives exactly one mean non-positive while leaving the other two healthy. That is
        // what makes the three checks individually necessary rather than jointly sufficient.
        var options = new HarDatasetOptions { HorizonDays = 2, WeeklyWindow = 2, MonthlyWindow = 4 };

        // Ramp gives day i a variance of i+1, so for t = 6:
        //   daily   = days[6]
        //   weekly  = mean(days[5], days[6])
        //   monthly = mean(days[3..6])
        (int Index, double Value)[] cases =
        [
            (6, -1.0),    // daily alone: weekly 2.5, monthly 3.5, both still positive
            (5, -10.0),   // weekly alone: daily 7, monthly 1.75
            (3, -30.0),   // monthly alone: daily 7, weekly 6.5
        ];

        foreach (var (index, value) in cases)
        {
            var days = Ramp(20);
            days[index].TotalVariance = value;

            Assert.DoesNotContain(HarDatasetBuilder.Build(days, options), s => s.Date == Start.AddDays(6));
        }

        // The unperturbed series does produce that sample, or the assertions above are vacuous.
        Assert.Contains(HarDatasetBuilder.Build(Ramp(20), options), s => s.Date == Start.AddDays(6));
    }

    [Fact]
    public void NonOverlappingSpacingCountsFromTheWarmUp()
    {
        var options = new HarDatasetOptions
        {
            HorizonDays = 3, WeeklyWindow = 2, MonthlyWindow = 4, NonOverlappingOnly = true,
        };

        var samples = HarDatasetBuilder.Build(Ramp(40), options);

        // warmup = max(4,3)-1 = 3, so the first kept sample sits exactly on the warm-up index
        // and every later one is a whole horizon after it.
        Assert.Equal(Start.AddDays(3), samples[0].Date);
        for (int i = 1; i < samples.Count; i++)
        {
            Assert.Equal(3, (samples[i].Date - samples[i - 1].Date).Days);
        }
    }

    // ---------- HAR model contracts ----------

    [Fact]
    public void HarModelBlamesTheParameterAtFault()
    {
        var model = new HarRvModel();

        Assert.Equal("samples", Assert.Throws<ArgumentNullException>(() => model.Fit(null!)).ParamName);
        Assert.Contains("empty sample", Assert.Throws<ArgumentException>(() => model.Fit([])).Message,
            StringComparison.Ordinal);
        Assert.Contains("must be fitted",
            Assert.Throws<InvalidOperationException>(() => model.PredictLogVariance([1.0])).Message,
            StringComparison.Ordinal);

        Assert.Equal("forecastVariance",
            Assert.Throws<ArgumentOutOfRangeException>(() => HarRvModel.QuasiLikelihood(1.0, 0.0)).ParamName);
        Assert.Equal("actualVariance",
            Assert.Throws<ArgumentOutOfRangeException>(() => HarRvModel.QuasiLikelihood(0.0, 1.0)).ParamName);
    }

    private static List<HarSample> Samples(int count, double noise)
    {
        var rng = new Random(23);
        return Enumerable.Range(0, count).Select(i =>
        {
            double d = -9.0 + (i % 11) * 0.1, w = -9.0 + (i % 7) * 0.1, m = -9.0 + (i % 5) * 0.1;
            var target = -1.0 + 0.5 * d + 0.3 * w + 0.2 * m + (rng.NextDouble() - 0.5) * noise;
            return new HarSample
            {
                Date = Start.AddDays(i),
                Features = [d, w, m],
                Target = target,
                ForwardVariance = Math.Exp(target),
                RandomWalkForecast = Math.Exp(-9.2),
            };
        }).ToList();
    }

    [Fact]
    public void EvaluationBenchmarksAreComputedFromTheirOwnDefinitions()
    {
        var samples = Samples(80, 0.5);
        var model = new HarRvModel();
        model.Fit(samples);

        var e = model.Evaluate(samples);

        var actuals = samples.Select(s => s.Target).ToList();
        var mean = actuals.Average();
        var modelSse = samples.Sum(s => Math.Pow(s.Target - model.PredictLogVariance(s.Features), 2));
        var meanSse = actuals.Sum(a => Math.Pow(a - mean, 2));
        var rwSse = samples.Sum(s => Math.Pow(s.Target - Math.Log(s.RandomWalkForecast), 2));

        // The benchmark is the mean of the targets, and each R-squared is one minus its own
        // sum-of-squares ratio.
        Assert.Equal(1.0 - modelSse / meanSse, e.RSquaredVersusMean, 12);
        Assert.Equal(1.0 - modelSse / rwSse, e.RSquaredVersusRandomWalk, 12);
        Assert.Equal(modelSse / samples.Count, e.LogMeanSquaredError, 12);

        // Both benchmarks must actually differ, or the two R-squareds would be interchangeable.
        Assert.NotEqual(meanSse, rwSse, 6);
        Assert.NotEqual(e.RSquaredVersusMean, e.RSquaredVersusRandomWalk, 6);
    }

    [Fact]
    public void AWorseThanBenchmarkModelReportsANegativeRSquared()
    {
        // Fit on one relationship, evaluate on an unrelated one: the ratio exceeds one, so
        // 1 - ratio must go negative rather than being clamped or inverted.
        var model = new HarRvModel();
        model.Fit(Samples(60, 0.1));

        var mismatched = Samples(60, 0.1)
            .Select(s => new HarSample
            {
                Date = s.Date,
                Features = s.Features,
                Target = -s.Target,
                ForwardVariance = Math.Exp(-s.Target),
                RandomWalkForecast = s.RandomWalkForecast,
            }).ToList();

        Assert.True(model.Evaluate(mismatched).RSquaredVersusMean < 0.0);
    }

    // ---------- variance risk premium contracts ----------

    [Fact]
    public void PremiumBuilderBlamesTheParameterAtFault()
    {
        Assert.Equal("impliedDays",
            Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.Build(null!, [], 1)).ParamName);
        Assert.Equal("realizedDays",
            Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.Build([], null!, 1)).ParamName);
        Assert.Equal("horizonTradingDays",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                VarianceRiskPremiumBuilder.Build([], [], 0)).ParamName);
        Assert.Equal("days",
            Assert.Throws<ArgumentNullException>(() =>
                VarianceRiskPremiumBuilder.AttachForecasts(null!, _ => 1.0)).ParamName);
        Assert.Equal("forecastForDate",
            Assert.Throws<ArgumentNullException>(() =>
                VarianceRiskPremiumBuilder.AttachForecasts([], null!)).ParamName);
        Assert.Equal("days",
            Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.Summarize(null!)).ParamName);
        Assert.Contains("closed forward window",
            Assert.Throws<ArgumentException>(() => VarianceRiskPremiumBuilder.Summarize([])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothRealizedFiltersAreRequired()
    {
        var implied = new[] { new ImpliedVarianceDay { Symbol = "SPX", Date = Start, ImpliedVariance = 0.05, IsUsable = true } };

        // Incomplete alone disqualifies a session, and so does non-positive variance: an
        // `or` between them would let one of the two through.
        var incomplete = Enumerable.Range(0, 6).Select(i => new RealizedVolatilityDay
        {
            Date = Start.AddDays(i), TotalVariance = 1e-4, IsComplete = i != 1,
        }).ToList();
        var zeroed = Enumerable.Range(0, 6).Select(i => new RealizedVolatilityDay
        {
            Date = Start.AddDays(i), TotalVariance = i == 1 ? 0.0 : 1e-4, IsComplete = true,
        }).ToList();

        var a = VarianceRiskPremiumBuilder.Build(implied, incomplete, 2);
        var b = VarianceRiskPremiumBuilder.Build(implied, zeroed, 2);

        Assert.True(a[0].HasRealizedForward);
        Assert.True(b[0].HasRealizedForward);
        // Both exclusions remove the same session, so the two windows must agree exactly.
        Assert.Equal(a[0].RealizedForwardVariance, b[0].RealizedForwardVariance, 15);
    }

    [Fact]
    public void TheForwardWindowNeedsAStrictlyLaterSession()
    {
        var realized = Enumerable.Range(0, 4).Select(i => new RealizedVolatilityDay
        {
            Date = Start.AddDays(i), TotalVariance = 1e-4, IsComplete = true,
        }).ToList();

        ImpliedVarianceDay Implied(int i) =>
            new() { Symbol = "SPX", Date = Start.AddDays(i), ImpliedVariance = 0.05, IsUsable = true };

        // Index 1 with a 2-day horizon needs indices 2 and 3 plus one more session to exist:
        // the guard is `index + horizon < count`, so index 1 fits and index 2 does not.
        Assert.True(VarianceRiskPremiumBuilder.Build([Implied(1)], realized, 2)[0].HasRealizedForward);
        Assert.False(VarianceRiskPremiumBuilder.Build([Implied(2)], realized, 2)[0].HasRealizedForward);
    }

    // ---------- csv formatting ----------

    [Fact]
    public void EveryNumericColumnIsWrittenAtSeventeenSignificantDigits()
    {
        // Values whose G17 rendering differs from the shortest round-trippable form, so a
        // dropped format string changes the text even though it would still parse back.
        var day = new RealizedVolatilityDay
        {
            Symbol = "SPX",
            Date = new DateTime(2024, 3, 4),
            TotalVariance = 0.1,
            IntradayVariance = 0.2,
            BipowerVariation = 0.3,
            JumpVariation = 0.7,
            UpsideVariance = 0.11,
            DownsideVariance = 0.12,
            RealizedQuarticity = 0.13,
            OvernightReturn = 0.14,
            CloseToCloseReturn = 0.16,
            DividendAdjustment = 0.17,
            ReturnCount = 78,
            StaleSamples = 4,
            SessionOpen = 0.18,
            SessionClose = 0.19,
            FirstBarTime = new DateTime(2024, 3, 4, 9, 31, 0),
            LastBarTime = new DateTime(2024, 3, 4, 16, 0, 0),
        };

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        try
        {
            RealizedVolatilityCsv.Write(path, [day]);
            var fields = File.ReadAllLines(path)[1].Split(',');

            Assert.Equal(day.AnnualizedVolatility.ToString("G17", Inv), fields[2]);
            Assert.Equal(0.1.ToString("G17", Inv), fields[3]);
            Assert.Equal(0.2.ToString("G17", Inv), fields[4]);
            Assert.Equal(0.3.ToString("G17", Inv), fields[5]);
            Assert.Equal(0.7.ToString("G17", Inv), fields[6]);
            Assert.Equal(0.11.ToString("G17", Inv), fields[7]);
            Assert.Equal(0.12.ToString("G17", Inv), fields[8]);
            Assert.Equal(0.13.ToString("G17", Inv), fields[9]);
            Assert.Equal(0.14.ToString("G17", Inv), fields[10]);
            Assert.Equal(0.16.ToString("G17", Inv), fields[11]);
            Assert.Equal(0.17.ToString("G17", Inv), fields[12]);
            Assert.Equal(0.18.ToString("G17", Inv), fields[15]);
            Assert.Equal(0.19.ToString("G17", Inv), fields[16]);

            // Sanity: the format really is doing something, or these assertions are vacuous.
            Assert.NotEqual(0.1.ToString(Inv), 0.1.ToString("G17", Inv));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CsvWriteBlamesTheParameterAtFault()
    {
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => RealizedVolatilityCsv.Write(null!, [])).ParamName);
        Assert.Equal("days", Assert.Throws<ArgumentNullException>(() => RealizedVolatilityCsv.Write("x.csv", null!)).ParamName);
    }

    // ---------- diagnostics and scaling contracts ----------

    [Fact]
    public void RemainingGuardsBlameTheParameterAtFault()
    {
        Assert.Equal("days",
            Assert.Throws<ArgumentNullException>(() => SeriesDiagnostics.Summarize(null!)).ParamName);
        Assert.Equal("returns",
            Assert.Throws<ArgumentNullException>(() => RealizedVolatilityEstimator.FromReturns(null!)).ParamName);
        Assert.Equal("grids",
            Assert.Throws<ArgumentNullException>(() => RealizedVolatilityEstimator.Average(null!)).ParamName);
        Assert.Equal("meanDailyVariance",
            Assert.Throws<ArgumentOutOfRangeException>(() => VolatilityScaling.AnnualizeVolatility(-1.0)).ParamName);
        Assert.Equal("calendarDays",
            Assert.Throws<ArgumentOutOfRangeException>(() => VolatilityScaling.CalendarDaysToTradingDays(0)).ParamName);
        Assert.Equal("sourceVariance",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new VolatilityComparisonResult().TransferVariance(0.0)).ParamName);
        Assert.Equal("source",
            Assert.Throws<ArgumentNullException>(() => VolatilityComparison.Compare(null!, [])).ParamName);
        Assert.Equal("target",
            Assert.Throws<ArgumentNullException>(() => VolatilityComparison.Compare([], null!)).ParamName);
        Assert.Equal("expirations",
            Assert.Throws<ArgumentNullException>(() => ConstantMaturityVariance.Interpolate(null!)).ParamName);
        Assert.Equal("rates",
            Assert.Throws<ArgumentNullException>(() => new HistoricalRiskFreeRate(null!)).ParamName);
        Assert.Equal("slices",
            Assert.Throws<ArgumentNullException>(() =>
                new ImpliedVarianceSeriesBuilder(new FlatRiskFreeRate(0.03)).BuildDay("SPX", Start, null!)).ParamName);
        Assert.Equal("chainsByDate",
            Assert.Throws<ArgumentNullException>(() =>
                new ImpliedVarianceSeriesBuilder(new FlatRiskFreeRate(0.03)).Build("SPX", null!)).ParamName);
        Assert.Equal("rates",
            Assert.Throws<ArgumentNullException>(() => new ImpliedVarianceSeriesBuilder(null!)).ParamName);
    }

    [Fact]
    public void RemainingFailuresExplainWhatIsWrong()
    {
        Assert.Contains("empty series",
            Assert.Throws<ArgumentException>(() => SeriesDiagnostics.Summarize([])).Message, StringComparison.Ordinal);

        Assert.Contains("at least three overlapping",
            Assert.Throws<InvalidOperationException>(() => VolatilityComparison.Compare(
                [new RealizedVolatilityDay { Date = Start, TotalVariance = 1e-4, IsComplete = true }],
                [new RealizedVolatilityDay { Date = Start, TotalVariance = 1e-4, IsComplete = true }])).Message,
            StringComparison.Ordinal);

        Assert.Contains("Two expirations with positive variance",
            Assert.Throws<InvalidOperationException>(() => ConstantMaturityVariance.Interpolate([])).Message,
            StringComparison.Ordinal);

        Assert.Contains("At least one rate observation",
            Assert.Throws<ArgumentException>(() => new HistoricalRiskFreeRate([])).Message, StringComparison.Ordinal);
    }
}
