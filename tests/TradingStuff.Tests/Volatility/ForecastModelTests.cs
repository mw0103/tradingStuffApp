using TradingStuff.Volatility.Baselines;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the baseline ladder's rungs 0 through 2.
/// </summary>
/// <remarks>
/// Causality is the property under test. Every rung here may look backward along the series
/// and none may look at the day it is forecasting, so each test checks that a day's own
/// observation enters the state only after its forecast has been made. A model that quietly
/// includes the current day scores beautifully and means nothing.
/// </remarks>
public class ForecastModelTests
{
    private static readonly DateTime Start = new(2024, 1, 1);

    private static HarSample Sample(int day, double logVariance) => new()
    {
        Date = Start.AddDays(day),
        Features = [logVariance, logVariance, logVariance],
        Target = logVariance,
        ForwardVariance = Math.Exp(logVariance),
        RandomWalkForecast = Math.Exp(logVariance),
    };

    private static List<HarSample> Series(params double[] logVariances) =>
        logVariances.Select((v, i) => Sample(i, v)).ToList();

    // ---------- rung 0 ----------

    [Fact]
    public void TheMeanModelPredictsTheTrainingMeanEverywhere()
    {
        var model = new MeanLogVarianceModel();
        model.Fit(Series(-9.0, -8.0, -7.0));

        var forecasts = model.PredictLogVariance(Series(1.0, 2.0, 3.0));

        Assert.Equal("rung0-mean", model.Name);
        Assert.Equal(-8.0, model.Mean, 12);
        Assert.Equal([-8.0, -8.0, -8.0], forecasts);
    }

    [Fact]
    public void TheMeanModelIgnoresTheSamplesItIsPredictingFor()
    {
        var model = new MeanLogVarianceModel();
        model.Fit(Series(-9.0, -9.0));

        // Wildly different test targets must not move a constant forecast.
        Assert.All(model.PredictLogVariance(Series(0.0, 100.0, -100.0)), f => Assert.Equal(-9.0, f, 12));
    }

    [Fact]
    public void AnUnfittedMeanModelRefusesToPredict()
    {
        var model = new MeanLogVarianceModel();

        Assert.Throws<InvalidOperationException>(() => model.Mean);
        Assert.Throws<InvalidOperationException>(() => model.PredictLogVariance(Series(1.0)));
    }

    // ---------- rung 1a ----------

    [Fact]
    public void TheRollingModelRejectsANonPositiveWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingMeanLogVarianceModel(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingMeanLogVarianceModel(-1));
    }

    [Fact]
    public void TheRollingWindowDefaultsToTwentyTwoDays()
    {
        var model = new RollingMeanLogVarianceModel();

        Assert.Equal(22, model.WindowDays);
        Assert.Equal("rung1-rolling22", model.Name);
    }

    [Fact]
    public void TheRollingWindowIsSeededFromTheTailOfTraining()
    {
        var model = new RollingMeanLogVarianceModel(windowDays: 3);
        // Only the last three matter, so the leading -100 must not reach the first forecast.
        model.Fit(Series(-100.0, -9.0, -8.0, -7.0));

        var forecasts = model.PredictLogVariance(Series(0.0));

        Assert.Equal((-9.0 + -8.0 + -7.0) / 3.0, forecasts[0], 12);
    }

    [Fact]
    public void TheRollingForecastPrecedesTheDayItForecasts()
    {
        var model = new RollingMeanLogVarianceModel(windowDays: 2);
        model.Fit(Series(-10.0, -10.0));

        var forecasts = model.PredictLogVariance(Series(0.0, 0.0, 0.0));

        // First forecast is the training tail; each later one folds in only prior test days.
        Assert.Equal(-10.0, forecasts[0], 12);
        Assert.Equal((-10.0 + 0.0) / 2.0, forecasts[1], 12);
        Assert.Equal((0.0 + 0.0) / 2.0, forecasts[2], 12);
    }

    [Fact]
    public void TheRollingWindowDropsTheOldestObservation()
    {
        var model = new RollingMeanLogVarianceModel(windowDays: 2);
        model.Fit(Series(-6.0, -4.0));

        var forecasts = model.PredictLogVariance(Series(10.0, 20.0, 30.0));

        Assert.Equal(-5.0, forecasts[0], 12);
        Assert.Equal((-4.0 + 10.0) / 2.0, forecasts[1], 12);
        Assert.Equal((10.0 + 20.0) / 2.0, forecasts[2], 12);
    }

    [Fact]
    public void AWindowLongerThanTrainingGrowsRatherThanEvicting()
    {
        var model = new RollingMeanLogVarianceModel(windowDays: 5);
        model.Fit(Series(-9.0, -8.0, -7.0));

        var forecasts = model.PredictLogVariance(Series(-6.0, -5.0, -4.0));

        // Seeded with three observations against a window of five, so the next two days are
        // added without evicting anything; only once the window is full does it slide.
        Assert.Equal(-8.0, forecasts[0], 12);                                   // mean of 3
        Assert.Equal((-9.0 + -8.0 + -7.0 + -6.0) / 4.0, forecasts[1], 12);      // mean of 4
        Assert.Equal((-9.0 + -8.0 + -7.0 + -6.0 + -5.0) / 5.0, forecasts[2], 12); // mean of 5
    }

    [Fact]
    public void AnUnfittedRollingModelRefusesToPredict() =>
        Assert.Throws<InvalidOperationException>(() => new RollingMeanLogVarianceModel().PredictLogVariance(Series(1.0)));

    // ---------- rung 1b ----------

    [Fact]
    public void TheEwmaRejectsALambdaOutsideTheUnitInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaLogVarianceModel(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaLogVarianceModel(1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaLogVarianceModel(-0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaLogVarianceModel(1.5));
    }

    [Fact]
    public void TheEwmaDefaultsToTheRiskMetricsDecay()
    {
        var model = new EwmaLogVarianceModel();

        Assert.Equal(0.94, model.Lambda);
        Assert.Equal("rung1-ewma0.94", model.Name);
    }

    [Fact]
    public void TheEwmaSeedRunsTheRecursionThroughTraining()
    {
        var model = new EwmaLogVarianceModel(0.5);
        model.Fit(Series(-8.0, -4.0));

        // level starts at the first observation, then 0.5*-8 + 0.5*-4 = -6.
        Assert.Equal(-6.0, model.PredictLogVariance(Series(0.0))[0], 12);
    }

    [Fact]
    public void TheEwmaForecastPrecedesTheDayItForecasts()
    {
        var model = new EwmaLogVarianceModel(0.5);
        model.Fit(Series(-8.0, -4.0));

        // Non-zero test targets, so the update's sign and both weights are pinned: a zero
        // target satisfies `+` and `-` alike, and any weight multiplied by it vanishes.
        var forecasts = model.PredictLogVariance(Series(-2.0, 6.0, 10.0));

        Assert.Equal(-6.0, forecasts[0], 12);
        // Only after forecasting does the day update the level: 0.5*-6 + 0.5*-2 = -4.
        Assert.Equal(-4.0, forecasts[1], 12);
        // 0.5*-4 + 0.5*6 = 1.
        Assert.Equal(1.0, forecasts[2], 12);
    }

    [Fact]
    public void TheEwmaWeightsSumToOne()
    {
        // A constant series must stay at its level: any weighting that does not sum to one
        // would drift away from it.
        var model = new EwmaLogVarianceModel(0.7);
        model.Fit(Series(-9.0, -9.0, -9.0));

        Assert.All(model.PredictLogVariance(Series(-9.0, -9.0, -9.0)), f => Assert.Equal(-9.0, f, 12));
    }

    [Fact]
    public void ASingleTrainingObservationSeedsTheEwmaDirectly()
    {
        var model = new EwmaLogVarianceModel(0.94);
        model.Fit(Series(-9.0));

        Assert.Equal(-9.0, model.PredictLogVariance(Series(0.0))[0], 12);
    }

    [Fact]
    public void AHigherLambdaReactsMoreSlowly()
    {
        var slow = new EwmaLogVarianceModel(0.99);
        var fast = new EwmaLogVarianceModel(0.5);
        var train = Series(-9.0, -9.0, -9.0);
        slow.Fit(train);
        fast.Fit(train);

        var shock = Series(0.0, 0.0);

        // After the same shock the slow model is still nearer its old level.
        Assert.True(slow.PredictLogVariance(shock)[1] < fast.PredictLogVariance(shock)[1]);
    }

    [Fact]
    public void AnUnfittedEwmaRefusesToPredict() =>
        Assert.Throws<InvalidOperationException>(() => new EwmaLogVarianceModel().PredictLogVariance(Series(1.0)));

    // ---------- rung 2 ----------

    [Fact]
    public void TheHarWrapperDelegatesToTheOneHarImplementation()
    {
        var samples = Enumerable.Range(0, 60).Select(i =>
        {
            double d = -9.0 + (i % 11) * 0.1, w = -9.0 + (i % 7) * 0.1, m = -9.0 + (i % 5) * 0.1;
            var target = -1.0 + 0.5 * d + 0.3 * w + 0.2 * m;
            return new HarSample
            {
                Date = Start.AddDays(i),
                Features = [d, w, m],
                Target = target,
                ForwardVariance = Math.Exp(target),
                RandomWalkForecast = Math.Exp(-9.0),
            };
        }).ToList();

        var wrapper = new HarForecastModel();
        wrapper.Fit(samples);

        Assert.Equal("rung2-har", wrapper.Name);
        Assert.True(wrapper.Model.IsFitted);

        var forecasts = wrapper.PredictLogVariance(samples);
        for (int i = 0; i < samples.Count; i++)
        {
            Assert.Equal(wrapper.Model.PredictLogVariance(samples[i].Features), forecasts[i], 12);
            Assert.Equal(samples[i].Target, forecasts[i], 6);
        }
    }

    [Fact]
    public void TheHarWrapperPassesFeatureNamesThrough()
    {
        var names = new HarDatasetOptions().FeatureNames();
        var wrapper = new HarForecastModel(names);

        wrapper.Fit(Enumerable.Range(0, 20).Select(i => new HarSample
        {
            Date = Start.AddDays(i),
            Features = [-9.0 + i * 0.1, -9.0, -9.0],
            Target = -9.0 + i * 0.05,
        }).ToList());

        Assert.Same(names, wrapper.Model.FeatureNames);
    }

    // ---------- shared guards ----------

    public static TheoryData<IVarianceForecastModel> AllModels() =>
    [
        new MeanLogVarianceModel(),
        new RollingMeanLogVarianceModel(3),
        new EwmaLogVarianceModel(0.5),
        new HarForecastModel(),
    ];

    [Theory]
    [MemberData(nameof(AllModels))]
    public void EveryModelRejectsAnEmptyOrMissingTrainingBlock(IVarianceForecastModel model)
    {
        Assert.Equal("train", Assert.Throws<ArgumentNullException>(() => model.Fit(null!)).ParamName);
        Assert.Contains("empty training block",
            Assert.Throws<ArgumentException>(() => model.Fit([])).Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllModels))]
    public void EveryModelRejectsAnOutOfOrderRun(IVarianceForecastModel model)
    {
        model.Fit(Enumerable.Range(0, 30).Select(i => new HarSample
        {
            Date = Start.AddDays(i),
            Features = [-9.0 + i * 0.01, -9.0, -9.0],
            Target = -9.0 + i * 0.01,
        }).ToList());

        var descending = new List<HarSample> { Sample(5, -9.0), Sample(1, -9.0) };

        Assert.Equal("samples",
            Assert.Throws<ArgumentNullException>(() => model.PredictLogVariance(null!)).ParamName);
        Assert.Contains("ascending by date",
            Assert.Throws<ArgumentException>(() => model.PredictLogVariance(descending)).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllModels))]
    public void EveryModelReturnsOneForecastPerSample(IVarianceForecastModel model)
    {
        var samples = Enumerable.Range(0, 30).Select(i => new HarSample
        {
            Date = Start.AddDays(i),
            Features = [-9.0 + i * 0.01, -9.0, -9.0],
            Target = -9.0 + i * 0.01,
        }).ToList();

        model.Fit(samples);

        Assert.Equal(samples.Count, model.PredictLogVariance(samples).Count);
        Assert.Empty(model.PredictLogVariance([]));
    }

    [Fact]
    public void RepeatedTimestampsAreAcceptedAsOrdered()
    {
        // The guard rejects a decrease, not a tie: two samples on the same date are a data
        // problem for the dataset builder to catch, not a look-ahead.
        var model = new MeanLogVarianceModel();
        model.Fit(Series(-9.0, -9.0));

        Assert.Equal(2, model.PredictLogVariance([Sample(1, -9.0), Sample(1, -9.0)]).Count);
    }
}
