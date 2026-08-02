using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// The rule from docs/FOLLOWUP.md §4.5: ingestion defaults to '1m', and selecting 'tick' requires an
/// explicit, separate confirmation rather than merely naming the interval. Factored out of
/// <see cref="OptionChainEndpoints"/> so the rule itself — not just the minimal-API lambda that
/// applies it — is directly unit-testable.
/// </summary>
public static class OptionChainIntervalValidation
{
    public readonly record struct Result(bool IsValid, string? ResolvedInterval, string? ErrorTitle, string? ErrorDetail)
    {
        public static Result Ok(string interval) => new(true, interval, null, null);
        public static Result Invalid(string title, string detail) => new(false, null, title, detail);
    }

    /// <summary>
    /// Resolves and validates a caller-supplied interval string.
    /// </summary>
    /// <param name="requestedInterval">Null/blank means "use the default" — resolves to '1m'.</param>
    /// <param name="confirmTick">
    /// Must be exactly <c>true</c> for a request naming 'tick' to succeed. Any other value —
    /// including omitted — is rejected: an implicit or accidental tick selection must not be
    /// possible.
    /// </param>
    public static Result Validate(string? requestedInterval, bool? confirmTick)
    {
        var resolved = string.IsNullOrWhiteSpace(requestedInterval)
            ? OptionChainIntervals.OneMinute
            : requestedInterval.Trim().ToLowerInvariant();

        if (resolved is not (OptionChainIntervals.OneMinute or OptionChainIntervals.Tick))
        {
            return Result.Invalid("Unrecognized interval.", $"'{requestedInterval}' is not '1m' or 'tick'.");
        }

        if (resolved == OptionChainIntervals.Tick && confirmTick != true)
        {
            return Result.Invalid(
                "Tick ingestion requires explicit confirmation.",
                "Bulk tick ingestion is study-scoped, not an automatic default (see docs/FOLLOWUP.md §4.5). " +
                "Pass confirmTick=true to acknowledge this, but note that even a confirmed tick job is created " +
                "paused and is never planned or claimed by the automatic coordinator — building an automatic " +
                "tick drain was explicitly ruled out of scope.");
        }

        return Result.Ok(resolved);
    }
}
