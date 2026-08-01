using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Pins chain parsing, settlement resolution, and the strike-units guard.
/// </summary>
/// <remarks>
/// Two things here fail silently if wrong. A wrong <c>StrikeDivisor</c> produces a chain that
/// parses cleanly and is off by three orders of magnitude, which is why the loader
/// cross-checks strikes against the underlying level. And AM-settled SPX monthlies versus
/// PM-settled SPXW weeklies is most of a trading day at a 23-day near term — enough to move
/// the annualization visibly.
/// </remarks>
public class ThetaDataChainLoaderTests
{
    private static readonly DateTime Expiry = new(2024, 3, 15);

    private static CsvTable Table(params string[] rows) =>
        CsvTable.Parse("date,ms_of_day,strike,right,bid,ask\n" + string.Join("\n", rows) + "\n");

    /// <summary>API v3 quotes strikes in dollars, so 5000.0 is written 5000.000.</summary>
    private static string Row(string date, double strike, string right, double bid, double ask, int ms = 56_700_000) =>
        $"{date},{ms},{strike:F3},{right},{bid},{ask}";

    // ---------- settlement ----------

    [Fact]
    public void SettlementDefaultsSplitMorningAndAfternoonRoots()
    {
        var s = new ExpirationSettlement();

        Assert.Equal(new TimeSpan(9, 30, 0), s.MorningSettlementTime);
        Assert.Equal(new TimeSpan(16, 0, 0), s.AfternoonSettlementTime);
        Assert.Contains("SPX", s.MorningSettledRoots);
    }

    [Fact]
    public void MonthlySpxSettlesInTheMorningAndWeekliesAtTheClose()
    {
        var s = new ExpirationSettlement();

        // Standard SPX monthlies settle against the Special Opening Quotation; SPXW
        // weeklies settle against the close. Most of a trading day apart.
        Assert.Equal(Expiry.Date.AddHours(9).AddMinutes(30), s.SettlementFor("SPX", Expiry));
        Assert.Equal(Expiry.Date.AddHours(16), s.SettlementFor("SPXW", Expiry));
    }

    [Fact]
    public void MorningSettledRootsAreMatchedCaseInsensitively() =>
        Assert.Equal(Expiry.Date.AddHours(9).AddMinutes(30), new ExpirationSettlement().SettlementFor("spx", Expiry));

    [Fact]
    public void SettlementDiscardsAnyTimeOnTheExpirationDate() =>
        Assert.Equal(Expiry.Date.AddHours(16),
            new ExpirationSettlement().SettlementFor("SPXW", Expiry.AddHours(11)));

    // ---------- parsing ----------

    [Fact]
    public void ParseRejectsAMissingTable() =>
        Assert.Throws<ArgumentNullException>(() => new ThetaDataChainLoader().Parse(null!, "SPXW", Expiry));

    [Fact]
    public void AMissingRequiredColumnIsReported()
    {
        var table = CsvTable.Parse("date,strike,right,bid\n20240304,5000000,C,1.0\n");

        // No ask column: a schema change must throw rather than read a neighbouring field.
        Assert.Throws<InvalidOperationException>(() => new ThetaDataChainLoader().Parse(table, "SPXW", Expiry));
    }

    [Fact]
    public void StrikesArriveInDollars()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 5000.0, "C", 1.0, 1.2)), "SPXW", Expiry);

        Assert.Single(slices);
        Assert.Equal(5000.0, slices[0].Quotes[0].Strike, 9);
    }

    [Fact]
    public void TheDivisorIsConfigurableForFeedsThatDoNot()
    {
        // The v2 feed quoted tenths of a cent and needed 1000. Kept configurable rather than
        // hardcoded, because the units are a property of the feed, not of options.
        var loader = new ThetaDataChainLoader(new ThetaDataOptions { StrikeDivisor = 1000.0 });

        var slices = loader.Parse(Table(Row("20240304", 5_000_000.0, "C", 1.0, 1.2)), "SPXW", Expiry);

        Assert.Equal(5000.0, slices[0].Quotes[0].Strike, 9);
    }

    [Fact]
    public void QuotesCarryTheirRightBidAndAsk()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 5000.0, "C", 1.0, 1.2), Row("20240304", 5000.0, "P", 2.0, 2.4)),
            "SPXW", Expiry);

        var call = slices[0].Quotes[0];
        var put = slices[0].Quotes[1];

        Assert.Equal(OptionRight.Call, call.Right);
        Assert.Equal(1.0, call.Bid, 9);
        Assert.Equal(1.2, call.Ask, 9);
        Assert.Equal(OptionRight.Put, put.Right);
        Assert.Equal(2.0, put.Bid, 9);
    }

    [Theory]
    [InlineData("C", OptionRight.Call)]
    [InlineData("c", OptionRight.Call)]
    [InlineData("CALL", OptionRight.Call)]
    [InlineData("P", OptionRight.Put)]
    [InlineData("put", OptionRight.Put)]
    public void TheRightIsReadFromItsFirstCharacter(string text, OptionRight expected)
    {
        var slices = new ThetaDataChainLoader().Parse(Table(Row("20240304", 5000.0, text, 1.0, 1.2)), "SPXW", Expiry);

        Assert.Equal(expected, slices[0].Quotes[0].Right);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("")]
    public void AnUnrecognizedRightIsRejected(string text) =>
        Assert.Throws<InvalidOperationException>(() =>
            new ThetaDataChainLoader().Parse(Table(Row("20240304", 5000.0, text, 1.0, 1.2)), "SPXW", Expiry));

    [Fact]
    public void NonPositiveStrikesAreDropped()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 0.0, "C", 1.0, 1.2), Row("20240304", 5000.0, "C", 1.0, 1.2)),
            "SPXW", Expiry);

        Assert.Single(slices[0].Quotes);
        Assert.Equal(5000.0, slices[0].Quotes[0].Strike, 9);
    }

    [Fact]
    public void RowsAreGroupedIntoOneSlicePerObservationDate()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(
                Row("20240305", 5000.0, "C", 1.0, 1.2),
                Row("20240304", 5000.0, "C", 1.0, 1.2),
                Row("20240304", 5100.0, "P", 2.0, 2.4)),
            "SPXW", Expiry);

        Assert.Equal(2, slices.Count);
        // Ordered by observation time, not by the order rows arrived.
        Assert.Equal(new DateTime(2024, 3, 4), slices[0].ObservedAt.Date);
        Assert.Equal(2, slices[0].Quotes.Count);
        Assert.Single(slices[1].Quotes);
    }

    [Fact]
    public void TheObservationTimeComesFromTheRowWhenPresent()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 5000.0, "C", 1.0, 1.2, ms: 36_000_000)), "SPXW", Expiry);

        Assert.Equal(new DateTime(2024, 3, 4, 10, 0, 0), slices[0].ObservedAt);
    }

    [Fact]
    public void TheConfiguredSnapshotTimeIsUsedWhenTheColumnIsAbsent()
    {
        var table = CsvTable.Parse("date,strike,right,bid,ask\n20240304,5000000,C,1.0,1.2\n");

        var slices = new ThetaDataChainLoader().Parse(table, "SPXW", Expiry);

        // 15:45 by default.
        Assert.Equal(new DateTime(2024, 3, 4, 15, 45, 0), slices[0].ObservedAt);
    }

    [Fact]
    public void TheSliceCarriesItsRootAndSettlement()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 5000.0, "C", 1.0, 1.2)), "SPX", Expiry);

        Assert.Equal("SPX", slices[0].Root);
        Assert.Equal(Expiry.Date.AddHours(9).AddMinutes(30), slices[0].SettlesAt);
    }

    // ---------- date parsing ----------

    [Fact]
    public void CompactDatesAreParsed()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 5000.0, "C", 1.0, 1.2)), "SPXW", Expiry);

        Assert.Equal(new DateTime(2024, 3, 4), slices[0].ObservedAt.Date);
    }

    [Fact]
    public void IsoDatesAreAlsoAccepted()
    {
        var table = CsvTable.Parse("date,strike,right,bid,ask\n2024-03-04,5000000,C,1.0,1.2\n");

        var slices = new ThetaDataChainLoader().Parse(table, "SPXW", Expiry);

        Assert.Equal(new DateTime(2024, 3, 4), slices[0].ObservedAt.Date);
    }

    [Fact]
    public void AnUnparseableDateNamesTheOffendingValue()
    {
        var table = CsvTable.Parse("date,strike,right,bid,ask\nnot-a-date,5000000,C,1.0,1.2\n");

        var ex = Assert.Throws<InvalidOperationException>(() => new ThetaDataChainLoader().Parse(table, "SPXW", Expiry));
        Assert.Contains("not-a-date", ex.Message, StringComparison.Ordinal);
    }

    // ---------- strike-units guard ----------

    [Fact]
    public void AChainBracketingTheUnderlyingPasses()
    {
        var slices = new ThetaDataChainLoader().Parse(
            Table(Row("20240304", 4500.0, "P", 1.0, 1.2), Row("20240304", 5500.0, "C", 1.0, 1.2)),
            "SPXW", Expiry, expectedUnderlyingLevel: 5000.0);

        Assert.Single(slices);
    }

    [Fact]
    public void AWrongDivisorIsCaughtByTheUnderlyingCrossCheck()
    {
        // A divisor left at the v2 value of 1000 turns 5000-point strikes into 5: parses
        // cleanly, and is wrong by three orders of magnitude.
        var loader = new ThetaDataChainLoader(new ThetaDataOptions { StrikeDivisor = 1000.0 });

        var ex = Assert.Throws<InvalidOperationException>(() => loader.Parse(
            Table(Row("20240304", 4500.0, "P", 1.0, 1.2), Row("20240304", 5500.0, "C", 1.0, 1.2)),
            "SPXW", Expiry, expectedUnderlyingLevel: 5000.0));

        Assert.Contains("units mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("StrikeDivisor", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SPXW", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardToleratesAChainOnlyPartlyBracketingTheLevel()
    {
        // The bounds are deliberately loose - half the lowest strike to twice the highest -
        // because a one-sided pull is normal and only an order-of-magnitude error matters.
        var loader = new ThetaDataChainLoader();

        loader.Parse(Table(Row("20240304", 6000.0, "C", 1.0, 1.2)), "SPXW", Expiry, expectedUnderlyingLevel: 5000.0);
        loader.Parse(Table(Row("20240304", 4000.0, "P", 1.0, 1.2)), "SPXW", Expiry, expectedUnderlyingLevel: 5000.0);
    }

    [Fact]
    public void TheGuardIsSkippedWhenNoLevelIsSupplied()
    {
        var loader = new ThetaDataChainLoader(new ThetaDataOptions { StrikeDivisor = 1000.0 });

        // No exception: the caller did not ask for the check.
        Assert.Single(loader.Parse(Table(Row("20240304", 5000.0, "C", 1.0, 1.2)), "SPXW", Expiry));
    }

    // ---------- expiration selection ----------

    [Fact]
    public void SelectionRejectsAMissingList() =>
        Assert.Throws<ArgumentNullException>(() =>
            ThetaDataChainLoader.SelectBracketingExpirations(null!, new DateTime(2024, 3, 4)));

    [Fact]
    public void SelectionBracketsTheTargetMaturity()
    {
        var asOf = new DateTime(2024, 3, 4);
        DateTime[] available = [asOf.AddDays(10), asOf.AddDays(25), asOf.AddDays(35), asOf.AddDays(60)];

        var selected = ThetaDataChainLoader.SelectBracketingExpirations(available, asOf);

        Assert.Equal([asOf.AddDays(25), asOf.AddDays(35)], selected);
    }

    [Fact]
    public void TheNearTermIsTheLatestInsideTheWindow()
    {
        var asOf = new DateTime(2024, 3, 4);
        DateTime[] available = [asOf.AddDays(23), asOf.AddDays(28), asOf.AddDays(35)];

        // Closest to the target from below, not merely the first eligible.
        Assert.Equal(asOf.AddDays(28), ThetaDataChainLoader.SelectBracketingExpirations(available, asOf)[0]);
    }

    [Fact]
    public void TheNextTermIsTheEarliestBeyondTheTarget()
    {
        var asOf = new DateTime(2024, 3, 4);
        DateTime[] available = [asOf.AddDays(25), asOf.AddDays(32), asOf.AddDays(36)];

        Assert.Equal(asOf.AddDays(32), ThetaDataChainLoader.SelectBracketingExpirations(available, asOf)[1]);
    }

    [Fact]
    public void ExpirationsInsideTheMinimumAreIneligible()
    {
        var asOf = new DateTime(2024, 3, 4);

        // 22 days is below the 23-day floor; VIX rolls out of very short-dated options.
        Assert.Empty(ThetaDataChainLoader.SelectBracketingExpirations([asOf.AddDays(22)], asOf));
        Assert.Single(ThetaDataChainLoader.SelectBracketingExpirations([asOf.AddDays(23)], asOf));
    }

    [Fact]
    public void ExpirationsBeyondTheMaximumAreIneligible()
    {
        var asOf = new DateTime(2024, 3, 4);

        Assert.Empty(ThetaDataChainLoader.SelectBracketingExpirations([asOf.AddDays(38)], asOf));
        Assert.Single(ThetaDataChainLoader.SelectBracketingExpirations([asOf.AddDays(37)], asOf));
    }

    [Fact]
    public void AnExpirationExactlyAtTheTargetIsTheNearTerm()
    {
        var asOf = new DateTime(2024, 3, 4);

        // The near-term window is inclusive of the target and the next term is strictly
        // beyond it, so 30 days can only be the near leg.
        var selected = ThetaDataChainLoader.SelectBracketingExpirations([asOf.AddDays(30), asOf.AddDays(35)], asOf);

        Assert.Equal([asOf.AddDays(30), asOf.AddDays(35)], selected);
    }

    [Fact]
    public void DuplicatesAndOrderingAreNormalized()
    {
        var asOf = new DateTime(2024, 3, 4);
        DateTime[] available =
        [
            asOf.AddDays(35), asOf.AddDays(25).AddHours(11), asOf.AddDays(25), asOf.AddDays(35),
        ];

        var selected = ThetaDataChainLoader.SelectBracketingExpirations(available, asOf);

        Assert.Equal([asOf.AddDays(25), asOf.AddDays(35)], selected);
    }

    [Fact]
    public void NoEligibleExpirationsYieldsAnEmptySelection()
    {
        var asOf = new DateTime(2024, 3, 4);

        Assert.Empty(ThetaDataChainLoader.SelectBracketingExpirations([asOf.AddDays(1), asOf.AddDays(90)], asOf));
    }

    [Fact]
    public void TheSelectionWindowIsConfigurable()
    {
        var asOf = new DateTime(2024, 3, 4);
        DateTime[] available = [asOf.AddDays(6), asOf.AddDays(9)];

        var selected = ThetaDataChainLoader.SelectBracketingExpirations(
            available, asOf, targetDays: 7, minimumNearTermDays: 5.0, maximumNextTermDays: 10.0);

        Assert.Equal([asOf.AddDays(6), asOf.AddDays(9)], selected);
    }
}
