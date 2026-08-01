namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// The volatility-forecast-residual study's registered, untouched holdout window
/// (<c>docs/research/volatility-forecast-residual-study.md</c>, "Validation design"): 2024-01-01
/// through 2026-07-31, opened exactly once, after the full pipeline configuration is frozen, in a
/// single scripted run whose result stands.
/// </summary>
/// <remarks>
/// A development run is never that scripted run. Every code path that can reach the holdout dates —
/// the bar loader, the dataset builder, the walk-forward split — must be unreachable by construction
/// from this endpoint, not merely unlikely by convention. <see cref="ClampToExcludeHoldout"/> is the
/// single place that decision is made; nothing downstream re-derives it.
/// </remarks>
public static class ReservedHoldout
{
    public static readonly DateOnly Start = new(2024, 1, 1);
    public static readonly DateOnly End = new(2026, 7, 31);

    /// <summary>The day before the holdout opens — the latest date any development run may use.</summary>
    public static readonly DateOnly LastUsableDate = Start.AddDays(-1);

    public static bool Overlaps(DateOnly from, DateOnly to) => from <= End && to >= Start;

    /// <summary>
    /// Clamps a caller-requested <c>[from, to]</c> window so it can never touch the reserved
    /// holdout, regardless of what was asked for.
    /// </summary>
    /// <param name="requestedFrom">Caller's lower bound, or null for "earliest available".</param>
    /// <param name="requestedTo">Caller's upper bound, or null for "latest available, pre-holdout".</param>
    /// <param name="earliestAvailable">
    /// A floor for the effective "from" when the caller did not supply one — the earliest date data
    /// could plausibly exist, so an unbounded request does not translate into a query spanning
    /// decades of empty partitions.
    /// </param>
    public static ClampedWindow ClampToExcludeHoldout(
        DateOnly? requestedFrom, DateOnly? requestedTo, DateOnly earliestAvailable)
    {
        var from = requestedFrom ?? earliestAvailable;
        var to = requestedTo ?? LastUsableDate;

        // Clamp the top of the window first: nothing at or after Start may ever be used, no matter
        // what the caller asked for.
        var clampedTo = to > LastUsableDate ? LastUsableDate : to;
        var clampedFrom = from < earliestAvailable ? earliestAvailable : from;

        var wasClamped = clampedTo != to || clampedFrom != from;

        if (clampedFrom > clampedTo)
        {
            // The entire requested range sat inside (or after) the holdout: there is no usable
            // window left to run, at all. This is reported by the caller as insufficient-data, not
            // fabricated into an empty-but-"ok" result.
            return new ClampedWindow(clampedFrom, clampedTo, wasClamped, IsEmpty: true);
        }

        return new ClampedWindow(clampedFrom, clampedTo, wasClamped, IsEmpty: false);
    }
}

public readonly record struct ClampedWindow(DateOnly From, DateOnly To, bool WasClamped, bool IsEmpty);
