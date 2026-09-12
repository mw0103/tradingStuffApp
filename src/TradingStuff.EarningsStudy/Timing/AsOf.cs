using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>
/// WP2. As-of selection: what the market could have known at the entry close. The only place a
/// point-in-time pick is made, so the leakage review has one function to read.
/// </summary>
public static class AsOf
{
    /// <summary>
    /// The shares-outstanding fact in force at the <paramref name="entryDate"/> close: the latest by
    /// filing date among facts filed <b>strictly before</b> that date. Null when nothing had been
    /// filed yet.
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
    /// <see cref="SharesFactRow.Filed"/> is <b>strictly before</b> <paramref name="entryDate"/>.
    /// Filed is the date the number became public; <see cref="SharesFactRow.PeriodEnd"/> is the date
    /// it describes and is always earlier, often by months. Selecting on PeriodEnd — the obvious
    /// reading of "the count as of the entry date" — would routinely pick a fact that had not been
    /// filed yet, which is look-ahead straight into the market-cap split, and it would be invisible:
    /// the split would simply be a little sharper than it should be, in a memo whose other numbers
    /// are all correct.
    /// </para>
    /// <para>
    /// <b>Why strictly before, and not "on or before".</b> The entry measurement is the 16:00 ET
    /// close, and <c>filed</c> is a date with no time on it, so a fact stamped with the entry date
    /// cannot be shown to have been public at that close. It usually was not: EDGAR accepts
    /// submissions until 22:00 ET and stamps the <i>same</i> filing date until 17:30, so a 10-Q
    /// accepted at 16:30 carries the day it was accepted while having been unknowable at that day's
    /// close. That case is not exotic — for a same-day 10-Q filer the entry date <i>is</i> the print
    /// date, which is exactly when the filing lands. Admitting it was measured to move one pick from
    /// 100M to 250M shares, a market cap of 10B to 25B, which crosses quintile boundaries in the
    /// registered split. Excluding a fact that happened to be filed in the morning costs one stale
    /// quarter on a number that moves by single-digit percents a quarter; admitting one filed in the
    /// afternoon is look-ahead. The date column cannot tell the two apart, so the only defensible
    /// reading of a point-in-time function is the one that cannot leak.
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
            // Look-ahead: never. Strict, because a fact stamped with the entry date may have been
            // accepted by EDGAR as late as 22:00 ET — after the close the entry is measured at.
            // Everything else in this function is tie-breaking.
            if (fact.Filed >= entryDate)
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
