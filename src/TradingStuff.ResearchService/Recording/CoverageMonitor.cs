using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Recording;

/// <summary>Knobs for the coverage report, bound from the <c>Coverage</c> configuration section.</summary>
public sealed class CoverageOptions
{
    /// <summary>
    /// The calendars whose sessions define what "expected" means. The default is the Cboe index pair
    /// because that is what the recorder mostly records: every one of the 54 option nodes is SPX/SPXW,
    /// and the roadmap's Phase 1 acceptance criterion is <i>"a full RTH+GTH session at &gt;=95%
    /// coverage"</i> for exactly those instruments.
    /// </summary>
    /// <remarks>
    /// <b>Known residual.</b> This is ONE denominator for every conId in the report, and the three
    /// core underlyings do not all trade against it: SPY is NYSE-calendared, and neither the SPX nor
    /// the VIX index level updates through Cboe GTH. Those three lines therefore cannot exceed roughly
    /// a third of the RTH+GTH denominator no matter how healthy the recording is, and
    /// <c>OverallCoverageRatio</c> is an unweighted mean across all reporting nodes and unassigned
    /// conIds, so they pull it down by a couple of points. This is not a regression — the old
    /// wall-clock denominator was worse for every instrument — but it means the 95% gate should be
    /// read against the per-node rows until per-conId calendars exist. Doing that properly needs a
    /// conId → instrument → calendar mapping, and the core underlyings are not in
    /// <c>node_assignments</c> to hang one off.
    /// </remarks>
    public string[] Calendars { get; set; } = ["CBOE_INDEX_RTH", "CBOE_INDEX_GTH"];

    /// <summary>
    /// Longest window the report will measure. The tick scan is over the raw event tables (~7M rows
    /// a day), so an operator who types a decade into the query string gets a clear refusal rather
    /// than a query that appears to hang.
    /// </summary>
    public int MaxWindowDays { get; set; } = 92;
}

/// <summary>Why a coverage ratio can or cannot be believed. See <see cref="CoverageBasis"/>.</summary>
public static class CoverageBasisStatus
{
    /// <summary>The sessions table agreed with the generator and the window contains real session minutes.</summary>
    public const string Measured = "measured";

    /// <summary>No 'trading' connection string; nothing was read at all.</summary>
    public const string NotConfigured = "not-configured";

    /// <summary>The window genuinely contains no session — a weekend, a holiday, or an overnight lull.</summary>
    public const string NoSessionInWindow = "no-session-in-window";

    /// <summary>
    /// <c>research.sessions</c> does not match what <see cref="ISessionClock"/> generates for the same
    /// window. The denominator cannot be trusted, so no ratio is reported.
    /// </summary>
    public const string SessionsOutOfSync = "sessions-out-of-sync";

    /// <summary>The requested window was empty, inverted, or longer than <see cref="CoverageOptions.MaxWindowDays"/>.</summary>
    public const string WindowRejected = "window-rejected";

    /// <summary>A configured calendar key is not in the shipped calendar dataset.</summary>
    public const string CalendarUnknown = "calendar-unknown";
}

/// <summary>
/// Per-conId minute coverage over the window's expected session minutes, for a conId
/// <c>research.node_assignments</c> knows nothing about (the core underlyings — SPX, VIX, SPY —
/// which are single, never-rotating instruments, not registered nodes). The whole window is the
/// correct denominator for these because there is no assignment interval to narrow it to.
/// </summary>
public sealed record ConIdCoverage(int ConId, int MinutesWithData, int TotalMinutes, double CoverageRatio);

/// <summary>
/// One conId's tenure as a node's assignment, clipped to the report window and measured against
/// the session minutes it overlaps — never the whole window. <see cref="MeasuredFromUtc"/> and
/// <see cref="MeasuredToUtc"/> are <see cref="AssignedFrom"/>/<see cref="AssignedTo"/> narrowed to
/// the window and floored to whole minutes, the same clipping <see cref="CoverageSession"/> applies
/// to a session; <see cref="TotalMinutes"/> is that clipped interval intersected with the session
/// union, not its raw width, so an assignment held overnight through a session gap is not charged
/// for the gap.
/// </summary>
/// <param name="CoverageRatio">
/// NULL when <see cref="TotalMinutes"/> is zero — a segment can be clipped down to nothing (a node
/// rotated twice within the same UTC minute, or a brand-new assignment whose sliver of the window
/// falls entirely outside a session) and 0/0 is not 0%, it is unmeasured.
/// </param>
public sealed record NodeConIdSegment(
    int ConId,
    DateTimeOffset AssignedFrom,
    DateTimeOffset? AssignedTo,
    DateTimeOffset MeasuredFromUtc,
    DateTimeOffset MeasuredToUtc,
    int MinutesWithData,
    int TotalMinutes,
    double? CoverageRatio);

/// <summary>
/// Coverage for one registered option-node ROLE (e.g. <c>7DTE-ATM-C</c>), aggregated across every
/// conId that held the role during the window.
/// </summary>
/// <remarks>
/// This, not a bare conId, is the unit the roadmap's 95% acceptance gate is actually about:
/// <c>node_assignments</c> exists precisely so a node's identity survives a strike or expiry roll,
/// and a rotation is not an outage. Reporting per conId instead — measuring a retired conId's brief
/// tail and a new conId's long remainder each against the WHOLE window — is the exact defect this
/// type replaces: it turned a flawlessly-recorded, merely-rotated node into two partial rows whose
/// unweighted average reads roughly 50% no matter how healthy the recording was, and it made a node
/// reassigned moments before the request read as freshly 0%. Summing <see cref="NodeConIdSegment"/>
/// entries is safe without double- or under-counting because <c>node_assignments</c>' partial unique
/// index guarantees at most one open row per node at a time, and the row that closes an old
/// assignment and the row that opens the new one are written in the same transaction against the
/// same <c>now()</c> — so one node's segments never overlap and never leave a gap between them
/// inside the window.
/// </remarks>
/// <param name="CoverageRatio">NULL under the same zero-denominator rule as <see cref="NodeConIdSegment.CoverageRatio"/>.</param>
public sealed record NodeCoverage(
    short NodeId,
    string Role,
    int MinutesWithData,
    int TotalMinutes,
    double? CoverageRatio,
    IReadOnlyList<NodeConIdSegment> ConIdSegments);

/// <summary>
/// One <c>research.sessions</c> row clipped to the report window, with the whole minutes it
/// contributes to the denominator.
/// </summary>
/// <param name="ExpectedMinutes">
/// Minutes contributed by THIS session alone. These do not have to sum to the window's expected
/// minutes: <c>CME_ES</c> nests its RTH row inside its Globex GTH row, so the union is what counts
/// and the per-session numbers deliberately overlap.
/// </param>
public sealed record CoverageSession(
    long SessionId,
    string Calendar,
    DateOnly TradingDate,
    string Label,
    bool IsHalfDay,
    DateTimeOffset OpenUtc,
    DateTimeOffset CloseUtc,
    DateTimeOffset MeasuredFromUtc,
    DateTimeOffset MeasuredToUtc,
    int ExpectedMinutes);

/// <summary>
/// Where the denominator came from and whether it can be believed.
/// </summary>
/// <param name="PersistedSessions">Sessions read from <c>research.sessions</c> that overlap the window.</param>
/// <param name="GeneratedSessions">
/// Sessions <see cref="ISessionClock"/> produces for the same window. Reported next to
/// <paramref name="PersistedSessions"/> on purpose: a missing table row shrinks the denominator, and
/// a shrinking denominator makes coverage look BETTER.
/// </param>
public sealed record CoverageBasis(
    string Status,
    IReadOnlyList<string> Calendars,
    int ExpectedMinutes,
    int PersistedSessions,
    int GeneratedSessions,
    IReadOnlyList<CoverageSession> Sessions,
    string? Detail);

/// <summary>A recorder gap, open or closed, for the requested window.</summary>
/// <param name="ClosedBy">
/// <c>observed</c> when the recorder watched recording resume, <c>inferred</c> when a later process
/// bounded a gap its owner died holding — in which case <see cref="EndedAt"/> is an upper bound on
/// the outage, not a measurement. NULL while the gap is still open.
/// </param>
public sealed record RecorderGapSummary(
    long GapId,
    string Scope,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Reason,
    string? ClosedBy);

/// <summary>
/// Coverage as required by the Phase 1 acceptance criterion: a full RTH+GTH session at &gt;=95%
/// coverage, with every gap explained.
/// </summary>
/// <param name="OverallCoverageRatio">
/// NULL whenever <see cref="CoverageBasis.Status"/> is not
/// <see cref="CoverageBasisStatus.Measured"/> — an unmeasurable window reports no number rather than
/// a plausible one. A weekend is not 0% covered and an unsynced calendar is not 100% covered; both
/// were previously indistinguishable from a real reading. The unweighted mean is taken over every
/// <see cref="NodeCoverage"/> with a non-null ratio plus every <see cref="ConIdCoverage"/> in
/// <paramref name="UnassignedConIds"/> — a node whose own denominator collapsed to zero (see
/// <see cref="NodeCoverage.CoverageRatio"/>) contributes nothing to average rather than a
/// fabricated number.
/// </param>
public sealed record CoverageReport(
    DateTimeOffset From,
    DateTimeOffset To,
    CoverageBasis Basis,
    IReadOnlyList<NodeCoverage> PerNode,
    IReadOnlyList<ConIdCoverage> UnassignedConIds,
    double? OverallCoverageRatio,
    int TotalMinutes,
    IReadOnlyList<RecorderGapSummary> Gaps);

/// <summary>
/// Computes recording coverage from the raw event tables against the exchange sessions in
/// <c>research.sessions</c>: the fraction of the window's expected session minutes that carry at
/// least one recorded tick, per node and overall.
/// </summary>
/// <remarks>
/// <para>
/// <b>Expected minutes come from the session calendar, never from the wall clock.</b> The previous
/// version divided tick-bearing minutes by <c>(to - from)</c>, so the default trailing-24h window
/// asked for 1,440 minutes of a market that is open for about 1,185 of them and reported single-digit
/// percentages on a perfectly healthy day. That made the 95% acceptance threshold unreachable by
/// construction and therefore meaningless. The denominator is now the union of the RTH and GTH
/// sessions that overlap the window, clipped to it, taken from the sessions table.
/// </para>
/// <para>
/// <b>The numerator is filtered by the same session intervals as the denominator</b>, so a tick
/// recorded between sessions (a stale snapshot, a late print) can never push a conId past 100%.
/// Every minute counted in the numerator is by construction a minute counted in the denominator.
/// </para>
/// <para>
/// <b>Why the generator is consulted even though the table is the source of the number.</b> A query
/// over <c>research.sessions</c> cannot emit a row for a session that is missing from the table, and
/// a missing session shrinks the denominator — which makes coverage read HIGHER. That is the exact
/// failure shape behind three of the Phase 1 review's eight confirmed defects: absence rendering as
/// health, and here it renders as health in the direction nobody double-checks. So the persisted rows
/// are compared, boundary for boundary, against what <see cref="ISessionClock"/> generates for the
/// same window; on any disagreement the report refuses to produce a ratio and says
/// <see cref="CoverageBasisStatus.SessionsOutOfSync"/> instead. The clock is a pure function of the
/// checked-in calendar data (see <c>SessionClock</c>'s remarks on why it does not read the table), so
/// this is a genuinely independent witness and not the same query asked twice.
/// </para>
/// <para>
/// A node is included even when it has ZERO ticks in the window, for every conId that held its
/// role during the window: a plain <c>GROUP BY con_id</c> over the raw event tables cannot produce
/// a row for a conId that never ticked, which would make a fully-dead subscription — the worst
/// case this whole report exists to catch — invisible instead of showing 0%. Core underlyings
/// (SPX/VIX/SPY) are not tracked in <c>node_assignments</c>, so a fully-dead underlying
/// subscription is not yet covered by this check; that gap is narrower (three fixed, easily
/// resolved symbols vs. up to 54 option nodes) and left as a known residual for now.
/// </para>
/// <para>
/// <b>The denominator is per-NODE assignment tenure, not per-conId window length.</b> Node
/// rotation is routine, not exceptional: <c>RecorderOrchestrator</c> re-runs node selection every
/// two minutes, and both the target expiry (advances at UTC midnight) and the target strike (moves
/// whenever the nearest-5-point spot proxy crosses a boundary) can change on any pass, closing one
/// <c>node_assignments</c> row and opening another. Measuring each resulting conId against the
/// WHOLE window — the previous design — turns one flawlessly-recorded, merely-rotated node into two
/// partial rows (a short-lived retiring conId and a long-lived new one) whose unweighted average
/// reads roughly 50% regardless of how healthy the recording actually was, and made a node rotated
/// moments before the request read as freshly 0/window = 0%. Both failures share one root: the
/// denominator ignored <c>assigned_from</c>/<c>assigned_to</c>, which is exactly the interval
/// <c>node_assignments</c> exists to record. <see cref="NodeCoverage"/> instead sums
/// <see cref="NodeConIdSegment"/> entries — each conId's own tenure intersected with the window and
/// the session union — so a rotation redistributes minutes between segments of the SAME node
/// instead of fragmenting it into unrelated per-conId rows. The per-segment breakdown is kept
/// alongside the aggregate (not collapsed away) specifically so a conId that goes dead the moment
/// it becomes a node's assignment is still visible immediately, rather than waiting for enough of
/// the window to elapse for the node's running average to show it.
/// </para>
/// </remarks>
public sealed class CoverageMonitor(
    IConfiguration configuration,
    ISessionClock clock,
    IOptions<CoverageOptions> options,
    ILogger<CoverageMonitor> logger)
{
    private readonly CoverageOptions _options = options.Value;

    public async Task<CoverageReport> GetCoverageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // The whole report is expressed in whole UTC minutes, so the window is snapped to minute
        // boundaries before anything else happens. Both ends floor: flooring `to` drops the partial
        // minute in progress, which would otherwise be expected-but-unfillable and show up as a
        // permanent ~1-minute deficit on every live report.
        var windowFrom = SessionMinutes.FloorToMinute(from);
        var windowTo = SessionMinutes.FloorToMinute(to);
        var calendars = _options.Calendars;

        if (windowTo <= windowFrom || (windowTo - windowFrom).TotalDays > _options.MaxWindowDays)
        {
            return Rejected(
                windowFrom, windowTo, calendars, CoverageBasisStatus.WindowRejected,
                $"The window must be a positive span of at most {_options.MaxWindowDays} days.");
        }

        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; coverage cannot be computed.");

            return Rejected(
                windowFrom, windowTo, calendars, CoverageBasisStatus.NotConfigured,
                "No 'trading' connection string.");
        }

        IReadOnlyList<TradingSession> generated;

        try
        {
            generated = GenerateOverlapping(calendars, windowFrom, windowTo);
        }
        catch (ArgumentException ex)
        {
            return Rejected(windowFrom, windowTo, calendars, CoverageBasisStatus.CalendarUnknown, ex.Message);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var persisted = await QueryOverlappingSessionsAsync(connection, calendars, windowFrom, windowTo, cancellationToken);
        var gaps = await QueryGapsAsync(connection, windowFrom, windowTo, cancellationToken);

        if (!SessionMinutes.Matches(persisted, generated))
        {
            logger.LogError(
                "research.sessions disagrees with the session generator over {From:O}..{To:O}: {Persisted} " +
                "persisted session(s) vs {Generated} generated. Coverage cannot be measured until the " +
                "calendar is re-synced — a missing session row shrinks the denominator and inflates coverage.",
                windowFrom, windowTo, persisted.Count, generated.Count);

            return Rejected(
                windowFrom, windowTo, calendars, CoverageBasisStatus.SessionsOutOfSync,
                $"{persisted.Count} persisted session(s) vs {generated.Count} generated. " +
                "GET /research/sessions names the offending rows; SessionCalendarSynchronizer " +
                "re-materialises them on startup and on its resync timer.",
                persisted,
                generated.Count,
                gaps);
        }

        var sessions = SessionMinutes.Clip(persisted, windowFrom, windowTo);
        var expectedMinutes = SessionMinutes.DistinctMinutes(sessions);

        if (expectedMinutes == 0)
        {
            return Rejected(
                windowFrom, windowTo, calendars, CoverageBasisStatus.NoSessionInWindow,
                "No exchange session overlaps this window; there is nothing that ought to have been recorded.",
                persisted,
                generated.Count,
                gaps);
        }

        // The whole-window, per-conId tick count. Used directly for conIds node_assignments knows
        // nothing about (the core underlyings); for conIds that ARE a node's assignment it is
        // discarded in favour of the segment-scoped numerator below, which is bounded to the conId's
        // own tenure rather than the whole window.
        var minutesByConId = await QueryMinuteCoverageAsync(connection, windowFrom, windowTo, sessions, cancellationToken);

        // Every node_assignments row whose tenure overlaps the window — a rotated node contributes
        // one row per conId that held it, not just the current one.
        var assignments = await QueryAssignmentIntervalsAsync(connection, windowFrom, windowTo, cancellationToken);

        var clippedSegments = assignments
            .Select(assignment =>
            {
                // The same floor-both clipping SessionMinutes.Clip applies to a session, applied here
                // to an assignment's tenure: floor the start down (can only ever ask for a minute the
                // tenure partly covers) and the end down (can only ever drop a partial minute), so the
                // boundary between two back-to-back segments of the same node lands on exactly one of
                // them and is never double-counted or dropped between them.
                var clippedFrom = SessionMinutes.FloorToMinute(
                    assignment.AssignedFrom > windowFrom ? assignment.AssignedFrom : windowFrom);
                var effectiveTo = assignment.AssignedTo ?? windowTo;
                var clippedTo = SessionMinutes.FloorToMinute(effectiveTo < windowTo ? effectiveTo : windowTo);

                var totalMinutes = clippedTo <= clippedFrom
                    ? 0
                    : SessionMinutes.IntersectMinutes(sessions, clippedFrom, clippedTo);

                return (Assignment: assignment, From: clippedFrom, To: clippedTo, TotalMinutes: totalMinutes);
            })
            .ToArray();

        // Only segments with a real (non-zero) denominator need a numerator query at all — a
        // degenerate segment (clipped to nothing) trivially carries zero minutes with data.
        var minutesBySegment = await QuerySegmentMinuteCoverageAsync(
            connection,
            windowFrom,
            windowTo,
            sessions,
            clippedSegments
                .Where(segment => segment.TotalMinutes > 0)
                .Select(segment => (segment.Assignment.ConId, segment.From, segment.To))
                .ToArray(),
            cancellationToken);

        var perNode = clippedSegments
            .GroupBy(segment => (segment.Assignment.NodeId, segment.Assignment.Role))
            .Select(group =>
            {
                var conIdSegments = group
                    .Select(segment =>
                    {
                        var minutesWithData = segment.TotalMinutes == 0
                            ? 0
                            : minutesBySegment.GetValueOrDefault((segment.Assignment.ConId, segment.From, segment.To));

                        return new NodeConIdSegment(
                            segment.Assignment.ConId,
                            segment.Assignment.AssignedFrom,
                            segment.Assignment.AssignedTo,
                            segment.From,
                            segment.To,
                            minutesWithData,
                            segment.TotalMinutes,
                            segment.TotalMinutes == 0 ? null : (double)minutesWithData / segment.TotalMinutes);
                    })
                    .OrderBy(segment => segment.AssignedFrom)
                    .ToArray();

                // Safe to sum rather than re-union: one node's segments are time-disjoint by
                // construction (see NodeCoverage's remarks on the partial unique index).
                var nodeTotalMinutes = conIdSegments.Sum(segment => segment.TotalMinutes);
                var nodeMinutesWithData = conIdSegments.Sum(segment => segment.MinutesWithData);

                return new NodeCoverage(
                    group.Key.NodeId,
                    group.Key.Role,
                    nodeMinutesWithData,
                    nodeTotalMinutes,
                    nodeTotalMinutes == 0 ? null : (double)nodeMinutesWithData / nodeTotalMinutes,
                    conIdSegments);
            })
            .OrderBy(node => node.NodeId)
            .ToArray();

        // A ticking conId that is not any node's assignment for this window (a core underlying, or a
        // stray subscription with no assignment row at all) falls back to the whole-window
        // denominator — the same measurement this report has always made for those conIds.
        var assignedConIds = new HashSet<int>(assignments.Select(assignment => assignment.ConId));

        var unassignedConIds = minutesByConId.Keys
            .Where(conId => !assignedConIds.Contains(conId))
            .Select(conId =>
            {
                var minutes = minutesByConId[conId];
                return new ConIdCoverage(conId, minutes, expectedMinutes, (double)minutes / expectedMinutes);
            })
            .OrderBy(row => row.ConId)
            .ToArray();

        var ratios = perNode
            .Where(node => node.CoverageRatio.HasValue)
            .Select(node => node.CoverageRatio!.Value)
            .Concat(unassignedConIds.Select(row => row.CoverageRatio))
            .ToArray();

        var basis = new CoverageBasis(
            CoverageBasisStatus.Measured, calendars, expectedMinutes, persisted.Count, generated.Count, sessions, null);

        return new CoverageReport(
            windowFrom,
            windowTo,
            basis,
            perNode,
            unassignedConIds,
            // NULL, not 0d, when there is nothing to average. An unweighted mean over an empty set is
            // undefined, and reporting it as 0% says "every instrument is dead" when the truth is
            // "no instrument is being measured" — a fresh deployment, or any window before the
            // recorder acquired its first lease. Those are opposite operator actions, and the
            // catastrophic-looking one is a false alarm; a gate that cries wolf is a gate nobody
            // reads. Same reasoning as the weekend case above, which is why the field is nullable.
            ratios.Length == 0 ? null : ratios.Average(),
            expectedMinutes,
            gaps);
    }

    /// <summary>A report with no ratio: the window could not be measured, and says so rather than guessing.</summary>
    private static CoverageReport Rejected(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string> calendars,
        string status,
        string detail,
        IReadOnlyList<TradingSession>? persisted = null,
        int generated = 0,
        IReadOnlyList<RecorderGapSummary>? gaps = null)
    {
        IReadOnlyList<CoverageSession> sessions = persisted is null ? [] : SessionMinutes.Clip(persisted, from, to);
        var expectedMinutes = SessionMinutes.DistinctMinutes(sessions);

        return new CoverageReport(
            from,
            to,
            // The RAW persisted count, not the clipped one: it is the number an operator reads against
            // GeneratedSessions to see how far the table has drifted, and clipping would net some of
            // the difference out.
            new CoverageBasis(status, calendars, expectedMinutes, persisted?.Count ?? 0, generated, sessions, detail),
            [],
            [],
            null,
            expectedMinutes,
            gaps ?? []);
    }

    /// <summary>
    /// The sessions the in-process generator produces for the same window. Trading dates are padded
    /// by two days either side because an overnight session opens on the calendar day BEFORE its
    /// trading date (and the widest shipped session is the ~23h Globex day), so a session overlapping
    /// the window can carry a trading date outside it — the same ±2 slack <c>SessionClock</c> uses.
    /// </summary>
    private IReadOnlyList<TradingSession> GenerateOverlapping(
        IReadOnlyList<string> calendars, DateTimeOffset from, DateTimeOffset to)
    {
        var fromDate = DateOnly.FromDateTime(from.UtcDateTime).AddDays(-2);
        var toDate = DateOnly.FromDateTime(to.UtcDateTime).AddDays(2);
        var sessions = new List<TradingSession>();

        foreach (var calendar in calendars)
        {
            foreach (var session in clock.SessionsBetween(calendar, fromDate, toDate))
            {
                if (session.OpenUtc < to && session.CloseUtc > from)
                {
                    sessions.Add(session);
                }
            }
        }

        return sessions;
    }

    private static async Task<IReadOnlyList<TradingSession>> QueryOverlappingSessionsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> calendars,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // Half-open overlap, matching SessionClock's containment rule: a session touching the window
        // at exactly one endpoint contributes no minutes and is not a session "in" the window.
        await using var command = new NpgsqlCommand(
            "SELECT session_id, calendar, trading_date, open_utc, close_utc, label, is_half_day " +
            "FROM research.sessions " +
            "WHERE calendar = ANY($1) AND open_utc < $3 AND close_utc > $2 " +
            "ORDER BY open_utc, calendar, label",
            connection);
        command.Parameters.AddWithValue(calendars.ToArray());
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var sessions = new List<TradingSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new TradingSession(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetFieldValue<DateOnly>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                reader.GetBoolean(6)));
        }

        return sessions;
    }

    /// <summary>One <c>research.node_assignments</c> row whose tenure overlaps the report window.</summary>
    private sealed record NodeAssignmentInterval(
        short NodeId, string Role, int ConId, DateTimeOffset AssignedFrom, DateTimeOffset? AssignedTo);

    /// <summary>
    /// Every node_assignments row whose tenure overlaps [<paramref name="from"/>,
    /// <paramref name="to"/>) — not just the current (<c>assigned_to IS NULL</c>) row per node.
    /// </summary>
    /// <remarks>
    /// The previous version queried only the current row, with no window filter at all, so a node
    /// reassigned any time before "now" — including moments before an entirely historical report —
    /// was measured as if it had held the assignment for the WHOLE window. This is a proper interval
    /// overlap (<c>assigned_from &lt; to AND (assigned_to IS NULL OR assigned_to &gt; from)</c>), the
    /// same shape as the session and gap overlap queries below, so a rotated node correctly yields
    /// one row per conId that held it during the window and nothing for tenures outside it.
    /// </remarks>
    private static async Task<IReadOnlyList<NodeAssignmentInterval>> QueryAssignmentIntervalsAsync(
        NpgsqlConnection connection, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT na.node_id, n.role, na.con_id, na.assigned_from, na.assigned_to " +
            "FROM research.node_assignments na " +
            "JOIN research.option_nodes n ON n.node_id = na.node_id " +
            "WHERE na.assigned_from < $2 AND (na.assigned_to IS NULL OR na.assigned_to > $1) " +
            "ORDER BY na.node_id, na.assigned_from",
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var rows = new List<NodeAssignmentInterval>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NodeAssignmentInterval(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    /// <summary>
    /// Distinct in-session minutes carrying at least one tick, per conId, across both raw event
    /// tables, measured against the WHOLE window. Correct as-is for a conId with no node assignment
    /// (a core underlying); for an assigned conId this whole-window count is discarded in favour of
    /// <see cref="QuerySegmentMinuteCoverageAsync"/>, which bounds the count to the conId's own
    /// assignment tenure so a tick recorded before the tenure started or after it ended cannot count
    /// toward it.
    /// </summary>
    private static async Task<Dictionary<int, int>> QueryMinuteCoverageAsync(
        NpgsqlConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<CoverageSession> sessions,
        CancellationToken cancellationToken)
    {
        // The session intervals are passed as parallel arrays rather than re-derived in SQL so that
        // the denominator (computed in SessionMinutes, unit-tested) and the numerator are filtered by
        // the SAME clipped, minute-floored bounds. Two independent derivations of "which minutes
        // count" is precisely how a ratio drifts past 100%.
        //
        // date_trunc's three-argument form pins the truncation to UTC instead of inheriting the
        // server's TimeZone setting. Minute truncation is offset-invariant for every whole-minute
        // zone, so this is belt-and-braces — but this is the alignment every downstream number is
        // built on, and it costs nothing to say so explicitly (Postgres 16+; the repo targets 17).
        //
        // The window predicate keeps $1/$2 as bare parameters rather than joining a CTE so runtime
        // partition pruning still applies to the daily-partitioned event tables.
        await using var command = new NpgsqlCommand(
            """
            SELECT t.con_id, count(DISTINCT t.minute)
            FROM (
                SELECT con_id, date_trunc('minute', observed_at, 'UTC') AS minute
                FROM gateway.option_quote_events
                WHERE observed_at >= $1 AND observed_at < $2
                UNION ALL
                SELECT con_id, date_trunc('minute', observed_at, 'UTC')
                FROM gateway.underlying_tick_events
                WHERE observed_at >= $1 AND observed_at < $2
            ) AS t
            WHERE EXISTS (
                SELECT 1
                FROM unnest($3::timestamptz[], $4::timestamptz[]) AS s(measured_from, measured_to)
                WHERE t.minute >= s.measured_from AND t.minute < s.measured_to)
            GROUP BY t.con_id
            """,
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);
        command.Parameters.AddWithValue(sessions.Select(session => session.MeasuredFromUtc).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.MeasuredToUtc).ToArray());

        var minutesByConId = new Dictionary<int, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            minutesByConId[reader.GetInt32(0)] = (int)reader.GetInt64(1);
        }

        return minutesByConId;
    }

    /// <summary>
    /// Distinct in-session minutes carrying at least one tick for each (conId, tenure) segment,
    /// bounded to that segment's OWN clipped interval — never the whole window.
    /// </summary>
    /// <remarks>
    /// This is what makes a rotation measure each conId against its own tenure instead of the other
    /// conId's: a tick recorded before the assignment started or after it ended (teardown lag on the
    /// old lease, setup lag on the new one, both real async effects) falls outside <c>[segment_from,
    /// segment_to)</c> and is excluded, which is also what keeps a segment's numerator a subset of
    /// its own denominator by construction — the same guarantee <see cref="QueryMinuteCoverageAsync"/>
    /// gives the whole-window numerator, narrowed to a sub-interval. Segments are looked up by exact
    /// (conId, from, to) match on return, which <c>WITH ORDINALITY</c> makes safe even if two
    /// segments happen to share a conId and bounds.
    /// </remarks>
    private static async Task<Dictionary<(int ConId, DateTimeOffset From, DateTimeOffset To), int>> QuerySegmentMinuteCoverageAsync(
        NpgsqlConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<CoverageSession> sessions,
        IReadOnlyList<(int ConId, DateTimeOffset From, DateTimeOffset To)> segments,
        CancellationToken cancellationToken)
    {
        var minutesBySegment = new Dictionary<(int, DateTimeOffset, DateTimeOffset), int>();

        if (segments.Count == 0)
        {
            return minutesBySegment;
        }

        await using var command = new NpgsqlCommand(
            """
            WITH distinct_ticks AS (
                SELECT DISTINCT con_id, minute
                FROM (
                    SELECT con_id, date_trunc('minute', observed_at, 'UTC') AS minute
                    FROM gateway.option_quote_events
                    WHERE observed_at >= $1 AND observed_at < $2
                    UNION ALL
                    SELECT con_id, date_trunc('minute', observed_at, 'UTC')
                    FROM gateway.underlying_tick_events
                    WHERE observed_at >= $1 AND observed_at < $2
                ) AS t
            ),
            segments AS (
                SELECT s.con_id, s.segment_from, s.segment_to, s.segment_ix
                FROM unnest($3::integer[], $4::timestamptz[], $5::timestamptz[])
                    WITH ORDINALITY AS s(con_id, segment_from, segment_to, segment_ix)
            )
            SELECT seg.segment_ix, count(DISTINCT t.minute)
            FROM segments seg
            JOIN distinct_ticks t
                ON t.con_id = seg.con_id AND t.minute >= seg.segment_from AND t.minute < seg.segment_to
            WHERE EXISTS (
                SELECT 1
                FROM unnest($6::timestamptz[], $7::timestamptz[]) AS sess(measured_from, measured_to)
                WHERE t.minute >= sess.measured_from AND t.minute < sess.measured_to)
            GROUP BY seg.segment_ix
            """,
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);
        command.Parameters.AddWithValue(segments.Select(segment => segment.ConId).ToArray());
        command.Parameters.AddWithValue(segments.Select(segment => segment.From).ToArray());
        command.Parameters.AddWithValue(segments.Select(segment => segment.To).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.MeasuredFromUtc).ToArray());
        command.Parameters.AddWithValue(sessions.Select(session => session.MeasuredToUtc).ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var segment = segments[(int)reader.GetInt64(0) - 1]; // WITH ORDINALITY is 1-based
            minutesBySegment[(segment.ConId, segment.From, segment.To)] = (int)reader.GetInt64(1);
        }

        return minutesBySegment;
    }

    private static async Task<IReadOnlyList<RecorderGapSummary>> QueryGapsAsync(
        NpgsqlConnection connection, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // A gap "overlaps the window" if it started before the window ends and either never ended
        // or ended after the window began.
        await using var command = new NpgsqlCommand(
            "SELECT gap_id, scope, started_at, ended_at, reason, closed_by FROM gateway.recorder_gaps " +
            "WHERE started_at < $2 AND (ended_at IS NULL OR ended_at > $1) " +
            "ORDER BY started_at DESC",
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var gaps = new List<RecorderGapSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            gaps.Add(new RecorderGapSummary(
                reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return gaps;
    }
}

/// <summary>
/// The minute arithmetic behind the coverage denominator: clip sessions to a window, and count the
/// minutes their union covers.
/// </summary>
/// <remarks>
/// Pure, and separated from <see cref="CoverageMonitor"/> so it can be tested against sessions the
/// calendar really produces without a database in the way. It contains no timezone logic whatsoever —
/// every instant it touches is already UTC, produced by <c>SessionClock</c>, which is the only type
/// permitted to convert.
/// </remarks>
internal static class SessionMinutes
{
    /// <summary>The UTC minute <paramref name="instant"/> falls in, as an instant.</summary>
    internal static DateTimeOffset FloorToMinute(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();

        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
    }

    /// <summary>
    /// Clips each session to [<paramref name="from"/>, <paramref name="to"/>) on whole-minute
    /// boundaries, dropping any that contributes no whole minute.
    /// </summary>
    /// <remarks>
    /// Both bounds floor, which is the conservative pair: flooring the start can only ever expect a
    /// minute the session partly covers (coverage reads lower), and flooring the end can only ever
    /// drop a minute the window partly covers (coverage reads no higher). Every real boundary here is
    /// already minute-aligned — session opens and closes come from wall-clock HH:MM times — so this
    /// is a guard against a caller's ragged window, not routine rounding.
    /// </remarks>
    internal static IReadOnlyList<CoverageSession> Clip(
        IEnumerable<TradingSession> sessions, DateTimeOffset from, DateTimeOffset to)
    {
        var clipped = new List<CoverageSession>();

        foreach (var session in sessions)
        {
            var start = FloorToMinute(session.OpenUtc > from ? session.OpenUtc : from);
            var end = FloorToMinute(session.CloseUtc < to ? session.CloseUtc : to);

            if (end <= start)
            {
                continue;
            }

            clipped.Add(new CoverageSession(
                session.SessionId,
                session.Calendar,
                session.TradingDate,
                session.Label,
                session.IsHalfDay,
                session.OpenUtc,
                session.CloseUtc,
                start,
                end,
                (int)((end - start).Ticks / TimeSpan.TicksPerMinute)));
        }

        return
        [
            .. clipped
                .OrderBy(session => session.MeasuredFromUtc)
                .ThenBy(session => session.Calendar, StringComparer.Ordinal)
                .ThenBy(session => session.Label, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Minutes covered by the UNION of the clipped sessions — a minute inside two sessions is one
    /// minute.
    /// </summary>
    /// <remarks>
    /// Summing per-session minutes would double-count wherever sessions overlap, and they do:
    /// <c>CME_ES</c> records its 08:30-15:15 CT RTH row nested inside the ~23h Globex GTH row for the
    /// same trading date, so a naive sum inflates a CME denominator by 405 minutes a day and quietly
    /// depresses every coverage figure measured against it. Done by an ordered sweep rather than by
    /// materialising a set of minutes, so a long window costs nothing in memory.
    /// </remarks>
    internal static int DistinctMinutes(IReadOnlyList<CoverageSession> sessions)
    {
        var total = 0;
        var cursor = DateTimeOffset.MinValue;

        foreach (var session in sessions.OrderBy(session => session.MeasuredFromUtc))
        {
            // A session wholly inside one already counted contributes nothing AND must not move the
            // cursor backwards — it ends earlier than the interval that swallowed it.
            var start = session.MeasuredFromUtc > cursor ? session.MeasuredFromUtc : cursor;

            if (session.MeasuredToUtc <= start)
            {
                continue;
            }

            total += (int)((session.MeasuredToUtc - start).Ticks / TimeSpan.TicksPerMinute);
            cursor = session.MeasuredToUtc;
        }

        return total;
    }

    /// <summary>
    /// Minutes of <paramref name="sessions"/> (already clipped to the report window) that further
    /// fall within [<paramref name="from"/>, <paramref name="to"/>) — the same clip-then-union-count
    /// shape as <see cref="Clip"/> plus <see cref="DistinctMinutes"/>, generalised to an arbitrary
    /// sub-interval so a node's own assignment tenure, not just the window as a whole, can be
    /// measured against session minutes.
    /// </summary>
    /// <remarks>
    /// Bounds are floored the same way <see cref="Clip"/> floors a session: flooring
    /// <paramref name="from"/> down can only ever ask for a minute the sub-interval partly covers,
    /// and flooring <paramref name="to"/> down can only ever drop one. Two segments of the same node
    /// meeting at a sub-minute instant (the row that closes an old assignment and the row that opens
    /// the new one share the same transaction <c>now()</c>, so they meet EXACTLY, never overlap by a
    /// few microseconds) therefore attribute that boundary minute to exactly one of them — the new
    /// segment, since its start floors down to include it while the old segment's end floors down to
    /// exclude it — never to both and never to neither. A session nested inside another (CME_ES) is
    /// still counted once here, for the same reason it is in <see cref="DistinctMinutes"/>.
    /// </remarks>
    internal static int IntersectMinutes(IReadOnlyList<CoverageSession> sessions, DateTimeOffset from, DateTimeOffset to)
    {
        var boundedFrom = FloorToMinute(from);
        var boundedTo = FloorToMinute(to);

        if (boundedTo <= boundedFrom)
        {
            return 0;
        }

        var total = 0;
        var cursor = DateTimeOffset.MinValue;

        foreach (var session in sessions.OrderBy(session => session.MeasuredFromUtc))
        {
            var start = session.MeasuredFromUtc > boundedFrom ? session.MeasuredFromUtc : boundedFrom;
            start = start > cursor ? start : cursor;
            var end = session.MeasuredToUtc < boundedTo ? session.MeasuredToUtc : boundedTo;

            if (end <= start)
            {
                continue;
            }

            total += (int)((end - start).Ticks / TimeSpan.TicksPerMinute);
            cursor = end;
        }

        return total;
    }

    /// <summary>
    /// Whether the persisted sessions are exactly the sessions the generator produces — same count,
    /// same boundaries, same half-day flags.
    /// </summary>
    /// <remarks>
    /// Deliberately not a count comparison. A row whose <c>open_utc</c> is an hour off (the classic
    /// DST mistake, and the one migration-era failure this table has actually seen) leaves the count
    /// untouched while moving the denominator by 60 minutes, so the whole tuple is compared.
    /// <c>SessionId</c> is excluded because it is assigned by the database and the generator has none.
    /// </remarks>
    internal static bool Matches(IReadOnlyList<TradingSession> persisted, IReadOnlyList<TradingSession> generated)
    {
        if (persisted.Count != generated.Count)
        {
            return false;
        }

        static IEnumerable<TradingSession> Normalize(IReadOnlyList<TradingSession> sessions) =>
            sessions
                .Select(session => session with { SessionId = 0 })
                .OrderBy(session => session.OpenUtc)
                .ThenBy(session => session.Calendar, StringComparer.Ordinal)
                .ThenBy(session => session.Label, StringComparer.Ordinal);

        return Normalize(persisted).SequenceEqual(Normalize(generated));
    }
}
