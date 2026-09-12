using TradingStuff.EarningsStudy.Options;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// <see cref="ChainMeasureBuilder"/>'s pure logic, exercised with hand-built <see cref="ChainFetchResult"/>
/// fixtures - no HTTP, no fetcher. Covers every FetchStatus reachable from the entry-date selection
/// path, the "quotes still recorded" rule on a refusal, and the pre-entry/exit diagnostics.
/// </summary>
/// <remarks>
/// Two vocabulary members are deliberately NOT exercised here: <c>chain_empty</c> and
/// <c>no_paired_strike</c>. Both are gated by a strictly weaker condition that WP3b's own required
/// check sequence always tests FIRST and returns early on: <c>chain_empty</c> requires
/// <c>chain.Count==0</c>, which the "no rows on the entry date -&gt; no_entry_quotes" check (spec
/// item 5) already intercepts before <c>ImpliedSpotWithReason</c> ever runs; <c>no_paired_strike</c>
/// requires zero paired strikes, but <c>ImpliedSpotWithReason</c>'s own "solvable" set is a SUBSET of
/// <c>SelectAtmWithReason</c>'s "paired" set (two-sided implies both rows exist), so parity succeeding
/// guarantees a non-empty paired ladder before <c>SelectAtmWithReason</c> is ever reached. Neither is
/// a bug - it means those two exclusion rows should read zero in the memo, not that they are
/// untested dead branches - but it is worth a reviewer's eyes because it was not obvious until
/// derived here, and the same math means no fixture can force them.
/// </remarks>
public sealed class ChainMeasureBuilderTests
{
    private static readonly DateOnly PreEntry = new(2024, 1, 31);
    private static readonly DateOnly EntryDate = new(2024, 2, 1);
    private static readonly DateOnly ExitDate = new(2024, 2, 2);
    private static readonly DateOnly Expiration = new(2024, 2, 16);

    private static ChainFetchResult Fetch(
        Dictionary<DateOnly, List<ChainQuote>> byDate, Dictionary<DateOnly, decimal>? underlying = null) =>
        new("AAPL", null, null, Expiration, "eod", "16:00:00", byDate, underlying ?? []);

    [Fact]
    public void NoRowsOnTheEntryDateIsNoEntryQuotes()
    {
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate, Fetch([]));

        Assert.Equal(ChainsFetchStatus.NoEntryQuotes, row.FetchStatus);
        Assert.Equal("AAPL", row.Root);
        Assert.Equal(Expiration, row.Expiration);
        Assert.Equal(15, row.DteCalendarDays); // still computed: it needs only Expiration and EntryDate
        Assert.Null(row.AtmStrike);
    }

    [Fact]
    public void ACallWithNoMatchingPutIsNoTwoSidedStrike()
    {
        var entry = new List<ChainQuote> { new(100m, true, 1.00m, 1.10m) };
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate, Fetch(new() { [EntryDate] = entry }));

        Assert.Equal(ChainsFetchStatus.NoTwoSidedStrike, row.FetchStatus);
        Assert.Null(row.SpotParityEntry);
    }

    [Fact]
    public void ASpotNearestATwoStrikeLadderEdgeIsSpotOutsideStrikeLadder()
    {
        // Only two paired strikes: whichever one parity picks IS the low or high edge, so the ATM
        // selection always refuses - ties go to the lower strike, so 100 wins over 200.
        var entry = new List<ChainQuote>
        {
            new(100m, true, 5.00m, 5.10m), new(100m, false, 5.00m, 5.10m),
            new(200m, true, 1.00m, 1.10m), new(200m, false, 1.00m, 1.10m),
        };
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate, Fetch(new() { [EntryDate] = entry }));

        Assert.Equal(ChainsFetchStatus.SpotOutsideStrikeLadder, row.FetchStatus);
        Assert.Equal(100.00m, row.SpotParityEntry); // parity succeeded before the ATM refusal; still recorded
        Assert.Null(row.AtmStrike);
    }

    [Fact]
    public void AnImplausiblyTinyStraddleIsScaleImplausibleButKeepsItsQuotes()
    {
        var entry = new List<ChainQuote>
        {
            new(95m, true, 5.015m, 5.025m), new(95m, false, 0.015m, 0.025m),
            new(100m, true, 0.020m, 0.030m), new(100m, false, 0.020m, 0.030m),
            new(105m, true, 0.015m, 0.025m), new(105m, false, 5.015m, 5.025m),
        };
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate, Fetch(new() { [EntryDate] = entry }));

        Assert.Equal(ChainsFetchStatus.ScaleImplausible, row.FetchStatus);
        Assert.Equal(100m, row.AtmStrike); // the refusal does not blank out what was already computed
        Assert.Equal(0.05m, row.StraddleMidEntry);
    }

    [Fact]
    public void ANonPositiveParitySpotMakesTheScaleCheckNullRatherThanFalse()
    {
        // An artificial fixture (negative strikes) built solely to reach LooksLikeDollarScale's
        // documented "referencePrice <= 0 -> null" branch: the winning (min |C-P|) strike must
        // itself be negative, and a second strike keeps it interior to the paired ladder so ATM
        // selection still succeeds.
        var entry = new List<ChainQuote>
        {
            new(-10m, true, 0.005m, 0.015m), new(-10m, false, 10.995m, 11.005m),
            new(-5m, true, 0.995m, 1.005m), new(-5m, false, 0.995m, 1.005m),
            new(0m, true, 6.00m, 6.10m), new(0m, false, 0.95m, 1.05m),
        };
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate, Fetch(new() { [EntryDate] = entry }));

        Assert.Equal(ChainsFetchStatus.Ok, row.FetchStatus);
        Assert.Equal(-5m, row.SpotParityEntry);
        Assert.Equal(-5m, row.AtmStrike);
        Assert.Contains("dollar-scale check not measurable.", row.Note); // pre-entry/exit also note "no row"; that is incidental here
    }

    [Fact]
    public void AFetchFailureFlowsThroughUntouched()
    {
        var fetch = ChainFetchResult.Failure("AAPL", ChainsFetchStatus.NoSpanningExpiry, "no listed expiry spans the exit date.", "eod");
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate, fetch);

        Assert.Equal(ChainsFetchStatus.NoSpanningExpiry, row.FetchStatus);
        Assert.Equal("no listed expiry spans the exit date.", row.Note);
        Assert.Null(row.Expiration);
        Assert.Null(row.DteCalendarDays);
        Assert.Null(row.AtmStrike);
    }

    // A three-strike, interior-ATM-at-100 entry chain shared by the two diagnostics below - a
    // single-strike ladder can never bracket (ChainSelection's own rule), so these need enough
    // strikes for the entry-date ATM selection to succeed before the pre-entry/exit note is reached.
    private static List<ChainQuote> ThreeStrikeEntryAt100() =>
    [
        new(90m, true, 10.02m, 10.03m), new(90m, false, 0.02m, 0.03m),
        new(100m, true, 1.00m, 1.10m), new(100m, false, 1.00m, 1.10m),
        new(110m, true, 0.02m, 0.03m), new(110m, false, 10.02m, 10.03m),
    ];

    [Fact]
    public void AMissingRowAtTheAtmStrikeOnExitLeavesTheStraddleNullWithANote()
    {
        var exit = new List<ChainQuote> { new(110m, true, 1.00m, 1.10m), new(110m, false, 1.00m, 1.10m) }; // no row at 100
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate,
            Fetch(new() { [EntryDate] = ThreeStrikeEntryAt100(), [ExitDate] = exit }));

        Assert.Equal(ChainsFetchStatus.Ok, row.FetchStatus);
        Assert.Equal(100m, row.AtmStrike);
        Assert.Null(row.StraddleMidExit);
        Assert.Contains("exit straddle unavailable: no call or put row at the ATM strike.", row.Note);
    }

    [Fact]
    public void ACrossedMarketOnPreEntryLeavesTheStraddleNullWithANote()
    {
        var preEntry = new List<ChainQuote> { new(100m, true, 2.00m, 1.00m), new(100m, false, 1.00m, 1.10m) }; // call crossed: ask < bid
        var row = ChainMeasureBuilder.Build("e1", PreEntry, EntryDate, ExitDate,
            Fetch(new() { [EntryDate] = ThreeStrikeEntryAt100(), [PreEntry] = preEntry }));

        Assert.Equal(ChainsFetchStatus.Ok, row.FetchStatus);
        Assert.Equal(100m, row.AtmStrike);
        Assert.Null(row.StraddleMidPreEntry);
        Assert.Contains("pre-entry straddle unavailable: a crossed market on the call or put leg.", row.Note);
    }
}
