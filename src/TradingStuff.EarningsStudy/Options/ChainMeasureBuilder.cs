using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Options;

/// <summary>
/// WP3b's pure measurement step: given one event's fetched chain (or an already-decided fetch
/// failure), applies <see cref="ChainSelection"/> to the entry snapshot to find the ATM pair, and
/// reads the pre-entry/exit diagnostics at that same strike. No I/O — a function of the
/// <see cref="ChainFetchResult"/> already in hand, so it is testable without a fake HTTP handler.
/// </summary>
/// <remarks>
/// Every early return still carries whatever was computed before the refusal point (docs' "quotes
/// still recorded"): a parity refusal leaves the pair fields null but the note populated; an ATM
/// refusal keeps the parity spot that WAS found. Nothing here drops the row — every path ends in
/// exactly one <see cref="OptionMeasuresRow"/>.
/// </remarks>
internal static class ChainMeasureBuilder
{
    public static OptionMeasuresRow Build(
        string eventId, DateOnly preEntryDate, DateOnly entryDate, DateOnly exitDate, ChainFetchResult fetch)
    {
        if (!fetch.Succeeded)
        {
            return new OptionMeasuresRow(
                eventId, fetch.Root, fetch.Expiration, null, fetch.SnapshotKind, fetch.SnapshotTimeEt,
                null, null, null, null, null, null, null, null, null, null, null, null, null,
                fetch.FailureStatus!, fetch.Note);
        }

        var dte = fetch.Expiration!.Value.DayNumber - entryDate.DayNumber;
        var notes = new List<string>();
        if (!string.IsNullOrEmpty(fetch.Note)) notes.Add(fetch.Note);

        decimal? spotParityEntry = null;
        decimal? spotFeedEntry = null;

        // Captures the closure variables above at CALL time, not declaration time, so a refusal
        // raised after spotParityEntry/spotFeedEntry are set still reports them.
        OptionMeasuresRow Refuse(string status, AtmPair? pairSoFar = null) => new(
            eventId, fetch.Root, fetch.Expiration, dte, fetch.SnapshotKind, fetch.SnapshotTimeEt,
            spotParityEntry, spotFeedEntry,
            pairSoFar?.Strike, pairSoFar?.Call.Bid, pairSoFar?.Call.Ask, pairSoFar?.Put.Bid, pairSoFar?.Put.Ask,
            pairSoFar?.StraddleMid, pairSoFar?.CombinedSpread,
            null, null, null, null,
            status, NoteOf(notes));

        var entryQuotes = fetch.QuotesByDate.GetValueOrDefault(entryDate) ?? [];
        if (entryQuotes.Count == 0)
        {
            return Refuse(ChainsFetchStatus.NoEntryQuotes);
        }

        var parity = ChainSelection.ImpliedSpotWithReason(entryQuotes);
        if (parity.Reason is not null)
        {
            AddDetail(notes, parity.Detail);
            return Refuse(parity.Reason);
        }
        spotParityEntry = parity.Parity!.Spot;

        spotFeedEntry = fetch.UnderlyingByDate.TryGetValue(entryDate, out var feedSpot) ? feedSpot : null;
        var spotForAtm = spotFeedEntry ?? spotParityEntry.Value;

        var atmSelection = ChainSelection.SelectAtmWithReason(entryQuotes, spotForAtm);
        if (atmSelection.Reason is not null)
        {
            AddDetail(notes, atmSelection.Detail);
            return Refuse(atmSelection.Reason);
        }

        var pair = atmSelection.Pair!;
        var (straddleMidPreEntry, spotParityPreEntry) = PreOrExitDiagnostic(fetch, preEntryDate, pair.Strike, notes, "pre-entry");
        var (straddleMidExit, spotParityExit) = PreOrExitDiagnostic(fetch, exitDate, pair.Strike, notes, "exit");

        var scale = ChainSelection.LooksLikeDollarScale(entryQuotes, spotParityEntry.Value);
        var status = ChainsFetchStatus.Ok;
        if (scale == false) status = ChainsFetchStatus.ScaleImplausible;
        else if (scale is null) notes.Add("dollar-scale check not measurable.");

        return new OptionMeasuresRow(
            eventId, fetch.Root, fetch.Expiration, dte, fetch.SnapshotKind, fetch.SnapshotTimeEt,
            spotParityEntry, spotFeedEntry, pair.Strike,
            pair.Call.Bid, pair.Call.Ask, pair.Put.Bid, pair.Put.Ask,
            pair.StraddleMid, pair.CombinedSpread,
            straddleMidPreEntry, spotParityPreEntry, straddleMidExit, spotParityExit,
            status, NoteOf(notes));
    }

    /// <summary>
    /// At the entry's ATM strike: a two-sided (non-crossed) call and put give a straddle mid — a
    /// zero bid is allowed here, since an expiring OTM leg is legitimately bid 0. The parity spot on
    /// this date is diagnostic only and is never a refusal reason.
    /// </summary>
    private static (decimal? StraddleMid, decimal? SpotParity) PreOrExitDiagnostic(
        ChainFetchResult fetch, DateOnly date, decimal atmStrike, List<string> notes, string label)
    {
        var quotes = fetch.QuotesByDate.GetValueOrDefault(date) ?? [];
        var call = quotes.FirstOrDefault(q => q.IsCall && q.Strike == atmStrike);
        var put = quotes.FirstOrDefault(q => !q.IsCall && q.Strike == atmStrike);

        decimal? straddleMid = null;
        if (call is not null && put is not null && call.Ask >= call.Bid && put.Ask >= put.Bid)
        {
            straddleMid = call.Mid + put.Mid;
        }
        else
        {
            notes.Add($"{label} straddle unavailable: {DescribeMissingPair(call, put)}.");
        }

        return (straddleMid, ChainSelection.ImpliedSpot(quotes)?.Spot);
    }

    private static string DescribeMissingPair(ChainQuote? call, ChainQuote? put) =>
        (call, put) switch
        {
            (null, null) => "no call or put row at the ATM strike",
            (null, not null) => "no call row at the ATM strike",
            (not null, null) => "no put row at the ATM strike",
            _ => "a crossed market on the call or put leg",
        };

    private static void AddDetail(List<string> notes, string? detail)
    {
        if (!string.IsNullOrEmpty(detail)) notes.Add(detail);
    }

    private static string? NoteOf(List<string> notes) => notes.Count == 0 ? null : string.Join(" ", notes);
}
