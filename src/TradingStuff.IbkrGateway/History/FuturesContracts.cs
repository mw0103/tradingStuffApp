using System.Globalization;
using IBApi;

namespace TradingStuff.IbkrGateway.History;

/// <summary>
/// One contract IBKR lists for a futures family — expired or current — as returned by
/// <c>reqContractDetails</c> with <c>IncludeExpired</c> set.
/// </summary>
/// <remarks>
/// This is how a deep ES intraday backfill discovers what to walk. A live <c>CONTFUT</c> rejects a
/// past <c>endDateTime</c> with error 10339 (RUNTIME-verified, see
/// docs/research/ibkr-data-capability-matrix.md constraint 3) — it cannot page backward into history
/// at all. Deep history is therefore only reachable by requesting each individual quarterly
/// contract directly, with <c>Contract.IncludeExpired = true</c>, each within its own listing
/// window — which first requires knowing what those contracts and their conIds are.
/// </remarks>
public sealed record FuturesContractDefinition(
    int ConId,
    DateOnly LastTradeDateOrContractMonth,
    string? TradingClass,
    string Exchange,
    string Currency);

/// <summary>
/// Resolves a futures contract's last trading day from <see cref="ContractDetails"/>, tolerating the
/// two wire shapes IBKR uses.
/// </summary>
internal static class FuturesContractExpiry
{
    private const string DayFormat = "yyyyMMdd";
    private const string MonthFormat = "yyyyMM";

    /// <summary>
    /// Prefers <c>RealExpirationDate</c> — documented by IBKR as the exact last trading day,
    /// available since TWS 968 / API 973.04 (comfortably true for this project's vendored
    /// 10.45.01) — over <c>Contract.LastTradeDateOrContractMonth</c>, which IBKR documents can in
    /// principle be contract-month-only (<c>yyyyMM</c>) rather than an exact day. A month-only value
    /// is treated as the last calendar day of that month so a contract's own window is never
    /// clamped narrower than it may actually have traded. Returns null when neither field parses,
    /// so the caller can skip the contract loudly rather than silently mis-dating it.
    /// </summary>
    public static DateOnly? Resolve(ContractDetails details)
    {
        if (TryParseDay(details.RealExpirationDate, out var real))
        {
            return real;
        }

        var raw = details.Contract.LastTradeDateOrContractMonth;

        if (TryParseDay(raw, out var exact))
        {
            return exact;
        }

        if (!string.IsNullOrWhiteSpace(raw) &&
            DateOnly.TryParseExact(raw.Trim(), MonthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
        }

        return null;
    }

    private static bool TryParseDay(string? raw, out DateOnly value)
    {
        value = default;

        return !string.IsNullOrWhiteSpace(raw) &&
               DateOnly.TryParseExact(raw.Trim(), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
