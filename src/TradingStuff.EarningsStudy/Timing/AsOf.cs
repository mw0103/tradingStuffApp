using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>
/// WP2. As-of selection: what the market could have known at the entry close. The only place a
/// point-in-time pick is made, so the leakage review has one function to read.
/// </summary>
public static class AsOf
{
    /// <summary>
    /// The shares-outstanding fact in force at <paramref name="entryDate"/>: the latest by filing
    /// date among facts filed on or before that date. Null when nothing had been filed yet.
    /// </summary>
    /// <param name="facts">
    /// Every <c>dei:EntityCommonStockSharesOutstanding</c> fact for <b>one</b> CIK. This function
    /// does not filter by CIK — <see cref="SharesFactRow.Cik"/> is not read — because a caller that
    /// passes two companies' facts has a join defect that filtering would hide rather than fix.
    /// </param>
    /// <param name="entryDate">The entry trading date, from <see cref="EventTimingRow.EntryDate"/>.</param>
    /// <remarks>
    /// <para>
    /// <b>The leakage rule, and it is the whole function.</b> A fact is admissible only if
    /// <see cref="SharesFactRow.Filed"/> is on or before <paramref name="entryDate"/>. Filed is the
    /// date the number became public; <see cref="SharesFactRow.PeriodEnd"/> is the date it describes
    /// and is always earlier, often by months. Selecting on PeriodEnd — the obvious reading of "the
    /// count as of the entry date" — would routinely pick a fact that had not been filed yet, which
    /// is look-ahead straight into the market-cap split, and it would be invisible: the split would
    /// simply be a little sharper than it should be, in a memo whose other numbers are all correct.
    /// Filed on the entry date itself is admissible, because the entry measurement is the close and
    /// an EDGAR filing on that date precedes it.
    /// </para>
    /// <para>
    /// <b>Ties.</b> Two facts filed the same day are broken by the later
    /// <see cref="SharesFactRow.PeriodEnd"/> (the more recent count), then by the larger
    /// <see cref="SharesFactRow.Value"/>, then by the greater
    /// <see cref="SharesFactRow.AccessionNumber"/> ordinally. The last two are arbitrary but fixed:
    /// what matters is that the pick does not depend on the order rows happen to arrive in, so the
    /// memo reproduces exactly. Only exact duplicates of (Filed, PeriodEnd) can reach them.
    /// </para>
    /// </remarks>
    public static SharesFactRow? SharesOutstanding(IEnumerable<SharesFactRow> facts, DateOnly entryDate)
    {
        ArgumentNullException.ThrowIfNull(facts);

        SharesFactRow? best = null;

        foreach (var fact in facts)
        {
            // Look-ahead: never. Everything else in this function is tie-breaking.
            if (fact.Filed > entryDate)
            {
                continue;
            }

            if (best is null || IsPreferredTo(fact, best))
            {
                best = fact;
            }
        }

        return best;
    }

    private static bool IsPreferredTo(SharesFactRow candidate, SharesFactRow incumbent)
    {
        if (candidate.Filed != incumbent.Filed)
        {
            return candidate.Filed > incumbent.Filed;
        }

        if (candidate.PeriodEnd != incumbent.PeriodEnd)
        {
            return candidate.PeriodEnd > incumbent.PeriodEnd;
        }

        if (candidate.Value != incumbent.Value)
        {
            return candidate.Value > incumbent.Value;
        }

        return string.CompareOrdinal(candidate.AccessionNumber, incumbent.AccessionNumber) > 0;
    }
}
