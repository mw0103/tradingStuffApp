using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Tests.Volatility;
using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// Pins the two new exploratory candidates: B1 (equal-weight HARX+VIX) and A1 (HARQ-X).
/// </summary>
/// <remarks>
/// Both are dev-tier candidates from <c>docs/research/model-candidates.md</c>. Nothing here
/// registers a variant; these tests pin mechanics — the average really is the average, the
/// attenuation term really is train-centred, dependency ordering fails loudly — so that when a
/// dev run produces a number, the number is about the idea and not about a wiring mistake.
/// </remarks>
public class CandidateMethodTests
{
    private const string Calendar = SessionBars.CboeIndex;

    private static (List<VolResidualRawRow> Train, List<VolResidualRawRow> Test) FixedRows()
    {
        var dates = SessionBars.TradingDates(120, from: new DateOnly(2015, 1, 5), calendar: Calendar);
        var spx = VolatilityPresets.BuildSpxStudyTarget(
            SessionBars.Clock,
            dates.SelectMany((d, i) => SessionBars.Wiggly(d, baseline: 100.0 + i * 0.1, calendar: Calendar)));
        var vix = dates
            .Select((d, i) => (Date: d, Value: 15.0 + Math.Sin(i * 0.3) * 3.0 + i * 0.02))
            .ToDictionary(x => x.Date, x => x.Value);

        var rows = VolResidualFeatureBuilder.BuildRawRows(spx, vix);
        return (rows.Take(60).ToList(), rows.Skip(60).Take(10).ToList());
    }

    private static VolResidualFoldContext FittedContext(params VolResidualMethod[] extra)
    {
        var (train, test) = FixedRows();
        var context = VolResidualFoldContext.Build(new VolResidualFoldSplit(
            new WalkForwardFold { Name = "candidates" }, train, test));

        foreach (var method in VolResidualMethodCatalog.Registered.Concat(extra))
        {
            context.Fitted[method.Key] = method.Fit(context);
        }

        return context;
    }

    // ---------- B1 ----------

    [Fact]
    public void TheEqualWeightForecastIsExactlyTheAverageOfItsMembers()
    {
        var context = FittedContext(new EqualWeightMethod());
        var harx = context.Fitted[VolResidualModelKeys.HarX];
        var vix = context.Fitted[VolResidualModelKeys.Vix];
        var ew = context.Fitted[VolResidualModelKeys.EqualWeight];

        foreach (var row in context.Test)
        {
            Assert.Equal(0.5 * (harx.Forecast(row) + vix.Forecast(row)), ew.Forecast(row), 15);
        }
    }

    [Fact]
    public void TheEqualWeightMethodHasNothingOfItsOwn()
    {
        var context = FittedContext(new EqualWeightMethod());
        var ew = context.Fitted[VolResidualModelKeys.EqualWeight];

        // No log fit to correct, no floor: a pure combination exposes only its forecast. This is
        // what keeps it a zero-parameter candidate rather than a model.
        Assert.Null(ew.LogForecast);
        Assert.Null(ew.FloorBinds);
        Assert.False(new EqualWeightMethod().Registered);
    }

    [Fact]
    public void TheEqualWeightMethodFailsLoudlyWithoutItsMembers()
    {
        var (train, test) = FixedRows();
        var context = VolResidualFoldContext.Build(new VolResidualFoldSplit(
            new WalkForwardFold { Name = "bare" }, train, test));

        var ex = Assert.Throws<InvalidOperationException>(() => new EqualWeightMethod().Fit(context));
        Assert.Contains("Catalog order is load-bearing", ex.Message, StringComparison.Ordinal);
    }

    // ---------- A1 (HARQ-X) ----------

    [Fact]
    public void HarqxCollapsesTowardHarxWhenQuarticityCarriesNoInformation()
    {
        // With RqDMinus1 identical on every row, the demeaned interaction is exactly zero
        // everywhere and OLS on the remaining columns sees HAR-X's design. The forecasts are not
        // bit-identical to the gate's - HAR-X fits under NNLS, this under OLS - but the
        // interaction can contribute nothing.
        var (train, test) = FixedRows();

        VolResidualRawRow Flat(VolResidualRawRow r) => r with { RqDMinus1 = 2.5e-8 };
        var flatTrain = train.Select(Flat).ToList();
        var flatTest = test.Select(Flat).ToList();

        var context = VolResidualFoldContext.Build(new VolResidualFoldSplit(
            new WalkForwardFold { Name = "flat-rq" }, flatTrain, flatTest));
        foreach (var method in VolResidualMethodCatalog.Registered) context.Fitted[method.Key] = method.Fit(context);

        var harqx = new HarqxMethod().Fit(context);

        // Zero-information quarticity: the interaction column is all zeros, so the fit is the
        // OLS HAR-X fit. Compare against exactly that, built by hand.
        var olsHarx = new HarMethodShim(context);
        foreach (var row in flatTest)
        {
            Assert.Equal(olsHarx.Forecast(row), harqx.Forecast(row), 12);
        }
    }

    /// <summary>OLS over HAR-X's six features with the standard retransformation — the collapse target.</summary>
    private sealed class HarMethodShim
    {
        private readonly Func<VolResidualRawRow, double> _forecast;

        public HarMethodShim(VolResidualFoldContext context)
        {
            double[] Features(VolResidualRawRow r) =>
                [r.LogRvDMinus1, r.MeanLogRv5, r.MeanLogRv22, r.LogPriorVix2, r.Vix5DayChange, context.Divergence(r)];

            var coefficients = TradingStuff.Volatility.Baselines.OrdinaryLeastSquares.Fit(
                context.Train.Select(Features).ToList(), context.TrainLogTargets.ToList());
            double Log(VolResidualRawRow r) =>
                TradingStuff.Volatility.Baselines.OrdinaryLeastSquares.Predict(coefficients, Features(r));
            var factor = QlikeRetransformation.FitFactor(
                context.TrainActuals.ToList(), context.Train.Select(r => Math.Exp(Log(r))).ToList());

            _forecast = r => factor * Math.Exp(Log(r));
        }

        public double Forecast(VolResidualRawRow r) => _forecast(r);
    }

    [Fact]
    public void HarqxRespondsToQuarticityWhereHarxCannot()
    {
        // Two test rows identical except for quarticity must produce different HARQ-X forecasts
        // and identical HAR-X forecasts. This is the entire point of the candidate, asserted
        // directly.
        var context = FittedContext(new HarqxMethod());
        var harx = context.Fitted[VolResidualModelKeys.HarX];
        var harqx = context.Fitted[VolResidualModelKeys.HarqX];

        var row = context.Test[0];
        var noisy = row with { RqDMinus1 = row.RqDMinus1 * 100.0 + 1e-6 };

        Assert.Equal(harx.Forecast(row), harx.Forecast(noisy), 15);
        Assert.NotEqual(harqx.Forecast(row), harqx.Forecast(noisy));
    }

    [Fact]
    public void TheAttenuationCentreIsTrainFrozen()
    {
        // Corrupting TEST quarticity must not move the forecast for an untouched test row: the
        // demeaning centre comes from the training window only.
        var (train, test) = FixedRows();

        VolResidualFoldContext Build(List<VolResidualRawRow> testRows)
        {
            var context = VolResidualFoldContext.Build(new VolResidualFoldSplit(
                new WalkForwardFold { Name = "frozen" }, train, testRows));
            foreach (var method in VolResidualMethodCatalog.Registered) context.Fitted[method.Key] = method.Fit(context);
            context.Fitted[VolResidualModelKeys.HarqX] = new HarqxMethod().Fit(context);
            return context;
        }

        var original = Build(test);
        var corrupted = Build(test.Select((r, i) => i == 0 ? r : r with { RqDMinus1 = r.RqDMinus1 * 1e3 + 1.0 }).ToList());

        Assert.Equal(
            original.Fitted[VolResidualModelKeys.HarqX].Forecast(test[0]),
            corrupted.Fitted[VolResidualModelKeys.HarqX].Forecast(test[0]), 15);
    }

    [Fact]
    public void QuarticityReachesTheRowsFromTheEstimator()
    {
        var (train, test) = FixedRows();

        // The plumb-through is real: rows carry the estimator's quarticity, not the default.
        Assert.Contains(train.Concat(test), r => r.RqDMinus1 > 0.0);
    }

    [Fact]
    public void BothCandidatesAreExploratoryAndInTheCatalog()
    {
        Assert.False(new EqualWeightMethod().Registered);
        Assert.False(new HarqxMethod().Registered);

        var exploratoryKeys = VolResidualMethodCatalog.Exploratory.Select(m => m.Key).ToList();
        Assert.Contains(VolResidualModelKeys.EqualWeight, exploratoryKeys);
        Assert.Contains(VolResidualModelKeys.HarqX, exploratoryKeys);

        // And the registered catalog is untouched: still exactly the four adjudicated models.
        Assert.Equal(
            [VolResidualModelKeys.Har, VolResidualModelKeys.HarX, VolResidualModelKeys.Vix, VolResidualModelKeys.Corrected],
            VolResidualMethodCatalog.Registered.Select(m => m.Key).OrderBy(k => k switch
            {
                VolResidualModelKeys.Har => 0, VolResidualModelKeys.HarX => 1,
                VolResidualModelKeys.Vix => 2, _ => 3,
            }));
    }
}
