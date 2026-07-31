namespace TradingStuff.ResearchService.Sessions;

/// <summary>
/// Which sessions a given instrument's DATA is expected to exist in.
/// </summary>
/// <param name="Calendar">
/// A calendar key <c>ISessionClock</c> resolves — <c>"CBOE_INDEX_RTH"</c>, <c>"NYSE"</c>, and so on.
/// Never a timezone, never a wall-clock time: this record names a calendar, it does not describe one.
/// </param>
/// <param name="Label">
/// The session label within that calendar this expectation applies to (<c>"RTH"</c> or <c>"GTH"</c>).
/// Named per entry rather than inferred from the calendar key because <c>CME_ES</c> carries both
/// labels under one key, with GTH nesting RTH for the same trading date.
/// </param>
public sealed record InstrumentSessionExpectation(string Calendar, string Label);

/// <summary>
/// A window in which an instrument genuinely trades but for which this platform has no session
/// definition, so no expectation can be computed.
/// </summary>
/// <param name="Description">
/// Human-readable, and meant to be read: it is what a report prints instead of a silent pass.
/// </param>
public sealed record UnmodelledSessionWindow(string Description);

/// <summary>
/// What an instrument's expected sessions are, plus what is knowingly missing from that answer.
/// </summary>
/// <param name="Expectations">
/// The (calendar, label) pairs whose minutes this instrument's data should fill. Empty means the
/// instrument has no mapping at all and the caller must refuse to check it rather than guess.
/// </param>
/// <param name="Unmodelled">
/// Windows the instrument trades in that <paramref name="Expectations"/> deliberately does NOT
/// cover. Reported rather than dropped: a consumer that silently omits them turns a genuinely
/// unaudited stretch into a clean bill of health, which is the same absent-row failure that keeps
/// showing up in this platform's reviews. Adding an entry here is an admission, not a feature.
/// </param>
public sealed record InstrumentCalendarMapping(
    IReadOnlyList<InstrumentSessionExpectation> Expectations,
    IReadOnlyList<UnmodelledSessionWindow> Unmodelled)
{
    public static readonly InstrumentCalendarMapping None = new([], []);

    /// <summary>The distinct calendar keys to ask <c>ISessionClock</c> for.</summary>
    public IReadOnlyList<string> Calendars => [.. Expectations.Select(e => e.Calendar).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether a generated session belongs to this instrument's expectation set.</summary>
    public bool Includes(string calendar, string label) =>
        Expectations.Any(e =>
            string.Equals(e.Calendar, calendar, StringComparison.Ordinal) &&
            string.Equals(e.Label, label, StringComparison.Ordinal));
}

/// <summary>
/// The platform's single answer to "which exchange sessions should this instrument have data in".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Both consumers of session expectations used ONE shared calendar set
/// for every instrument: <c>CoverageOptions.Calendars</c> defaults to the Cboe pair for every conId
/// it measures, and gap detection carried its own private copy of the same idea. That is wrong in
/// both directions at once. SPY is NYSE-calendared and neither SPX nor VIX updates through the NYSE
/// day; and — the finding that motivated this file — the Cboe GTH session is a 780-minute
/// <b>index-option</b> window, which was being used as the expectation for VIX <b>index</b> bars.
/// Every correct, complete VIX overnight session therefore reported <c>succeeded_but_absent</c>, and
/// a report that cries wolf on every session of an instrument trains its operator to stop reading
/// it, which costs more than the defect it was meant to catch.
/// </para>
/// <para>
/// <b>Why it lives in Sessions/ rather than next to either consumer.</b> It is a statement about
/// instruments and exchange calendars, not about coverage or about backfill, and there must be
/// exactly one of it — the same reasoning that makes <c>ISessionClock</c> the only type permitted to
/// convert a timezone. Two copies of this mapping is precisely the state that produced the defect.
/// </para>
/// <para>
/// <b>Nothing here converts a timezone or describes a session's hours.</b> It maps an instrument to
/// calendar KEYS; <c>exchange-calendars.json</c> remains the auditable source of truth for what those
/// keys mean, and <c>ISessionClock</c> remains the only thing that resolves them.
/// </para>
/// </remarks>
public static class InstrumentCalendars
{
    /// <summary>
    /// The mapping for an instrument, keyed the way <c>research.instruments</c> keys it.
    /// </summary>
    /// <param name="kind">
    /// <c>research.instruments.kind</c> — <c>index</c>, <c>stock</c>, <c>option_class</c>,
    /// <c>future_family</c>. Load-bearing, not decoration: SPX the index and SPX the option class
    /// share a symbol and do NOT share an expectation, which is the exact confusion that put a
    /// 780-minute option-session expectation on index bars.
    /// </param>
    /// <returns>
    /// <see cref="InstrumentCalendarMapping.None"/> for anything unmapped. Callers must treat that
    /// as "refuse to check", never as "check against a default" — a wrong calendar does not fail
    /// loudly, it produces a confident report about the wrong sessions.
    /// </returns>
    public static InstrumentCalendarMapping For(string symbol, string kind) => (symbol, kind) switch
    {
        // Cboe index options trade the full 19:15-08:15 CT GTH window as well as RTH.
        ("SPX", "option_class") => new(
            [new("CBOE_INDEX_RTH", "RTH"), new("CBOE_INDEX_GTH", "GTH")],
            []),

        // The SPX INDEX level, by contrast, is a computed value, and the capability matrix records
        // it published through the Cboe GTH window alongside the options. RTH is the only session
        // this platform's SPX jobs actually request (they are useRth=true), so the GTH entry only
        // matters if a useRth=false SPX job is ever added.
        ("SPX", "index") => new(
            [new("CBOE_INDEX_RTH", "RTH"), new("CBOE_INDEX_GTH", "GTH")],
            []),

        // VIX is where the shared default was actively wrong. CBOE_INDEX_GTH describes the index
        // OPTION session — 19:15 CT the prior day through 08:15 CT, 780 minutes — and VIX index
        // values are not published across it. Expecting 780 minutes where roughly 360 exist reported
        // every correct VIX overnight session as succeeded_but_absent.
        //
        // RUNTIME-VERIFIED against the paper account on 2026-07-31: a useRth=false 1-minute TRADES
        // request for trading date 2026-07-30 returned 810 bars, the earliest at 02:15 CT and the
        // latest at 15:59 CT, with one internal gap from 08:14 to 08:30 CT. So VIX index bars are
        // two legs — an overnight 02:15-08:14 CT and a day 08:30-15:59 CT — and NEITHER matches
        // CBOE_INDEX_GTH's 19:15-08:15 window.
        //
        // Two consequences, deliberately different:
        //  * The overnight leg is listed as UNMODELLED rather than approximated. Writing a
        //    "CBOE_VIX_GTH 02:15-08:15" calendar is the complete fix, but it belongs in
        //    exchange-calendars.json — the auditable source of truth every downstream artifact is
        //    validated against — and one day's observation is not a published schedule. Until it is
        //    entered there, the honest answer is that VIX's overnight bars are not audited, which
        //    consumers report explicitly. The previous behaviour asserted a window that does not
        //    exist; the behaviour before that said nothing at all.
        //  * The day leg runs to 15:59 CT, past CBOE_INDEX_RTH's 15:15 close, so the RTH expectation
        //    is a strict SUBSET of what actually lands (405 minutes expected against ~450 present).
        //    That errs toward under-flagging, which is the safe direction for an expectation: it can
        //    miss a shortfall, but it cannot manufacture one.
        ("VIX", "index") => new(
            [new("CBOE_INDEX_RTH", "RTH")],
            [new(
                "VIX 1-minute bars run 02:15-08:14 CT overnight (runtime-verified 2026-07-31), far short " +
                "of the 19:15-08:15 CT CBOE_INDEX_GTH option session, and no calendar describes that " +
                "window. Overnight VIX bars are therefore NOT audited — their absence would not be reported.")]),

        // SPY is NYSE/ARCA cash equities, not Cboe. Nothing about its session resembles the index
        // pair, and the shared default had it measured against Cboe hours.
        ("SPY", "stock") => new([new("NYSE", "RTH")], []),

        // CME_ES carries both labels under one key, with the Globex GTH row NESTING the RTH row for
        // the same trading date. A consumer that adds the two together double-counts the overlap;
        // one that takes GTH alone is correct, because RTH is inside it.
        ("ES", "future_family") => new([new("CME_ES", "GTH"), new("CME_ES", "RTH")], []),

        _ => InstrumentCalendarMapping.None,
    };
}
