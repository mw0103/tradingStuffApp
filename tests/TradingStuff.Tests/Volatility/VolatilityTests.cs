using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility.Baselines;
using TradingStuff.Volatility;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Volatility;
using static TradingStuff.Tests.Volatility.VolatilityAssert;

namespace TradingStuff.Tests.Volatility
{
    /// <summary>
    /// Verification suite for the realized volatility layer.
    ///
    /// The statistical checks matter as much as the unit checks here. Realized variance
    /// that is wrong by a constant factor still looks entirely reasonable in isolation,
    /// so the simulation tests pin the estimator against a path with a known volatility.
    /// </summary>
    public class VolatilityTests
    {
        // ---------- estimator core ----------

        [Fact]
        public void EstimatorFormulasMatchHandComputedValues()
        {
            var returns = new List<double> { 0.01, -0.02, 0.015, -0.005 };
            var m = RealizedVolatilityEstimator.FromReturns(returns);

            Check("RV equals sum of squared returns", m.RealizedVariance, 7.5e-4, 1e-12);
            Check("upside variance", m.UpsideVariance, 3.25e-4, 1e-12);
            Check("downside variance", m.DownsideVariance, 4.25e-4, 1e-12);
            Check("realized quarticity", m.RealizedQuarticity, 2.95e-7, 1e-15);

            // BV = (pi/2) * (n/(n-1)) * sum |r_j||r_{j-1}|
            var expectedBv = (Math.PI / 2.0) * (4.0 / 3.0) * (0.02 * 0.01 + 0.015 * 0.02 + 0.005 * 0.015);
            Check("bipower variation", m.BipowerVariation, expectedBv, 1e-12);

            // On four returns BV exceeds RV from sampling noise alone; the jump component
            // must floor at zero rather than go negative.
            IsTrue("jump floors at zero", m.JumpVariation == 0.0);
            Check("continuous variance falls back to RV", m.ContinuousVariance, 7.5e-4, 1e-12);

            var empty = RealizedVolatilityEstimator.FromReturns(new List<double>());
            IsTrue("empty return series yields zero RV", empty.RealizedVariance == 0.0);
            IsTrue("single return yields zero BV", RealizedVolatilityEstimator
                .FromReturns(new List<double> { 0.01 }).BipowerVariation == 0.0);
        }

        [Fact]
        public void SemivarianceDecomposesRealizedVariance()
        {
            var rng = new Random(11);
            var returns = Enumerable.Range(0, 500).Select(_ => (rng.NextDouble() - 0.5) * 0.02).ToList();
            var m = RealizedVolatilityEstimator.FromReturns(returns);

            Check("RS+ plus RS- equals RV",
                m.UpsideVariance + m.DownsideVariance, m.RealizedVariance, 1e-15);
        }

        [Fact]
        public void BipowerIsRobustToAnInjectedJump()
        {
            var rng = new Random(7);
            var clean = SimulateReturns(rng, 78, 0.01 / Math.Sqrt(78));

            var cleanMoments = RealizedVolatilityEstimator.FromReturns(clean);

            var jumped = new List<double>(clean);
            jumped[40] += 0.05; // a single large discontinuity
            var jumpedMoments = RealizedVolatilityEstimator.FromReturns(jumped);

            var rvIncrease = jumpedMoments.RealizedVariance - cleanMoments.RealizedVariance;
            var bvIncrease = jumpedMoments.BipowerVariation - cleanMoments.BipowerVariation;

            IsTrue("a jump inflates RV substantially", rvIncrease > 2.0e-3);
            IsTrue("BV absorbs far less of the jump than RV", bvIncrease < rvIncrease * 0.5);
            IsTrue("jump component becomes positive", jumpedMoments.JumpVariation > 0.0);
        }

        [Fact]
        public void OlsRecoversKnownCoefficients()
        {
            var rng = new Random(3);
            var design = new List<double[]>();
            var targets = new List<double>();

            for (int i = 0; i < 400; i++)
            {
                var x1 = rng.NextDouble() * 4.0 - 2.0;
                var x2 = rng.NextDouble() * 4.0 - 2.0;
                design.Add(new[] { x1, x2 });
                targets.Add(1.5 + 0.75 * x1 - 0.25 * x2 + Gaussian(rng) * 0.01);
            }

            var beta = OrdinaryLeastSquares.Fit(design, targets);
            Check("OLS intercept", beta[0], 1.5, 0.01);
            Check("OLS first slope", beta[1], 0.75, 0.01);
            Check("OLS second slope", beta[2], -0.25, 0.01);
        }

        // ---------- sampling and cleaning ----------

        [Fact]
        public void PreviousTickSamplingPicksLastClosedBar()
        {
            var day = new DateTime(2024, 3, 4);
            var times = new List<DateTime>();
            var prices = new List<double>();

            // Bars closing every minute from 09:31 to 09:46, price = 100 + minute index.
            for (int i = 0; i < 16; i++)
            {
                times.Add(day.AddHours(9).AddMinutes(31 + i));
                prices.Add(100.0 + i);
            }

            var sampled = BarResampler.Sample(times, prices,
                day.AddHours(9).AddMinutes(30), day.AddHours(9).AddMinutes(45), 5, 0);

            // Grid 09:30 (no bar yet), 09:35, 09:40, 09:45.
            IsTrue("grid point before first bar is skipped", sampled.Prices.Count == 3);
            Check("09:35 takes the bar closing at 09:35", sampled.Prices[0], 104.0, 1e-12);
            Check("09:40 takes the bar closing at 09:40", sampled.Prices[1], 109.0, 1e-12);
            Check("09:45 takes the bar closing at 09:45", sampled.Prices[2], 114.0, 1e-12);
            IsTrue("no stale samples when data is dense", sampled.StaleSamples == 0);

            // A gap between 09:36 and 09:44 must reuse the last known price and be counted.
            var gappedTimes = new List<DateTime> { day.AddHours(9).AddMinutes(35), day.AddHours(9).AddMinutes(46) };
            var gappedPrices = new List<double> { 100.0, 101.0 };
            var gapped = BarResampler.Sample(gappedTimes, gappedPrices,
                day.AddHours(9).AddMinutes(30), day.AddHours(9).AddMinutes(45), 5, 0);

            IsTrue("stale grid points are counted", gapped.StaleSamples > 0);
        }

        [Fact]
        public void CleaningHandlesReversedOrderAndDuplicates()
        {
            var bars = BuildFlatSession(new DateTime(2024, 3, 4), 100.0);

            var forward = BuildSeries(bars);
            var reversed = BuildSeries(Enumerable.Reverse(bars).ToList());
            var duplicated = BuildSeries(bars.Concat(bars).ToList());

            IsTrue("reversed input produces a session", reversed.Count == 1);
            Check("reversed input matches sorted input",
                reversed[0].IntradayVariance, forward[0].IntradayVariance, 1e-18);
            Check("duplicate rows are removed",
                duplicated[0].IntradayVariance, forward[0].IntradayVariance, 1e-18);
            IsTrue("duplicate rows do not create extra sessions", duplicated.Count == 1);

            var negative = new List<IntradayBar>(bars)
            {
                new IntradayBar(new DateTime(2024, 3, 4, 10, 0, 0), -1.0, -1.0, -1.0, -1.0)
            };
            Check("non-positive prices are dropped",
                BuildSeries(negative)[0].IntradayVariance, forward[0].IntradayVariance, 1e-18);
        }

        // ---------- statistical validation ----------

        [Fact]
        public void RealizedVarianceRecoversSimulatedVolatility()
        {
            const double annualizedVol = 0.16;
            var trueDailyVariance = (annualizedVol * annualizedVol) / 252.0;

            var days = SimulateSeries(seed: 42, sessions: 600, dailyVariance: trueDailyVariance);
            var complete = days.Where(d => d.IsComplete).ToList();

            IsTrue("all simulated sessions are complete", complete.Count == days.Count);

            var meanVariance = complete.Average(d => d.IntradayVariance);
            var ratio = meanVariance / trueDailyVariance;

            // Previous-tick sampling anchors on the first sampled price, so roughly half a
            // sampling interval of variance at the start of each session is not measured.
            // That is a known ~1% shortfall, not a defect; the sampling error over 600
            // sessions is well under another percent. A tolerance of 4% still catches any
            // real scaling error.
            Console.WriteLine(string.Format("    [info] mean RV / true daily variance = {0:F4}", ratio));
            IsTrue("realized variance recovers simulated variance", ratio > 0.96 && ratio < 1.04);

            var meanAnnualized = complete.Average(d => Math.Sqrt(d.IntradayVariance * 252.0));
            Console.WriteLine(string.Format("    [info] mean annualized RV = {0:P2} (true {1:P2})",
                meanAnnualized, annualizedVol));
            IsTrue("annualized volatility is in the right neighbourhood",
                Math.Abs(meanAnnualized - annualizedVol) < 0.01);

            var bvRatio = complete.Average(d => d.BipowerVariation) / meanVariance;
            Console.WriteLine(string.Format("    [info] mean BV / mean RV = {0:F4}", bvRatio));
            IsTrue("BV tracks RV on a jump-free path", bvRatio > 0.94 && bvRatio < 1.06);
        }

        [Fact]
        public void SubsamplingReducesEstimatorDispersion()
        {
            const double dailyVariance = 1.0e-4;

            var single = SimulateSeries(seed: 99, sessions: 300, dailyVariance: dailyVariance, subsample: false);
            var subsampled = SimulateSeries(seed: 99, sessions: 300, dailyVariance: dailyVariance, subsample: true);

            var singleDispersion = Dispersion(single.Select(d => d.IntradayVariance / dailyVariance).ToList());
            var subsampledDispersion = Dispersion(subsampled.Select(d => d.IntradayVariance / dailyVariance).ToList());

            Console.WriteLine(string.Format("    [info] dispersion single={0:F4} subsampled={1:F4}",
                singleDispersion, subsampledDispersion));
            IsTrue("subsampling lowers estimator dispersion", subsampledDispersion < singleDispersion);
        }

        // ---------- overnight handling ----------

        [Fact]
        public void OvernightPoliciesOrderAsExpected()
        {
            var bars = new List<IntradayBar>();
            bars.AddRange(BuildFlatSession(new DateTime(2024, 3, 4), 100.0));
            // Next session opens 1% higher: a pure overnight move with no intraday action.
            bars.AddRange(BuildFlatSession(new DateTime(2024, 3, 5), 101.0));

            var excluded = BuildSeries(bars, OvernightPolicy.Exclude);
            var added = BuildSeries(bars, OvernightPolicy.AddSquaredReturn);

            var overnight = added[1].OvernightReturn;
            Check("overnight return is the close-to-open log move", overnight, Math.Log(101.0 / 100.0), 1e-9);

            IsTrue("excluding the overnight move ignores it", excluded[1].TotalVariance == excluded[1].IntradayVariance);
            Check("adding the squared overnight move increases total variance",
                added[1].TotalVariance - added[1].IntradayVariance, overnight * overnight, 1e-15);
            IsTrue("total variance is higher once the overnight move is included",
                added[1].TotalVariance > excluded[1].TotalVariance);

            IsTrue("the first session has no overnight return", !added[0].HasOvernightReturn);
            IsTrue("later sessions have an overnight return", added[1].HasOvernightReturn);
        }

        [Fact]
        public void ExDividendAdjustmentRemovesMechanicalGap()
        {
            var exDate = new DateTime(2024, 3, 5);
            var dividend = 1.75;

            var bars = new List<IntradayBar>();
            bars.AddRange(BuildFlatSession(new DateTime(2024, 3, 4), 500.0));
            // The ETF opens lower by exactly the distribution: a cash transfer, not volatility.
            bars.AddRange(BuildFlatSession(exDate, 500.0 - dividend));

            var unadjusted = BuildSeries(bars, OvernightPolicy.AddSquaredReturn);

            var options = DefaultOptions(OvernightPolicy.AddSquaredReturn);
            options.ExDividends[exDate] = dividend;
            var adjusted = new RealizedVolatilitySeriesBuilder(SessionProfile.UsEquity(), options)
                .Build("SPY", bars);

            IsTrue("unadjusted ex-dividend date shows a spurious gap",
                Math.Abs(unadjusted[1].OvernightReturn) > 0.003);
            IsTrue("adjustment removes the mechanical gap",
                Math.Abs(adjusted[1].OvernightReturn) < 1e-9);
            IsTrue("adjustment lowers the day's total variance",
                adjusted[1].TotalVariance < unadjusted[1].TotalVariance);
            Check("the applied dividend is recorded", adjusted[1].DividendAdjustment, dividend, 1e-12);
        }

        // ---------- session flags ----------

        [Fact]
        public void ShortSessionIsDetected()
        {
            var full = BuildSeries(BuildFlatSession(new DateTime(2024, 3, 4), 100.0));
            IsTrue("a full session is not flagged short", !full[0].IsShortSession);

            var half = BuildSeries(BuildFlatSession(new DateTime(2024, 11, 29), 100.0, closeHour: 13));
            IsTrue("an early close is flagged short", half[0].IsShortSession);
        }

        [Fact]
        public void IncompleteDaysAreFlaggedNotDropped()
        {
            var day = new DateTime(2024, 3, 4);
            var bars = new List<IntradayBar>();
            // Only 30 minutes of data: enough to build a session, not enough to trust.
            for (int i = 0; i < 30; i++)
            {
                var t = day.AddHours(9).AddMinutes(31 + i);
                bars.Add(new IntradayBar(t, 100.0, 100.0, 100.0, 100.0));
            }

            var days = BuildSeries(bars);
            IsTrue("a thin session is still emitted", days.Count == 1);
            IsTrue("a thin session is flagged incomplete", !days[0].IsComplete);
            IsTrue("a thin session is not padded out to a full return count",
                days[0].ReturnCount < 20);
        }

        [Fact]
        public void MidSessionGapIsFlagged()
        {
            var day = new DateTime(2024, 3, 4);

            var full = BuildFlatSession(day, 100.0);
            IsTrue("a dense session is complete", BuildSeries(full)[0].IsComplete);

            // Same session span, but the middle four hours are missing. Previous-tick
            // sampling fills the hole with repeated prices, so the return count stays high
            // while the variance is silently understated.
            var gapped = full
                .Where(b => b.Timestamp.TimeOfDay < new TimeSpan(10, 0, 0)
                            || b.Timestamp.TimeOfDay >= new TimeSpan(15, 30, 0))
                .ToList();

            var result = BuildSeries(gapped)[0];
            Console.WriteLine(string.Format("    [info] gapped session: returns={0} stale={1}",
                result.ReturnCount, result.StaleSamples));

            IsTrue("a mid-session gap still yields many returns", result.ReturnCount >= 20);
            IsTrue("stale samples are counted", result.StaleSamples > 0);
            IsTrue("a mid-session gap is flagged incomplete", !result.IsComplete);
        }

        // ---------- HAR dataset and model ----------

        [Fact]
        public void HarTargetsUseOnlyForwardInformation()
        {
            var days = SyntheticVolatilitySeries(seed: 5, count: 300);
            var options = new HarDatasetOptions { HorizonDays = 5, WeeklyWindow = 5, MonthlyWindow = 22 };
            var samples = HarDatasetBuilder.Build(days, options);

            IsTrue("samples are produced", samples.Count > 200);

            var byDate = days.ToDictionary(d => d.Date, d => d);
            var ordered = days.OrderBy(d => d.Date).ToList();

            foreach (var sample in samples.Take(50))
            {
                var index = ordered.FindIndex(d => d.Date == sample.Date);
                var forward = ordered.Skip(index + 1).Take(options.HorizonDays).Average(d => d.TotalVariance);

                Check("target is the mean of the forward window only",
                    sample.Target, Math.Log(forward), 1e-12);

                // The daily feature must be the variance known at the sample date.
                Check("daily feature is the variance at the sample date",
                    sample.Features[0], Math.Log(byDate[sample.Date].TotalVariance), 1e-12);
            }

            var last = samples.Max(s => s.Date);
            var horizonCutoff = ordered[ordered.Count - 1 - options.HorizonDays].Date;
            IsTrue("the final horizon of days is left unlabelled", last <= horizonCutoff);
        }

        [Fact]
        public void HarSplitEmbargoRemovesOverlap()
        {
            var days = SyntheticVolatilitySeries(seed: 6, count: 400);
            var options = new HarDatasetOptions { HorizonDays = 21 };
            var samples = HarDatasetBuilder.Build(days, options);

            List<HarSample> train, test;
            HarDatasetBuilder.Split(samples, 0.7, options.HorizonDays, out train, out test);

            IsTrue("train and test are both populated", train.Count > 0 && test.Count > 0);
            IsTrue("the embargo drops samples", train.Count + test.Count < samples.Count);
            IsTrue("test starts strictly after train ends",
                test[0].Date > train[train.Count - 1].Date);

            var tradingDaysBetween = samples
                .Count(s => s.Date > train[train.Count - 1].Date && s.Date < test[0].Date);
            IsTrue("the gap is at least the forecast horizon", tradingDaysBetween >= options.HorizonDays - 1);
        }

        [Fact]
        public void HarBeatsRandomWalkOnPersistentVolatility()
        {
            var days = SyntheticVolatilitySeries(seed: 17, count: 3000);
            var options = new HarDatasetOptions { HorizonDays = 21 };
            var samples = HarDatasetBuilder.Build(days, options);

            List<HarSample> train, test;
            HarDatasetBuilder.Split(samples, 0.7, options.HorizonDays, out train, out test);

            var model = new HarRvModel();
            model.Fit(train, options.FeatureNames());

            var evaluation = model.Evaluate(test);
            Console.WriteLine("    [info] " + evaluation);

            IsTrue("HAR explains most of the out-of-sample variation", evaluation.RSquaredVersusMean > 0.3);
            IsTrue("HAR beats the trailing-window forecast", evaluation.BeatsRandomWalk);
        }

        [Fact]
        public void RetransformationCorrectionRaisesLevelForecast()
        {
            var days = SyntheticVolatilitySeries(seed: 23, count: 1200);
            var options = new HarDatasetOptions { HorizonDays = 21 };
            var samples = HarDatasetBuilder.Build(days, options);

            var model = new HarRvModel();
            model.Fit(samples, options.FeatureNames());

            var features = samples[samples.Count - 1].Features;
            var naive = Math.Exp(model.PredictLogVariance(features));
            var corrected = model.PredictVariance(features);

            IsTrue("residual variance is positive", model.ResidualVariance > 0.0);
            IsTrue("the retransformation correction raises the level forecast", corrected > naive);
            Check("correction equals exp(sigma^2/2)",
                corrected / naive, Math.Exp(0.5 * model.ResidualVariance), 1e-12);

            var annualized = model.PredictAnnualizedVolatility(features);
            IsTrue("annualized forecast is plausible", annualized > 0.01 && annualized < 2.0);
        }

        // ---------- SPY to SPX transfer ----------

        [Fact]
        public void ComparisonRecoversKnownCalibration()
        {
            var rng = new Random(31);
            var source = SyntheticVolatilitySeries(seed: 8, count: 500, symbol: "SPY");

            // Construct a target whose log variance is a known affine function of the
            // source's, plus noise: log(target) = -0.15 + 0.95 * log(source) + e.
            var target = source.Select(d => new RealizedVolatilityDay
            {
                Symbol = "SPX",
                Date = d.Date,
                IsComplete = true,
                IntradayVariance = Math.Exp(-0.15 + 0.95 * Math.Log(d.TotalVariance) + Gaussian(rng) * 0.05),
                TotalVariance = Math.Exp(-0.15 + 0.95 * Math.Log(d.TotalVariance) + Gaussian(rng) * 0.05)
            }).ToList();

            var result = VolatilityComparison.Compare(source, target);
            Console.WriteLine("    [info] " + result);

            Check("calibration intercept is recovered", result.CalibrationIntercept, -0.15, 0.05);
            Check("calibration slope is recovered", result.CalibrationSlope, 0.95, 0.02);
            IsTrue("log variances are highly correlated", result.LogVarianceCorrelation > 0.98);
            IsTrue("all overlapping days are matched", result.MatchedDays == source.Count);
            IsTrue("divergences are reported worst first",
                result.LargestDivergences[0].AbsoluteLogVarianceRatio
                >= result.LargestDivergences[1].AbsoluteLogVarianceRatio);

            // The fitted calibration should map a source variance onto the target's scale.
            var mapped = result.TransferVariance(source[0].TotalVariance);
            var expected = Math.Exp(-0.15 + 0.95 * Math.Log(source[0].TotalVariance));
            IsTrue("transfer maps onto the target scale", Math.Abs(Math.Log(mapped / expected)) < 0.1);

            // Dates missing from the target must simply not match, never throw.
            var partial = target.Where((d, i) => i % 2 == 0).ToList();
            IsTrue("missing target dates are skipped",
                VolatilityComparison.Compare(source, partial).MatchedDays == partial.Count);
        }

        // ---------- adapters, diagnostics, export ----------

        [Fact]
        public void AdapterPreservesPricesAndOrdering()
        {
            // 09:30 ET on 2024-03-04 is 14:30 UTC (EST, UTC-5). Bars are held in UTC.
            var open = new DateTimeOffset(2024, 3, 4, 14, 30, 0, TimeSpan.Zero);
            var rows = new List<HistoricalBarDto>();

            for (int i = 0; i < 390; i++)
            {
                var price = 500m + (i % 7) * 0.25m;
                rows.Add(new HistoricalBarDto(
                    Timestamp: open.AddMinutes(i),
                    TradingDate: null,
                    Open: price,
                    High: price,
                    Low: price,
                    Close: price,
                    Volume: 1000m,
                    Count: 10,
                    Wap: price));
            }

            // Feed them in reverse, the order these endpoints actually return. The adapter
            // preserves input order deliberately; the estimator's cleaning step sorts.
            var bars = HistoricalBarAdapter.ToIntradayBars(Enumerable.Reverse(rows)).ToList();
            IsTrue("adapter converts every row", bars.Count == rows.Count);
            IsTrue("adapter preserves the order it was given",
                bars[0].Timestamp == rows[rows.Count - 1].Timestamp!.Value.UtcDateTime);
            IsTrue("timestamps stay UTC", bars[0].Timestamp.Kind == DateTimeKind.Utc);

            Check("the last price survives the decimal conversion exactly",
                bars[0].Close, 500.0 + (389 % 7) * 0.25, 0.0);
            Check("the first price survives the decimal conversion exactly",
                bars[bars.Count - 1].Close, 500.0, 0.0);
            IsTrue("volume is carried across", bars[0].Volume == 1000L);
        }

        [Fact]
        public void AdapterRejectsDailyBars()
        {
            // Daily bars carry TradingDate and no Timestamp. Reading one as midnight would
            // collapse a whole session onto a single instant rather than fail.
            var daily = new[]
            {
                new HistoricalBarDto(
                    Timestamp: null,
                    TradingDate: new DateOnly(2024, 3, 4),
                    Open: 500m, High: 501m, Low: 499m, Close: 500.5m,
                    Volume: 1_000_000m, Count: 5000, Wap: 500m)
            };

            Throws("a daily bar is rejected rather than silently misplaced",
                () => HistoricalBarAdapter.ToIntradayBars(daily).ToList());
        }

        [Fact]
        public void DiagnosticsSummarizeSeriesAndFlagOutliers()
        {
            var days = SyntheticVolatilitySeries(seed: 44, count: 250, symbol: "SPY");

            // One session at an implausible level, the signature of a data fault.
            days[100].TotalVariance = 0.5;
            days[100].IntradayVariance = 0.5;

            var summary = SeriesDiagnostics.Summarize(days);
            Console.WriteLine("    [info] " + summary.ToString().Replace("\n", "\n    "));

            IsTrue("all sessions are counted", summary.TotalSessions == 250);
            IsTrue("the implausible session is flagged", summary.Outliers.Count == 1);
            Check("the flagged session is the corrupted one",
                summary.Outliers[0].Date.Ticks, days[100].Date.Ticks, 0);
            IsTrue("median volatility is plausible",
                summary.MedianAnnualizedVolatility > 0.05 && summary.MedianAnnualizedVolatility < 1.0);
            IsTrue("the median resists the outlier",
                summary.MedianAnnualizedVolatility < summary.MaxAnnualizedVolatility);
            IsTrue("weekend gaps are detected", summary.LargestGapDays >= 3);
        }

        [Fact]
        public void CsvExportRoundTripsRowCount()
        {
            var days = SyntheticVolatilitySeries(seed: 55, count: 40, symbol: "SPY");
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rv_export_test.csv");

            RealizedVolatilityCsv.Write(path, days);
            var lines = System.IO.File.ReadAllLines(path);

            IsTrue("csv has a header plus one row per session", lines.Length == days.Count + 1);
            IsTrue("header names the symbol column first", lines[0].StartsWith("symbol,date,"));
            IsTrue("rows carry the symbol", lines[1].StartsWith("SPY,"));

            var columns = lines[1].Split(',');
            IsTrue("every header column is populated",
                columns.Length == lines[0].Split(',').Length);

            System.IO.File.Delete(path);
        }

        // ---------- helpers ----------

        private static RealizedVolatilityOptions DefaultOptions(
            OvernightPolicy policy = OvernightPolicy.Exclude, bool subsample = true)
        {
            return new RealizedVolatilityOptions
            {
                SourceBarMinutes = 1,
                SamplingMinutes = 5,
                UseSubsampling = subsample,
                TimestampConvention = BarTimestampConvention.BarStart,
                OvernightPolicy = policy,
                OvernightScalingWindow = 252
            };
        }

        private static List<RealizedVolatilityDay> BuildSeries(
            IEnumerable<IntradayBar> bars,
            OvernightPolicy policy = OvernightPolicy.Exclude,
            bool subsample = true)
        {
            var builder = new RealizedVolatilitySeriesBuilder(SessionProfile.UsEquity(), DefaultOptions(policy, subsample));
            return builder.Build("TEST", bars);
        }

        /// <summary>A flat session: constant price, so realized variance is exactly zero.</summary>
        private static List<IntradayBar> BuildFlatSession(DateTime date, double price, int closeHour = 16)
        {
            var bars = new List<IntradayBar>();
            var t = date.AddHours(9).AddMinutes(30);
            var end = date.AddHours(closeHour);
            while (t < end)
            {
                bars.Add(new IntradayBar(t, price, price, price, price));
                t = t.AddMinutes(1);
            }
            return bars;
        }

        private static List<RealizedVolatilityDay> SimulateSeries(
            int seed, int sessions, double dailyVariance, bool subsample = true)
        {
            var rng = new Random(seed);
            var perMinuteVol = Math.Sqrt(dailyVariance / 390.0);

            var bars = new List<IntradayBar>();
            var date = new DateTime(2020, 1, 1);
            var price = 400.0;

            for (int s = 0; s < sessions; s++)
            {
                while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                {
                    date = date.AddDays(1);
                }

                var t = date.AddHours(9).AddMinutes(30);
                for (int minute = 0; minute < 390; minute++)
                {
                    price *= Math.Exp(Gaussian(rng) * perMinuteVol);
                    bars.Add(new IntradayBar(t, price, price, price, price));
                    t = t.AddMinutes(1);
                }

                date = date.AddDays(1);
            }

            return BuildSeries(bars, OvernightPolicy.Exclude, subsample);
        }

        /// <summary>
        /// Daily variance following a persistent AR(1) in logs, which is how realized
        /// volatility actually behaves and what makes it forecastable at all.
        /// </summary>
        private static List<RealizedVolatilityDay> SyntheticVolatilitySeries(
            int seed, int count, string symbol = "TEST")
        {
            var rng = new Random(seed);
            var days = new List<RealizedVolatilityDay>();

            var longRunMean = Math.Log(1.0e-4);
            var logVariance = longRunMean;
            var date = new DateTime(2015, 1, 1);

            for (int i = 0; i < count; i++)
            {
                while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                {
                    date = date.AddDays(1);
                }

                logVariance = longRunMean + 0.985 * (logVariance - longRunMean) + Gaussian(rng) * 0.15;

                // Observed realized variance carries estimation noise around the latent level.
                var observed = Math.Exp(logVariance + Gaussian(rng) * 0.2);

                days.Add(new RealizedVolatilityDay
                {
                    Symbol = symbol,
                    Date = date,
                    IntradayVariance = observed,
                    TotalVariance = observed,
                    BipowerVariation = observed * 0.95,
                    UpsideVariance = observed * 0.5,
                    DownsideVariance = observed * 0.5,
                    ReturnCount = 78,
                    IsComplete = true
                });

                date = date.AddDays(1);
            }

            return days;
        }

        private static List<double> SimulateReturns(Random rng, int count, double sigma)
        {
            var returns = new List<double>(count);
            for (int i = 0; i < count; i++) returns.Add(Gaussian(rng) * sigma);
            return returns;
        }

        private static double Dispersion(IReadOnlyList<double> values)
        {
            var mean = values.Average();
            return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        }

    }
}
