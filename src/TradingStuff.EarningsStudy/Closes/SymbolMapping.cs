namespace TradingStuff.EarningsStudy.Closes;

/// <summary>
/// Translates a universe row's CBOE symbol and SEC exchange field into the spelling IBKR's contract
/// resolution expects. Kept separate from <see cref="BarsFetcher"/> so the mapping rules — the part
/// most likely to need a new case as the universe grows — are one small, independently testable
/// surface.
/// </summary>
internal static class SymbolMapping
{
    /// <summary>
    /// SEC exchange field (as recorded on <c>UniverseRow.Exchange</c>) to IBKR <c>primaryExchange</c>.
    /// Only the three exchanges the universe rule admits (<c>Gates.PrimaryListing</c>,
    /// <c>C1Registration.AdmittedExchanges</c>) are recognised — every <c>Eligible</c> row should
    /// carry one of these three spellings, never anything else.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExchangeToPrimary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NYSE"] = "NYSE",
            ["Nasdaq"] = "NASDAQ",
            ["NYSE American"] = "AMEX",
        };

    /// <summary>
    /// CBOE spells a share class with a dot ("BRK.B"); IBKR spells the same instrument with a space
    /// ("BRK B"). Every other character passes through unchanged.
    /// </summary>
    public static string ToIbkrSymbol(string symbol) => symbol.Replace('.', ' ');

    /// <summary>
    /// Maps the SEC exchange field to IBKR's <c>primaryExchange</c>. An <c>Eligible</c> universe row
    /// should always carry one of the three admitted spellings (that is what gate 03 enforces) — an
    /// unrecognised value here is a data bug upstream of this step, not a case this step is meant to
    /// interpret. Rather than silently dropping the request or crashing the whole batch over one bad
    /// row, it is logged through <paramref name="onUnrecognised"/> and passed through unmapped, so
    /// the request still reaches IBKR (SMART routing may still resolve it) and the anomaly is visible
    /// in the run's log instead of vanishing.
    /// </summary>
    public static string? ToPrimaryExchange(string? secExchange, Action<string>? onUnrecognised = null)
    {
        if (secExchange is null)
        {
            return null;
        }

        if (ExchangeToPrimary.TryGetValue(secExchange, out var mapped))
        {
            return mapped;
        }

        onUnrecognised?.Invoke(secExchange);
        return secExchange;
    }
}
