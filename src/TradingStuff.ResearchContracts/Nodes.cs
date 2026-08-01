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

/// <summary>
/// One registered node's current state: what it was selected for, what it is bound to, and how far
/// apart those are.
/// </summary>
/// <remarks>
/// The reporting shape exists because of a specific failure. <c>/research/nodes</c> reported only
/// <c>(node_id, con_id)</c>, and the selector bound every out-of-window node to the edge strike of
/// the chain window it was handed — so nine roles per DTE bucket collapsed onto four contracts, and
/// nothing in the system compared an assigned strike against the target it was chosen for. Every
/// role pointed at a live contract, coverage read ~100% across all 54 nodes, and the only way to see
/// the problem was to diff 54 conIds by hand. So: the target, the assignment, and the deviation
/// between them are all reported, and an unassigned node reports WHY.
/// </remarks>
/// <param name="MoneynessTarget">The seeded offset from spot this role is defined by, e.g. <c>-0.110</c>.</param>
/// <param name="TargetStrike"><c>ReferencePrice × (1 + MoneynessTarget)</c> at selection time.</param>
/// <param name="StrikeDeviation">
/// <c>(Strike − TargetStrike) / ReferencePrice</c>, signed. The number that was missing: a node whose
/// deviation is a large fraction of spot is not the node its role says it is.
/// </param>
/// <param name="ExpirationInBucket">
/// Whether the assigned expiration actually falls in the node's DTE window. False is not a refusal —
/// TWS lists what it lists, and the SPX monthly series can genuinely have no expiration inside a
/// bucket on a given day — but it does mean the role label overstates what is being recorded.
/// </param>
/// <param name="DuplicateConId">
/// Whether another currently-assigned node points at this same conId. Should never be true; if it is,
/// one of the two roles is recording a contract selected for the other.
/// </param>
/// <param name="Unassigned">
/// Refusal code from the most recent selection pass (see <see cref="NodeUnassignedReasons"/>), or
/// null when the node is assigned. Present only for passes this process ran.
/// </param>
public sealed record NodeGridEntry(
    short NodeId,
    string Role,
    string TradingClass,
    OptionRight Right,
    int MinDte,
    int MaxDte,
    decimal MoneynessTarget,
    int? ConId,
    DateOnly? Expiration,
    decimal? Strike,
    decimal? TargetStrike,
    decimal? ReferencePrice,
    decimal? StrikeDeviation,
    DateTimeOffset? AssignedFrom,
    string? Reason,
    short? SelectorVersion,
    bool ExpirationInBucket,
    bool DuplicateConId,
    string? Unassigned,
    string? UnassignedDetail);

/// <summary>
/// The registered grid and how much of it is actually bound — every registered node, assigned or not.
/// </summary>
/// <param name="DistinctConIds">
/// Distinct conIds across assigned nodes. This must equal <paramref name="Assigned"/>; anything less
/// means roles are sharing contracts, which is the failure mode this report was built to expose.
/// </param>
public sealed record NodeGridReport(
    int Registered,
    int Assigned,
    int Unassigned,
    int DistinctConIds,
    IReadOnlyList<NodeGridEntry> Nodes);

/// <summary>
/// Why a registered node has no current assignment. A node is left unassigned rather than bound to
/// an approximation — the whole point of the codes below is that "close enough" is not a state this
/// selector can be in.
/// </summary>
public static class NodeUnassignedReasons
{
    /// <summary>No usable chain window came back for the node's bucket at all.</summary>
    public const string ChainUnavailable = "chain-unavailable";

    /// <summary>The window held no contract of the node's right.</summary>
    public const string NoCandidates = "no-candidates";

    /// <summary>
    /// The node's target strike lies outside the strikes the window actually contains, so the
    /// "nearest" strike would be the window's edge rather than a match. The structural check: an
    /// edge clamp cannot pass it, whatever the window width or the strike increment.
    /// </summary>
    public const string TargetOutsideWindow = "target-outside-window";

    /// <summary>The nearest listed strike is further from the target than the tolerance allows.</summary>
    public const string StrikeDeviation = "strike-deviation";

    /// <summary>The broker did not resolve the selected contract to a conId.</summary>
    public const string UnresolvedContract = "unresolved-contract";

    /// <summary>Another node role already holds the conId this node selected.</summary>
    public const string DuplicateConId = "duplicate-con-id";
}

/// <summary>Reasons a <see cref="NodeAssignment"/> changes. Stored as free text; these are the known values.</summary>
public static class NodeAssignmentReasons
{
    public const string SessionOpen = "session_open";
    public const string StrikeDrift = "strike_drift";
    public const string ExpiryRoll = "expiry_roll";
    public const string Reconnect = "reconnect";
    public const string Bootstrap = "bootstrap";
}
