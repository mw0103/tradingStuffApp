using TradingStuff.Contracts;

namespace TradingStuff.ResearchContracts;

/// <summary>How a node's target strike is currently being selected.</summary>
public enum StrikeSelectionKind
{
    /// <summary>
    /// Bootstrap mode: strike chosen by an offset from spot (no delta known yet — nothing has
    /// streamed for the candidate contract).
    /// </summary>
    Moneyness = 0,

    /// <summary>Refined mode: strike chosen to track a target |delta| once model Greeks arrive.</summary>
    Delta = 1,
}

/// <summary>A DTE window a set of nodes is drawn from, and the option series to draw them from.</summary>
public sealed record ExpiryBucket(string Label, int MinDte, int MaxDte, string TradingClass);

/// <summary>How to pick a node's strike, and against what target.</summary>
public sealed record StrikeRule(StrikeSelectionKind Kind, decimal Target);

/// <summary>
/// A permanent, role-based node identity (e.g. "30DTE-25D-P"). The registered grid is seeded once;
/// which conId currently plays a role is tracked separately in <see cref="NodeAssignment"/> so the
/// role survives expiry rolls and strike drift.
/// </summary>
public sealed record NodeSpec(
    short NodeId,
    string Surface,
    string Role,
    ExpiryBucket Bucket,
    OptionRight Right,
    StrikeRule Strike);

/// <summary>
/// Which conId played a node's role during <paramref name="AssignedFrom"/>..<paramref name="AssignedTo"/>
/// (null <c>AssignedTo</c> means current). Node continuity is explicit data, never an implicit
/// splice across a strike swap or expiry roll.
/// </summary>
public sealed record NodeAssignment(
    long AssignmentId,
    short NodeId,
    int ConId,
    DateTimeOffset AssignedFrom,
    DateTimeOffset? AssignedTo,
    string Reason,
    short SelectorVersion);

/// <summary>Reasons a <see cref="NodeAssignment"/> changes. Stored as free text; these are the known values.</summary>
public static class NodeAssignmentReasons
{
    public const string SessionOpen = "session_open";
    public const string StrikeDrift = "strike_drift";
    public const string ExpiryRoll = "expiry_roll";
    public const string Reconnect = "reconnect";
    public const string Bootstrap = "bootstrap";
}
