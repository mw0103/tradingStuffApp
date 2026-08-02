namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// Translates this platform's canonical (underlying, trading class) pair into ThetaData's own
/// "symbol" query parameter.
/// </summary>
/// <remarks>
/// This is the adapter boundary docs/DECISIONS.md §15 requires: ThetaData does not distinguish
/// underlying from trading class the way <c>research.option_chain_jobs</c> and
/// <c>research.option_chain_quotes</c> do — it addresses SPX AM-settled monthlies and SPXW
/// PM-settled weeklies/dailies as two entirely separate "symbol" roots, with no separate underlying
/// field at all. That vendor quirk is resolved here, once, and nowhere else: the canonical schema
/// never stores a ThetaData symbol string as an identifying column, only as descriptive provenance
/// (<c>option_chain_quotes.vendor_symbol</c>).
/// </remarks>
public static class ThetaSymbolMap
{
    /// <summary>
    /// The vendor symbol to request for a canonical (underlying, trading class) pair.
    /// </summary>
    /// <remarks>
    /// For every root this platform currently ingests (SPX/SPXW, VIX), ThetaData's own symbol IS the
    /// trading class verbatim. Kept as an explicit mapping function — not a bare pass-through call
    /// site — so a future root whose vendor symbol genuinely differs from its trading class (a stock
    /// class option, say, where ThetaData might use the root ticker rather than an OCC-style class
    /// code) has one place to add that without touching every caller.
    /// </remarks>
    public static string VendorSymbolFor(string underlying, string tradingClass)
    {
        if (string.IsNullOrWhiteSpace(underlying))
        {
            throw new ArgumentException("A ThetaData request needs an underlying.", nameof(underlying));
        }

        if (string.IsNullOrWhiteSpace(tradingClass))
        {
            throw new ArgumentException("A ThetaData request needs a trading class.", nameof(tradingClass));
        }

        return tradingClass.ToUpperInvariant();
    }
}
