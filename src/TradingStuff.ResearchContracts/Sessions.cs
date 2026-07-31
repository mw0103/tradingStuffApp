namespace TradingStuff.ResearchContracts;

/// <summary>
/// One exchange trading session (a row of <c>research.sessions</c>) — a concrete open/close instant
/// pair for a given calendar and trading date, e.g. Cboe index RTH on a given date, or a CME ES
/// session including its overnight leg.
/// </summary>
/// <param name="Calendar">
/// Free-text calendar key, e.g. <c>"CBOE_INDEX_RTH"</c>, <c>"CBOE_INDEX_GTH"</c>, <c>"CME_ES"</c>,
/// <c>"NYSE"</c>. Not an enum: the set of calendars this platform tracks grows as instruments are
/// added.
/// </param>
/// <param name="OpenUtc">
/// Session open, in UTC. Always the literal UTC instant — never an exchange-local wall-clock time
/// paired with a timezone name. See the UTC-canonical doctrine on <see cref="ISessionClock"/>.
/// </param>
/// <param name="CloseUtc">Session close, in UTC. See <see cref="OpenUtc"/>.</param>
/// <param name="Label">
/// <c>"RTH"</c> (regular trading hours) or <c>"GTH"</c> (global/overnight trading hours). A single
/// calendar can carry both as separate rows for the same <paramref name="TradingDate"/> — RTH and
/// GTH are different sessions, not a single session with two boundaries.
/// </param>
public sealed record TradingSession(
    long SessionId,
    string Calendar,
    DateOnly TradingDate,
    DateTimeOffset OpenUtc,
    DateTimeOffset CloseUtc,
    string Label,
    bool IsHalfDay);

/// <summary>
/// Resolves exchange session boundaries and trading dates from UTC instants.
/// </summary>
/// <remarks>
/// <para>
/// UTC-canonical doctrine: every instant this platform stores or reasons about — observation
/// timestamps, bar timestamps, backfill request boundaries — is UTC, full stop. Exchange-local
/// wall-clock time (America/Chicago for Cboe products, America/New_York for NYSE-listed ones, and
/// so on) exists only as a human-facing concept: what session an instant falls in, and what
/// calendar date that session belongs to for a human looking at "today's SPX session."
/// </para>
/// <para>
/// <see cref="ISessionClock"/> — and specifically the future <c>SessionClock</c> implementation of
/// it — is the ONLY type in this platform permitted to convert between UTC and an exchange-local
/// timezone. Every other component (the recorder, the backfill coordinator, coverage/feature code)
/// must go through this interface rather than reach for <c>TimeZoneInfo</c> directly. That
/// concentration is deliberate: timezone conversion is exactly the kind of logic that is easy to get
/// subtly wrong per-instrument (DST transitions, half days, a session that spans midnight UTC), and
/// a single, well-tested authority is far safer than the same conversion re-implemented at every call
/// site that happens to need a trading date.
/// </para>
/// <para>
/// This interface is a contract only — implementing it (reading <c>research.sessions</c>, generating
/// missing rows, handling half days) is a later work package. Nothing in this phase implements it.
/// </para>
/// </remarks>
public interface ISessionClock
{
    /// <summary>The session covering <paramref name="instantUtc"/> on <paramref name="calendar"/>, or null if none does.</summary>
    TradingSession? SessionAt(string calendar, DateTimeOffset instantUtc);

    /// <summary>
    /// The trading date <paramref name="instantUtc"/> belongs to on <paramref name="calendar"/>.
    /// Not simply <c>instantUtc</c>'s UTC calendar date — an overnight (GTH) session, or a session
    /// that opens before UTC midnight in exchange-local time, can place an instant on a different
    /// trading date than its UTC date would naively suggest.
    /// </summary>
    DateOnly TradingDateOf(string calendar, DateTimeOffset instantUtc);

    /// <summary>Every session on <paramref name="calendar"/> with a trading date in [<paramref name="from"/>, <paramref name="to"/>], inclusive.</summary>
    IReadOnlyList<TradingSession> SessionsBetween(string calendar, DateOnly from, DateOnly to);
}
