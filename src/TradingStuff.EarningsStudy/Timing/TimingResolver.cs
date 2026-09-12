using System.Globalization;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.ResearchContracts;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>
/// One event's resolution, with the facts <see cref="TimingStep"/> tallies but
/// <see cref="EventTimingRow"/> has no column for.
/// </summary>
/// <param name="Row">The row written to <c>event_timing.csv</c>.</param>
/// <param name="AcceptanceInstantUtc">The resolved acceptance instant, or null when the wall clock named no instant.</param>
/// <param name="AcceptanceDateIsTradingDay">Whether the exchange held a regular session on the acceptance date.</param>
/// <param name="AcceptanceAnomaly">A DST note from the EDGAR boundary, or null.</param>
public sealed record TimingResolution(
    EventTimingRow Row,
    DateTimeOffset? AcceptanceInstantUtc,
    bool AcceptanceDateIsTradingDay,
    string? AcceptanceAnomaly);

/// <summary>
/// WP2's ground truth: the acceptance instant to BMO/AMC/INTRADAY classification and the
/// calendar-resolved measurement dates every later measure is computed on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The window rule, and the one tie in it.</b> The pre-registration does not state the window in
/// those terms. What it says, in full, is <c>RF = |close(T+1 after print) / entry close - 1|</c> and,
/// of the entry snapshot, <c>BMO -&gt; prior close, AMC -&gt; same-day close</c>. This resolver
/// <i>interprets</i> that as: entry is the last close strictly before the print and exit the first
/// close strictly after — which is what those two clauses come to on a trading day, and the reading
/// the rest of this file is built on. It is an interpretation and not a quotation, so it is recorded
/// as one; if it is ever judged the wrong reading, that is a C1-PREREG v2 event and not a code
/// change. Implemented as: entry is the trading date of the last session whose close is <b>at or
/// before</b> the acceptance instant, exit the trading date of the first session whose close is
/// strictly after it. The "at or before" is not a loosening of the interpretation, it is the same
/// boundary the classification uses — <c>instant &gt;= close</c> reads as AMC, and AMC's entry is
/// that day's own close, so a filing stamped exactly at the closing bell has that close on the entry
/// side. Reading it the other way would classify a 16:00:00 filing AMC and then hand it the previous
/// day's entry, which is two different rules for one instant.
/// </para>
/// <para>
/// Everything else follows from that single comparison, which is why it is the only one here. On a
/// trading day it reproduces the registered cases exactly: a pre-open acceptance (BMO) has the
/// previous session's close as the last close at or before it and the print day's close as the first
/// after; a post-close acceptance (AMC) has the print day's close and the next session's. An
/// acceptance inside the session gets the same dates as BMO — which is what the strictly-before /
/// strictly-after reading gives — and is quarantined anyway.
/// </para>
/// <para>
/// <b>Why the calendar authority and not 09:30-16:00.</b> Session boundaries come from
/// <see cref="ISessionClock"/> on the <c>NYSE</c> calendar, so a half day is handled by reading its
/// real close: an acceptance at 13:30 ET on 2024-11-29 is AMC, because that session closed at 13:00,
/// while the same wall time on an ordinary day is INTRADAY. Hardcoding 16:00 would classify the
/// first as intraday and quarantine a perfectly good event; hardcoding it the other way round would
/// admit an event whose entry snapshot is post-print. Neither error is visible downstream.
/// </para>
/// <para>
/// <b>Days the exchange never opened.</b> EDGAR's business calendar is not the exchange's — Good
/// Friday is an ordinary EDGAR day and a NYSE holiday, so acceptances land on days with no session
/// at all. The measurement window is unaffected (last close before, first close after: Thursday and
/// the following Monday for a Good Friday print), so the only open question is which side of the
/// BMO/AMC readout the event is counted on. The rule is the wall clock against
/// <see cref="EdgarAcceptance.NoonEt"/>: before noon ET reads as BMO of the next trading day, at or
/// after noon as AMC of the previous one. It is a label for the pre-registered split and nothing
/// else — it selects no date, changes no measure, and both readings resolve to the same entry and
/// exit. Noon is chosen because the split is about issuer behaviour, morning release versus
/// after-close release, and the wall clock is the only evidence of that on a day with no session to
/// be before or after. The count of such events is written into the gate-07 note rather than left to
/// be inferred.
/// </para>
/// <para>
/// <b>Nothing throws for one row.</b> A date that cannot be resolved — no session within the search
/// horizon in either direction, or an acceptance wall clock that names no instant — produces a
/// complete row classified <see cref="Unresolved"/> and quarantined with the reason, and is removed
/// at gate 07 alongside the intraday ones. The alternative, an exception, loses every other event in
/// the run.
/// </para>
/// </remarks>
public static class TimingResolver
{
    /// <summary>Accepted before the regular session opened: entry is the previous close, exit this day's.</summary>
    public const string Bmo = "BMO";

    /// <summary>Accepted at or after the regular close: entry is this day's close, exit the next session's.</summary>
    public const string Amc = "AMC";

    /// <summary>Accepted while the regular session was trading. Quarantined at gate 07.</summary>
    public const string Intraday = "INTRADAY";

    /// <summary>A date could not be resolved on the calendar. Quarantined at gate 07.</summary>
    public const string Unresolved = "UNRESOLVED";

    /// <summary>The label of the regular session on the NYSE calendar. GTH rows, if a calendar ever carries them, are not the earnings window.</summary>
    public const string RegularTradingHours = "RTH";

    /// <summary>Quarantine reason for an acceptance inside the regular session.</summary>
    public const string IntradayReason = "accepted during the regular session";

    // The search window around the acceptance date. Backwards must reach the pre-entry session
    // across the longest run of closed days the calendar carries (the four-session September 2001
    // closure, plus the weekends either side) and one more session beyond it; forwards must reach
    // the exit session across the same. 30 and 15 calendar days are slack, not estimates, and the
    // generator memoises per calendar-year so a wider window costs nothing after the first event.
    private const int LookbackDays = 30;
    private const int LookaheadDays = 15;

    // The dates this study will ask an exchange calendar about. EDGAR's own record starts in the
    // 1990s and the shipped NYSE calendar asserts nothing before 1993, so anything outside this is a
    // corrupt acceptance stamp rather than an old or a future filing. The bound is declared rather
    // than discovered by catching an exception: a date far outside it makes the session generator
    // resolve holiday rules for years that are not representable at all, and a refusal whose
    // condition is written down can be tested, whereas one that depends on where the generator
    // happens to overflow cannot.
    private static readonly DateOnly EarliestAnswerableDate = new(1900, 1, 1);
    private static readonly DateOnly LatestAnswerableDate = new(2200, 12, 31);

    /// <summary>
    /// Classifies one acceptance and resolves its measurement dates on <paramref name="calendar"/>.
    /// </summary>
    /// <param name="clock">The platform session authority. The only source of "is this a trading day".</param>
    /// <param name="calendar">The calendar key, <see cref="C1Registration.Calendar"/> for this study.</param>
    /// <param name="eventId">The event this row belongs to; echoed, never parsed.</param>
    /// <param name="acceptanceEt">EDGAR's acceptance time as an Eastern wall clock. See <see cref="EdgarAcceptance"/>.</param>
    public static TimingResolution Resolve(
        ISessionClock clock, string calendar, string eventId, DateTime acceptanceEt)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var acceptance = EdgarAcceptance.Resolve(acceptanceEt);
        var printDate = acceptance.DateEt;
        var week = EarningsWeek(printDate);

        if (acceptance.InstantUtc is not { } instant)
        {
            // The boundary always names what was wrong when it declines to resolve; the fallback is
            // here so this row stays countable even if that ever stops being true.
            return Unresolvable(
                eventId, printDate, week,
                acceptance.Anomaly ?? "acceptance wall clock could not be resolved to an instant",
                acceptance, tradingDay: false);
        }

        if (printDate < EarliestAnswerableDate || printDate > LatestAnswerableDate)
        {
            return Unresolvable(
                eventId, printDate, week,
                $"acceptance date {printDate:yyyy-MM-dd} is outside {EarliestAnswerableDate:yyyy-MM-dd}.." +
                $"{LatestAnswerableDate:yyyy-MM-dd}, which no exchange calendar in this platform describes",
                acceptance, tradingDay: false);
        }

        // Ordered by close, so the entry session is the last one whose close has already happened at
        // the acceptance instant and the exit session is the very next element.
        var sessions = clock
            .SessionsBetween(calendar, printDate.AddDays(-LookbackDays), printDate.AddDays(LookaheadDays))
            .Where(session => string.Equals(session.Label, RegularTradingHours, StringComparison.Ordinal))
            .OrderBy(session => session.CloseUtc)
            .ToList();

        var sessionOnPrintDate = sessions.FirstOrDefault(session => session.TradingDate == printDate);
        var acceptanceDateIsTradingDay = sessionOnPrintDate is not null;

        var entryIndex = -1;

        foreach (var session in sessions)
        {
            if (session.CloseUtc > instant)
            {
                break;
            }

            entryIndex++;
        }

        if (entryIndex < 0)
        {
            return Unresolvable(
                eventId, printDate, week,
                $"no {calendar} close at or before the acceptance instant within {LookbackDays} days",
                acceptance, acceptanceDateIsTradingDay);
        }

        // entryIndex == 0 means the pre-entry session is outside the searched window rather than
        // absent, and the two are indistinguishable from here. Only reachable at the very start of
        // the calendar's data, which is exactly where refusing is right.
        if (entryIndex == 0)
        {
            return Unresolvable(
                eventId, printDate, week,
                $"no {calendar} session before the entry session within {LookbackDays} days " +
                "(pre-entry date unresolvable)",
                acceptance, acceptanceDateIsTradingDay);
        }

        if (entryIndex + 1 >= sessions.Count)
        {
            return Unresolvable(
                eventId, printDate, week,
                $"no {calendar} close after the acceptance instant within {LookaheadDays} days " +
                "(exit date beyond the generated horizon)",
                acceptance, acceptanceDateIsTradingDay);
        }

        var preEntry = sessions[entryIndex - 1].TradingDate;
        var entry = sessions[entryIndex].TradingDate;
        var exit = sessions[entryIndex + 1].TradingDate;

        var quarantined = false;
        string? quarantineReason = null;
        string timingClass;

        if (sessionOnPrintDate is null)
        {
            // No session to be before or after; the wall clock is the only evidence of intent.
            timingClass = acceptance.TimeEt < EdgarAcceptance.NoonEt ? Bmo : Amc;
        }
        else if (instant < sessionOnPrintDate.OpenUtc)
        {
            timingClass = Bmo;
        }
        else if (instant >= sessionOnPrintDate.CloseUtc)
        {
            timingClass = Amc;
        }
        else
        {
            timingClass = Intraday;
            quarantined = true;
            quarantineReason = IntradayReason;
        }

        return new TimingResolution(
            new EventTimingRow(eventId, timingClass, printDate, preEntry, entry, exit, week, quarantined, quarantineReason),
            instant,
            acceptanceDateIsTradingDay,
            acceptance.Anomaly);
    }

    /// <summary>
    /// The ISO-8601 week of <paramref name="date"/> as <c>2024-W05</c>. The year is the ISO week-year,
    /// not the calendar year: 2024-12-30 is 2025-W01 and 2021-01-01 is 2020-W53, and getting that
    /// wrong would split one bootstrap cluster in two across every year boundary.
    /// </summary>
    public static string EarningsWeek(DateOnly date)
    {
        var asDateTime = date.ToDateTime(TimeOnly.MinValue);

        return string.Concat(
            ISOWeek.GetYear(asDateTime).ToString("D4", CultureInfo.InvariantCulture),
            "-W",
            ISOWeek.GetWeekOfYear(asDateTime).ToString("D2", CultureInfo.InvariantCulture));
    }

    private static TimingResolution Unresolvable(
        string eventId,
        DateOnly printDate,
        string week,
        string reason,
        EdgarAcceptanceInstant acceptance,
        bool tradingDay) =>
        new(
            new EventTimingRow(eventId, Unresolved, printDate, null, null, null, week, true, reason),
            acceptance.InstantUtc,
            tradingDay,
            acceptance.Anomaly);
}
