using TradingStuff.EarningsStudy.Options;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// WP3b happy-path fixtures: a fake Terminal serving expirations plus a three-date EOD chain, run
/// through <see cref="ThetaChainFetcher"/> + <see cref="ChainMeasureBuilder"/> exactly as
/// <see cref="ChainsStep"/> wires them. Assertions are exact decimals throughout
/// (docs/LESSONS.md: reproduce, don't approximate) since IM is a ratio of two of these numbers and
/// an off-by-a-cent test would not catch an off-by-a-cent bug.
/// </summary>
public sealed class ChainsStepTests
{
    private const string ExpirationsCsv = "expiration\n2024-01-05\n2024-02-02\n2024-02-09\n";

    // Parity holds closely at strike 190 (|C-P|=0.05, versus 5.00 and 4.50 at the neighbours), so
    // 190 is unambiguously the ATM strike. The exit row's call leg is bid 0 - a legitimately
    // expiring OTM leg - and is still two-sided (ask 0.05 >= bid 0.00), exercising the "zero bid is
    // allowed on pre-entry/exit" rule in the same fixture as the happy path.
    private const string EodWithUnderlying =
        "date,strike,right,bid,ask,underlying_price\n" +
        "2024-01-31,190,C,2.20,2.30,\n" +
        "2024-01-31,190,P,1.70,1.80,\n" +
        "2024-02-01,185,C,6.00,6.10,190.00\n" +
        "2024-02-01,185,P,1.00,1.10,190.00\n" +
        "2024-02-01,190,C,2.00,2.10,190.00\n" +
        "2024-02-01,190,P,1.95,2.05,190.00\n" +
        "2024-02-01,195,C,0.50,0.60,190.00\n" +
        "2024-02-01,195,P,5.00,5.10,190.00\n" +
        "2024-02-02,190,C,0.00,0.05,\n" +
        "2024-02-02,190,P,3.00,3.10,\n";

    private const string EodWithoutUnderlying =
        "date,strike,right,bid,ask\n" +
        "2024-01-31,190,C,2.20,2.30\n" +
        "2024-01-31,190,P,1.70,1.80\n" +
        "2024-02-01,185,C,6.00,6.10\n" +
        "2024-02-01,185,P,1.00,1.10\n" +
        "2024-02-01,190,C,2.00,2.10\n" +
        "2024-02-01,190,P,1.95,2.05\n" +
        "2024-02-01,195,C,0.50,0.60\n" +
        "2024-02-01,195,P,5.00,5.10\n" +
        "2024-02-02,190,C,0.00,0.05\n" +
        "2024-02-02,190,P,3.00,3.10\n";

    private static readonly DateOnly PreEntry = new(2024, 1, 31);
    private static readonly DateOnly Entry = new(2024, 2, 1);
    private static readonly DateOnly Exit = new(2024, 2, 2);

    internal static ThetaChainFetcher NewFetcher(FakeThetaHandler handler, StudyPaths paths, ThetaDataOptions? options = null, string snapshotKind = "eod") =>
        new(new ThetaDataClient(options ?? new ThetaDataOptions(), new HttpClient(handler)), paths, snapshotKind, delayMs: 0, log: _ => { });

    [Fact]
    public async Task AThreeDateEodChainWithUnderlyingPriceProducesOneExactOkRow()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, ExpirationsCsv)
            .On(ThetaDataEndpoints.OptionEndOfDay, EodWithUnderlying);
        using var tempDir = new TempStudyDirectory();

        var fetch = await NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);
        var row = ChainMeasureBuilder.Build("e1", PreEntry, Entry, Exit, fetch);

        Assert.Equal(ChainsFetchStatus.Ok, row.FetchStatus);
        Assert.Equal("AAPL", row.Root);
        Assert.Equal(new DateOnly(2024, 2, 2), row.Expiration);
        Assert.Equal(1, row.DteCalendarDays);
        Assert.Equal("eod", row.SnapshotKind);
        Assert.Equal("16:00:00", row.SnapshotTimeEt);
        Assert.Equal(190.00m, row.SpotFeedEntry);
        Assert.Equal(190.05m, row.SpotParityEntry);
        Assert.Equal(190m, row.AtmStrike);
        Assert.Equal(2.00m, row.CallBidEntry);
        Assert.Equal(2.10m, row.CallAskEntry);
        Assert.Equal(1.95m, row.PutBidEntry);
        Assert.Equal(2.05m, row.PutAskEntry);
        Assert.Equal(4.05m, row.StraddleMidEntry); // exact, not approximate
        Assert.Equal(0.20m, row.CombinedSpreadEntry);
        Assert.Equal(4.00m, row.StraddleMidPreEntry);
        Assert.Equal(190.50m, row.SpotParityPreEntry);
        Assert.Equal(3.075m, row.StraddleMidExit); // zero-bid call leg still yields a mid
        Assert.Null(row.SpotParityExit); // exit's call leg is not two-sided; diagnostic only, never a refusal
        Assert.Equal("eod snapshot time assumed 16:00:00 ET.", row.Note);
    }

    [Fact]
    public async Task WithoutAnUnderlyingPriceColumnSpotFeedIsNullAndParitySpotStillDrivesSelection()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, ExpirationsCsv)
            .On(ThetaDataEndpoints.OptionEndOfDay, EodWithoutUnderlying);
        using var tempDir = new TempStudyDirectory();

        var fetch = await NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);
        var row = ChainMeasureBuilder.Build("e1", PreEntry, Entry, Exit, fetch);

        Assert.Equal(ChainsFetchStatus.Ok, row.FetchStatus);
        Assert.Null(row.SpotFeedEntry);
        Assert.Equal(190.05m, row.SpotParityEntry);
        Assert.Equal(190m, row.AtmStrike);
        Assert.Equal(4.05m, row.StraddleMidEntry);
    }

    [Fact]
    public async Task ADecimalWithSevenFractionalDigitsSurvivesExactly()
    {
        const string chain = "date,strike,right,bid,ask\n2024-02-01,100,C,1.2345678,1.30\n2024-02-01,100,P,1.20,1.30\n";
        var handler = new FakeThetaHandler().On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n").On(ThetaDataEndpoints.OptionEndOfDay, chain);
        using var tempDir = new TempStudyDirectory();

        var fetch = await NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("ZZZZ", PreEntry, Exit, CancellationToken.None);

        Assert.Equal(1.2345678m, fetch.QuotesByDate[Entry].Single(q => q.IsCall).Bid);
    }

    [Fact]
    public async Task TheStrikeDivisorIsAppliedToEveryStrike()
    {
        const string chain = "date,strike,right,bid,ask\n2024-02-01,100000,C,1.00,1.10\n2024-02-01,100000,P,1.00,1.10\n";
        var handler = new FakeThetaHandler().On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n").On(ThetaDataEndpoints.OptionEndOfDay, chain);
        using var tempDir = new TempStudyDirectory();

        var fetch = await NewFetcher(handler, new StudyPaths(tempDir.Path), new ThetaDataOptions { StrikeDivisor = 1000.0 })
            .FetchAsync("ZZZZ", PreEntry, Exit, CancellationToken.None);

        Assert.Equal(100m, fetch.QuotesByDate[Entry][0].Strike);
    }

    [Theory]
    [InlineData("expiration\n2024-02-02\n")] // ISO
    [InlineData("expiration\n20240202\n")]   // compact yyyyMMdd
    public async Task BothExpirationDateShapesParse(string expirationsCsv)
    {
        const string chain = "date,strike,right,bid,ask\n2024-02-01,100,C,1.00,1.10\n2024-02-01,100,P,1.00,1.10\n";
        var handler = new FakeThetaHandler().On(ThetaDataEndpoints.Expirations, expirationsCsv).On(ThetaDataEndpoints.OptionEndOfDay, chain);
        using var tempDir = new TempStudyDirectory();

        var fetch = await NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("ZZZZ", PreEntry, Exit, CancellationToken.None);

        Assert.Equal(new DateOnly(2024, 2, 2), fetch.Expiration);
    }

    [Theory]
    [InlineData("2024-02-01T15:45:00.000")] // full ISO timestamp, as the bulk quote endpoint sends
    [InlineData("20240201")]                // compact yyyyMMdd
    public async Task BothChainRowDateShapesGroupIntoTheSameDate(string dateText)
    {
        var chain = $"timestamp,strike,right,bid,ask\n{dateText},100,C,1.00,1.10\n{dateText},100,P,1.00,1.10\n";
        var handler = new FakeThetaHandler().On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n").On(ThetaDataEndpoints.OptionEndOfDay, chain);
        using var tempDir = new TempStudyDirectory();

        var fetch = await NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("ZZZZ", PreEntry, Exit, CancellationToken.None);

        Assert.True(fetch.QuotesByDate.ContainsKey(Entry));
    }
}
