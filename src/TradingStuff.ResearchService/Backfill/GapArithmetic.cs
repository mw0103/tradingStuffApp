using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Recording;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>Whether a job's bar size names a per-minute grid, a per-trading-date one, or neither.</summary>
public enum BarSizeKind
{
    /// <summary>Minute or hour bars: expectation is measured in whole session minutes.</summary>
    Intraday,

    /// <summary>
    /// Day bars: expectation is exactly one bar per trading date, keyed on <c>trading_date</c>, never
    /// <c>ts_utc</c> minutes — see migration 004's split and <see cref="BackfillStore.LandBarsAsync"/>.
    /// </summary>
    Daily,

    /// <summary>
    /// A bar size this detector refuses to check rather than guess at: sub-minute (not a backfill
    /// target at all — see <see cref="BackfillPlanner.CadenceForBarSize"/>), week/month (no
    /// runtime-verified per-bar expectation), or unrecognized text.
    /// </summary>
    Unsupported,
}

/// <summary><see cref="BarSizeKind"/> plus, for <see cref="BarSizeKind.Intraday"/>, the bar's own width in minutes.</summary>
public readonly record struct BarSizeShape(BarSizeKind Kind, int? IntervalMinutes);

/// <summary>
/// One <c>research.backfill_requests</c> row, reduced to the window it nominally covers and its
/// outcome — everything <see cref="GapArithmetic.DetermineBasis"/> needs, computed once so the sweep
/// over many expectation units never touches the database or re-parses a duration string.
/// </summary>
public readonly record struct RequestWindow(DateTimeOffset Start, DateTimeOffset End, string State, int Attempts);

// The instrument -> calendar mapping this file used to carry privately now lives in
// TradingStuff.ResearchService.Sessions.InstrumentCalendars. Two copies of that mapping is what
// produced the defect it was moved to fix: this one mapped VIX to the 780-minute Cboe index-OPTION
// GTH session, against which every correct VIX overnight session reported succeeded_but_absent, and
// CoverageMonitor independently used one shared Cboe pair as the denominator for every conId
// including NYSE-listed SPY. It is a statement about instruments and exchange calendars, so it
// belongs beside ISessionClock, and there must be exactly one of it.

/// <summary>
/// The pure range arithmetic behind gap detection: classifying a bar size, deriving a request row's
/// nominal window, ranking which outstanding request explains a shortfall, and merging consecutive
/// shortfalls into reported ranges. No database, no clock — the same separation
/// <see cref="CoverageMonitor"/> keeps from its own <c>SessionMinutes</c>, for the same reason: this is
/// testable against hand-computed truth without a connection string.
/// </summary>
internal static class GapArithmetic
{
    /// <summary>Classifies a TWS bar-size string the way <see cref="BackfillPlanner.CadenceForBarSize"/> does, but into an expectation shape rather than a slice cadence.</summary>
    public static BarSizeShape ClassifyBarSize(string barSize)
    {
        var normalized = barSize.Trim().ToLowerInvariant();
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var leading = parts.Length > 0 && int.TryParse(parts[0], out var n) && n > 0 ? n : 1;

        if (normalized.Contains("sec"))
        {
            return new BarSizeShape(BarSizeKind.Unsupported, null);
        }

        if (normalized.Contains("min"))
        {
            return new BarSizeShape(BarSizeKind.Intraday, leading);
        }

        if (normalized.Contains("hour"))
        {
            return new BarSizeShape(BarSizeKind.Intraday, leading * 60);
        }

        // Neither "week" nor "month" contains "day" as a substring, so this matches only genuine day
        // bars — anything else (week/month/unrecognized) falls through to Unsupported below rather
        // than guessing at an expectation formula nothing has verified.
        return normalized.Contains("day")
            ? new BarSizeShape(BarSizeKind.Daily, null)
            : new BarSizeShape(BarSizeKind.Unsupported, null);
    }

    /// <summary>
    /// Every <see cref="BackfillRequestWindowRow"/> reduced to its nominal [start, end)
    /// window, sorted ascending by start.
    /// </summary>
    /// <remarks>
    /// Uses the SAME arithmetic <see cref="BackfillPlanner"/> uses to plan a slice in the first place:
    /// <c>SliceCadence.Previous</c> is a plain "N grid units back from end", which is exact both for a
    /// grid-aligned slice and for the one per-job "leading slice" that deliberately over-reaches past
    /// its boundary (see <c>BackfillPlanner.PlanHistorical</c>'s remarks) — there is no separate
    /// grid-alignment case to handle here. A duration <see cref="BackfillPlanner.TryParseCadence"/>
    /// does not recognise (only a top-up's seconds-based duration, in practice) falls back to
    /// <see cref="BackfillPlanner.ApproximateSpanOf"/>, which is exact for a seconds duration too.
    /// </remarks>
    public static IReadOnlyList<RequestWindow> ComputeRequestWindows(
        IReadOnlyList<BackfillRequestWindowRow> rows)
    {
        var windows = new List<RequestWindow>(rows.Count);

        foreach (var row in rows)
        {
            var start = BackfillPlanner.TryParseCadence(row.Duration) is { } cadence
                ? cadence.Previous(row.EndTimeUtc)
                : row.EndTimeUtc - BackfillPlanner.ApproximateSpanOf(row.Duration);

            windows.Add(new RequestWindow(start, row.EndTimeUtc, row.State, row.Attempts));
        }

        return [.. windows.OrderBy(w => w.Start)];
    }

    /// <summary>
    /// Which <see cref="GapBasis"/> explains a range no (or too few) bars landed for, given the
    /// request row window(s) whose nominal span overlaps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered most-alarming first. Two covering rows for the same range is rare — only a historical
    /// job's newest ("leading") slice is allowed to overlap its neighbour — but when it happens, the
    /// worse explanation must win: an operator reading <see cref="GapBasis.SucceededButAbsent"/> must
    /// never have the same range ALSO carry a covering <c>pending</c> row that could have masked it.
    /// </para>
    /// <para>
    /// <b><see cref="GapBasis.Exhausted"/> outranks <see cref="GapBasis.Permanent"/> and
    /// <see cref="GapBasis.Empty"/>, and the order is not a matter of taste.</b> Those two are
    /// EXPLAINED — TWS said the data is not there, or said the request can never succeed — and a
    /// caller is told to treat them as resolved. Exhausted means nobody established anything: the
    /// coordinator tried N times and stopped. Ranking the explained pair above it let a range the
    /// platform had abandoned report as benign-and-resolved, which is the same class of overstatement
    /// as a job reporting <c>complete</c> with dead slices in it, one level down.
    /// </para>
    /// </remarks>
    public static string DetermineBasis(IReadOnlyList<RequestWindow> covering, int maxAttempts)
    {
        if (covering.Count == 0)
        {
            return GapBasis.NotRequested;
        }

        if (covering.Any(w => w.State == "succeeded"))
        {
            return GapBasis.SucceededButAbsent;
        }

        if (covering.Any(w => w.State == "failed" && w.Attempts >= maxAttempts))
        {
            return GapBasis.Exhausted;
        }

        if (covering.Any(w => w.State == "permanent"))
        {
            return GapBasis.Permanent;
        }

        if (covering.Any(w => w.State == "empty"))
        {
            return GapBasis.Empty;
        }

        if (covering.Any(w => w.State == "failed"))
        {
            return GapBasis.Retrying;
        }

        if (covering.Any(w => w.State == "inflight"))
        {
            return GapBasis.Inflight;
        }

        if (covering.Any(w => w.State == "pending"))
        {
            return GapBasis.Pending;
        }

        // Every BackfillRequestState is one of the cases above; reachable only if migration 004's
        // CHECK constraint and this method ever drift apart. Not-requested is the safe default here —
        // it never UNDERSTATES a gap the way silently returning null would.
        return GapBasis.NotRequested;
    }

    /// <summary>
    /// Finds, for a sequence of expectation units queried in non-decreasing start order, which request
    /// windows overlap each one — in O(units + requests) rather than O(units × requests), because both
    /// sequences are consumed in non-decreasing start order and a window that falls behind the sweep
    /// can never be needed again.
    /// </summary>
    public sealed class RequestWindowSweep(IReadOnlyList<RequestWindow> sortedByStart)
    {
        private int _lowIndex;

        /// <param name="from">Must be non-decreasing across successive calls on one instance.</param>
        public IReadOnlyList<RequestWindow> FindOverlapping(DateTimeOffset from, DateTimeOffset to)
        {
            // A window that ends at or before this range's start can never overlap this range OR any
            // later one (later ranges only start later, by the non-decreasing contract above), so it
            // is dropped for good rather than re-examined.
            while (_lowIndex < sortedByStart.Count && sortedByStart[_lowIndex].End <= from)
            {
                _lowIndex++;
            }

            var result = new List<RequestWindow>();

            for (var i = _lowIndex; i < sortedByStart.Count && sortedByStart[i].Start < to; i++)
            {
                if (sortedByStart[i].End > from)
                {
                    result.Add(sortedByStart[i]);
                }
            }

            return result;
        }
    }

    /// <summary>Every RTH-labelled trading date whose daily bar would fall in [from, to) — the daily-bar expectation unit.</summary>
    /// <remarks>
    /// <para>
    /// RTH specifically, not "any label": daily bars are not RTH/GTH-specific (a "1 day" bar is one
    /// row per trading date, full stop), and the RTH calendar covers the full history for every
    /// family <c>InstrumentCalendars</c> maps, whereas a GTH counterpart may start decades later
    /// (Cboe's GTH calendar is only effective from 2022-11-21) — enumerating from GTH would
    /// under-report trading dates for all of that earlier history.
    /// </para>
    /// <para>
    /// <b>A date is in range iff its bar's INSTANT is</b> — that is, iff its UTC midnight lies in
    /// [from, to). This is the identical predicate
    /// <see cref="BackfillStore.GetLandedTradingDatesAsync"/> applies (<c>ts_utc &gt;= from AND ts_utc
    /// &lt; to</c>), and the two must match exactly, because one builds the expected set and the
    /// other measures reality against it. The previous rule here was whole-day OVERLAP
    /// (<c>start &lt; to AND start + 1d &gt; from</c>), which for any non-midnight <c>from</c> —
    /// including every head-clamped lower bound, which is where this bites in practice — expected a
    /// date whose bar the landed query could not return, and reported
    /// <see cref="GapBasis.SucceededButAbsent"/> over data that is present and correct.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DateOnly> TradingDatesInRange(
        IReadOnlyList<TradingSession> rawSessions, DateTimeOffset from, DateTimeOffset to) =>
        rawSessions
            .Where(s => s.Label == "RTH")
            .Select(s => s.TradingDate)
            .Distinct()
            .Select(date => (Date: date, Start: new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)))
            .Where(x => x.Start >= from && x.Start < to)
            .OrderBy(x => x.Date)
            .Select(x => x.Date)
            .ToArray();

    /// <summary>
    /// The intraday-bar expectation units: sessions clipped to [from, to), filtered to RTH only when
    /// the job requested <c>useRth</c>.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="SessionMinutes.Clip"/> — the exact clipping <see cref="CoverageMonitor"/>'s
    /// denominator uses — rather than re-deriving minute-boundary arithmetic here. Each returned
    /// <see cref="CoverageSession"/> stays a SEPARATE unit (one per calendar/label/trading date); they
    /// are not unioned into one span per date the way <c>DistinctMinutes</c> would, because gap
    /// detection needs to know which SPECIFIC window a shortfall falls in, not just a total minute
    /// count.
    /// </remarks>
    public static IReadOnlyList<CoverageSession> BuildSessionUnits(
        IReadOnlyList<TradingSession> rawSessions, bool useRth, DateTimeOffset from, DateTimeOffset to)
    {
        var filtered = useRth ? rawSessions.Where(s => s.Label == "RTH").ToArray() : rawSessions;

        return SessionMinutes.Clip(filtered, from, to);
    }

    /// <summary>A half-open [From, To) instant span. Used for the audited-versus-claimed reconciliation.</summary>
    public readonly record struct Span(DateTimeOffset From, DateTimeOffset To)
    {
        public bool IsEmpty => To <= From;
    }

    /// <summary>Merges overlapping and touching spans into a minimal ascending set.</summary>
    public static IReadOnlyList<Span> Union(IEnumerable<Span> spans)
    {
        var merged = new List<Span>();

        foreach (var span in spans.Where(s => !s.IsEmpty).OrderBy(s => s.From))
        {
            if (merged.Count > 0 && span.From <= merged[^1].To)
            {
                merged[^1] = merged[^1] with { To = span.To > merged[^1].To ? span.To : merged[^1].To };
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    /// <summary>
    /// Everything in <paramref name="claimed"/> that no span in <paramref name="audited"/> covers.
    /// </summary>
    /// <remarks>
    /// The reconciliation the gap report was missing. Every check in this file measures ONE job's
    /// window against ONE job's data, so the seam BETWEEN two jobs covering the same series — a
    /// historical job whose ceiling stopped advancing and a top-up job that only looks back two days
    /// — was audited by neither, and both reported themselves clean over it. Subtracting what was
    /// actually audited from what the jobs collectively claim is the only statement that can catch
    /// that, because it is the only one computed across jobs rather than within one.
    /// </remarks>
    public static IReadOnlyList<Span> Subtract(IEnumerable<Span> claimed, IEnumerable<Span> audited)
    {
        var covered = Union(audited);
        var remaining = new List<Span>();

        foreach (var span in Union(claimed))
        {
            var cursor = span.From;

            foreach (var cover in covered.Where(c => c.To > span.From && c.From < span.To))
            {
                if (cover.From > cursor)
                {
                    remaining.Add(new Span(cursor, cover.From));
                }

                if (cover.To > cursor)
                {
                    cursor = cover.To;
                }
            }

            if (cursor < span.To)
            {
                remaining.Add(new Span(cursor, span.To));
            }
        }

        return remaining;
    }

    /// <summary>
    /// Turns an ordered sequence of expectation units — each either fully covered
    /// (<paramref name="unitsInOrder"/>'s <c>Basis</c> is null) or explained by a
    /// <see cref="GapBasis"/> — into merged <see cref="GapRange"/>s, capped at
    /// <paramref name="maxRanges"/>.
    /// </summary>
    /// <remarks>
    /// <b>A covered unit always breaks a run</b>, even one sandwiched between two incomplete units that
    /// share a basis: merging across it would silently fold a session that has full data INTO a
    /// reported gap. Two incomplete units with the same basis that are merely not adjacent in
    /// wall-clock time — the ordinary case of consecutive trading days separated by a closed weeknight,
    /// or a whole unplanned range spanning many weekends — merge freely, because nothing of interest
    /// (no covered session, no different explanation) sits between them in the sequence that was
    /// actually checked. This is deliberate: a naive "same basis AND touching in time" rule would never
    /// merge anything at all, since even two consecutive trading days' RTH sessions do not touch
    /// (the market is shut overnight), and a 15-year unplanned backlog would render as thousands of
    /// single-day ranges instead of one legible one.
    /// </remarks>
    public static (IReadOnlyList<GapRange> Ranges, bool Truncated) BuildRanges(
        IEnumerable<(DateTimeOffset From, DateTimeOffset To, string? Basis)> unitsInOrder, int maxRanges)
    {
        var merged = new List<GapRange>();
        var truncated = false;
        var runFrom = default(DateTimeOffset);
        var runTo = default(DateTimeOffset);
        string? runBasis = null;

        void Flush()
        {
            if (runBasis is null)
            {
                return;
            }

            if (merged.Count >= maxRanges)
            {
                truncated = true;
                return;
            }

            merged.Add(new GapRange(runFrom, runTo, runBasis));
        }

        foreach (var (from, to, basis) in unitsInOrder)
        {
            if (truncated)
            {
                break;
            }

            if (basis is null)
            {
                Flush();
                runBasis = null;
                continue;
            }

            if (runBasis == basis)
            {
                runTo = to;
            }
            else
            {
                Flush();
                runFrom = from;
                runTo = to;
                runBasis = basis;
            }
        }

        if (!truncated)
        {
            Flush();
        }

        return (merged, truncated);
    }
}
