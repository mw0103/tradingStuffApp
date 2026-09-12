namespace TradingStuff.EarningsStudy.Options;

/// <summary>One option quote at a snapshot. Prices in dollars, parsed to decimal straight from the feed's text.</summary>
public sealed record ChainQuote(decimal Strike, bool IsCall, decimal Bid, decimal Ask)
{
    public bool HasTwoSidedMarket => Bid > 0 && Ask > 0 && Ask >= Bid;
    public decimal Mid => (Bid + Ask) / 2m;
}

/// <summary>The spot implied by put-call parity at the strike where the call and put mids are closest.</summary>
public sealed record ParitySpot(decimal Spot, decimal Strike, decimal CallMid, decimal PutMid, int PairsConsidered);

/// <summary>The ATM call/put pair selected for one snapshot.</summary>
public sealed record AtmPair(decimal Strike, ChainQuote Call, ChainQuote Put)
{
    public decimal StraddleMid => Call.Mid + Put.Mid;
    public decimal CombinedSpread => (Call.Ask - Call.Bid) + (Put.Ask - Put.Bid);
}

/// <summary>
/// WP3a. The pure selection semantics that define the primary sample: which expiration spans the
/// event, what the spot is, which strike is ATM, and which tier a pair falls in. No I/O. Every
/// rule here is quoted from the pre-registration and pinned by tests that include the degenerate
/// shapes (ties, one-sided quotes, same-day expiration, wide and asymmetric markets).
/// </summary>
public static class ChainSelection
{
    /// <summary>The front expiration spanning the event: the earliest listed expiration on or after <paramref name="exitDate"/>. Null if none is listed.</summary>
    public static DateOnly? FrontExpiration(IEnumerable<DateOnly> listedExpirations, DateOnly exitDate) =>
        throw new NotImplementedException("WP3a: ChainSelection.FrontExpiration");

    /// <summary>
    /// Parity-implied spot: at the strike minimising |call mid − put mid| over strikes with both
    /// sides two-sided, spot = K + C − P. Carry is ignored: at the DTEs this study sees the term is
    /// far below quote noise. Null when no strike has both sides quoted.
    /// </summary>
    public static ParitySpot? ImpliedSpot(IReadOnlyList<ChainQuote> chain) =>
        throw new NotImplementedException("WP3a: ChainSelection.ImpliedSpot");

    /// <summary>
    /// The ATM pair: the strike nearest <paramref name="spot"/> at which both a call and a put quote
    /// exist (a quote row, not necessarily a positive bid). Ties resolve to the lower strike.
    /// </summary>
    public static AtmPair? SelectAtm(IReadOnlyList<ChainQuote> chain, decimal spot) =>
        throw new NotImplementedException("WP3a: ChainSelection.SelectAtm");

    /// <summary>Quotable: ATM call AND put bid &gt; 0 at the entry snapshot.</summary>
    public static bool IsQuotable(AtmPair pair) =>
        throw new NotImplementedException("WP3a: ChainSelection.IsQuotable");

    /// <summary>Tradable tier: combined ATM spread &lt;= 15% of the straddle mid. Requires quotable.</summary>
    public static bool IsTradable(AtmPair pair) =>
        throw new NotImplementedException("WP3a: ChainSelection.IsTradable");
}
