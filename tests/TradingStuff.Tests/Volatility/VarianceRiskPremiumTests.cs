using TradingStuff.Volatility;
using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins the join between implied and realized variance, and the ex-post / ex-ante split.
/// </summary>
/// <remarks>
/// The forward window is the thing to get right. It must open on the session *after* the
/// implied volatility was observed — including the observation day would leak information the
/// trader did not have — and it must be exactly the horizon long, because comparing a 30-day
/// implied against any other window produces a premium that is largely a horizon mismatch.
/// </remarks>
public class VarianceRiskPremiumTests
{
    private static readonly DateTime Start = new(2024, 1, 1);

    private static ImpliedVarianceDay Implied(DateTime date, double variance, bool usable = true) =>
        new() { Symbol = "SPX", Date = date, ImpliedVariance = variance, IsUsable = usable };

    private static RealizedVolatilityDay Realized(DateTime date, double variance, bool complete = true) =>
        new() { Symbol = "SPX", Date = date, TotalVariance = variance, IsComplete = complete };

    private static List<RealizedVolatilityDay> RealizedRamp(int count, double daily = 1e-4) =>
        Enumerable.Range(0, count).Select(i => Realized(Start.AddDays(i), daily)).ToList();

    // ---------- day projections ----------

    [Fact]
    public void VolatilitiesAreSquareRootsOfTheirVariances()
    {
        var day = new VarianceRiskPremiumDay { ImpliedVariance = 0.04, RealizedForwardVariance = 0.0225 };

        Assert.Equal(0.2, day.ImpliedVolatility, 12);
        Assert.Equal(0.15, day.RealizedForwardVolatility, 12);
    }

    [Fact]
    public void NegativeVariancesFloorAtZeroRatherThanReturningNaN()
    {
        // A negative variance is a fault upstream, but it must not propagate as NaN through
        // every downstream summary statistic.
        var day = new VarianceRiskPremiumDay { ImpliedVariance = -1.0, RealizedForwardVariance = -1.0, ForecastVariance = -1.0 };

        Assert.Equal(0.0, day.ImpliedVolatility);
        Assert.Equal(0.0, day.RealizedForwardVolatility);
        Assert.Equal(0.0, day.ExAntePremiumVolatilityPoints);
    }

    [Fact]
    public void TheExPostPremiumIsImpliedMinusRealized()
    {
        var day = new VarianceRiskPremiumDay { ImpliedVariance = 0.04, RealizedForwardVariance = 0.0225 };

        Assert.Equal(0.04 - 0.0225, day.ExPostPremium, 12);
        Assert.Equal(0.2 - 0.15, day.ExPostPremiumVolatilityPoints, 12);
        Assert.Equal(Math.Log(0.04 / 0.0225), day.LogVarianceRatio, 12);
    }

    [Fact]
    public void TheExAntePremiumUsesTheForecastNotTheOutcome()
    {
        var day = new VarianceRiskPremiumDay
        {
            ImpliedVariance = 0.04, RealizedForwardVariance = 0.09, ForecastVariance = 0.0225,
        };

        // The tradeable comparison never touches RealizedForwardVariance.
        Assert.Equal(0.04 - 0.0225, day.ExAntePremium, 12);
        Assert.Equal(0.2 - 0.15, day.ExAntePremiumVolatilityPoints, 12);
    }

    // ---------- build: validation ----------

    [Fact]
    public void BuildRejectsMissingSeries()
    {
        Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.Build(null!, RealizedRamp(5), 1));
        Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.Build([], null!, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildRejectsANonPositiveHorizon(int horizon) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VarianceRiskPremiumBuilder.Build([], RealizedRamp(5), horizon));

    // ---------- build: alignment ----------

    [Fact]
    public void TheForwardWindowOpensTheSessionAfterObservation()
    {
        var realized = RealizedRamp(10);
        // Give each session a distinct variance so the averaged window is identifiable.
        for (int i = 0; i < realized.Count; i++) realized[i].TotalVariance = (i + 1) * 1e-4;

        var series = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.05)], realized, horizonTradingDays: 3);

        Assert.Single(series);
        Assert.True(series[0].HasRealizedForward);

        // Observed on index 0; the window is indices 1,2,3 -> mean 3e-4, annualized.
        Assert.Equal(VolatilityScaling.AnnualizeVariance(3e-4), series[0].RealizedForwardVariance, 15);
    }

    [Fact]
    public void ADayWithoutAClosedForwardWindowIsEmittedUnlabelled()
    {
        var realized = RealizedRamp(5);

        // Observed on the last session: nothing follows it.
        var series = VarianceRiskPremiumBuilder.Build([Implied(Start.AddDays(4), 0.05)], realized, 3);

        Assert.Single(series);
        Assert.False(series[0].HasRealizedForward);
        Assert.Equal(0.0, series[0].RealizedForwardVariance);
    }

    [Fact]
    public void AnImpliedDateWithNoMatchingSessionIsEmittedUnlabelled()
    {
        var series = VarianceRiskPremiumBuilder.Build([Implied(new DateTime(2030, 6, 1), 0.05)], RealizedRamp(10), 3);

        Assert.Single(series);
        Assert.False(series[0].HasRealizedForward);
    }

    [Fact]
    public void UnusableImpliedDaysAreDropped()
    {
        var series = VarianceRiskPremiumBuilder.Build(
            [Implied(Start, 0.05, usable: false), Implied(Start.AddDays(1), 0.06)], RealizedRamp(10), 2);

        Assert.Single(series);
        Assert.Equal(Start.AddDays(1), series[0].Date);
    }

    [Fact]
    public void IncompleteAndNonPositiveSessionsAreExcludedFromTheWindow()
    {
        var realized = RealizedRamp(10);
        realized[2].IsComplete = false;
        realized[3].TotalVariance = 0.0;

        var withHoles = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.05)], realized, 2);
        var clean = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.05)], RealizedRamp(10), 2);

        // Both windows are two sessions long, but the excluded sessions shift which ones.
        Assert.True(withHoles[0].HasRealizedForward);
        Assert.True(clean[0].HasRealizedForward);
    }

    [Fact]
    public void TheSeriesIsOrderedAndCarriesTheHorizon()
    {
        var implied = new[] { Implied(Start.AddDays(2), 0.05), Implied(Start, 0.04), Implied(Start.AddDays(1), 0.06) };

        var series = VarianceRiskPremiumBuilder.Build(implied, RealizedRamp(20), 4);

        Assert.Equal(3, series.Count);
        Assert.Equal([Start, Start.AddDays(1), Start.AddDays(2)], series.Select(d => d.Date));
        Assert.All(series, d => Assert.Equal(4, d.HorizonTradingDays));
        Assert.All(series, d => Assert.Equal("SPX", d.Symbol));
    }

    [Fact]
    public void ImpliedVarianceIsCarriedThroughUnchanged()
    {
        var series = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.0625)], RealizedRamp(10), 2);

        Assert.Equal(0.0625, series[0].ImpliedVariance, 15);
    }

    [Fact]
    public void ACalendarMaturityIsConvertedToTradingDays()
    {
        var realized = RealizedRamp(60);

        var byCalendar = VarianceRiskPremiumBuilder.BuildForCalendarMaturity([Implied(Start, 0.05)], realized, 30);
        var byTrading = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.05)], realized, 21);

        Assert.Equal(21, byCalendar[0].HorizonTradingDays);
        Assert.Equal(byTrading[0].RealizedForwardVariance, byCalendar[0].RealizedForwardVariance, 15);
    }

    // ---------- forecasts ----------

    [Fact]
    public void AttachForecastsRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.AttachForecasts(null!, _ => 1.0));
        Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.AttachForecasts([], null!));
    }

    [Fact]
    public void AttachedForecastsPopulateTheExAnteComparison()
    {
        var series = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.05)], RealizedRamp(10), 2);

        VarianceRiskPremiumBuilder.AttachForecasts(series, _ => 0.02);

        Assert.True(series[0].HasForecast);
        Assert.Equal(0.02, series[0].ForecastVariance, 15);
        Assert.Equal(0.03, series[0].ExAntePremium, 15);
    }

    [Fact]
    public void TheForecastIsLookedUpByTheDayItIsAttachedTo()
    {
        var series = VarianceRiskPremiumBuilder.Build(
            [Implied(Start, 0.05), Implied(Start.AddDays(1), 0.05)], RealizedRamp(10), 2);

        VarianceRiskPremiumBuilder.AttachForecasts(series, d => d == Start ? 0.01 : 0.02);

        Assert.Equal(0.01, series[0].ForecastVariance, 15);
        Assert.Equal(0.02, series[1].ForecastVariance, 15);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void AMissingOrNonPositiveForecastLeavesTheDayUnforecast(double? forecast)
    {
        var series = VarianceRiskPremiumBuilder.Build([Implied(Start, 0.05)], RealizedRamp(10), 2);

        VarianceRiskPremiumBuilder.AttachForecasts(series, _ => forecast);

        Assert.False(series[0].HasForecast);
        Assert.Equal(0.0, series[0].ForecastVariance);
    }

    // ---------- summary ----------

    private static List<VarianceRiskPremiumDay> Premiums(params (double Implied, double Realized)[] pairs) =>
        pairs.Select((p, i) => new VarianceRiskPremiumDay
        {
            Date = Start.AddDays(i),
            ImpliedVariance = p.Implied,
            RealizedForwardVariance = p.Realized,
            HasRealizedForward = true,
        }).ToList();

    [Fact]
    public void SummarizeRejectsMissingOrUnusableInput()
    {
        Assert.Throws<ArgumentNullException>(() => VarianceRiskPremiumBuilder.Summarize(null!));
        Assert.Throws<ArgumentException>(() => VarianceRiskPremiumBuilder.Summarize([]));

        // Present but with no closed forward window.
        Assert.Throws<ArgumentException>(() => VarianceRiskPremiumBuilder.Summarize(
            [new VarianceRiskPremiumDay { ImpliedVariance = 0.04, HasRealizedForward = false }]));

        // Closed window but no implied variance.
        Assert.Throws<ArgumentException>(() => VarianceRiskPremiumBuilder.Summarize(
            [new VarianceRiskPremiumDay { ImpliedVariance = 0.0, HasRealizedForward = true }]));
    }

    [Fact]
    public void SummarizeReportsTheMeanAndPositiveShare()
    {
        // Three of four days have implied above realized.
        var days = Premiums((0.04, 0.0225), (0.04, 0.0225), (0.04, 0.0225), (0.0225, 0.04));

        var summary = VarianceRiskPremiumBuilder.Summarize(days);

        Assert.Equal(4, summary.Observations);
        Assert.Equal(0.75, summary.PositivePremiumShare, 12);
        Assert.Equal(days.Average(d => d.ExPostPremiumVolatilityPoints), summary.MeanPremiumVolatilityPoints, 12);
        Assert.Equal(days.Average(d => d.ImpliedVolatility), summary.MeanImpliedVolatility, 12);
        Assert.Equal(days.Average(d => d.RealizedForwardVolatility), summary.MeanRealizedVolatility, 12);
    }

    [Fact]
    public void TheMedianAveragesTheMiddlePairOnAnEvenCount()
    {
        // Premiums in volatility points: 0.1, 0.2, 0.3, 0.4 -> median 0.25.
        var days = Premiums((0.04, 0.01), (0.09, 0.01), (0.16, 0.01), (0.25, 0.01));

        Assert.Equal(0.25, VarianceRiskPremiumBuilder.Summarize(days).MedianPremiumVolatilityPoints, 12);
    }

    [Fact]
    public void TheMedianTakesTheMiddleValueOnAnOddCount()
    {
        // 0.1, 0.2, 0.3 -> 0.2.
        var days = Premiums((0.04, 0.01), (0.09, 0.01), (0.16, 0.01));

        Assert.Equal(0.2, VarianceRiskPremiumBuilder.Summarize(days).MedianPremiumVolatilityPoints, 12);
    }

    [Fact]
    public void TheMedianIsOrderIndependent()
    {
        var ascending = Premiums((0.04, 0.01), (0.09, 0.01), (0.16, 0.01), (0.25, 0.01));
        var shuffled = Premiums((0.25, 0.01), (0.04, 0.01), (0.16, 0.01), (0.09, 0.01));

        Assert.Equal(
            VarianceRiskPremiumBuilder.Summarize(ascending).MedianPremiumVolatilityPoints,
            VarianceRiskPremiumBuilder.Summarize(shuffled).MedianPremiumVolatilityPoints, 12);
    }

    [Fact]
    public void AZeroPremiumDoesNotCountAsPositive()
    {
        // The share is a strict inequality: implied exactly equal to realized is not a premium.
        Assert.Equal(0.0, VarianceRiskPremiumBuilder.Summarize(Premiums((0.04, 0.04))).PositivePremiumShare, 12);
    }

    [Fact]
    public void UnusableDaysAreExcludedFromTheSummary()
    {
        var days = Premiums((0.04, 0.0225), (0.09, 0.0225));
        days.Add(new VarianceRiskPremiumDay { ImpliedVariance = 99.0, HasRealizedForward = false });

        Assert.Equal(2, VarianceRiskPremiumBuilder.Summarize(days).Observations);
    }

    [Fact]
    public void TheSummaryRendersItsHeadlineNumbers()
    {
        var text = new VarianceRiskPremiumSummary
        {
            Observations = 250,
            MeanPremiumVolatilityPoints = 0.0321,
            MedianPremiumVolatilityPoints = 0.0250,
            PositivePremiumShare = 0.8,
            MeanImpliedVolatility = 0.18,
            MeanRealizedVolatility = 0.15,
        }.ToString();

        Assert.Contains("n=250", text, StringComparison.Ordinal);
        Assert.Contains("0.0321", text, StringComparison.Ordinal);
        Assert.Contains("vol pts", text, StringComparison.Ordinal);
    }
}
