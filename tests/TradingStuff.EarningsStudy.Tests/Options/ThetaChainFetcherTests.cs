using System.Net;
using TradingStuff.EarningsStudy.Options;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// The network and cache decisions <see cref="ThetaChainFetcher"/> makes: eod-vs-minute, root
/// retry, and cache-vs-network. Most tests here double as their own negative control: the fake
/// handler's routes are registered exactly as many times as the correct behaviour should call them,
/// so a regression (an extra request, a missing fallback, a fallback that should not have fired)
/// makes the handler throw "unexpected or exhausted" rather than the assertion coincidentally
/// passing on wrong data.
/// </summary>
public sealed class ThetaChainFetcherTests
{
    private const string Chain = "date,strike,right,bid,ask\n2024-02-01,100,C,1.00,1.10\n2024-02-01,100,P,1.00,1.10\n";

    private const string MinuteChain =
        "symbol,expiration,strike,right,timestamp,bid,ask\n" +
        "\"AAPL\",\"2024-02-02\",100,\"C\",2024-02-01T15:45:00.000,1.00,1.10\n" +
        "\"AAPL\",\"2024-02-02\",100,\"P\",2024-02-01T15:45:00.000,1.00,1.10\n";

    private static readonly DateOnly PreEntry = new(2024, 1, 31);
    private static readonly DateOnly Entry = new(2024, 2, 1);
    private static readonly DateOnly Exit = new(2024, 2, 2);

    [Fact]
    public async Task AGeneric400OnEodFallsBackToMinuteForThatEventOnlyNotPermanently()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n")
            .OnStatus(ThetaDataEndpoints.OptionEndOfDay, HttpStatusCode.BadRequest, "bad request")
            .OnStatus(ThetaDataEndpoints.OptionEndOfDay, HttpStatusCode.BadRequest, "bad request")
            .On(ThetaDataEndpoints.OptionQuotes, MinuteChain)
            .On(ThetaDataEndpoints.OptionQuotes, MinuteChain);
        using var tempDir = new TempStudyDirectory();
        var fetcher = ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path));

        var first = await fetcher.FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);
        // A different range so the second minute request is not served from the on-disk cache.
        var second = await fetcher.FetchAsync("AAPL", new DateOnly(2024, 1, 15), new DateOnly(2024, 2, 1), CancellationToken.None);

        Assert.Equal("minute", first.SnapshotKind);
        Assert.Equal(ThetaDataClient.TimeOfDay(new ThetaDataOptions().SnapshotTimeOfDay), first.SnapshotTimeEt);
        Assert.Equal("minute", second.SnapshotKind);
        // Both EOD attempts happened - a generic non-2xx does not permanently disable EOD.
        Assert.Equal(2, handler.RequestedUrls.Count(u => u.Contains(ThetaDataEndpoints.OptionEndOfDay)));
    }

    [Fact]
    public async Task ASubscriptionExceptionOnEodPermanentlySwitchesLaterEventsToMinute()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n")
            .OnStatus(ThetaDataEndpoints.OptionEndOfDay, HttpStatusCode.Forbidden, "no subscription")
            .On(ThetaDataEndpoints.OptionQuotes, MinuteChain)
            .On(ThetaDataEndpoints.OptionQuotes, MinuteChain);
        using var tempDir = new TempStudyDirectory();
        var fetcher = ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path));

        var first = await fetcher.FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);
        // Only one EOD response is queued above: if the permanent switch failed, this second call
        // would retry EOD and the handler would throw "unexpected or exhausted" instead of returning.
        var second = await fetcher.FetchAsync("AAPL", new DateOnly(2024, 1, 15), new DateOnly(2024, 2, 1), CancellationToken.None);

        Assert.Equal("minute", first.SnapshotKind);
        Assert.Equal("minute", second.SnapshotKind);
        Assert.Equal(1, handler.RequestedUrls.Count(u => u.Contains(ThetaDataEndpoints.OptionEndOfDay)));
    }

    [Fact]
    public async Task ADotInTheSymbolRetriesWithItStripped()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, "expiration\n") // "BRK.A": header only, zero expirations
            .On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n") // "BRKA": answers
            .On(ThetaDataEndpoints.OptionEndOfDay, Chain);
        using var tempDir = new TempStudyDirectory();

        var fetch = await ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path))
            .FetchAsync("BRK.A", PreEntry, Exit, CancellationToken.None);

        Assert.Equal("BRKA", fetch.Root);
        Assert.Contains("BRK.A", fetch.Note);
        Assert.Contains("BRKA", fetch.Note);
    }

    [Fact]
    public async Task NeitherRootAnsweringIsNoChain()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, "expiration\n")
            .On(ThetaDataEndpoints.Expirations, "expiration\n");
        using var tempDir = new TempStudyDirectory();

        var fetch = await ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path))
            .FetchAsync("BRK.A", PreEntry, Exit, CancellationToken.None);

        Assert.Equal(ChainsFetchStatus.NoChain, fetch.FailureStatus);
    }

    [Fact]
    public async Task NoSpanningExpirationIsRecordedAsSuch()
    {
        var handler = new FakeThetaHandler().On(ThetaDataEndpoints.Expirations, "expiration\n2024-01-05\n");
        using var tempDir = new TempStudyDirectory();

        var fetch = await ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path))
            .FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);

        Assert.Equal(ChainsFetchStatus.NoSpanningExpiry, fetch.FailureStatus);
    }

    [Fact]
    public async Task ARerunWithAWarmCacheMakesZeroRequests()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n")
            .On(ThetaDataEndpoints.OptionEndOfDay, Chain);
        using var tempDir = new TempStudyDirectory();
        var paths = new StudyPaths(tempDir.Path);

        var first = await ChainsStepTests.NewFetcher(handler, paths).FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);
        Assert.Equal(2, handler.RequestCount); // expirations + eod

        // A brand-new fetcher (empty in-memory cache) against the same directory: every route above
        // is exhausted, so any further network call throws - this proves the ON-DISK cache, not just
        // the in-memory one.
        var second = await ChainsStepTests.NewFetcher(handler, paths).FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(first.QuotesByDate[Entry].Count, second.QuotesByDate[Entry].Count);
    }

    [Fact]
    public async Task AVersionExceptionOnExpirationsPropagatesUncaught()
    {
        var handler = new FakeThetaHandler().OnStatus(ThetaDataEndpoints.Expirations, HttpStatusCode.Gone, "outdated API version");
        using var tempDir = new TempStudyDirectory();

        await Assert.ThrowsAsync<ThetaDataVersionException>(() =>
            ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None));
    }

    [Fact]
    public async Task ANoDataResponseOnEodPropagatesRatherThanBeingTreatedAsAnEmptyChain()
    {
        var handler = new FakeThetaHandler()
            .On(ThetaDataEndpoints.Expirations, "expiration\n2024-02-02\n")
            .On(ThetaDataEndpoints.OptionEndOfDay, "No data for the specified request.");
        using var tempDir = new TempStudyDirectory();

        // Deliberately NOT caught inside the fetcher: the spec lists "no-data" as one of the causes
        // of a per-event FetchStatus "error", so it must reach the caller rather than silently
        // rendering as a chain with zero rows.
        await Assert.ThrowsAsync<ThetaDataNoDataException>(() =>
            ChainsStepTests.NewFetcher(handler, new StudyPaths(tempDir.Path)).FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None));
    }
}
