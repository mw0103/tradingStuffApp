using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>Outcome of the two-signal price QA for one event.</summary>
public sealed record TimingQaResult(
    bool Quarantined,
    string? Reason,
    decimal? PreEntryMove,
    decimal? EventMove);

/// <summary>
/// WP2. The second signal of the timing QA: given the official closes around the event, decides
/// whether the entry snapshot can be trusted as pre-print. The hazard it exists for is an 8-K
/// accepted a session or more after the release, which places the entry close after the move.
/// The rule is mechanical and is documented in the memo verbatim.
/// </summary>
public static class TimingQa
{
    public static TimingQaResult Evaluate(EventTimingRow timing, ClosesRow closes) =>
        throw new NotImplementedException("WP2: TimingQa.Evaluate");
}
