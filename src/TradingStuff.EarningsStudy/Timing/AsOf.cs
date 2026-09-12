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
    public static SharesFactRow? SharesOutstanding(IEnumerable<SharesFactRow> facts, DateOnly entryDate) =>
        throw new NotImplementedException("WP2: AsOf.SharesOutstanding");
}
