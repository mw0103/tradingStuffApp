using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility.Baselines;

namespace TradingStuff.Volatility.Forecasting
{
    /// <summary>
    /// A forecaster of log mean daily variance over the label window.
    /// </summary>
    /// <remarks>
    /// Prediction is batched over an ordered run of samples rather than done one at a time.
    /// Rung-1 models carry state along the series - an EWMA is a function of everything before
    /// it - and a per-sample interface would either hide that state or force each caller to
    /// drive it. Batching makes the causality explicit: a model sees the run in date order and
    /// may look backward within it, never forward.
    /// </remarks>
    public interface IVarianceForecastModel
    {
        /// <summary>Name recorded in the trial registry alongside results.</summary>
        string Name { get; }

        /// <summary>Fits on the training block. Called once per fold, on train data only.</summary>
        void Fit(IReadOnlyList<HarSample> train);

        /// <summary>
        /// Log variance forecasts, one per sample, in the order given. Samples must be
        /// ascending by date.
        /// </summary>
        IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples);
    }

    internal static class ForecastGuards
    {
        public static void RequireFittable(IReadOnlyList<HarSample> train)
        {
            if (train == null) throw new ArgumentNullException("train");
            if (train.Count == 0) throw new ArgumentException("Cannot fit on an empty training block.");
        }

        public static void RequireOrdered(IReadOnlyList<HarSample> samples)
        {
            if (samples == null) throw new ArgumentNullException("samples");

            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].Date < samples[i - 1].Date)
                    throw new ArgumentException(
                        "Samples must be ascending by date; a model that walks the series cannot " +
                        "distinguish an out-of-order run from a look-ahead.");
            }
        }
    }

    /// <summary>
    /// Rung 0 of the baseline ladder: the unconditional mean of log variance over the
    /// training window.
    /// </summary>
    /// <remarks>
    /// Deliberately the floor. Anything that cannot beat a constant has learned nothing, and
    /// reporting it keeps the R-squared figures honest - against a sufficiently badly chosen
    /// benchmark almost any model looks skilful.
    /// </remarks>
    public class MeanLogVarianceModel : IVarianceForecastModel
    {
        private double? _mean;

        public string Name { get { return "rung0-mean"; } }

        public double Mean
        {
            get
            {
                if (!_mean.HasValue) throw new InvalidOperationException("The model must be fitted before it can predict.");
                return _mean.Value;
            }
        }

        public void Fit(IReadOnlyList<HarSample> train)
        {
            ForecastGuards.RequireFittable(train);
            _mean = train.Average(s => s.Target);
        }

        public IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples)
        {
            ForecastGuards.RequireOrdered(samples);
            var mean = Mean;
            return samples.Select(_ => mean).ToList();
        }
    }

    /// <summary>
    /// Rung 1a: the trailing mean of log variance over a fixed window of trading days.
    /// </summary>
    /// <remarks>
    /// Twenty-two days by default, matching the HAR monthly component. Note this averages
    /// log variance, whereas HAR's monthly regressor is the log of the averaged variance -
    /// the two differ by a Jensen term and are not interchangeable, which is exactly why this
    /// rung is reported separately rather than assumed equivalent.
    /// <para>
    /// The window walks the supplied run and falls back to the training mean until enough
    /// history has accumulated, so the first forecasts of a test block are never built from
    /// fewer observations than the window claims.
    /// </para>
    /// </remarks>
    public class RollingMeanLogVarianceModel : IVarianceForecastModel
    {
        private readonly int _window;
        private List<double> _trainingTail;

        public RollingMeanLogVarianceModel(int windowDays = 22)
        {
            if (windowDays <= 0) throw new ArgumentOutOfRangeException("windowDays", "The window must be positive.");
            _window = windowDays;
        }

        public string Name { get { return string.Format("rung1-rolling{0}", _window); } }

        public int WindowDays { get { return _window; } }

        public void Fit(IReadOnlyList<HarSample> train)
        {
            ForecastGuards.RequireFittable(train);

            // The tail of training seeds the window, so the first test forecast is a genuine
            // trailing mean rather than a mean of one observation. Fit rejects an empty block,
            // so the seed always holds at least one observation.
            _trainingTail = train.Select(s => s.Target).Skip(Math.Max(0, train.Count - _window)).ToList();
        }

        public IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples)
        {
            ForecastGuards.RequireOrdered(samples);
            if (_trainingTail == null) throw new InvalidOperationException("The model must be fitted before it can predict.");

            var history = new List<double>(_trainingTail);
            var forecasts = new List<double>(samples.Count);

            foreach (var sample in samples)
            {
                forecasts.Add(history.Average());

                // Only after forecasting does the day's own observation enter the window.
                history.Add(sample.Target);
                if (history.Count > _window) history.RemoveAt(0);
            }

            return forecasts;
        }
    }

    /// <summary>
    /// Rung 1b: exponentially weighted mean of log variance.
    /// </summary>
    /// <remarks>
    /// Lambda 0.94 is the RiskMetrics daily convention, carried over because it is the
    /// familiar reference point rather than because it is optimal here; it is fixed, not
    /// fitted, so it consumes no degrees of freedom and cannot be tuned into significance.
    /// </remarks>
    public class EwmaLogVarianceModel : IVarianceForecastModel
    {
        private readonly double _lambda;
        private double? _seed;

        public EwmaLogVarianceModel(double lambda = 0.94)
        {
            if (lambda <= 0.0 || lambda >= 1.0)
                throw new ArgumentOutOfRangeException("lambda", "Lambda must be strictly between 0 and 1.");
            _lambda = lambda;
        }

        public string Name { get { return string.Format("rung1-ewma{0:0.00}", _lambda); } }

        public double Lambda { get { return _lambda; } }

        public void Fit(IReadOnlyList<HarSample> train)
        {
            ForecastGuards.RequireFittable(train);

            // Run the recursion through training so the test block opens from the level
            // training ended at, not from a cold start.
            var level = train[0].Target;
            for (int i = 1; i < train.Count; i++)
            {
                level = _lambda * level + (1.0 - _lambda) * train[i].Target;
            }
            _seed = level;
        }

        public IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples)
        {
            ForecastGuards.RequireOrdered(samples);
            if (!_seed.HasValue) throw new InvalidOperationException("The model must be fitted before it can predict.");

            var level = _seed.Value;
            var forecasts = new List<double>(samples.Count);

            foreach (var sample in samples)
            {
                forecasts.Add(level);
                level = _lambda * level + (1.0 - _lambda) * sample.Target;
            }

            return forecasts;
        }
    }

    /// <summary>
    /// Rung 2: the HAR-RV gate baseline, behind the common forecasting interface.
    /// </summary>
    /// <remarks>
    /// Everything above this rung is measured against it. Wrapping rather than reimplementing
    /// keeps one HAR in the codebase, so the gate cannot drift away from the model the rest of
    /// the pipeline uses.
    /// </remarks>
    public class HarForecastModel : IVarianceForecastModel
    {
        private readonly HarRvModel _model = new HarRvModel();
        private readonly string[] _featureNames;

        public HarForecastModel(string[] featureNames = null)
        {
            _featureNames = featureNames;
        }

        public string Name { get { return "rung2-har"; } }

        /// <summary>The wrapped model, for its residual variance and coefficients.</summary>
        public HarRvModel Model { get { return _model; } }

        public void Fit(IReadOnlyList<HarSample> train)
        {
            ForecastGuards.RequireFittable(train);
            _model.Fit(train, _featureNames);
        }

        public IReadOnlyList<double> PredictLogVariance(IReadOnlyList<HarSample> samples)
        {
            ForecastGuards.RequireOrdered(samples);
            return samples.Select(s => _model.PredictLogVariance(s.Features)).ToList();
        }
    }
}
