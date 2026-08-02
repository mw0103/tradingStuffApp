using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.Tests.Volatility;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// The 21-trading-day label, pinned from four directions. An off-by-one here silently changes every
/// number the study produces and nothing else in the pipeline would notice: a 20- or 22-session
/// label still fits every downstream type, still produces a plausible QLIKE, and still sorts into
/// quintiles.
/// </summary>
public class VrpConditioningLabelTests
{
    private static readonly DateOnly Start = new(2016, 1, 4);

    [Fact]
    public void TheLabelSpansExactlyTwentyOneTradingSessionsBeginningTheSessionAfterTheDecisionDate()
    {
        var dates = VrpConditioningFixture.TradingDates(140, Start);
        var sessions = VrpConditioningFixture.Usable(VrpConditioningFixture.SpxDays(dates));
        var rows = VrpConditioningFeatureBuilder.BuildRawRows(
            VrpConditioningFixture.SpxDays(dates), VrpConditioningFixture.Vix(dates));

        Assert.NotEmpty(rows);

        var indexOf = sessions
            .Select((s, i) => (Date: DateOnly.FromDateTime(s.Date), Index: i))
            .ToDictionary(x => x.Date, x => x.Index);

        foreach (var row in rows)
        {
            var t = indexOf[row.Date];

            Assert.Equal(21, row.LabelSessions);
            Assert.Equal(VrpConditioningHorizon.LabelTradingDays, row.LabelSessions);

            // t+1 and t+21 in the SESSION series, not the calendar.
            Assert.Equal(DateOnly.FromDateTime(sessions[t + 1].Date), row.LabelFrom);
            Assert.Equal(DateOnly.FromDateTime(sessions[t + 21].Date), row.LabelTo);

            // And the label window must NOT include the decision date itself.
            Assert.True(row.LabelFrom > row.Date, $"{row.Date}: label window opens at {row.LabelFrom}, which is not after the decision date.");
        }
    }

    [Fact]
    public void TheLabelIsTheSumOfExactlyThoseTwentyOneSessionsRealizedVariance()
    {
        var dates = VrpConditioningFixture.TradingDates(140, Start);
        var sessions = VrpConditioningFixture.Usable(VrpConditioningFixture.SpxDays(dates));
        var rows = VrpConditioningFeatureBuilder.BuildRawRows(
            VrpConditioningFixture.SpxDays(dates), VrpConditioningFixture.Vix(dates));

        var indexOf = sessions
            .Select((s, i) => (Date: DateOnly.FromDateTime(s.Date), Index: i))
            .ToDictionary(x => x.Date, x => x.Index);

        foreach (var row in rows)
        {
            var t = indexOf[row.Date];

            var expected = 0.0;
            for (var k = t + 1; k <= t + 21; k++) expected += sessions[k].TotalVariance;

            Assert.Equal(expected, row.LabelCumulativeVariance, 15);

            // The 20-session and 22-session windows are the two off-by-ones that would survive
            // every other assertion in this file, so they are excluded explicitly.
            var twenty = expected - sessions[t + 21].TotalVariance;
            Assert.NotEqual(twenty, row.LabelCumulativeVariance);

            // The 22-session comparison needs a 22nd session to exist. The builder emits rows
            // while t < Count - horizon, so the last row's label ends on the final session and
            // there is nothing beyond it — reading one further is the very off-by-one this
            // assertion is about.
            if (t + 22 < sessions.Count)
            {
                var twentyTwo = expected + sessions[t + 22].TotalVariance;
                Assert.NotEqual(twentyTwo, row.LabelCumulativeVariance);
            }
        }
    }

    [Fact]
    public void CountingTradingDaysOnTheRealCalendarBetweenLabelFromAndLabelToGivesTwentyOne()
    {
        // The session-index assertions above would still pass if "trading day" quietly meant "row of
        // whatever series happened to be loaded". This counts against the exchange calendar itself.
        var dates = VrpConditioningFixture.TradingDates(140, Start);
        var rows = VrpConditioningFixture.Rows(dates);

        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            var count = 0;
            for (var d = row.LabelFrom; d <= row.LabelTo; d = d.AddDays(1))
            {
                if (SessionBars.Regular(d, VrpConditioningFixture.Calendar) is not null) count++;
            }

            Assert.Equal(21, count);

            // And the calendar span is NOT 21 days — proving the count is sessions, not dates.
            Assert.True(row.LabelTo.DayNumber - row.LabelFrom.DayNumber + 1 > 21,
                $"{row.Date}: label window {row.LabelFrom}..{row.LabelTo} spans " +
                $"{row.LabelTo.DayNumber - row.LabelFrom.DayNumber + 1} calendar days; a 21-trading-day " +
                "window must span more than 21 calendar days once a weekend falls inside it.");
        }
    }

    [Fact]
    public void NoRowIsEmittedForADecisionDateWithoutAFullForwardWindow()
    {
        var dates = VrpConditioningFixture.TradingDates(140, Start);
        var sessions = VrpConditioningFixture.Usable(VrpConditioningFixture.SpxDays(dates));
        var rows = VrpConditioningFixture.Rows(dates);

        var lastUsable = DateOnly.FromDateTime(sessions[^(1 + 21)].Date);

        Assert.Equal(lastUsable, rows[^1].Date);
        Assert.DoesNotContain(rows, r => r.Date > lastUsable);

        // The last 21 sessions can never be decision dates: their labels would be short, and a short
        // label is dropped rather than padded (LESSONS.md #8 — refuse rather than project).
        for (var k = sessions.Count - 21; k < sessions.Count; k++)
        {
            var date = DateOnly.FromDateTime(sessions[k].Date);
            Assert.DoesNotContain(rows, r => r.Date == date);
        }
    }

    [Fact]
    public void TheImpliedLegIsVixSquaredDeAnnualizedOntoTheTwentyOneTradingDayBasis()
    {
        var dates = VrpConditioningFixture.TradingDates(140, Start);
        var vix = VrpConditioningFixture.Vix(dates);
        var rows = VrpConditioningFixture.Rows(dates);

        foreach (var row in rows)
        {
            // The VIX used is the DECISION DATE's own close, not a neighbour's.
            Assert.Equal(vix[row.Date], row.VixLevel, 12);

            var expected = vix[row.Date] / 100.0 * (vix[row.Date] / 100.0) * (21.0 / 252.0);
            Assert.Equal(expected, row.ImpliedVariance, 15);
            Assert.Equal(Math.Log(expected), row.LogImpliedVariance, 15);
        }
    }

    [Fact]
    public void AnnualizingTheLabelAndTheVarianceSwapPayoffAreEachOthersInverseAtTheStrike()
    {
        // A realized outcome exactly equal to the strike must pay the short exactly zero. This pins
        // the two conversions against each other: a mismatched annualization factor between the
        // implied leg and the realized leg would show up here as a non-zero payoff.
        const double vix = 18.0;
        var strikeVol = vix / 100.0;
        var labelVarianceAtStrike = strikeVol * strikeVol * (21.0 / 252.0);

        Assert.Equal(labelVarianceAtStrike, VrpConditioningHorizon.ImpliedVarianceOverLabelHorizon(vix), 15);

        var realizedVol = VrpConditioningHorizon.AnnualizedVolatilityFromLabel(labelVarianceAtStrike);
        Assert.Equal(strikeVol, realizedVol, 12);
        Assert.Equal(0.0, VrpConditioningHorizon.ShortVarianceSwapPayoffPerVegaNotional(strikeVol, realizedVol), 12);

        // And the short is paid when realized comes in below the strike.
        var calmer = VrpConditioningHorizon.AnnualizedVolatilityFromLabel(labelVarianceAtStrike * 0.25);
        Assert.True(VrpConditioningHorizon.ShortVarianceSwapPayoffPerVegaNotional(strikeVol, calmer) > 0.0);
    }

    [Fact]
    public void TheHacLagReachesAtLeastTheHorizonsMechanicalOverlap()
    {
        // The requirement is lag >= horizon - 1: two decisions h-1 days apart still share a label
        // day, so the Bartlett sum has to reach that far or the long-run variance is understated and
        // the statistic inflated.
        Assert.True(VrpConditioningHorizon.OverlappingHacLag >= VrpConditioningHorizon.LabelTradingDays - 1,
            $"overlapping HAC lag {VrpConditioningHorizon.OverlappingHacLag} does not reach the " +
            $"{VrpConditioningHorizon.LabelTradingDays - 1}-lag mechanical overlap of a " +
            $"{VrpConditioningHorizon.LabelTradingDays}-day label.");

        // The bootstrap block must exceed the same overlap, or blocks routinely cut through a single
        // label's own dependence structure.
        Assert.True(VrpConditioningHorizon.BootstrapMeanBlockLength > VrpConditioningHorizon.LabelTradingDays,
            "the bootstrap block length must exceed the label horizon.");

        // And the purge must cover the whole label, or a training row's label overlaps the next block.
        Assert.True(VrpConditioningHorizon.PurgeRows >= VrpConditioningHorizon.LabelTradingDays,
            "the purge must be at least the label horizon.");
    }
}
