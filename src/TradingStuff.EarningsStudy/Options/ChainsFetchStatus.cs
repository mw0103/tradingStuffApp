namespace TradingStuff.EarningsStudy.Options;

/// <summary>
/// The fixed status vocabulary WP3b writes into <c>option_measures.fetch_status</c>. Every event
/// that enters the <c>chains</c> verb gets exactly one row and exactly one of these values — a
/// refusal is a recorded reason, never a missing row (docs/LESSONS.md 3).
/// </summary>
/// <remarks>
/// Four members are literally <see cref="ChainSelectionReasons"/>'s values rather than restated
/// literals: that module's refusal reasons were designed to BE this verb's fetch statuses, and a
/// second hand-typed copy of the same string is exactly the kind of duplication that drifts apart
/// silently the day one of them is edited.
/// </remarks>
public static class ChainsFetchStatus
{
    /// <summary>A full measurement was produced (whether or not the scale check was measurable).</summary>
    public const string Ok = "ok";

    /// <summary>The timing row is missing one of pre-entry/entry/exit; no fetch was attempted.</summary>
    public const string DatesUnresolved = "dates_unresolved";

    /// <summary>Neither the root as listed nor its dot-stripped variant listed any expirations.</summary>
    public const string NoChain = "no_chain";

    /// <summary>Expirations were listed, but none falls on or after the exit date.</summary>
    public const string NoSpanningExpiry = "no_spanning_expiry";

    /// <summary>The chain fetch succeeded but returned zero rows for the entry date specifically.</summary>
    public const string NoEntryQuotes = "no_entry_quotes";

    /// <inheritdoc cref="ChainSelectionReasons.ChainEmpty"/>
    public const string ChainEmpty = ChainSelectionReasons.ChainEmpty;

    /// <inheritdoc cref="ChainSelectionReasons.NoTwoSidedStrike"/>
    public const string NoTwoSidedStrike = ChainSelectionReasons.NoTwoSidedStrike;

    /// <inheritdoc cref="ChainSelectionReasons.NoPairedStrike"/>
    public const string NoPairedStrike = ChainSelectionReasons.NoPairedStrike;

    /// <inheritdoc cref="ChainSelectionReasons.SpotOutsideStrikeLadder"/>
    public const string SpotOutsideStrikeLadder = ChainSelectionReasons.SpotOutsideStrikeLadder;

    /// <summary>The entry straddle fails <see cref="ChainSelection.LooksLikeDollarScale"/>. Quotes are still recorded.</summary>
    public const string ScaleImplausible = "scale_implausible";

    /// <summary>A Terminal or parse failure for this one event specifically; the message is in <c>Note</c>.</summary>
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All =
    [
        Ok, DatesUnresolved, NoChain, NoSpanningExpiry, NoEntryQuotes, ChainEmpty,
        NoTwoSidedStrike, NoPairedStrike, SpotOutsideStrikeLadder, ScaleImplausible, Error,
    ];
}
