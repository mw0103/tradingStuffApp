namespace TradingStuff.ResearchContracts;

/// <summary>
/// One runtime-verified IBKR capability fact (a row of <c>research.capability_probes</c>).
/// </summary>
/// <remarks>
/// Capability facts are data, not lore: what TWS actually serves changes with upgrades and
/// entitlements, and every research design decision that leans on a capability should be able to
/// point at the probe that verified it. See docs/research/ibkr-data-capability-matrix.md.
/// </remarks>
public sealed record CapabilityProbeRecord(
    long ProbeId,
    string ProbeKey,
    int? ConId,
    DateTimeOffset RanAt,
    int? TwsServerVersion,
    int? MarketDataType,
    bool Succeeded,
    string ResultJson,
    int? ErrorCode,
    string? Notes);
