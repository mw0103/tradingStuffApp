using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility;

namespace TradingStuff.Volatility.Baselines
{
    /// <summary>
    /// The HAR-RV model of Corsi (2009): log forward variance regressed on log realized
    /// variance averaged over daily, weekly and monthly horizons.
    ///
    /// This exists to be the bar the LSTM has to clear. Three regressors and an OLS fit
    /// capture most of the predictable structure in realized volatility, and the
    /// published gains from recurrent networks over HAR are real but modest. If the
    /// network cannot beat this out of sample, the network is not adding anything.
    /// </summary>
    public class HarRvModel
    {
        private double[] _coefficients;
        private double _residualVariance;

        public string[] FeatureNames { get; private set; }

        public IReadOnlyList<double> Coefficients
        {
            get { return _coefficients; }
        }

        /// <summary>
        /// Residual variance of the log-space fit, used for the retransformation
        /// correction when converting a log forecast back to a variance level.
        /// </summary>
        public double ResidualVariance
        {
            get { return _residualVariance; }
        }

        public bool IsFitted
        {
            get { return _coefficients != null; }
        }

        public void Fit(IReadOnlyList<HarSample> samples, string[] featureNames = null)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (samples.Count == 0) throw new ArgumentException("Cannot fit on an empty sample.");

            var design = samples.Select(s => s.Features).ToList();
            var targets = samples.Select(s => s.Target).ToList();

            _coefficients = OrdinaryLeastSquares.Fit(design, targets);
            FeatureNames = featureNames;

            double sumSquaredResiduals = 0.0;
            for (int i = 0; i < samples.Count; i++)
            {
                var residual = targets[i] - OrdinaryLeastSquares.Predict(_coefficients, design[i]);
                sumSquaredResiduals += residual * residual;
            }

            var degreesOfFreedom = Math.Max(1, samples.Count - _coefficients.Length);
            _residualVariance = sumSquaredResiduals / degreesOfFreedom;
        }

        /// <summary>Predicted log mean daily variance over the forward window.</summary>
        public double PredictLogVariance(double[] features)
        {
            EnsureFitted();
            return OrdinaryLeastSquares.Predict(_coefficients, features);
        }

        /// <summary>
        /// Predicted mean daily variance, with the lognormal retransformation
        /// correction. Exponentiating a log-space forecast directly returns the median
        /// rather than the mean and systematically understates variance - which, in a
        /// variance risk premium, biases the premium wider than it really is.
        /// </summary>
        public double PredictVariance(double[] features)
        {
            return Math.Exp(PredictLogVariance(features) + 0.5 * _residualVariance);
        }

        /// <summary>Predicted annualized volatility, directly comparable to a quoted implied vol.</summary>
        public double PredictAnnualizedVolatility(double[] features)
        {
            return VolatilityScaling.AnnualizeVolatility(PredictVariance(features));
        }

        public HarEvaluation Evaluate(IReadOnlyList<HarSample> samples)
        {
            EnsureFitted();
            if (samples == null) throw new ArgumentNullException("samples");
            if (samples.Count == 0) throw new ArgumentException("Cannot evaluate on an empty sample.");

            var predictions = samples.Select(s => PredictLogVariance(s.Features)).ToList();
            var actuals = samples.Select(s => s.Target).ToList();
            var meanActual = actuals.Average();

            double modelSse = 0.0;
            double meanSse = 0.0;
            double randomWalkSse = 0.0;
            double modelQlike = 0.0;
            double randomWalkQlike = 0.0;

            for (int i = 0; i < samples.Count; i++)
            {
                var modelError = actuals[i] - predictions[i];
                modelSse += modelError * modelError;

                var meanError = actuals[i] - meanActual;
                meanSse += meanError * meanError;

                var randomWalkLog = Math.Log(samples[i].RandomWalkForecast);
                var randomWalkError = actuals[i] - randomWalkLog;
                randomWalkSse += randomWalkError * randomWalkError;

                modelQlike += QuasiLikelihood(samples[i].ForwardVariance, PredictVariance(samples[i].Features));
                randomWalkQlike += QuasiLikelihood(samples[i].ForwardVariance, samples[i].RandomWalkForecast);
            }

            var n = samples.Count;
            return new HarEvaluation
            {
                Observations = n,
                LogMeanSquaredError = modelSse / n,
                RSquaredVersusMean = 1.0 - (modelSse / meanSse),
                RSquaredVersusRandomWalk = 1.0 - (modelSse / randomWalkSse),
                QuasiLikelihoodLoss = modelQlike / n,
                RandomWalkQuasiLikelihoodLoss = randomWalkQlike / n
            };
        }

        /// <summary>
        /// QLIKE loss. Preferred over squared error for variance forecasts because it is
        /// robust to noise in the volatility proxy - the true variance is never observed,
        /// only estimated, and squared error penalizes that noise asymmetrically.
        /// </summary>
        public static double QuasiLikelihood(double actualVariance, double forecastVariance)
        {
            if (forecastVariance <= 0.0) throw new ArgumentOutOfRangeException("forecastVariance");
            if (actualVariance <= 0.0) throw new ArgumentOutOfRangeException("actualVariance");

            var ratio = actualVariance / forecastVariance;
            return ratio - Math.Log(ratio) - 1.0;
        }

        private void EnsureFitted()
        {
            if (_coefficients == null)
                throw new InvalidOperationException("The model must be fitted before it can predict.");
        }
    }

    public class HarEvaluation
    {
        public int Observations { get; set; }
        public double LogMeanSquaredError { get; set; }

        /// <summary>Out-of-sample R squared against a constant forecast.</summary>
        public double RSquaredVersusMean { get; set; }

        /// <summary>
        /// Out-of-sample R squared against the trailing-window forecast. This is the
        /// number that actually matters: beating a constant is easy, beating persistence
        /// is not.
        /// </summary>
        public double RSquaredVersusRandomWalk { get; set; }

        public double QuasiLikelihoodLoss { get; set; }
        public double RandomWalkQuasiLikelihoodLoss { get; set; }

        public bool BeatsRandomWalk
        {
            get { return RSquaredVersusRandomWalk > 0.0 && QuasiLikelihoodLoss < RandomWalkQuasiLikelihoodLoss; }
        }

        public override string ToString()
        {
            return string.Format(
                "n={0}  logMSE={1:F5}  R2_mean={2:P1}  R2_rw={3:P1}  QLIKE={4:F5} (rw {5:F5})  beatsRW={6}",
                Observations, LogMeanSquaredError, RSquaredVersusMean, RSquaredVersusRandomWalk,
                QuasiLikelihoodLoss, RandomWalkQuasiLikelihoodLoss, BeatsRandomWalk);
        }
    }
}
