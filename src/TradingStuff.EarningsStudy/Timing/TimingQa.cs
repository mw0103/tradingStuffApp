using System.Globalization;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>Outcome of the two-signal price QA for one event.</summary>
/// <param name="Quarantined">Whether the event is removed at gate 09. The only field that gates anything.</param>
/// <param name="Reason">Why, when quarantined; why it could not be evaluated, when it could not be; a note or null otherwise.</param>
/// <param name="PreEntryMove">|close(entry)/close(pre-entry) − 1|, or null when it could not be computed. With the trailing scale, the whole of the gate's input.</param>
/// <param name="EventMove">
/// |close(exit)/close(entry) − 1|, or null when it could not be computed. This is RF. Reported so the
/// memo can show both legs of a quarantine; <b>never</b> read by <see cref="TimingQa.Evaluate"/>,
/// because a gate that reads it conditions inclusion on the outcome variable.
/// </param>
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
/// <remarks>
/// <para>
/// <b>The rule.</b> An event is quarantined when the absolute pre-entry move — close(pre-entry) to
/// close(entry) — is larger than <see cref="ScaleMultiple"/> times the trailing 20-day median
/// absolute daily return, falling back to <see cref="FallbackFloor"/> when that scale is unavailable
/// or non-positive. <see cref="Describe"/> states it in one sentence for the memo. Those two — the
/// pre-entry closes and the name's own trailing scale — are the whole of the input. Both are known
/// at the entry snapshot, and nothing the rule reads is measured after it.
/// </para>
/// <para>
/// <b>Why that shape.</b> The first signal (acceptance time versus session boundaries, in
/// <see cref="TimingResolver"/>) catches an 8-K filed inside the session. It cannot catch one filed
/// a whole session late — an AMC release on Tuesday evening whose 8-K is accepted at 16:30 on
/// Wednesday classifies as a clean AMC and puts the entry close on Wednesday, a full day <i>after</i>
/// the market absorbed the news. The price signature of that is a pre-entry leg far larger than the
/// name ordinarily produces in a day. The comparison to the name's own trailing scale is what makes
/// that a signature rather than a coin flip: without it a quiet event day would make any pre-entry
/// noise "large", and a mega-cap drifting 0.4% would quarantine.
/// </para>
/// <para>
/// <b>The event move is computed and reported, never read.</b>
/// <see cref="TimingQaResult.EventMove"/> is |close(exit)/close(entry) − 1|, which is RF itself —
/// the numerator of the study's primary statistic. An earlier form of this rule <i>also</i> required
/// the pre-entry move to exceed it, reasoning that a run-up into a print is not a late filing. That
/// conditioned <b>inclusion on the outcome variable</b>, and the cost was not small. Two events with
/// an identical +6.0% pre-entry move, differing only after entry, were decided differently: a +0.28%
/// event move was quarantined and a −9.43% one was kept. Because the conjunct fires only when the
/// event move is small, and RF/IM is small exactly when RF is, it stripped out the sample's
/// strongest PASS evidence. On 6,000 synthetic events containing zero late filings — so every
/// quarantine there is a false positive — it removed 274 whose median(RF/IM) was 0.4431 and
/// P(RF&lt;IM) 0.8942, against a population 0.6325 and 0.7080, moving the surviving median to 0.6464;
/// on a population sitting at the verdict boundary the false positives alone moved median(RF/IM)
/// from 0.9788 to 1.0026 and turned a PASS into a FAIL. It also bought nothing: against 132 injected
/// true late filings the pre-entry-only rule at the same threshold caught the same 58, and it left
/// the clean population's median exactly where it found it (0.6325 → 0.6325).
/// </para>
/// <para>
/// <b>The argument for the conjunct, and the answer to it.</b> It was that without the comparison,
/// every name that happened to move the day before its print is cut — pre-earnings run-ups, a peer's
/// results, an index rebalance — a cut correlated with volatility and therefore with IM. The first
/// half is true and the cut is genuinely larger (397 events against 274 on the population above).
/// The second half is where it fails: a cut correlated with the <i>level</i> of IM is harmless,
/// because the statistic is a ratio and both terms scale with volatility together. What would not be
/// harmless is a cut correlated with RF/IM, and that is what the conjunct — not its absence —
/// produced. Measured on the same clean population, the events the pre-entry-only rule removes have
/// median(RF/IM) 0.6324 against the population's 0.6325; the events the conjunctive rule removed had
/// 0.4431. The cheaper cut was the biased one. The field stays in the result because the memo prints
/// it, and comparing the two legs is how a reader sanity-checks a quarantine after the fact — but a
/// reported number and a gate input are different things.
/// </para>
/// <para>
/// <b>Direction of the error.</b> A late filing crushes the entry straddle (the event is already in
/// the past at the snapshot) while leaving a normal-sized exit move, so RF/IM comes out too high —
/// biasing the study toward the FAIL that stops the whole program, and that the pre-registration
/// treats as strong evidence. Catching those is the point of the gate. A false positive — a genuine
/// non-earnings shock the day before a real print — now removes an event <i>without regard to its
/// RF</i>, because the decision cannot see RF: the removed set is a random sample of the population
/// with respect to the outcome, the sample shrinks slightly, and the count is in the exclusion table
/// for anyone to read. That is the property that matters, and it is precisely the one the earlier
/// conjunctive form lacked. It is not a claim that false positives are free — they cost sample size,
/// and a pre-entry-only rule produces more of them (397 against 274 on the population above) — only
/// that they can no longer push the verdict in either direction.
/// </para>
/// <para>
/// <b>What it is not.</b> Not a throw and not a silent skip: a missing or non-positive close yields
/// <c>not_evaluable</c> with the field named. Gate 08 (closes present) is applied before gate 09
/// precisely so nothing reaches here without closes, so a <c>not_evaluable</c> result is a guard
/// that fired, not a path the pipeline uses — WP5 should treat one as a defect upstream rather than
/// as a quiet exclusion.
/// </para>
/// </remarks>
public static class TimingQa
{
    /// <summary>How many trailing median absolute daily returns the pre-entry move must exceed.</summary>
    public const decimal ScaleMultiple = 3m;

    /// <summary>
    /// The absolute pre-entry move threshold used when the trailing scale is unavailable. 4% is
    /// roughly <see cref="ScaleMultiple"/> times the ~1.3% median absolute daily return of a typical
    /// optionable US single name, so the fallback sits about where the scaled rule would for such a
    /// name. It is a floor for an unmeasurable scale, not an estimate of one, and every row that
    /// takes it says so in its reason so the memo can count them.
    /// </summary>
    public const decimal FallbackFloor = 0.04m;

    /// <summary>
    /// The reference threshold for <see cref="ImCollapseDiagnostic"/>. Diagnostic only: no gate
    /// reads it, and the registered gate stays price-based per the pre-registration's wording.
    /// </summary>
    public const decimal ImCollapseThreshold = 0.60m;

    /// <summary>The rule in one sentence. The memo prints this verbatim; it is not prose to be paraphrased.</summary>
    public static string Describe() =>
        "Price QA (gate 09): an event is quarantined when the absolute pre-entry move (pre-entry close to " +
        $"entry close) is larger than {Multiple} times the trailing 20-day median absolute daily return — or " +
        $"larger than {Percent(FallbackFloor)} when that trailing scale is unavailable — because that is the " +
        "signature of an 8-K accepted a session or more after the release, which places the entry close after " +
        "the move it is supposed to precede, and the comparison reads nothing else.";

    /// <summary>
    /// Applies the rule to one event. <paramref name="timing"/> and <paramref name="closes"/> must
    /// describe the same event; a mismatch is a join defect in the caller and is refused rather than
    /// silently scoring one event against another's prices.
    /// </summary>
    public static TimingQaResult Evaluate(EventTimingRow timing, ClosesRow closes)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(closes);

        if (!string.Equals(timing.EventId, closes.EventId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"timing row is for event '{timing.EventId}' but closes row is for '{closes.EventId}'.",
                nameof(closes));
        }

        if (timing.PreEntryDate is null || timing.EntryDate is null || timing.ExitDate is null)
        {
            return new TimingQaResult(false, "not_evaluable: the event has no resolved pre-entry, entry or exit date", null, null);
        }

        if (closes.ClosePreEntry is not { } preEntryClose)
        {
            return new TimingQaResult(false, "not_evaluable: close_pre_entry is missing", null, null);
        }

        if (closes.CloseEntry is not { } entryClose)
        {
            return new TimingQaResult(false, "not_evaluable: close_entry is missing", null, null);
        }

        if (closes.CloseExit is not { } exitClose)
        {
            return new TimingQaResult(false, "not_evaluable: close_exit is missing", null, null);
        }

        if (preEntryClose <= 0m)
        {
            return new TimingQaResult(false, "not_evaluable: close_pre_entry is not positive", null, null);
        }

        if (entryClose <= 0m)
        {
            return new TimingQaResult(false, "not_evaluable: close_entry is not positive", null, null);
        }

        var preEntryMove = Math.Abs((entryClose / preEntryClose) - 1m);
        var eventMove = Math.Abs((exitClose / entryClose) - 1m);

        // A zero or negative trailing median is degenerate, not a scale: a name that did not move for
        // twenty sessions would otherwise get a zero threshold, and every event on it would quarantine
        // on any pre-entry move at all.
        var scaled = closes.MedianAbsReturn20 is { } median && median > 0m
            ? ScaleMultiple * median
            : (decimal?)null;

        var threshold = scaled ?? FallbackFloor;

        // The pre-entry move and the threshold are the only two terms here, deliberately. eventMove is
        // RF, the numerator of the study's primary statistic; reading it would condition inclusion on
        // the outcome. It is carried into the result for reporting only. See the remarks above.
        var quarantined = preEntryMove > threshold;

        var basis = scaled is not null
            ? $"{Multiple}x the trailing median absolute daily return ({Percent(threshold)})"
            : $"the {Percent(FallbackFloor)} fallback floor (trailing median absolute daily return unavailable)";

        if (quarantined)
        {
            return new TimingQaResult(
                true,
                $"late_filing_suspected: pre-entry move {Percent(preEntryMove)} exceeds {basis}",
                preEntryMove,
                eventMove);
        }

        // Not quarantined, but say when the scale was unmeasurable: it is the difference between a
        // rule that was applied and one that was approximated, and it has to be countable.
        return new TimingQaResult(
            false,
            scaled is null ? $"evaluated against {basis}" : null,
            preEntryMove,
            eventMove);
    }

    /// <summary>
    /// DIAGNOSTIC, NOT A GATE. How much of the pre-entry straddle survives into the entry snapshot:
    /// (straddle mid / spot at entry) ÷ (straddle mid / spot at pre-entry), i.e. IM at entry as a
    /// fraction of IM the session before. A value well below 1 — <see cref="ImCollapseThreshold"/> is
    /// the reference — means the option market had already crushed by the entry snapshot, which is
    /// what a print that has already happened looks like from the options side.
    /// </summary>
    /// <remarks>
    /// This is arguably the sharper of the two signals, because it observes the thing the study
    /// actually measures rather than a proxy for it, and it does not need the move to be large. It is
    /// exposed so WP5 can report how often it agrees with the registered gate, and deliberately gates
    /// nothing: the pre-registration names a price-based QA, and swapping the signal after the freeze
    /// would be a C1-PREREG v2 event, not a code change. Null whenever any input is missing or
    /// non-positive.
    /// </remarks>
    public static decimal? ImCollapseDiagnostic(OptionMeasuresRow measures)
    {
        ArgumentNullException.ThrowIfNull(measures);

        if (measures.StraddleMidPreEntry is not { } straddlePreEntry || straddlePreEntry <= 0m
            || measures.SpotParityPreEntry is not { } spotPreEntry || spotPreEntry <= 0m
            || measures.StraddleMidEntry is not { } straddleEntry || straddleEntry <= 0m
            || measures.SpotParityEntry is not { } spotEntry || spotEntry <= 0m)
        {
            return null;
        }

        return (straddleEntry / spotEntry) / (straddlePreEntry / spotPreEntry);
    }

    private static string Multiple => ScaleMultiple.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Percent(decimal fraction) =>
        (fraction * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";
}
