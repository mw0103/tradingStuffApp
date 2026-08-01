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
/// day; and — the finding that motivated this file — the Cboe GTH session is a thirteen-hour
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

        // The SPX INDEX level is a completely different session from the options written on it, and
        // conflating them was costing 100% of SPX RTH sessions. The index is computed from the cash
        // market and stops when the cash market does — 08:30-15:00 CT, 390 minutes — while the
        // options trade fifteen minutes longer. Measured 2026-08-01 off live TWS: SPX IND (conId
        // 416904) reports tradingHours 0830-1500 US/Central, five consecutive useRth=true 1-minute
        // sessions returned exactly 390 bars each (08:30..14:59 CT), never 405, and the platform's
        // own seeded SPX jobs were therefore computing 405 expected against 390 landed and returning
        // succeeded_but_absent on every single session. See CBOE_SPX_RTH.
        //
        // There is also NO overnight leg, and the entry claiming one cited the capability matrix,
        // which says the opposite ("index computed RTH only", "1-min bars RTH only"). Confirmed on
        // the wire: a useRth=false "2 D" request returns 780 bars = 2 x 390, all inside the day
        // window, with a single 1,051-minute overnight gap and not one GTH bar. A justifying comment
        // that cites a source saying the reverse is worse than no comment.
        ("SPX", "index") => new(
            [new("CBOE_SPX_RTH", "RTH")],
            []),

        // VIX is where the shared default was first found to be actively wrong. CBOE_INDEX_GTH
        // describes the index OPTION session — 19:15 CT the prior day through 08:25 CT — and VIX
        // index values are not published across it. Expecting that window over VIX bars reported
        // every correct VIX overnight session as succeeded_but_absent.
        //
        // The first fix removed the wrong calendar and declared the overnight window unmodelled,
        // because one day's bars are not a published schedule. That was right on the evidence then
        // and is wrong now, in two ways: the venue's own schedule is available (below), and an
        // unmodelled window makes the VIX series report as never-reconciled forever, which is the
        // same permanently-red gate this project has now written into STATE.md three times.
        //
        // MEASURED 2026-08-01 off live TWS, read-only. VIX IND (conId 13455763) reports tradingHours
        // and liquidHours as TWO legs, 0215-0815 and 0830-1600 US/Central. IBKR's published schedule
        // (reqHistoricalData whatToShow=SCHEDULE) returns both legs on all 752 trading dates over
        // 2023-08-02..2026-07-31, with the overnight leg unshortened on half days and the day leg at
        // 12:15 CT on them. Cross-checked against real bars rather than trusted: 810 1-minute bars a
        // day = 360 overnight (02:15..08:14 CT) + 450 day (08:30..15:59 CT). Both legs now have real
        // calendars, so nothing about VIX is unmodelled any more.
        ("VIX", "index") => new(
            [new("CBOE_VIX_RTH", "RTH"), new("CBOE_VIX_GTH", "GTH")],
            []),

        // SPY is NYSE/ARCA cash equities, not Cboe. Nothing about its session resembles the index
        // pair, and the shared default had it measured against Cboe hours.
        //
        // SPY does trade outside the NYSE regular session, and that window is declared unmodelled
        // rather than left silent. MEASURED 2026-08-01 off live TWS: SPY reports tradingHours
        // 0400-2000 US/Eastern against liquidHours 0930-1600, and a useRth=false 1-minute TRADES
        // request returns 960 bars a day running 04:00..19:59 ET — 570 minutes a day that the NYSE
        // calendar's 390-minute session does not describe. Latent today because both shipped SPY jobs
        // are useRth=true, exactly as the SPX index GTH claim was latent; the difference is that here
        // the extra data provably EXISTS, so an expectation-free window is a real audit hole and gets
        // named. Writing an NYSE_EXTENDED calendar is the complete fix and belongs in
        // exchange-calendars.json alongside the rest.
        ("SPY", "stock") => new(
            [new("NYSE", "RTH")],
            [new(
                "SPY trades 04:00-20:00 ET while the NYSE calendar models only the 09:30-16:00 ET " +
                "regular session (runtime-verified 2026-08-01: 960 useRth=false 1-minute bars a day, " +
                "04:00..19:59 ET). Pre- and post-market SPY bars are therefore NOT audited — their " +
                "absence would not be reported.")]),

        // CME_ES carries both labels under one key, with the Globex GTH row NESTING the RTH row for
        // the same trading date. A consumer that adds the two together double-counts the overlap;
        // one that takes GTH alone is correct, because RTH is inside it.
        ("ES", "future_family") => new([new("CME_ES", "GTH"), new("CME_ES", "RTH")], []),

        _ => InstrumentCalendarMapping.None,
    };
}
