using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Sessions;

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
    /// <c>OverallCoverageRatio</c> is an unweighted mean across every registered node and every
    /// unassigned conId, so they pull it down by a couple of points. This is not a regression — the old
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
    /// window — different boundaries, or a row written by a different
    /// <c>SessionGenerator.GeneratorVersion</c>. The denominator cannot be trusted, so no ratio is
    /// reported. Note what this does and does not prove: see the class remarks on
    /// <see cref="CoverageMonitor"/>.
    /// </summary>
    public const string SessionsOutOfSync = "sessions-out-of-sync";

    /// <summary>The requested window was empty, inverted, or longer than <see cref="CoverageOptions.MaxWindowDays"/>.</summary>
    public const string WindowRejected = "window-rejected";

    /// <summary>A configured calendar key is not in the shipped calendar dataset.</summary>
    public const string CalendarUnknown = "calendar-unknown";

    // There is deliberately NO "grid-incomplete" status, even though an unassigned registered node
    // used to be exactly the kind of thing that would need one. A status here means "the ratio
    // cannot be believed", and a partly-assigned grid no longer produces an unbelievable ratio: the
    // mean's denominator IS the registered grid (see CoverageReport.OverallCoverageRatio), so an
    // unassigned role drags the number down instead of vanishing from it. Reporting `null` for a
    // partly-assigned grid was the alternative and was rejected — it would make an outage
    // (53 of 54 roles recording nothing) render as *unmeasurable*, indistinguishable from a
    // weekend or a fresh deployment, which is the same absence-as-health failure this whole report
    // keeps being bitten by, one level over. The grid's completeness is reported as a number
    // instead: CoverageBasis.RegisteredNodes vs AssignedNodes, and NodeCoverage.IsAssigned per row.
}

/// <summary>
/// Per-conId minute coverage over the window's expected session minutes, for a conId
/// <c>research.node_assignments</c> knows nothing about (the core underlyings — SPX, VIX, SPY —
/// which are single, never-rotating instruments, not registered nodes). The whole window is the
/// correct denominator for these because there is no assignment interval to narrow it to.
/// </summary>
/// <remarks>
/// Not to be confused with an UNASSIGNED NODE (<see cref="NodeCoverage.IsAssigned"/> false), which is
/// the opposite shape: a registered role with no conId, rather than a conId with no registered role.
/// This type is populated from conIds that ticked, so it can never report one that is entirely dead —
/// which is exactly why the option nodes are enumerated from a registry instead, and why a dead core
/// underlying remains a known residual.
/// </remarks>
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
/// <param name="IsAssigned">
/// Whether ANY conId held this role during the window. False is a first-class, reportable state, not
/// an absent row: <c>research.option_nodes</c> defines the 54 roles that ought to be recorded and
/// <c>NodeSelector.BootstrapAssignmentsAsync</c> has three <c>continue</c> paths that leave one
/// unassigned (an empty candidate list skips a whole 9-node DTE bucket, no best strike, an
/// unresolved conId). Building the expected set from <c>node_assignments</c> made those roles
/// disappear from the report entirely — see <see cref="CoverageReport.OverallCoverageRatio"/> for
/// what that did to the headline number.
/// </param>
/// <param name="CoverageRatio">
/// How well this node's ASSIGNMENT was recorded: minutes with data over the session minutes it was
/// actually assigned for. NULL under the same zero-denominator rule as
/// <see cref="NodeConIdSegment.CoverageRatio"/>, which an unassigned node always hits — 0/0 is
/// unmeasured, not 0%, and "no conId was ever chosen for this role" is a different (and more
/// actionable) failure than "the chosen conId streamed nothing". Use
/// <paramref name="GridCoverageRatio"/> to ask the grid-level question instead.
/// </param>
/// <param name="GridCoverageRatio">
/// How much of the WINDOW's expected session minutes this role recorded — the same numerator over
/// the whole window's denominator rather than over the assignment tenure. Never null: the role was
/// expected to be assigned and recording for the entire window, so its expectation exists whether or
/// not an assignment does. This, not <paramref name="CoverageRatio"/>, is what
/// <see cref="CoverageReport.OverallCoverageRatio"/> averages, and it is reported per row so the
/// headline figure can be re-derived from the table rather than taken on trust. It also exposes the
/// continuous version of the unassigned case: a node assigned for the last ten minutes of a session
/// and perfect in them reads 100% here on <paramref name="CoverageRatio"/> and ~1% on this one.
/// </param>
public sealed record NodeCoverage(
    short NodeId,
    string Role,
    bool IsAssigned,
    int MinutesWithData,
    int TotalMinutes,
    double? CoverageRatio,
    double GridCoverageRatio,
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
/// a shrinking denominator makes coverage look BETTER. What agreement between the two does and does
/// not prove is on <see cref="CoverageMonitor"/>'s remarks — it is narrower than it reads.
/// </param>
/// <param name="RegisteredNodes">
/// Rows in <c>research.option_nodes</c> — the roles that ought to be recorded, and the denominator
/// of the grid the 95% gate is about. Reported because <see cref="CoverageReport.PerNode"/> having
/// 54 rows is only meaningful against the number that ought to be there: if the registry itself were
/// ever emptied, every node row would vanish and the mean would fall back to whatever conIds happened
/// to be ticking, which is this same defect one level higher again. The registry has no runtime
/// writer (migration 003 seeds it and nothing else touches it), so this is a tripwire, not a
/// suspicion.
/// </param>
/// <param name="AssignedNodes">
/// How many of <paramref name="RegisteredNodes"/> had a conId at any point in the window. Below
/// <paramref name="RegisteredNodes"/> means the grid is not fully assigned and the headline ratio is
/// being dragged down by roles that recorded nothing at all — which is the honest reading, not a
/// reporting artefact.
/// </param>
public sealed record CoverageBasis(
    string Status,
    IReadOnlyList<string> Calendars,
    int ExpectedMinutes,
    int PersistedSessions,
    int GeneratedSessions,
    int RegisteredNodes,
    int AssignedNodes,
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
/// <param name="PerNode">
/// One row per row of <c>research.option_nodes</c> — the registered grid — whether or not it has an
/// assignment. Never a subset built from <c>node_assignments</c>: see
/// <see cref="NodeCoverage.IsAssigned"/>.
/// </param>
/// <param name="OverallCoverageRatio">
/// NULL whenever <see cref="CoverageBasis.Status"/> is not
/// <see cref="CoverageBasisStatus.Measured"/> — an unmeasurable window reports no number rather than
/// a plausible one. A weekend is not 0% covered and an unsynced calendar is not 100% covered; both
/// were previously indistinguishable from a real reading.
/// <para>
/// The unweighted mean is taken over <see cref="NodeCoverage.GridCoverageRatio"/> for EVERY
/// registered node plus every <see cref="ConIdCoverage"/> in <paramref name="UnassignedConIds"/>.
/// The two words doing the work are "every registered": the mean's denominator is the grid, not the
/// rows that happened to be measurable. Averaging only measurable rows — the previous rule, combined
/// with an expected set built from <c>node_assignments</c> — meant a role with no assignment was not
/// counted as a miss but removed from the question, so ONE assignment surviving while 53 vanished
/// reported <b>100%</b> for the same outage that reads 1.85% when all 54 are assigned and 53 of them
/// are dead. Losing an entire SPX DTE bucket to one failed chain call moved the reported number UP,
/// and the ≥95% gate passed while nothing was being recorded. The invariant that rules that out, and
/// the one to preserve in any future change here: <b>removing a node's assignment must never raise
/// this number.</b> It cannot now, because the numerator can only shrink while the denominator is
/// fixed by the registry.
/// </para>
/// <para>
/// Still NULL when nothing is being measured at all — no node assigned anywhere in the window and no
/// conId ticking. That is a fresh deployment or a window predating the recorder's first lease, and
/// reporting it as 0% says "every instrument is dead" (the loudest possible alarm) about a
/// non-problem; a gate that cries wolf is a gate nobody reads. The distinction from the case above is
/// exactly whether ANY evidence of recording exists in the window: one assigned node is evidence, and
/// then the other 53 roles are a genuine shortfall rather than an absence of measurement.
/// </para>
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
/// are compared, boundary for boundary and <c>generator_version</c> for <c>generator_version</c>,
/// against what <see cref="ISessionClock"/> generates for the same window; on any disagreement the
/// report refuses to produce a ratio and says <see cref="CoverageBasisStatus.SessionsOutOfSync"/>
/// instead.
/// </para>
/// <para>
/// <b>Be precise about what that reconciliation proves, because it is narrower than it reads.</b> It
/// proves the persisted table matches <i>what this build's generator produces for the same window</i>
/// — nothing more. It is NOT an independent witness that the calendar is right. Earlier wording here
/// (and in <c>docs/STATE.md</c>) claimed independence on the grounds that the clock never reads the
/// table. Table-independence is real; witness-independence is not: <c>SessionGenerator</c> is a
/// singleton, and both sides of the comparison — the rows <c>SessionCalendarService</c> wrote and the
/// sessions <c>SessionClock</c> generates — come from the same instance and the same memoised cache.
/// A wrong calendar entry is therefore certified rather than caught, and that is not hypothetical:
/// <c>CME_ES</c> emits no session at all on US holidays Globex actually trades, so both sets are
/// empty for that date, <see cref="SessionMinutes.Matches"/> returns true, and the window reports
/// <see cref="CoverageBasisStatus.Measured"/> while omitting a whole trading day. So: a green basis
/// means the table has not drifted from the generator (stale rows, a partial sync, a hand edit, an
/// older <c>generator_version</c>) and says nothing about whether the generator is right.
/// </para>
/// <para>
/// <b>Cheap genuine independence was looked for and not adopted.</b> Re-constructing a second
/// <c>SessionGenerator</c> instead of using the injected one buys only a second cache over identical
/// code, which witnesses nothing about the calendar data. The one real oracle already in the database
/// is the recorded data itself: minutes carrying ticks that fall OUTSIDE every session are evidence
/// the calendar is missing a session, and that check would have caught the <c>CME_ES</c> holiday.
/// It is deliberately not built here because out-of-session ticks are legitimately routine — TWS
/// delivers stale snapshots between the GTH close and the RTH open, and this file has a test pinning
/// that those must not count — so turning the signal into an alarm needs a sustained-block-across-many-conIds
/// threshold, i.e. a heuristic with its own false-alarm budget. That is a Phase 3 piece of work with
/// a design of its own, not a line in the coverage query.
/// </para>
/// <para>
/// <b>The expected set of nodes comes from <c>research.option_nodes</c>, never from
/// <c>node_assignments</c>.</b> Two rounds of the same defect landed here. First: a plain
/// <c>GROUP BY con_id</c> over the raw event tables cannot produce a row for a conId that never
/// ticked, so a fully-dead subscription — the worst case this whole report exists to catch — was
/// invisible instead of showing 0%. That was fixed by unioning tick counts with the assignment rows.
/// It was not fixed one level up: the assignment rows were still the definition of "expected", and
/// <c>NodeSelector</c> can leave a registered role with no assignment row at all (an empty candidate
/// list skips a whole 9-node DTE bucket), so the role vanished from the report — and because the
/// overall ratio averaged only the rows present, <b>losing 53 of 54 assignments moved the reported
/// number from 1.85% to 100%</b>. The registry is now the expected set and the assignments are LEFT
/// JOINed onto it, so an unassigned role is a visible row with no ratio and a zero contribution to
/// the grid mean. The general lesson, third time it has been paid for in this file: whatever table
/// defines "what should exist" must be the FROM, not the JOIN.
/// </para>
/// <para>
/// Core underlyings (SPX/VIX/SPY) are not tracked in <c>node_assignments</c> or
/// <c>option_nodes</c>, so a fully-dead underlying subscription still produces no row rather than a
/// 0% one; that gap is narrower (three fixed, easily resolved symbols vs. up to 54 option nodes) and
/// left as a known residual — it needs a registry of expected underlyings to be the FROM of, which
/// does not exist yet.
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

        var persistedRows = await QueryOverlappingSessionsAsync(connection, calendars, windowFrom, windowTo, cancellationToken);
        var persisted = persistedRows.Select(row => row.Session).ToArray();
        var gaps = await QueryGapsAsync(connection, windowFrom, windowTo, cancellationToken);

        // A row written by an older generator whose boundaries happen to be identical passes
        // Matches (which compares boundaries, not provenance) but is reported as `mismatched` by
        // GET /research/sessions, which does compare generator_version. Two surfaces disagreeing
        // about the same row is its own small defect: the operator who goes to the calendar page
        // because coverage looks fine finds rows flagged there, or vice versa. Compared here too,
        // so the two agree on what "in sync" means.
        var staleVersions = persistedRows
            .Where(row => row.GeneratorVersion != SessionGenerator.GeneratorVersion)
            .ToArray();

        if (staleVersions.Length > 0)
        {
            logger.LogError(
                "{Stale} of {Total} research.sessions rows over {From:O}..{To:O} were written by a different " +
                "generator version (expected {Expected}). Coverage cannot be measured until the calendar is " +
                "re-synced.",
                staleVersions.Length, persistedRows.Count, windowFrom, windowTo, SessionGenerator.GeneratorVersion);

            return Rejected(
                windowFrom, windowTo, calendars, CoverageBasisStatus.SessionsOutOfSync,
                $"{staleVersions.Length} of {persistedRows.Count} persisted session(s) carry a generator_version " +
                $"other than {SessionGenerator.GeneratorVersion}. GET /research/sessions names the offending " +
                "rows; SessionCalendarSynchronizer re-materialises them on startup and on its resync timer.",
                persisted,
                generated.Count,
                gaps);
        }

        if (!SessionMinutes.Matches(persisted, generated))
        {
            logger.LogError(
                "research.sessions disagrees with the session generator over {From:O}..{To:O}: {Persisted} " +
                "persisted session(s) vs {Generated} generated. Coverage cannot be measured until the " +
                "calendar is re-synced — a missing session row shrinks the denominator and inflates coverage.",
                windowFrom, windowTo, persisted.Length, generated.Count);

            return Rejected(
                windowFrom, windowTo, calendars, CoverageBasisStatus.SessionsOutOfSync,
                $"{persisted.Length} persisted session(s) vs {generated.Count} generated. " +
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

        // The registered grid, with every node_assignments row whose tenure overlaps the window
        // LEFT JOINed onto it — a rotated node contributes one row per conId that held it, and a
        // node nothing ever assigned contributes a row with no conId rather than no row at all.
        var registered = await QueryRegisteredNodesAsync(connection, windowFrom, windowTo, cancellationToken);
        var registeredNodes = registered.RegisteredNodes;
        var assignments = registered.Assignments;

        if (registeredNodes.Count == 0)
        {
            // Cannot happen through any code path that exists: migration 003 seeds the 54 roles and
            // nothing writes research.option_nodes at runtime. Logged rather than assumed away
            // because the whole point of this fix is that "the table that says what should exist was
            // empty" is precisely how a report ends up describing nothing and calling it health.
            logger.LogError(
                "research.option_nodes is empty. Coverage has no registered grid to measure against, so the " +
                "overall ratio describes only whatever conIds happened to tick.");
        }

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

        var segmentsByNode = clippedSegments.ToLookup(segment => segment.Assignment.NodeId);

        // Iterated over the REGISTRY, not over the segments: a registered role with no segment must
        // produce a row, and a GroupBy over segments structurally cannot emit one.
        var perNode = registeredNodes
            .Select(node =>
            {
                var conIdSegments = segmentsByNode[node.NodeId]
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
                    node.NodeId,
                    node.Role,
                    conIdSegments.Length > 0,
                    nodeMinutesWithData,
                    nodeTotalMinutes,
                    nodeTotalMinutes == 0 ? null : (double)nodeMinutesWithData / nodeTotalMinutes,
                    // expectedMinutes is > 0 here (the zero case returned NoSessionInWindow above),
                    // and the numerator counts only in-session minutes inside the node's own tenure,
                    // so this is bounded by 1 by construction — the same numerator-inside-denominator
                    // guarantee the per-segment ratios have, widened to the window.
                    (double)nodeMinutesWithData / expectedMinutes,
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

        // EVERY registered node contributes, measured against the whole window rather than against
        // its own assignment tenure. Averaging only the nodes with a tenure to measure — which is
        // what "skip the null ratios" amounted to once the expected set came from node_assignments —
        // let an unassigned role leave the question instead of failing it, so 1 assigned + 53
        // missing scored 100% on the same outage that scores 1.85% with all 54 assigned and dead.
        // With the registry fixing the denominator, dropping an assignment can only remove minutes
        // from a numerator: fewer recorded nodes can no longer produce a higher number.
        var assignedNodes = perNode.Count(node => node.IsAssigned);

        var ratios = perNode
            .Select(node => node.GridCoverageRatio)
            .Concat(unassignedConIds.Select(row => row.CoverageRatio))
            .ToArray();

        // ...but "the grid recorded nothing" and "nothing is being recorded at all" are different
        // claims, and only the first is a measurement. With no assignment anywhere in the window and
        // no conId ticking there is no evidence of a recorder to report on — a fresh deployment, or
        // any window predating the first lease — and 54 structural zeros would average to a
        // fabricated 0%, saying "every instrument is dead" about a non-problem. A gate that cries
        // wolf is a gate nobody reads (the same reasoning as the weekend case above, and why the
        // field is nullable). One assigned node is enough evidence to make the other 53 a genuine
        // shortfall rather than an absence of measurement.
        var somethingIsMeasured = assignedNodes > 0 || unassignedConIds.Length > 0;

        var basis = new CoverageBasis(
            CoverageBasisStatus.Measured,
            calendars,
            expectedMinutes,
            persisted.Length,
            generated.Count,
            registeredNodes.Count,
            assignedNodes,
            sessions,
            null);

        return new CoverageReport(
            windowFrom,
            windowTo,
            basis,
            perNode,
            unassignedConIds,
            somethingIsMeasured ? ratios.Average() : null,
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
            // RegisteredNodes/AssignedNodes are 0 because a rejected report never got as far as
            // reading research.option_nodes — consistent with PerNode being empty here. They are not
            // a claim that the grid is empty; the status and detail say why nothing was measured.
            new CoverageBasis(
                status, calendars, expectedMinutes, persisted?.Count ?? 0, generated, 0, 0, sessions, detail),
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

    /// <summary>
    /// A persisted session row with the provenance <see cref="SessionMinutes.Matches"/> cannot see:
    /// two rows can agree on every boundary and still have been written by different generator
    /// versions, which <c>GET /research/sessions</c> reports as <c>mismatched</c>.
    /// </summary>
    private sealed record PersistedCoverageSession(TradingSession Session, short GeneratorVersion);

    private static async Task<IReadOnlyList<PersistedCoverageSession>> QueryOverlappingSessionsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> calendars,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // Half-open overlap, matching SessionClock's containment rule: a session touching the window
        // at exactly one endpoint contributes no minutes and is not a session "in" the window.
        await using var command = new NpgsqlCommand(
            "SELECT session_id, calendar, trading_date, open_utc, close_utc, label, is_half_day, generator_version " +
            "FROM research.sessions " +
            "WHERE calendar = ANY($1) AND open_utc < $3 AND close_utc > $2 " +
            "ORDER BY open_utc, calendar, label",
            connection);
        command.Parameters.AddWithValue(calendars.ToArray());
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var sessions = new List<PersistedCoverageSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new PersistedCoverageSession(
                new TradingSession(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateOnly>(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetString(5),
                    reader.GetBoolean(6)),
                reader.GetInt16(7)));
        }

        return sessions;
    }

    /// <summary>One <c>research.node_assignments</c> row whose tenure overlaps the report window.</summary>
    private sealed record NodeAssignmentInterval(
        short NodeId, string Role, int ConId, DateTimeOffset AssignedFrom, DateTimeOffset? AssignedTo);

    /// <summary>One registered role, whether or not anything has ever been assigned to it.</summary>
    private sealed record RegisteredNode(short NodeId, string Role);

    /// <summary>
    /// The registered grid, and the assignments that overlap [<paramref name="from"/>,
    /// <paramref name="to"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The FROM is <c>research.option_nodes</c> and the assignments are LEFT JOINed onto it</b>,
    /// which is the whole point of this method rather than an implementation detail. The previous
    /// version had it the other way round — <c>FROM node_assignments JOIN option_nodes</c> — so a
    /// registered role with no assignment row produced no row here, no row in
    /// <see cref="CoverageReport.PerNode"/>, and therefore nothing for the overall ratio to average.
    /// <c>NodeSelector.BootstrapAssignmentsAsync</c> has three <c>continue</c> paths that leave a
    /// node unassigned (one of them skips an entire 9-node DTE bucket on a single failed chain call),
    /// so this is a routine outcome, not a corrupt-database hypothetical, and it made the reported
    /// coverage go UP as recording went down. The join predicate has to sit in the <c>ON</c> clause,
    /// not a <c>WHERE</c>, or the outer join silently degenerates back into an inner one — the same
    /// bug wearing different syntax.
    /// </para>
    /// <para>
    /// The window filter is a proper interval overlap (<c>assigned_from &lt; to AND (assigned_to IS
    /// NULL OR assigned_to &gt; from)</c>), the same shape as the session and gap overlap queries, so
    /// a rotated node yields one row per conId that held it during the window and nothing for tenures
    /// outside it. An earlier version queried only the current (<c>assigned_to IS NULL</c>) row with
    /// no window filter at all, so a node reassigned any time before "now" was measured as if it had
    /// held the assignment for the whole window.
    /// </para>
    /// </remarks>
    private static async Task<(IReadOnlyList<RegisteredNode> RegisteredNodes, IReadOnlyList<NodeAssignmentInterval> Assignments)>
        QueryRegisteredNodesAsync(
            NpgsqlConnection connection, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT n.node_id, n.role, na.con_id, na.assigned_from, na.assigned_to " +
            "FROM research.option_nodes n " +
            "LEFT JOIN research.node_assignments na " +
            "    ON na.node_id = n.node_id " +
            "   AND na.assigned_from < $2 " +
            "   AND (na.assigned_to IS NULL OR na.assigned_to > $1) " +
            "ORDER BY n.node_id, na.assigned_from",
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var nodes = new List<RegisteredNode>();
        var assignments = new List<NodeAssignmentInterval>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetInt16(0);
            var role = reader.GetString(1);

            // Ordered by node_id, so one node's rows are contiguous; a node with several assignments
            // appears several times and must still be counted as one registered role.
            if (nodes.Count == 0 || nodes[^1].NodeId != nodeId)
            {
                nodes.Add(new RegisteredNode(nodeId, role));
            }

            // NULL con_id is the outer join's "no assignment overlapping this window" — the row
            // exists to carry the registered role, not an assignment.
            if (reader.IsDBNull(2))
            {
                continue;
            }

            assignments.Add(new NodeAssignmentInterval(
                nodeId,
                role,
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return (nodes, assignments);
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
