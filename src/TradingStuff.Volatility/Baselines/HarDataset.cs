using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility;

namespace TradingStuff.Volatility.Baselines
{
    /// <summary>One training row: features known at <see cref="Date"/>, target realized after it.</summary>
    public class HarSample
    {
        /// <summary>Last date whose information is used to build the features.</summary>
        public DateTime Date { get; set; }

        public double[] Features { get; set; }

        /// <summary>Log of the mean daily variance realized over the forward window.</summary>
        public double Target { get; set; }

        /// <summary>Mean daily variance over the forward window, in level terms.</summary>
        public double ForwardVariance { get; set; }

        /// <summary>
        /// The naive forecast: mean daily variance over the trailing window of the same
        /// length as the horizon. Any model that cannot beat this has learned nothing.
        /// </summary>
        public double RandomWalkForecast { get; set; }

        /// <summary>Annualized realized volatility over the forward window.</summary>
        public double ForwardAnnualizedVolatility
        {
            get { return VolatilityScaling.AnnualizeVolatility(ForwardVariance); }
        }
    }

    public class HarDatasetOptions
    {
        /// <summary>Forecast horizon in trading days.</summary>
        public int HorizonDays { get; set; }

        /// <summary>Lookback for the weekly HAR component.</summary>
        public int WeeklyWindow { get; set; }

        /// <summary>Lookback for the monthly HAR component.</summary>
        public int MonthlyWindow { get; set; }

        /// <summary>Adds the jump share of realized variance as a regressor (HAR-J).</summary>
        public bool IncludeJumpComponent { get; set; }

        /// <summary>Adds the downside share of realized variance, capturing the leverage effect.</summary>
        public bool IncludeSemivariance { get; set; }

        /// <summary>
        /// Variance floor applied before taking logs. Guards against a degenerate
        /// session (a data gap, or a holiday that slipped through) producing negative
        /// infinity and poisoning the fit.
        /// </summary>
        public double VarianceFloor { get; set; }

        /// <summary>
        /// Emit only every <see cref="HorizonDays"/>-th sample, so forward windows do not
        /// overlap. Overlapping samples are fine for fitting but make evaluation
        /// statistics badly overconfident, because consecutive rows share almost all of
        /// their target window.
        /// </summary>
        public bool NonOverlappingOnly { get; set; }

        public HarDatasetOptions()
        {
            HorizonDays = 21;
            WeeklyWindow = 5;
            MonthlyWindow = 22;
            IncludeJumpComponent = false;
            IncludeSemivariance = false;
            VarianceFloor = 1e-12;
            NonOverlappingOnly = false;
        }

        public string[] FeatureNames()
        {
            var names = new List<string> { "log_rv_daily", "log_rv_weekly", "log_rv_monthly" };
            if (IncludeJumpComponent) names.Add("jump_share");
            if (IncludeSemivariance) names.Add("downside_share");
            return names.ToArray();
        }
    }

    /// <summary>
    /// Builds HAR-style training rows from a daily realized volatility series.
    ///
    /// The forward alignment is the part worth reading twice: a sample dated t is
    /// labelled with variance realized over t+1..t+h, so its label does not exist until
    /// h days later. That has three consequences - the final h days of any series are
    /// unlabelled, train/test splits need an embargo of at least h days, and
    /// consecutive samples share h-1 days of their label window.
    /// </summary>
    public static class HarDatasetBuilder
    {
        public static List<HarSample> Build(IReadOnlyList<RealizedVolatilityDay> days, HarDatasetOptions options)
        {
            if (days == null) throw new ArgumentNullException("days");
            if (options == null) throw new ArgumentNullException("options");
            if (options.HorizonDays <= 0) throw new ArgumentOutOfRangeException("options", "HorizonDays must be positive.");
            if (options.MonthlyWindow < options.WeeklyWindow)
                throw new ArgumentException("MonthlyWindow must be at least as long as WeeklyWindow.");

            var ordered = days.OrderBy(d => d.Date).ToList();
            var samples = new List<HarSample>();

            var horizon = options.HorizonDays;
            var warmup = Math.Max(options.MonthlyWindow, horizon) - 1;

            for (int t = warmup; t + horizon < ordered.Count; t++)
            {
                if (options.NonOverlappingOnly && (t - warmup) % horizon != 0) continue;
                if (!ordered[t].IsComplete) continue;

                var forwardWindow = Window(ordered, t + 1, horizon);
                if (forwardWindow.Any(d => !d.IsComplete)) continue;

                var features = BuildFeatures(ordered, t, options);
                if (features == null) continue;

                var forwardVariance = forwardWindow.Average(d => d.TotalVariance);
                if (forwardVariance <= 0.0) continue;

                var trailingWindow = Window(ordered, t - horizon + 1, horizon);

                samples.Add(new HarSample
                {
                    Date = ordered[t].Date,
                    Features = features,
                    Target = Math.Log(Math.Max(forwardVariance, options.VarianceFloor)),
                    ForwardVariance = forwardVariance,
                    RandomWalkForecast = Math.Max(trailingWindow.Average(d => d.TotalVariance), options.VarianceFloor)
                });
            }

            return samples;
        }

        private static double[] BuildFeatures(IReadOnlyList<RealizedVolatilityDay> days, int t, HarDatasetOptions options)
        {
            var daily = days[t].TotalVariance;
            var weekly = Window(days, t - options.WeeklyWindow + 1, options.WeeklyWindow).Average(d => d.TotalVariance);
            var monthly = Window(days, t - options.MonthlyWindow + 1, options.MonthlyWindow).Average(d => d.TotalVariance);

            if (daily <= 0.0 || weekly <= 0.0 || monthly <= 0.0) return null;

            var features = new List<double>
            {
                Math.Log(Math.Max(daily, options.VarianceFloor)),
                Math.Log(Math.Max(weekly, options.VarianceFloor)),
                Math.Log(Math.Max(monthly, options.VarianceFloor))
            };

            // Shares rather than levels: both are bounded in [0, 1], so they stay on a
            // comparable scale to the log-variance regressors and need no separate
            // normalization.
            if (options.IncludeJumpComponent)
            {
                features.Add(Math.Min(days[t].JumpVariation / daily, 1.0));
            }

            if (options.IncludeSemivariance)
            {
                var totalSigned = days[t].UpsideVariance + days[t].DownsideVariance;
                features.Add(totalSigned > 0.0 ? days[t].DownsideVariance / totalSigned : 0.5);
            }

            return features.ToArray();
        }

        private static List<RealizedVolatilityDay> Window(IReadOnlyList<RealizedVolatilityDay> days, int start, int length)
        {
            var window = new List<RealizedVolatilityDay>(length);
            for (int i = start; i < start + length; i++)
            {
                window.Add(days[i]);
            }
            return window;
        }

        /// <summary>
        /// Chronological train/test split with an embargo. The embargo must be at least
        /// the forecast horizon: without it the last training samples are labelled with
        /// variance realized inside the test period, which leaks the answer.
        /// </summary>
        public static void Split(
            IReadOnlyList<HarSample> samples,
            double trainRatio,
            int embargoDays,
            out List<HarSample> train,
            out List<HarSample> test)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (trainRatio <= 0.0 || trainRatio >= 1.0)
                throw new ArgumentOutOfRangeException("trainRatio", "Train ratio must be strictly between 0 and 1.");
            if (embargoDays < 0) throw new ArgumentOutOfRangeException("embargoDays");

            var ordered = samples.OrderBy(s => s.Date).ToList();
            var cut = (int)(ordered.Count * trainRatio);

            train = ordered.Take(cut).ToList();
            test = ordered.Skip(Math.Min(cut + embargoDays, ordered.Count)).ToList();
        }
    }
}
