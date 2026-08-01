using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.Volatility.Forecasting
{
    /// <summary>One expanding-origin fold: a train block, a validation block and a test block.</summary>
    public class WalkForwardFold
    {
        public string Name { get; set; }
        public DateTime TrainStart { get; set; }
        public DateTime TrainEnd { get; set; }
        public DateTime ValidationStart { get; set; }
        public DateTime ValidationEnd { get; set; }
        public DateTime TestStart { get; set; }
        public DateTime TestEnd { get; set; }

        /// <summary>
        /// The folds registered for the volatility forecast residual study.
        /// </summary>
        /// <remarks>
        /// COVID falls inside F2's test block deliberately. A regime break belongs in test,
        /// where it measures whether the model survives one, not in train where it would be
        /// quietly learned.
        /// </remarks>
        public static List<WalkForwardFold> Registered()
        {
            return new List<WalkForwardFold>
            {
                new WalkForwardFold
                {
                    Name = "F1",
                    TrainStart = new DateTime(2010, 1, 1), TrainEnd = new DateTime(2016, 12, 31),
                    ValidationStart = new DateTime(2017, 1, 1), ValidationEnd = new DateTime(2017, 12, 31),
                    TestStart = new DateTime(2018, 1, 1), TestEnd = new DateTime(2019, 12, 31)
                },
                new WalkForwardFold
                {
                    Name = "F2",
                    TrainStart = new DateTime(2010, 1, 1), TrainEnd = new DateTime(2018, 12, 31),
                    ValidationStart = new DateTime(2019, 1, 1), ValidationEnd = new DateTime(2019, 12, 31),
                    TestStart = new DateTime(2020, 1, 1), TestEnd = new DateTime(2021, 12, 31)
                },
                new WalkForwardFold
                {
                    Name = "F3",
                    TrainStart = new DateTime(2010, 1, 1), TrainEnd = new DateTime(2020, 12, 31),
                    ValidationStart = new DateTime(2021, 1, 1), ValidationEnd = new DateTime(2021, 12, 31),
                    TestStart = new DateTime(2022, 1, 1), TestEnd = new DateTime(2023, 12, 31)
                }
            };
        }
    }

    /// <summary>The three blocks of one fold, after purge and embargo have been applied.</summary>
    public class WalkForwardSplit
    {
        public WalkForwardFold Fold { get; set; }
        public List<HarSample> Train { get; set; }
        public List<HarSample> Validation { get; set; }
        public List<HarSample> Test { get; set; }

        /// <summary>Samples removed to keep the blocks independent.</summary>
        public int PurgedFromTrain { get; set; }
        public int EmbargoedFromValidation { get; set; }
    }

    /// <summary>
    /// Cuts an ordered sample series into walk-forward blocks with a purge and an embargo.
    /// </summary>
    /// <remarks>
    /// The gaps are the point. A sample dated t carries a label realized after t, so the last
    /// few training samples before a validation block are labelled with information that falls
    /// inside it. Without a purge those samples leak the answer; without an embargo the same
    /// happens across the validation/test boundary. Both are counted and reported rather than
    /// silently applied, because a purge that quietly removes most of a block is a
    /// configuration error rather than a safety measure.
    /// </remarks>
    public static class WalkForwardSplitter
    {
        public static WalkForwardSplit Split(
            IReadOnlyList<HarSample> samples,
            WalkForwardFold fold,
            int purgeTradingDays = 5,
            int embargoTradingDays = 5)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (fold == null) throw new ArgumentNullException("fold");
            if (purgeTradingDays < 0) throw new ArgumentOutOfRangeException("purgeTradingDays");
            if (embargoTradingDays < 0) throw new ArgumentOutOfRangeException("embargoTradingDays");

            var ordered = samples.OrderBy(s => s.Date).ToList();

            var train = Between(ordered, fold.TrainStart, fold.TrainEnd);
            var validation = Between(ordered, fold.ValidationStart, fold.ValidationEnd);
            var test = Between(ordered, fold.TestStart, fold.TestEnd);

            // Purge the tail of training: those labels reach into validation.
            var purged = Math.Min(purgeTradingDays, train.Count);
            train.RemoveRange(train.Count - purged, purged);

            // Embargo the tail of validation for the same reason against test.
            var embargoed = Math.Min(embargoTradingDays, validation.Count);
            validation.RemoveRange(validation.Count - embargoed, embargoed);

            return new WalkForwardSplit
            {
                Fold = fold,
                Train = train,
                Validation = validation,
                Test = test,
                PurgedFromTrain = purged,
                EmbargoedFromValidation = embargoed
            };
        }

        private static List<HarSample> Between(IReadOnlyList<HarSample> ordered, DateTime start, DateTime end)
        {
            return ordered.Where(s => s.Date >= start.Date && s.Date <= end.Date).ToList();
        }
    }

    /// <summary>Per-fold performance of one model.</summary>
    public class FoldScore
    {
        public string FoldName { get; set; }
        public string ModelName { get; set; }
        public int Observations { get; set; }

        /// <summary>Mean QLIKE over the test block. The primary, and the only gated, loss.</summary>
        public double QuasiLikelihoodLoss { get; set; }

        /// <summary>Mean squared error of log variance. Reported, never gated on.</summary>
        public double LogMeanSquaredError { get; set; }

        /// <summary>Per-day QLIKE, needed for the Diebold-Mariano differential.</summary>
        public IReadOnlyList<double> DailyQuasiLikelihood { get; set; }

        /// <summary>Test-block dates, aligned with <see cref="DailyQuasiLikelihood"/>.</summary>
        public IReadOnlyList<DateTime> Dates { get; set; }
    }

    /// <summary>One model's results across every fold, measured against the gate baseline.</summary>
    public class ModelEvaluation
    {
        public string ModelName { get; set; }
        public List<FoldScore> Folds { get; set; }

        /// <summary>Pooled mean QLIKE across all test blocks, weighted by observation.</summary>
        public double PooledQuasiLikelihoodLoss { get; set; }

        /// <summary>
        /// Pooled QLIKE improvement over the baseline, as a fraction. Positive is better;
        /// the registered H1 gate is 2%.
        /// </summary>
        public double PooledQlikeGain { get; set; }

        /// <summary>Diebold-Mariano against the baseline on pooled daily differentials.</summary>
        public DieboldMarianoResult DieboldMariano { get; set; }

        /// <summary>Folds in which this model's QLIKE beat the baseline's.</summary>
        public int FoldsImproved { get; set; }

        /// <summary>
        /// Largest share of the total gain contributed by any single calendar year. The
        /// registered falsification threshold is 50%: a gain concentrated in one year is a
        /// regime artifact rather than an edge.
        /// </summary>
        public double LargestYearShareOfGain { get; set; }

        public override string ToString()
        {
            return string.Format(
                "{0}: QLIKE={1:F5}  gain={2:P2}  folds improved={3}/{4}  maxYearShare={5:P1}  {6}",
                ModelName, PooledQuasiLikelihoodLoss, PooledQlikeGain, FoldsImproved,
                Folds == null ? 0 : Folds.Count, LargestYearShareOfGain, DieboldMariano);
        }
    }

    /// <summary>
    /// Runs the baseline ladder across walk-forward folds and grades each rung against the
    /// gate baseline.
    /// </summary>
    /// <remarks>
    /// Losses are fixed at QLIKE (primary) and MSE of log variance (reported only). The study
    /// registration forbids other losses in code, because a loss chosen after seeing results
    /// is a free parameter.
    /// </remarks>
    public static class WalkForwardEvaluation
    {
        /// <summary>
        /// Fits and scores one model per fold. The model is refit from scratch on each fold's
        /// training block; nothing carries across folds.
        /// </summary>
        public static List<FoldScore> Score(
            Func<IVarianceForecastModel> modelFactory,
            IReadOnlyList<HarSample> samples,
            IReadOnlyList<WalkForwardFold> folds,
            int purgeTradingDays = 5,
            int embargoTradingDays = 5)
        {
            if (modelFactory == null) throw new ArgumentNullException("modelFactory");
            if (samples == null) throw new ArgumentNullException("samples");
            if (folds == null) throw new ArgumentNullException("folds");

            var scores = new List<FoldScore>();

            foreach (var fold in folds)
            {
                var split = WalkForwardSplitter.Split(samples, fold, purgeTradingDays, embargoTradingDays);
                if (split.Train.Count == 0 || split.Test.Count == 0) continue;

                var model = modelFactory();
                model.Fit(split.Train);

                var predictions = model.PredictLogVariance(split.Test);
                if (predictions.Count != split.Test.Count)
                    throw new InvalidOperationException(string.Format(
                        "{0} returned {1} forecasts for {2} test samples.",
                        model.Name, predictions.Count, split.Test.Count));

                var dailyQlike = new List<double>(split.Test.Count);
                double squaredError = 0.0;

                for (int i = 0; i < split.Test.Count; i++)
                {
                    var sample = split.Test[i];
                    var forecastVariance = Math.Exp(predictions[i]);

                    dailyQlike.Add(HarRvModel.QuasiLikelihood(sample.ForwardVariance, forecastVariance));

                    var error = sample.Target - predictions[i];
                    squaredError += error * error;
                }

                scores.Add(new FoldScore
                {
                    FoldName = fold.Name,
                    ModelName = model.Name,
                    Observations = split.Test.Count,
                    QuasiLikelihoodLoss = dailyQlike.Average(),
                    LogMeanSquaredError = squaredError / split.Test.Count,
                    DailyQuasiLikelihood = dailyQlike,
                    Dates = split.Test.Select(s => s.Date).ToList()
                });
            }

            return scores;
        }

        /// <summary>
        /// Grades a candidate's fold scores against the baseline's.
        /// </summary>
        /// <param name="hacLag">Newey-West truncation lag; five for the daily label.</param>
        public static ModelEvaluation Grade(
            string modelName,
            IReadOnlyList<FoldScore> candidate,
            IReadOnlyList<FoldScore> baseline,
            int hacLag = 5)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (baseline == null) throw new ArgumentNullException("baseline");
            if (candidate.Count == 0) throw new ArgumentException("The candidate has no scored folds.");
            if (candidate.Count != baseline.Count)
                throw new ArgumentException("Candidate and baseline must be scored over the same folds.");

            var candidateDaily = new List<double>();
            var baselineDaily = new List<double>();
            var dates = new List<DateTime>();
            var foldsImproved = 0;

            for (int i = 0; i < candidate.Count; i++)
            {
                if (candidate[i].FoldName != baseline[i].FoldName)
                    throw new ArgumentException("Candidate and baseline folds are not aligned.");
                if (candidate[i].Observations != baseline[i].Observations)
                    throw new ArgumentException("Candidate and baseline test blocks differ in size.");

                candidateDaily.AddRange(candidate[i].DailyQuasiLikelihood);
                baselineDaily.AddRange(baseline[i].DailyQuasiLikelihood);
                dates.AddRange(candidate[i].Dates);

                if (candidate[i].QuasiLikelihoodLoss < baseline[i].QuasiLikelihoodLoss) foldsImproved++;
            }

            var pooledCandidate = candidateDaily.Average();
            var pooledBaseline = baselineDaily.Average();

            return new ModelEvaluation
            {
                ModelName = modelName,
                Folds = candidate.ToList(),
                PooledQuasiLikelihoodLoss = pooledCandidate,
                PooledQlikeGain = pooledBaseline > 0.0 ? (pooledBaseline - pooledCandidate) / pooledBaseline : 0.0,
                DieboldMariano = DieboldMariano.Compare(candidateDaily, baselineDaily, hacLag),
                FoldsImproved = foldsImproved,
                LargestYearShareOfGain = LargestYearShareOfGain(dates, candidateDaily, baselineDaily)
            };
        }

        /// <summary>
        /// Share of the total loss reduction contributed by the single best calendar year.
        /// </summary>
        /// <remarks>
        /// Returns zero when there is no net gain to concentrate. Years that make the forecast
        /// worse are kept in the denominator - netting them out first would let one
        /// catastrophic year mask a gain that is otherwise entirely one other year.
        /// </remarks>
        private static double LargestYearShareOfGain(
            IReadOnlyList<DateTime> dates,
            IReadOnlyList<double> candidateDaily,
            IReadOnlyList<double> baselineDaily)
        {
            var byYear = new Dictionary<int, double>();
            double total = 0.0;

            for (int i = 0; i < dates.Count; i++)
            {
                var reduction = baselineDaily[i] - candidateDaily[i];
                total += reduction;

                double running;
                byYear.TryGetValue(dates[i].Year, out running);
                byYear[dates[i].Year] = running + reduction;
            }

            if (total <= 0.0) return 0.0;

            var largest = 0.0;
            foreach (var yearGain in byYear.Values)
            {
                if (yearGain > largest) largest = yearGain;
            }

            return largest / total;
        }
    }
}
