using TradingStuff.ResearchService.Gateway;
using TradingStuff.Volatility;

namespace TradingStuff.ResearchService.Volatility;

/// <summary>
/// Converts gateway historical bars into the estimator's bar type.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>decimal</c>-to-<c>double</c> boundary. The platform holds prices as
/// <c>decimal</c> because money must not carry binary rounding; the realized-variance
/// estimators work in <c>double</c> because they take logarithms, square roots and sums of
/// squares, none of which <c>decimal</c> supports and all of which it would make far slower
/// for no accuracy that survives the log. Converting here, once, keeps the estimator core free
/// of persistence types and keeps every other component in <c>decimal</c>.
/// </para>
/// <para>
/// Timestamps stay UTC, per the UTC-canonical doctrine on <c>ISessionClock</c>. This adapter
/// performs no timezone conversion and must not be given one: session membership and trading
/// dates are the session clock's job, and a second converter here is exactly the drift that
/// doctrine exists to prevent.
/// </para>
/// </remarks>
public static class HistoricalBarAdapter
{
    /// <summary>
    /// Converts intraday bars, preserving input order. Order is not corrected here — the
    /// estimator's own cleaning step sorts and de-duplicates, and doing it twice would hide
    /// whether the feed is actually returning what it claims.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A bar carries no <see cref="HistoricalBarDto.Timestamp"/>. Daily and coarser bars set
    /// <see cref="HistoricalBarDto.TradingDate"/> instead, and silently reading one as midnight
    /// would place a whole session's variance on a single instant.
    /// </exception>
    public static IEnumerable<IntradayBar> ToIntradayBars(IEnumerable<HistoricalBarDto> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        foreach (var bar in bars)
        {
            if (bar is null) continue;

            if (bar.Timestamp is not { } timestamp)
            {
                throw new ArgumentException(
                    $"Bar for trading date {bar.TradingDate} has no intraday timestamp. Daily bars " +
                    "cannot be used for realized variance; request an intraday bar size.",
                    nameof(bars));
            }

            yield return new IntradayBar(
                timestamp.UtcDateTime,
                (double)bar.Open,
                (double)bar.High,
                (double)bar.Low,
                (double)bar.Close,
                ToVolume(bar.Volume));
        }
    }

    /// <summary>
    /// TWS reports volume as a decimal (fractional share quantities exist) while the estimator
    /// carries a long. Volume is diagnostic here — no estimator reads it — so truncation is
    /// acceptable, but a value outside <see cref="long"/> is a parse fault rather than a large
    /// volume and must not wrap silently.
    /// </summary>
    private static long ToVolume(decimal volume)
    {
        if (volume <= 0m) return 0L;
        return volume >= long.MaxValue ? long.MaxValue : (long)volume;
    }
}
