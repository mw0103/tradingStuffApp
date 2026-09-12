namespace TradingStuff.EarningsStudy.Closes;

/// <summary>
/// One daily TRADES bar for a symbol, as written to and read from <c>raw/bars/{symbol}.csv</c>.
/// </summary>
/// <remarks>
/// IBKR TRADES daily bars are split-adjusted. That never matters for the pre-entry/entry/exit closes
/// this step joins — every comparison inside <see cref="ClosesJoiner"/> is within one event's few-day
/// window, well short of any split — but it would matter for a multi-year head-to-tail comparison, so
/// it is worth saying once here rather than assuming the next reader of this file knows it.
/// </remarks>
internal sealed record BarRow(
    DateOnly TradingDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
