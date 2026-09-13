using System.Globalization;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>One event as the coverage measurement sees it. Nothing about the NAME reaches here.</summary>
/// <param name="PrintDate">From <c>event_timing.csv</c>. Null when the timing step resolved none; such an event falls in no quarter and is counted apart.</param>
/// <param name="ChainStatus">The <c>option_measures.fetch_status</c>, or null when the row is absent. A recorded failure is a COMPLETED fetch and counts as coverage.</param>
/// <param name="ClosesStatus">The <c>closes.status</c>, or null when the row is absent. Same rule.</param>
public sealed record CoverageInput(DateOnly? PrintDate, string? ChainStatus, string? ClosesStatus)
{
    public bool HasChainRow => ChainStatus is not null;

    public bool HasClosesRow => ClosesStatus is not null;
}

/// <summary>One calendar quarter's fetch coverage. <paramref name="Covered"/> is the only field the subset rule reads.</summary>
/// <param name="Events">Events in window and kept after dedup whose print date falls in this quarter.</param>
/// <param name="MissingChainRow">Of those, how many have NO row in <c>option_measures.csv</c>.</param>
/// <param name="MissingClosesRow">Of those, how many have NO row in <c>closes.csv</c>.</param>
/// <param name="ChainFetchErrors">Of those, how many carry <c>fetch_status = error</c> — a completed but failed fetch, which the operator can re-run.</param>
/// <param name="ClosesNotOk">Of those, how many carry a <c>closes.status</c> other than <c>ok</c>.</param>
public sealed record QuarterCoverageRow(
    string Quarter,
    int Events,
    int MissingChainRow,
    int MissingClosesRow,
    int ChainFetchErrors,
    int ClosesNotOk,
    bool Covered);

/// <summary>
/// What the coverage measurement concluded, and the two facts the memo's title line depends on.
/// </summary>
/// <param name="Quarters">Every quarter of the REGISTERED window, in ascending order, covered or not. All sixteen are printed: a quarter missing from this table would be a gap that reads as health.</param>
/// <param name="Subset">The deliverable subset: the maximal run of consecutive covered quarters ending at the most recent quarter of the window, ascending. Empty when that most recent quarter is itself uncovered.</param>
/// <param name="FullWindow">The subset is every quarter of the window, so the memo is final rather than provisional.</param>
/// <param name="EventsWithNoPrintDate">In window and kept after dedup, but the timing step resolved no print date: such an event falls in no quarter, so it is in no coverage denominator and is reported here instead of vanishing.</param>
/// <param name="EventsOutsideWindowQuarters">In window and kept after dedup, with a print date outside every quarter of the registered window. Also outside the subset, and also reported rather than dropped.</param>
public sealed record CoverageReport(
    IReadOnlyList<QuarterCoverageRow> Quarters,
    IReadOnlyList<string> Subset,
    bool FullWindow,
    int EventsWithNoPrintDate,
    int EventsOutsideWindowQuarters,
    IReadOnlyList<Tally> ChainStatuses,
    IReadOnlyList<Tally> ClosesStatuses)
{
    /// <summary>Nothing is deliverable: the most recent quarter of the window is not covered, so the run ending there is empty.</summary>
    public bool NothingDeliverable => Subset.Count == 0;

    /// <summary>The memo is PROVISIONAL: something is deliverable, but not the whole registered window.</summary>
    public bool Provisional => !FullWindow && Subset.Count > 0;

    /// <summary>Whether an event's print date falls inside the deliverable subset. The ONLY question gate 07b asks.</summary>
    public bool Includes(DateOnly printDate) =>
        Subset.Contains(QuarterCoverage.QuarterOf(printDate), StringComparer.Ordinal);
}

/// <summary>
/// The v2 deadline fallback (<c>docs/research/c1-preregistration-v2.md</c> section 3), made
/// mechanical so no judgement is exercised on the day.
///
/// <b>Coverage of a quarter.</b> Over the events in window and kept after dedup whose print date
/// falls in that calendar quarter, EVERY one has a row in <c>option_measures.csv</c> AND a row in
/// <c>closes.csv</c>. Any status counts: a recorded failure is a fetch that completed and returned
/// an answer, and re-running it is the operator's business, not the subset rule's — which is why
/// the error statuses are COUNTED per quarter rather than allowed to silently withhold a quarter.
///
/// <b>A quarter with no events is covered.</b> Vacuously: every one of its zero events has both
/// rows. That reading is deliberate and is the one place this rule could let absence read as
/// health, so the event count is a column of the table and a zero is visible on its face. The
/// alternative — treating an empty quarter as uncovered — would make the subset depend on whether
/// the frozen universe happened to print in a quarter, which is not a statement about fetching.
///
/// <b>The subset.</b> The maximal run of consecutive covered quarters ending at the most recent
/// quarter of the registered window. A gap in the middle truncates the run at the gap; a gap at the
/// most recent quarter leaves the run empty, and an empty run is not a deliverable — the caller
/// writes the coverage table, no verdict, and exits non-zero.
///
/// <b>Time only.</b> Every input here is a date and two row-presence flags. No symbol, no exchange,
/// no market cap: symbol-partial subsets are prohibited in every form, and the way to make that
/// true is for the rule to be unable to express one.
/// </summary>
public static class QuarterCoverage
{
    /// <summary>The calendar quarter a date falls in, as the memo spells it: <c>2025Q4</c>.</summary>
    public static string QuarterOf(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.Year:0000}Q{((date.Month - 1) / 3) + 1}");

    /// <summary>Every quarter from <paramref name="from"/>'s to <paramref name="to"/>'s, inclusive, ascending.</summary>
    public static IReadOnlyList<string> QuartersIn(DateOnly from, DateOnly to)
    {
        var quarters = new List<string>();
        var year = from.Year;
        var quarter = ((from.Month - 1) / 3) + 1;
        var lastYear = to.Year;
        var lastQuarter = ((to.Month - 1) / 3) + 1;

        while (year < lastYear || (year == lastYear && quarter <= lastQuarter))
        {
            quarters.Add(string.Create(CultureInfo.InvariantCulture, $"{year:0000}Q{quarter}"));
            if (quarter == 4)
            {
                quarter = 1;
                year++;
            }
            else
            {
                quarter++;
            }
        }

        return quarters;
    }

    /// <summary>Measures coverage over the registered window and resolves the deliverable subset.</summary>
    public static CoverageReport Measure(IReadOnlyList<CoverageInput> events, DateOnly windowFrom, DateOnly windowTo)
    {
        var quarters = QuartersIn(windowFrom, windowTo);
        var index = quarters
            .Select((q, i) => (q, i))
            .ToDictionary(pair => pair.q, pair => pair.i, StringComparer.Ordinal);

        var counts = new int[quarters.Count];
        var missingChain = new int[quarters.Count];
        var missingCloses = new int[quarters.Count];
        var chainErrors = new int[quarters.Count];
        var closesNotOk = new int[quarters.Count];
        var noPrintDate = 0;
        var outside = 0;

        foreach (var e in events)
        {
            if (e.PrintDate is not { } printDate)
            {
                noPrintDate++;
                continue;
            }

            if (!index.TryGetValue(QuarterOf(printDate), out var i))
            {
                outside++;
                continue;
            }

            counts[i]++;
            if (!e.HasChainRow) missingChain[i]++;
            if (!e.HasClosesRow) missingCloses[i]++;
            if (string.Equals(e.ChainStatus, ChainFetchError, StringComparison.Ordinal)) chainErrors[i]++;
            if (e.HasClosesRow && !string.Equals(e.ClosesStatus, OkStatus, StringComparison.Ordinal)) closesNotOk[i]++;
        }

        var rows = new List<QuarterCoverageRow>(quarters.Count);
        for (var i = 0; i < quarters.Count; i++)
        {
            rows.Add(new QuarterCoverageRow(
                quarters[i], counts[i], missingChain[i], missingCloses[i], chainErrors[i], closesNotOk[i],
                missingChain[i] == 0 && missingCloses[i] == 0));
        }

        // The run ending at the LAST quarter of the window, walked backwards from it. Stopping at the
        // first uncovered quarter is what makes the subset contiguous and recent rather than a
        // cherry-picked set of quarters that happened to finish.
        var subset = new List<string>();
        for (var i = rows.Count - 1; i >= 0 && rows[i].Covered; i--) subset.Add(rows[i].Quarter);
        subset.Reverse();

        return new CoverageReport(
            rows,
            subset,
            subset.Count == rows.Count && rows.Count > 0,
            noPrintDate,
            outside,
            StatusTally(events.Select(e => e.ChainStatus)),
            StatusTally(events.Select(e => e.ClosesStatus)));
    }

    /// <summary>The <c>option_measures.fetch_status</c> of a fetch that failed transiently — the re-runnable class.</summary>
    public const string ChainFetchError = "error";

    private const string OkStatus = "ok";

    /// <summary>The absent row is a tally key of its own, so "no row" can never hide inside "no status".</summary>
    public const string NoRow = "(no row)";

    /// <summary>A row that exists but records no status. Distinct from <see cref="NoRow"/>: one is a missing fetch, the other a fetch that said nothing.</summary>
    public const string BlankStatus = "(blank)";

    private static IReadOnlyList<Tally> StatusTally(IEnumerable<string?> statuses) =>
    [
        .. statuses
            .GroupBy(s => s is null ? NoRow : s.Length == 0 ? BlankStatus : s, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Tally(g.Key, g.Count()))
    ];
}
