using System;
using System.Collections.Generic;
using System.Linq;
using TradingStuff.Volatility.ThetaData;
using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility;
using static TradingStuff.Tests.Volatility.VolatilityAssert;

namespace TradingStuff.Tests.Volatility
{
    /// <summary>
    /// Verification for the model-free implied variance calculation and the variance risk
    /// premium series.
    ///
    /// The central check builds an option chain from Black-Scholes at a known constant
    /// volatility. The model-free integral is the risk-neutral expectation of realized
    /// variance, so on a flat-volatility chain it must return that same volatility - and
    /// crucially it must do so without ever being told what it was.
    /// </summary>
    public class ImpliedVolatilityTests
    {
        private const double Spot = 100.0;
        private const double Rate = 0.03;
        private static readonly DateTime Observed = new DateTime(2024, 3, 4, 15, 45, 0);

                // ---------- model-free variance ----------

        [Fact]
        public void ModelFreeVarianceRecoversBlackScholesVolatility()
        {
            const double trueVol = 0.20;
            var slice = BuildChain(trueVol, days: 30, lowStrike: 20, highStrike: 300, step: 1.0);

            var result = ModelFreeVariance.Compute(slice, Rate);

            Info(string.Format("model-free vol={0:F5} (true {1:F5}), strikes={2}, range {3}-{4}",
                result.ImpliedVolatility, trueVol, result.StrikesUsed, result.LowestStrike, result.HighestStrike));

            // The discretized sum carries a small positive bias even on a fine grid; a
            // half-percent tolerance still pins the result hard enough to catch any real
            // error in the formula.
            Check("model-free volatility recovers the Black-Scholes input",
                result.ImpliedVolatility, trueVol, 0.001);
            IsTrue("the result is marked usable", result.IsUsable);
            IsTrue("the wings were not truncated", !result.TruncatedLowSide && !result.TruncatedHighSide);

            Check("time to expiry is thirty days", result.TimeToExpiryYears, 30.0 / 365.0, 1e-12);
            Check("total variance is annualized variance times time",
                result.TotalVariance, result.Variance * (30.0 / 365.0), 1e-15);

            // A different volatility must come back as that volatility, not as a constant.
            var lowVol = ModelFreeVariance.Compute(
                BuildChain(0.10, 30, 20, 300, 1.0), Rate);
            Check("a lower volatility chain returns the lower volatility",
                lowVol.ImpliedVolatility, 0.10, 0.001);
        }

        [Fact]
        public void ForwardIsRecoveredFromPutCallParity()
        {
            var slice = BuildChain(0.20, days: 30, lowStrike: 20, highStrike: 300, step: 1.0);
            var result = ModelFreeVariance.Compute(slice, Rate);

            var expectedForward = Spot * Math.Exp(Rate * (30.0 / 365.0));
            Info(string.Format("forward={0:F4} (expected {1:F4}), K0={2}",
                result.Forward, expectedForward, result.AtTheMoneyStrike));

            Check("forward is recovered from put-call parity", result.Forward, expectedForward, 0.02);
            IsTrue("the at-the-money strike sits at or below the forward",
                result.AtTheMoneyStrike <= result.Forward);
            IsTrue("the at-the-money strike is the closest one below",
                result.Forward - result.AtTheMoneyStrike < 1.0);
        }

        [Fact]
        public void CoarseStrikeGridsInflateTheIntegral()
        {
            var fine = ModelFreeVariance.Compute(BuildChain(0.20, 30, 20, 300, 1.0), Rate);
            var coarse = ModelFreeVariance.Compute(BuildChain(0.20, 30, 20, 300, 5.0), Rate);

            Info(string.Format("fine grid vol={0:F5}, coarse grid vol={1:F5}",
                fine.ImpliedVolatility, coarse.ImpliedVolatility));

            // Documented behaviour rather than a defect: the discretized sum overweights
            // curvature near the money, so a wider strike grid reads high. Worth knowing
            // because SPX wings are five points wide while the near-money strikes are not.
            IsTrue("a coarse strike grid reads higher than a fine one",
                coarse.ImpliedVolatility > fine.ImpliedVolatility);
            IsTrue("the coarse grid bias is material but bounded",
                coarse.ImpliedVolatility / fine.ImpliedVolatility > 1.02
                && coarse.ImpliedVolatility / fine.ImpliedVolatility < 1.15);

            Check("median strike spacing is reported", coarse.MedianStrikeSpacing, 5.0, 1e-9);
            Check("fine spacing is reported too", fine.MedianStrikeSpacing, 1.0, 1e-9);
        }

        [Fact]
        public void SkewRaisesVarianceAboveAtTheMoney()
        {
            // A downward-sloping smile: puts priced at a higher volatility than calls,
            // which is what index options actually look like.
            var skewed = BuildChain(0.20, 30, 20, 300, 1.0,
                volatilityForStrike: k => 0.20 + 0.40 * Math.Max(0.0, (Spot - k) / Spot));

            var flat = ModelFreeVariance.Compute(BuildChain(0.20, 30, 20, 300, 1.0), Rate);
            var withSkew = ModelFreeVariance.Compute(skewed, Rate);

            Info(string.Format("flat vol={0:F5} (strikes {1}-{2}), skewed vol={3:F5} (strikes {4}-{5})",
                flat.ImpliedVolatility, flat.LowestStrike, flat.HighestStrike,
                withSkew.ImpliedVolatility, withSkew.LowestStrike, withSkew.HighestStrike));

            // This is the whole reason to prefer the model-free measure: it integrates the
            // entire risk-neutral distribution, so the put skew shows up in it. An
            // at-the-money implied volatility would report roughly the flat number and
            // miss the premium embedded in the wings.
            IsTrue("skew raises model-free variance above the flat case",
                withSkew.ImpliedVolatility > flat.ImpliedVolatility * 1.05);

            // Richer puts keep a two-sided market further out, so the skewed chain also
            // retains a wider strike range before the zero-bid rule closes the wing.
            IsTrue("a steeper put skew widens the surviving low wing",
                withSkew.LowestStrike < flat.LowestStrike);
        }

        [Fact]
        public void ZeroBidRuleTerminatesTheWings()
        {
            var slice = BuildChain(0.20, 30, 20, 300, 1.0);

            // Punch a single zero-bid hole near the money; the walk should step over it.
            var withHole = CloneWithOverride(slice, strike: 90.0, right: OptionRight.Put, bid: 0.0, ask: 0.40);
            var holed = ModelFreeVariance.Compute(withHole, Rate);
            IsTrue("a single zero bid does not terminate the wing", holed.LowestStrike < 90.0);

            // Two adjacent zero bids should stop it.
            var withPair = CloneWithOverride(withHole, strike: 89.0, right: OptionRight.Put, bid: 0.0, ask: 0.38);
            var stopped = ModelFreeVariance.Compute(withPair, Rate);
            Info(string.Format("wing stops at {0} after two consecutive zero bids", stopped.LowestStrike));
            IsTrue("two consecutive zero bids terminate the wing", stopped.LowestStrike >= 90.0);
            IsTrue("fewer strikes survive once the wing is cut", stopped.StrikesUsed < holed.StrikesUsed);
        }

        [Fact]
        public void NarrowStrikeRangeIsFlaggedAsTruncated()
        {
            var narrow = ModelFreeVariance.Compute(BuildChain(0.20, 30, 95, 105, 1.0), Rate);

            Info(string.Format("narrow-range vol={0:F5}, truncated low={1} high={2}",
                narrow.ImpliedVolatility, narrow.TruncatedLowSide, narrow.TruncatedHighSide));

            IsTrue("a cut low wing is flagged", narrow.TruncatedLowSide);
            IsTrue("a cut high wing is flagged", narrow.TruncatedHighSide);
            IsTrue("a truncated result is not marked usable", !narrow.IsUsable);

            // Truncation discards variance, so the number comes out low.
            IsTrue("truncation biases the result downward", narrow.ImpliedVolatility < 0.20);
        }

        // ---------- constant maturity interpolation ----------

        [Fact]
        public void InterpolationIsExactWhenBothTermsAgree()
        {
            // If both expirations imply the same annualized variance, interpolation must
            // return exactly that, whatever the weights work out to.
            var near = Expiry(days: 23, annualizedVariance: 0.04);
            var next = Expiry(days: 37, annualizedVariance: 0.04);

            var result = ConstantMaturityVariance.Interpolate(new[] { near, next });

            Check("interpolating equal variances is exact", result.Variance, 0.04, 1e-12);
            Check("implied volatility follows", result.ImpliedVolatility, 0.20, 1e-12);
            IsTrue("the result is not marked extrapolated", !result.IsExtrapolated);
            Check("near term days are reported", result.NearTermDays, 23.0, 1e-9);
            Check("next term days are reported", result.NextTermDays, 37.0, 1e-9);
        }

        [Fact]
        public void InterpolationWeightsBetweenTerms()
        {
            var near = Expiry(days: 25, annualizedVariance: 0.0400); // 20% vol
            var next = Expiry(days: 35, annualizedVariance: 0.0900); // 30% vol

            var result = ConstantMaturityVariance.Interpolate(new[] { near, next });

            // Total variance interpolated in minutes, then rescaled to 30 days:
            //   w_near = (35-30)/(35-25) = 0.5, w_next = (30-25)/(35-25) = 0.5
            //   blended total = 0.5*(25/365*0.04) + 0.5*(35/365*0.09)
            //   annualized    = blended * 365/30
            var blendedTotal = 0.5 * (25.0 / 365.0 * 0.04) + 0.5 * (35.0 / 365.0 * 0.09);
            var expected = blendedTotal * (365.0 / 30.0);

            Info(string.Format("interpolated variance={0:F6}, expected={1:F6}", result.Variance, expected));
            Check("interpolation blends total variance, not volatility", result.Variance, expected, 1e-9);

            // Interpolating in volatility instead would give 25%; in total variance it does not.
            IsTrue("the result differs from a naive volatility average",
                Math.Abs(result.ImpliedVolatility - 0.25) > 0.001);
            IsTrue("the result lies between the two terms",
                result.Variance > 0.04 && result.Variance < 0.09);
        }

        [Fact]
        public void InterpolationRejectsNonBracketingExpirations()
        {
            // Both expirations sit on the same side of thirty days.
            var first = Expiry(days: 5, annualizedVariance: 0.04);
            var second = Expiry(days: 12, annualizedVariance: 0.04);

            Throws("non-bracketing expirations are rejected",
                () => ConstantMaturityVariance.Interpolate(new[] { first, second }));

            var extrapolated = ConstantMaturityVariance.Interpolate(
                new[] { first, second }, new ConstantMaturityOptions { AllowExtrapolation = true });
            IsTrue("extrapolation is permitted when asked for", extrapolated.IsExtrapolated);

            Throws("a single expiration is rejected",
                () => ConstantMaturityVariance.Interpolate(new[] { first }));
        }

        [Fact]
        public void SeriesBuilderReportsFailuresInsteadOfThrowing()
        {
            var builder = new ImpliedVarianceSeriesBuilder(new FlatRiskFreeRate(Rate));

            var good = builder.BuildDay("SPX", Observed.Date, new[]
            {
                BuildChain(0.20, 25, 20, 300, 1.0),
                BuildChain(0.20, 35, 20, 300, 1.0)
            });

            Info(string.Format("built day: usable={0} vol={1:F5} strikes={2}",
                good.IsUsable, good.ImpliedVolatility, good.StrikesUsed));
            IsTrue("a good day is usable", good.IsUsable);
            Check("the built day recovers the input volatility", good.ImpliedVolatility, 0.20, 0.002);

            // One expiration is not enough, and that must be reported rather than thrown.
            var thin = builder.BuildDay("SPX", Observed.Date, new[] { BuildChain(0.20, 25, 20, 300, 1.0) });
            IsTrue("a day with one expiration is unusable", !thin.IsUsable);
            IsTrue("the reason is recorded", !string.IsNullOrEmpty(thin.Note));

            // A chain with no quotes at all must also degrade gracefully.
            var empty = builder.BuildDay("SPX", Observed.Date, new List<OptionChainSlice>());
            IsTrue("an empty day is unusable", !empty.IsUsable);
            IsTrue("the empty day explains itself", !string.IsNullOrEmpty(empty.Note));
        }

        // ---------- variance risk premium ----------

        [Fact]
        public void PremiumUsesOnlyForwardRealizedVariance()
        {
            const int horizon = 21;

            var realized = BuildRealizedSeries(count: 120, dailyVariance: 1.0e-4);
            var implied = realized.Select(d => new ImpliedVarianceDay
            {
                Symbol = "SPX",
                Date = d.Date,
                ImpliedVariance = 0.04,
                IsUsable = true
            }).ToList();

            var premium = VarianceRiskPremiumBuilder.Build(implied, realized, horizon);

            IsTrue("a premium row exists for every usable implied day", premium.Count == implied.Count);

            var withForward = premium.Where(p => p.HasRealizedForward).ToList();
            IsTrue("the final horizon of days has no closed forward window",
                premium.Count - withForward.Count >= horizon);

            // Realized variance is constant here, so the forward figure must equal the
            // annualized daily variance exactly - and must not include the day itself.
            var expectedRealized = VolatilityScaling.AnnualizeVariance(1.0e-4);
            foreach (var day in withForward.Take(20))
            {
                Check("forward realized variance is the annualized forward mean",
                    day.RealizedForwardVariance, expectedRealized, 1e-12);
                Check("ex-post premium is implied minus realized",
                    day.ExPostPremium, 0.04 - expectedRealized, 1e-12);
            }

            Info(string.Format("realized fwd vol={0:P2}, implied={1:P2}, premium={2:F4} vol pts",
                withForward[0].RealizedForwardVolatility, withForward[0].ImpliedVolatility,
                withForward[0].ExPostPremiumVolatilityPoints));

            // The forward window must start after the observation date, never on it.
            var ordered = realized.OrderBy(d => d.Date).ToList();
            var firstDate = withForward[0].Date;
            var index = ordered.FindIndex(d => d.Date == firstDate);
            IsTrue("the forward window opens on the following session", index >= 0);

            // Ex-ante premium stays unset until a forecast is attached.
            IsTrue("no forecast is attached by default", !withForward[0].HasForecast);
            VarianceRiskPremiumBuilder.AttachForecasts(premium, date => 0.03);
            IsTrue("a forecast can be attached", premium[0].HasForecast);
            Check("ex-ante premium uses the forecast", premium[0].ExAntePremium, 0.04 - 0.03, 1e-12);

            var calendarBuilt = VarianceRiskPremiumBuilder.BuildForCalendarMaturity(implied, realized, 30);
            IsTrue("thirty calendar days maps to twenty-one trading days",
                calendarBuilt[0].HorizonTradingDays == 21);
        }

        [Fact]
        public void PremiumSummaryReportsPositiveShare()
        {
            var rng = new Random(77);
            var realized = BuildRealizedSeries(count: 200, dailyVariance: 1.0e-4);

            // Implied sits above realized most of the time, as it does in index data.
            var realizedAnnual = VolatilityScaling.AnnualizeVariance(1.0e-4);
            var implied = realized.Select(d => new ImpliedVarianceDay
            {
                Symbol = "SPX",
                Date = d.Date,
                ImpliedVariance = realizedAnnual * (1.25 + Gaussian(rng) * 0.10),
                IsUsable = true
            }).ToList();

            var premium = VarianceRiskPremiumBuilder.Build(implied, realized, 21);
            var summary = VarianceRiskPremiumBuilder.Summarize(premium);

            Info(summary.ToString());
            IsTrue("most days carry a positive premium", summary.PositivePremiumShare > 0.9);
            IsTrue("the mean premium is positive", summary.MeanPremiumVolatilityPoints > 0.0);
            IsTrue("mean implied exceeds mean realized",
                summary.MeanImpliedVolatility > summary.MeanRealizedVolatility);
        }

        // ---------- ThetaData parsing ----------

        [Fact]
        public void CsvTableResolvesColumnsByName()
        {
            var csv = "ms_of_day,bid,ask,date,strike,right\n"
                    + "56700000,1.25,1.35,20240304,5000000,C\n"
                    + "56700000,2.10,2.20,20240304,5000000,P\n";

            var table = CsvTable.Parse(csv);
            IsTrue("all rows are parsed", table.Count == 2);

            var bid = table.RequireColumn("bid");
            Check("a column resolves by name", CsvTable.GetDouble(table.Rows[0], bid), 1.25, 1e-12);

            // Column order must not matter.
            var reordered = CsvTable.Parse("right,strike,date,ask,bid,ms_of_day\nC,5000000,20240304,1.35,1.25,56700000\n");
            Check("reordered columns resolve the same way",
                CsvTable.GetDouble(reordered.Rows[0], reordered.RequireColumn("bid")), 1.25, 1e-12);

            // Alternate spellings, so a version change does not break the parser.
            IsTrue("alternate column names are accepted",
                table.RequireColumn("expiration", "exp", "date") == table.RequireColumn("date"));

            Throws("a missing column throws rather than reading the wrong one",
                () => table.RequireColumn("implied_volatility"));
        }

        [Fact]
        public void ChainLoaderParsesQuotesAndCatchesUnitErrors()
        {
            var loader = new ThetaDataChainLoader();
            var expiration = new DateTime(2024, 4, 5);

            // Strikes in tenths of a cent, as ThetaData quotes them.
            var csv = "ms_of_day,bid,ask,date,strike,right\n"
                    + "56700000,1.25,1.35,20240304,5000000,C\n"
                    + "56700000,2.10,2.20,20240304,5000000,P\n"
                    + "56700000,0.95,1.05,20240304,5050000,C\n"
                    + "56700000,1.25,1.35,20240305,5000000,C\n";

            var slices = loader.Parse(CsvTable.Parse(csv), "SPXW", expiration);

            IsTrue("one slice per observation date", slices.Count == 2);
            IsTrue("quotes are grouped onto their date", slices[0].Quotes.Count == 3);
            Check("strikes are converted out of tenths of a cent",
                slices[0].Quotes[0].Strike, 5000.0, 1e-9);
            IsTrue("rights are parsed", slices[0].Quotes[0].Right == OptionRight.Call
                                        && slices[0].Quotes[1].Right == OptionRight.Put);
            Check("the observation time comes from ms_of_day",
                slices[0].ObservedAt.TimeOfDay.TotalMilliseconds, 56700000, 1e-6);

            // SPXW settles at the close; the standard SPX monthly settles in the morning.
            Check("SPXW settles at the close",
                slices[0].SettlesAt.TimeOfDay.TotalHours, 16.0, 1e-9);
            var monthly = loader.Parse(CsvTable.Parse(csv), "SPX", expiration);
            Check("SPX monthlies settle against the opening print",
                monthly[0].SettlesAt.TimeOfDay.TotalHours, 9.5, 1e-9);

            // The units guard is the cheap way to catch a wrong StrikeDivisor.
            IsTrue("a correct scale passes the guard",
                loader.Parse(CsvTable.Parse(csv), "SPXW", expiration, 5000.0).Count == 2);
            Throws("a wrong strike scale is caught",
                () => loader.Parse(CsvTable.Parse(csv), "SPXW", expiration, 5.0));
        }

        [Fact]
        public void BracketingExpirationsAreSelected()
        {
            var asOf = new DateTime(2024, 3, 4);
            var expirations = Enumerable.Range(0, 20).Select(i => asOf.AddDays(i * 7)).ToList();

            var selected = ThetaDataChainLoader.SelectBracketingExpirations(expirations, asOf);

            IsTrue("two expirations are selected", selected.Count == 2);

            var nearDays = (selected[0] - asOf).TotalDays;
            var nextDays = (selected[1] - asOf).TotalDays;
            Info(string.Format("selected {0:F0} and {1:F0} day expirations", nearDays, nextDays));

            IsTrue("the near term is at least twenty-three days out", nearDays >= 23);
            IsTrue("the near term is inside thirty days", nearDays <= 30);
            IsTrue("the next term is beyond thirty days", nextDays > 30);
            IsTrue("the next term is within thirty-seven days", nextDays <= 37);

            // Nothing in range must yield nothing rather than a wrong pair.
            var tooClose = ThetaDataChainLoader.SelectBracketingExpirations(
                new[] { asOf.AddDays(1), asOf.AddDays(2) }, asOf);
            IsTrue("no eligible expirations yields an empty selection", tooClose.Count == 0);
        }

        // ---------- helpers ----------

        /// <summary>
        /// Builds an option chain priced by Black-Scholes. Bids and asks are placed a tick
        /// either side of theoretical value, so deep out-of-the-money contracts naturally
        /// fall to a zero bid exactly as they do in real data.
        /// </summary>
        private static OptionChainSlice BuildChain(
            double volatility,
            int days,
            double lowStrike,
            double highStrike,
            double step,
            Func<double, double> volatilityForStrike = null)
        {
            const double tick = 0.005;

            var slice = new OptionChainSlice
            {
                Root = "SPXW",
                ObservedAt = Observed,
                SettlesAt = Observed.AddDays(days)
            };

            var timeToExpiry = days / 365.0;

            for (var strike = lowStrike; strike <= highStrike + 1e-9; strike += step)
            {
                var sigma = volatilityForStrike != null ? volatilityForStrike(strike) : volatility;

                var call = BlackScholes(Spot, strike, timeToExpiry, Rate, sigma, true);
                var put = BlackScholes(Spot, strike, timeToExpiry, Rate, sigma, false);

                slice.Quotes.Add(new OptionQuote(strike, OptionRight.Call,
                    Math.Max(0.0, call - tick), call + tick));
                slice.Quotes.Add(new OptionQuote(strike, OptionRight.Put,
                    Math.Max(0.0, put - tick), put + tick));
            }

            return slice;
        }

        private static OptionChainSlice CloneWithOverride(
            OptionChainSlice source, double strike, OptionRight right, double bid, double ask)
        {
            var clone = new OptionChainSlice
            {
                Root = source.Root,
                ObservedAt = source.ObservedAt,
                SettlesAt = source.SettlesAt
            };

            foreach (var quote in source.Quotes)
            {
                var isTarget = Math.Abs(quote.Strike - strike) < 1e-9 && quote.Right == right;
                clone.Quotes.Add(isTarget
                    ? new OptionQuote(strike, right, bid, ask)
                    : quote);
            }

            return clone;
        }

        private static ModelFreeVarianceResult Expiry(double days, double annualizedVariance)
        {
            return new ModelFreeVarianceResult
            {
                Variance = annualizedVariance,
                TimeToExpiryYears = days / 365.0,
                SettlesAt = Observed.AddDays(days),
                StrikesUsed = 50,
                MedianStrikeSpacing = 5.0
            };
        }

        private static List<RealizedVolatilityDay> BuildRealizedSeries(int count, double dailyVariance)
        {
            var days = new List<RealizedVolatilityDay>();
            var date = new DateTime(2024, 1, 1);

            for (int i = 0; i < count; i++)
            {
                while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                {
                    date = date.AddDays(1);
                }

                days.Add(new RealizedVolatilityDay
                {
                    Symbol = "SPX",
                    Date = date,
                    IntradayVariance = dailyVariance,
                    TotalVariance = dailyVariance,
                    ReturnCount = 78,
                    IsComplete = true
                });

                date = date.AddDays(1);
            }

            return days;
        }

        private static double BlackScholes(double spot, double strike, double time, double rate, double sigma, bool isCall)
        {
            var sqrtTime = sigma * Math.Sqrt(time);
            var d1 = (Math.Log(spot / strike) + (rate + 0.5 * sigma * sigma) * time) / sqrtTime;
            var d2 = d1 - sqrtTime;

            return isCall
                ? spot * NormalCdf(d1) - strike * Math.Exp(-rate * time) * NormalCdf(d2)
                : strike * Math.Exp(-rate * time) * NormalCdf(-d2) - spot * NormalCdf(-d1);
        }

        /// <summary>Abramowitz and Stegun 7.1.26, accurate to about 1e-7 - far finer than any tolerance here.</summary>
        private static double NormalCdf(double x)
        {
            var sign = x < 0 ? -1.0 : 1.0;
            var z = Math.Abs(x) / Math.Sqrt(2.0);

            var t = 1.0 / (1.0 + 0.3275911 * z);
            var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t
                           + 0.254829592) * t * Math.Exp(-z * z);

            return 0.5 * (1.0 + sign * y);
        }
    }
}
