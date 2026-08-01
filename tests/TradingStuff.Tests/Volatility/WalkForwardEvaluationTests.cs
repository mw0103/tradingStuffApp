using TradingStuff.Volatility.Baselines;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the walk-forward split, the fold scoring, and the grading against the gate baseline.
/// </summary>
/// <remarks>
/// The purge and embargo gaps carry the weight here. A sample dated t is labelled with variance
/// realized after t, so the last training samples before a validation block are labelled with
/// information from inside it. Without the gaps the leak is invisible: everything runs, the
/// numbers look better, and the improvement is the leak.
/// </remarks>
public class WalkForwardEvaluationTests
{
    private static readonly DateTime Origin = new(2010, 1, 1);

    /// <summary>One sample per calendar day, so index arithmetic and dates line up.</summary>
    private static List<HarSample> DailySeries(int days, Func<int, double>? logVariance = null)
    {
        logVariance ??= i => -9.0 + Math.Sin(i / 30.0) * 0.5;
        return Enumerable.Range(0, days).Select(i =>
        {
            var v = logVariance(i);
            return new HarSample
            {
                Date = Origin.AddDays(i),
                Features = [v, v, v],
                Target = v,
                ForwardVariance = Math.Exp(v),
                RandomWalkForecast = Math.Exp(v),
            };
        }).ToList();
    }

    private static WalkForwardFold Fold(int trainDays, int validationDays, int testDays) => new()
    {
        Name = "T",
        TrainStart = Origin,
        TrainEnd = Origin.AddDays(trainDays - 1),
        ValidationStart = Origin.AddDays(trainDays),
        ValidationEnd = Origin.AddDays(trainDays + validationDays - 1),
        TestStart = Origin.AddDays(trainDays + validationDays),
        TestEnd = Origin.AddDays(trainDays + validationDays + testDays - 1),
    };

    // ---------- registered folds ----------

    [Fact]
    public void TheRegisteredFoldsAreExpandingOrigin()
    {
        var folds = WalkForwardFold.Registered();

        Assert.Equal(3, folds.Count);
        Assert.Equal(["F1", "F2", "F3"], folds.Select(f => f.Name));

        // Every fold trains from the same start and each reaches further forward.
        Assert.All(folds, f => Assert.Equal(new DateTime(2010, 1, 1), f.TrainStart));
        for (int i = 1; i < folds.Count; i++)
        {
            Assert.True(folds[i].TrainEnd > folds[i - 1].TrainEnd);
            Assert.True(folds[i].TestStart > folds[i - 1].TestStart);
        }
    }

    [Fact]
    public void EveryRegisteredFoldOrdersTrainThenValidationThenTest()
    {
        foreach (var f in WalkForwardFold.Registered())
        {
            Assert.True(f.TrainEnd < f.ValidationStart, f.Name);
            Assert.True(f.ValidationEnd < f.TestStart, f.Name);
            Assert.True(f.TestStart < f.TestEnd, f.Name);
        }
    }

    [Fact]
    public void CovidFallsInsideATestBlockNotATrainingBlock()
    {
        // A regime break belongs where it measures whether the model survives one.
        var covid = new DateTime(2020, 3, 16);
        var f2 = WalkForwardFold.Registered().Single(f => f.Name == "F2");

        Assert.True(covid >= f2.TestStart && covid <= f2.TestEnd);
        Assert.True(covid > f2.TrainEnd);
    }

    // ---------- splitting ----------

    [Fact]
    public void SplitRejectsMalformedInput()
    {
        Assert.Equal("samples",
            Assert.Throws<ArgumentNullException>(() => WalkForwardSplitter.Split(null!, Fold(10, 5, 5))).ParamName);
        Assert.Equal("fold",
            Assert.Throws<ArgumentNullException>(() => WalkForwardSplitter.Split(DailySeries(20), null!)).ParamName);
        Assert.Equal("purgeTradingDays",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                WalkForwardSplitter.Split(DailySeries(20), Fold(10, 5, 5), -1)).ParamName);
        Assert.Equal("embargoTradingDays",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                WalkForwardSplitter.Split(DailySeries(20), Fold(10, 5, 5), 0, -1)).ParamName);
    }

    [Fact]
    public void BlocksAreCutOnTheirDateRanges()
    {
        var split = WalkForwardSplitter.Split(DailySeries(40), Fold(20, 10, 10), purgeTradingDays: 0, embargoTradingDays: 0);

        Assert.Equal(20, split.Train.Count);
        Assert.Equal(10, split.Validation.Count);
        Assert.Equal(10, split.Test.Count);
        Assert.Equal(Origin, split.Train[0].Date);
        Assert.Equal(Origin.AddDays(20), split.Validation[0].Date);
        Assert.Equal(Origin.AddDays(30), split.Test[0].Date);
    }

    [Fact]
    public void ThePurgeRemovesTheTailOfTraining()
    {
        var split = WalkForwardSplitter.Split(DailySeries(40), Fold(20, 10, 10), purgeTradingDays: 5, embargoTradingDays: 0);

        Assert.Equal(15, split.Train.Count);
        Assert.Equal(5, split.PurgedFromTrain);
        // The gap sits immediately before validation, which is where the leak would be.
        Assert.Equal(Origin.AddDays(14), split.Train[^1].Date);
        Assert.Equal(Origin.AddDays(20), split.Validation[0].Date);
    }

    [Fact]
    public void TheEmbargoRemovesTheTailOfValidation()
    {
        var split = WalkForwardSplitter.Split(DailySeries(40), Fold(20, 10, 10), purgeTradingDays: 0, embargoTradingDays: 3);

        Assert.Equal(7, split.Validation.Count);
        Assert.Equal(3, split.EmbargoedFromValidation);
        Assert.Equal(Origin.AddDays(26), split.Validation[^1].Date);
    }

    [Fact]
    public void TheTestBlockIsNeverTrimmed()
    {
        var split = WalkForwardSplitter.Split(DailySeries(40), Fold(20, 10, 10), purgeTradingDays: 5, embargoTradingDays: 5);

        // Trimming test would change what is being measured, not protect it.
        Assert.Equal(10, split.Test.Count);
        Assert.Equal(Origin.AddDays(30), split.Test[0].Date);
        Assert.Equal(Origin.AddDays(39), split.Test[^1].Date);
    }

    [Fact]
    public void GapsLargerThanTheirBlockEmptyItRatherThanThrowing()
    {
        var split = WalkForwardSplitter.Split(
            DailySeries(40), Fold(20, 10, 10), purgeTradingDays: 500, embargoTradingDays: 500);

        Assert.Empty(split.Train);
        Assert.Empty(split.Validation);
        Assert.Equal(20, split.PurgedFromTrain);
        Assert.Equal(10, split.EmbargoedFromValidation);
    }

    [Fact]
    public void TheDefaultGapsAreFiveTradingDays()
    {
        var split = WalkForwardSplitter.Split(DailySeries(40), Fold(20, 10, 10));

        Assert.Equal(5, split.PurgedFromTrain);
        Assert.Equal(5, split.EmbargoedFromValidation);
    }

    [Fact]
    public void UnorderedInputIsSortedBeforeCutting()
    {
        var shuffled = DailySeries(40).OrderByDescending(s => s.Date).ToList();

        var split = WalkForwardSplitter.Split(shuffled, Fold(20, 10, 10), 0, 0);

        Assert.Equal(Origin, split.Train[0].Date);
        Assert.True(split.Train.Zip(split.Train.Skip(1)).All(p => p.First.Date < p.Second.Date));
    }

    [Fact]
    public void TheSplitCarriesItsFold()
    {
        var fold = Fold(20, 10, 10);

        Assert.Same(fold, WalkForwardSplitter.Split(DailySeries(40), fold).Fold);
    }

    // ---------- scoring ----------

    private static List<WalkForwardFold> TwoFolds() =>
    [
        new WalkForwardFold
        {
            Name = "A",
            TrainStart = Origin, TrainEnd = Origin.AddDays(59),
            ValidationStart = Origin.AddDays(60), ValidationEnd = Origin.AddDays(79),
            TestStart = Origin.AddDays(80), TestEnd = Origin.AddDays(119),
        },
        new WalkForwardFold
        {
            Name = "B",
            TrainStart = Origin, TrainEnd = Origin.AddDays(119),
            ValidationStart = Origin.AddDays(120), ValidationEnd = Origin.AddDays(139),
            TestStart = Origin.AddDays(140), TestEnd = Origin.AddDays(179),
        },
    ];

    [Fact]
    public void ScoreRejectsMalformedInput()
    {
        Assert.Equal("modelFactory",
            Assert.Throws<ArgumentNullException>(() =>
                WalkForwardEvaluation.Score(null!, DailySeries(200), TwoFolds())).ParamName);
        Assert.Equal("samples",
            Assert.Throws<ArgumentNullException>(() =>
                WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), null!, TwoFolds())).ParamName);
        Assert.Equal("folds",
            Assert.Throws<ArgumentNullException>(() =>
                WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), DailySeries(200), null!)).ParamName);
    }

    [Fact]
    public void EachFoldIsScoredOverItsOwnTestBlock()
    {
        var scores = WalkForwardEvaluation.Score(
            () => new MeanLogVarianceModel(), DailySeries(200), TwoFolds());

        Assert.Equal(2, scores.Count);
        Assert.Equal(["A", "B"], scores.Select(s => s.FoldName));
        Assert.All(scores, s => Assert.Equal(40, s.Observations));
        Assert.All(scores, s => Assert.Equal(40, s.DailyQuasiLikelihood.Count));
        Assert.All(scores, s => Assert.Equal("rung0-mean", s.ModelName));
        Assert.Equal(Origin.AddDays(80), scores[0].Dates[0]);
    }

    [Fact]
    public void AModelIsRefitPerFoldAndNothingCarriesAcross()
    {
        var fitted = new List<int>();

        WalkForwardEvaluation.Score(
            () => new RecordingModel(fitted), DailySeries(200), TwoFolds());

        // One fresh instance per fold, each seeing only that fold's training block.
        Assert.Equal(2, fitted.Count);
        Assert.True(fitted[1] > fitted[0]);
    }

    private sealed class RecordingModel(List<int> trainSizes) : IVarianceForecastModel
    {
        public string Name => "recording";

        public void Fit(IReadOnlyList<HarSample> train) => trainSizes.Add(train.Count);

        public IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples) =>
            samples.Select(s => s.Target).ToList();
    }

    [Fact]
    public void FoldsWithoutBothBlocksAreSkipped()
    {
        // A fold whose test range falls beyond the data has nothing to measure.
        List<WalkForwardFold> folds =
        [
            new WalkForwardFold
            {
                Name = "beyond",
                TrainStart = Origin, TrainEnd = Origin.AddDays(50),
                ValidationStart = Origin.AddDays(51), ValidationEnd = Origin.AddDays(60),
                TestStart = Origin.AddYears(20), TestEnd = Origin.AddYears(21),
            },
        ];

        Assert.Empty(WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), DailySeries(200), folds));
    }

    [Fact]
    public void APerfectForecastScoresZeroOnBothLosses()
    {
        var scores = WalkForwardEvaluation.Score(
            () => new RecordingModel([]), DailySeries(200), TwoFolds());

        // RecordingModel returns each sample's own target, so it is exactly right.
        Assert.All(scores, s => Assert.Equal(0.0, s.QuasiLikelihoodLoss, 12));
        Assert.All(scores, s => Assert.Equal(0.0, s.LogMeanSquaredError, 12));
    }

    [Fact]
    public void TheLossesAreQlikeAndSquaredLogError()
    {
        var samples = DailySeries(200);
        var scores = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), samples, TwoFolds());
        var fold = TwoFolds()[0];

        var split = WalkForwardSplitter.Split(samples, fold);
        var model = new MeanLogVarianceModel();
        model.Fit(split.Train);
        var forecasts = model.PredictLogVariance(split.Test);

        var expectedQlike = split.Test
            .Select((s, i) => HarRvModel.QuasiLikelihood(s.ForwardVariance, Math.Exp(forecasts[i])))
            .Average();
        var expectedMse = split.Test.Select((s, i) => Math.Pow(s.Target - forecasts[i], 2)).Average();

        Assert.Equal(expectedQlike, scores[0].QuasiLikelihoodLoss, 12);
        Assert.Equal(expectedMse, scores[0].LogMeanSquaredError, 12);
    }

    [Fact]
    public void AModelReturningTheWrongNumberOfForecastsIsRejected()
    {
        Assert.Contains("forecasts for",
            Assert.Throws<InvalidOperationException>(() => WalkForwardEvaluation.Score(
                () => new ShortModel(), DailySeries(200), TwoFolds())).Message,
            StringComparison.Ordinal);
    }

    private sealed class ShortModel : IVarianceForecastModel
    {
        public string Name => "short";
        public void Fit(IReadOnlyList<HarSample> train) { }
        public IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples) =>
            samples.Skip(1).Select(s => s.Target).ToList();
    }

    // ---------- grading ----------

    [Fact]
    public void GradeRejectsMisalignedInput()
    {
        var baseline = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), DailySeries(200), TwoFolds());

        Assert.Equal("candidate",
            Assert.Throws<ArgumentNullException>(() => WalkForwardEvaluation.Grade("x", null!, baseline)).ParamName);
        Assert.Equal("baseline",
            Assert.Throws<ArgumentNullException>(() => WalkForwardEvaluation.Grade("x", baseline, null!)).ParamName);
        Assert.Contains("no scored folds",
            Assert.Throws<ArgumentException>(() => WalkForwardEvaluation.Grade("x", [], baseline)).Message,
            StringComparison.Ordinal);
        Assert.Contains("same folds",
            Assert.Throws<ArgumentException>(() =>
                WalkForwardEvaluation.Grade("x", baseline, [baseline[0]])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GradeRejectsFoldsThatDoNotLineUp()
    {
        var baseline = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), DailySeries(200), TwoFolds());
        var swapped = new List<FoldScore> { baseline[1], baseline[0] };

        Assert.Contains("not aligned",
            Assert.Throws<ArgumentException>(() => WalkForwardEvaluation.Grade("x", swapped, baseline)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ABetterModelReportsAPositiveGainAndImprovedFolds()
    {
        var samples = DailySeries(200);
        var baseline = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), samples, TwoFolds());
        var candidate = WalkForwardEvaluation.Score(() => new HarForecastModel(), samples, TwoFolds());

        var evaluation = WalkForwardEvaluation.Grade("rung2-har", candidate, baseline);

        // HAR sees the level directly through its features; the constant cannot.
        Assert.True(evaluation.PooledQlikeGain > 0.0);
        Assert.Equal(2, evaluation.FoldsImproved);
        Assert.True(evaluation.DieboldMariano.CandidateHasLowerLoss);
        Assert.Equal("rung2-har", evaluation.ModelName);
    }

    [Fact]
    public void ComparingAModelWithItselfShowsNoGain()
    {
        var samples = DailySeries(200);
        var scores = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), samples, TwoFolds());

        var evaluation = WalkForwardEvaluation.Grade("self", scores, scores);

        Assert.Equal(0.0, evaluation.PooledQlikeGain, 12);
        Assert.Equal(0, evaluation.FoldsImproved);
        Assert.Equal(1.0, evaluation.DieboldMariano.PValue);
        Assert.Equal(0.0, evaluation.LargestYearShareOfGain);
    }

    [Fact]
    public void ThePooledLossIsOverEveryTestDayNotAnAverageOfFolds()
    {
        var samples = DailySeries(200);
        var scores = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), samples, TwoFolds());

        var evaluation = WalkForwardEvaluation.Grade("pooled", scores, scores);

        var allDays = scores.SelectMany(s => s.DailyQuasiLikelihood).ToList();
        Assert.Equal(allDays.Average(), evaluation.PooledQuasiLikelihoodLoss, 12);
    }

    [Fact]
    public void AGainConcentratedInOneYearIsReportedAsSuch()
    {
        // Two years of test days, with the whole improvement landing in the second.
        var dates = Enumerable.Range(0, 200).Select(i => new DateTime(2020, 1, 1).AddDays(i * 3)).ToList();
        var baselineDaily = Enumerable.Repeat(0.5, dates.Count).ToList();
        var candidateDaily = dates.Select(d => d.Year == 2021 ? 0.4 : 0.5).ToList();

        var baseline = new List<FoldScore>
        {
            new() { FoldName = "A", ModelName = "b", Observations = dates.Count, DailyQuasiLikelihood = baselineDaily, Dates = dates, QuasiLikelihoodLoss = baselineDaily.Average() },
        };
        var candidate = new List<FoldScore>
        {
            new() { FoldName = "A", ModelName = "c", Observations = dates.Count, DailyQuasiLikelihood = candidateDaily, Dates = dates, QuasiLikelihoodLoss = candidateDaily.Average() },
        };

        var evaluation = WalkForwardEvaluation.Grade("concentrated", candidate, baseline);

        // The registered falsification threshold is 50%: a gain from one year is a regime
        // artifact, not an edge.
        Assert.Equal(1.0, evaluation.LargestYearShareOfGain, 9);
    }

    [Fact]
    public void AnEvenlySpreadGainIsNotFlaggedAsConcentrated()
    {
        var dates = Enumerable.Range(0, 200).Select(i => new DateTime(2020, 1, 1).AddDays(i * 3)).ToList();
        var baselineDaily = Enumerable.Repeat(0.5, dates.Count).ToList();
        var candidateDaily = Enumerable.Repeat(0.4, dates.Count).ToList();

        var baseline = new List<FoldScore>
        {
            new() { FoldName = "A", ModelName = "b", Observations = dates.Count, DailyQuasiLikelihood = baselineDaily, Dates = dates, QuasiLikelihoodLoss = 0.5 },
        };
        var candidate = new List<FoldScore>
        {
            new() { FoldName = "A", ModelName = "c", Observations = dates.Count, DailyQuasiLikelihood = candidateDaily, Dates = dates, QuasiLikelihoodLoss = 0.4 },
        };

        var share = WalkForwardEvaluation.Grade("spread", candidate, baseline).LargestYearShareOfGain;

        Assert.True(share < 0.75, $"share was {share:P1}");
        Assert.True(share > 0.25);
    }

    [Fact]
    public void TheEvaluationRendersItsHeadlineNumbers()
    {
        var samples = DailySeries(200);
        var baseline = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), samples, TwoFolds());
        var candidate = WalkForwardEvaluation.Score(() => new HarForecastModel(), samples, TwoFolds());

        var text = WalkForwardEvaluation.Grade("rung2-har", candidate, baseline).ToString();

        Assert.Contains("rung2-har", text, StringComparison.Ordinal);
        Assert.Contains("QLIKE=", text, StringComparison.Ordinal);
        Assert.Contains("folds improved=2/2", text, StringComparison.Ordinal);
        Assert.Contains("DM=", text, StringComparison.Ordinal);
    }

    // ---------- the ladder, end to end ----------

    /// <summary>
    /// A persistent, mean-reverting log-variance series with genuinely lagged HAR features,
    /// so the regressors carry information the target does not already contain.
    /// </summary>
    private static List<HarSample> AutoregressiveSeries(int days, int seed = 31)
    {
        var rng = new Random(seed);
        var history = new List<double>();
        var level = -9.0;

        // Burn in, so the first sample is not an artifact of the starting value.
        for (int i = 0; i < 40; i++)
        {
            level = -9.0 + 0.92 * (level + 9.0) + (rng.NextDouble() - 0.5) * 0.25;
            history.Add(level);
        }

        var samples = new List<HarSample>(days);
        for (int i = 0; i < days; i++)
        {
            var daily = history[^1];
            var weekly = history.TakeLast(5).Average();
            var monthly = history.TakeLast(22).Average();

            level = -9.0 + 0.92 * (level + 9.0) + (rng.NextDouble() - 0.5) * 0.25;
            history.Add(level);

            samples.Add(new HarSample
            {
                Date = Origin.AddDays(i),
                Features = [daily, weekly, monthly],
                Target = level,
                ForwardVariance = Math.Exp(level),
                RandomWalkForecast = Math.Exp(daily),
            });
        }

        return samples;
    }

    [Fact]
    public void TheLadderRunsAndGradesEveryRungAgainstTheGateBaseline()
    {
        var samples = AutoregressiveSeries(400);
        var folds = TwoFolds();

        var gate = WalkForwardEvaluation.Score(() => new HarForecastModel(), samples, folds);
        var gatePooled = gate.SelectMany(s => s.DailyQuasiLikelihood).Average();

        var rungs = new (string Name, Func<IVarianceForecastModel> Factory)[]
        {
            ("rung0-mean", () => new MeanLogVarianceModel()),
            ("rung1-rolling22", () => new RollingMeanLogVarianceModel()),
            ("rung1-ewma0.94", () => new EwmaLogVarianceModel()),
        };

        foreach (var (name, factory) in rungs)
        {
            var scores = WalkForwardEvaluation.Score(factory, samples, folds);
            var evaluation = WalkForwardEvaluation.Grade(name, scores, gate);

            Assert.Equal(name, evaluation.ModelName);
            Assert.Equal(folds.Count, evaluation.Folds.Count);
            Assert.InRange(evaluation.DieboldMariano.PValue, 0.0, 1.0);

            // The reported gain must agree with the pooled losses it claims to summarize.
            var expectedGain = (gatePooled - evaluation.PooledQuasiLikelihoodLoss) / gatePooled;
            Assert.Equal(expectedGain, evaluation.PooledQlikeGain, 12);

            // And the fold count must agree with a direct comparison.
            var expectedImproved = scores
                .Where((s, i) => s.QuasiLikelihoodLoss < gate[i].QuasiLikelihoodLoss)
                .Count();
            Assert.Equal(expectedImproved, evaluation.FoldsImproved);
        }
    }

    [Fact]
    public void TheGateBaselineBeatsTheUnconditionalMeanOnAPersistentSeries()
    {
        // The arrangement the ladder assumes: on a series with real persistence, HAR's lags
        // carry information a constant cannot. If this ever inverted, the gate would be
        // measuring something other than skill.
        var samples = AutoregressiveSeries(400);
        var folds = TwoFolds();

        var gate = WalkForwardEvaluation.Score(() => new HarForecastModel(), samples, folds);
        var mean = WalkForwardEvaluation.Score(() => new MeanLogVarianceModel(), samples, folds);

        var evaluation = WalkForwardEvaluation.Grade("rung0-mean", mean, gate);

        Assert.True(evaluation.PooledQlikeGain < 0.0,
            $"the unconditional mean matched the gate baseline (gain {evaluation.PooledQlikeGain:P2})");
        Assert.False(evaluation.DieboldMariano.CandidateHasLowerLoss);
    }
}
