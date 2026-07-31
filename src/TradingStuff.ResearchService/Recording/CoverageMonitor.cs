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
    /// <c>OverallCoverageRatio</c> is an unweighted mean across all reporting conIds, so they pull it
    /// down by a couple of points. This is not a regression — the old wall-clock denominator was worse
    /// for every instrument — but it means the 95% gate should be read against the option-node rows
    /// until per-conId calendars exist. Doing that properly needs a conId → instrument → calendar
    /// mapping, and the core underlyings are not in <c>node_assignments</c> to hang one off.
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

/// <summary>Per-conId minute coverage over the window's expected session minutes.</summary>
public sealed record ConIdCoverage(int ConId, int MinutesWithData, int TotalMinutes, double CoverageRatio);

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
/// were previously indistinguishable from a real reading.
/// </param>
public sealed record CoverageReport(
    DateTimeOffset From,
    DateTimeOffset To,
    CoverageBasis Basis,
    IReadOnlyList<ConIdCoverage> PerConId,
    double? OverallCoverageRatio,
    int TotalMinutes,
    IReadOnlyList<RecorderGapSummary> Gaps);

/// <summary>
/// Computes recording coverage from the raw event tables against the exchange sessions in
/// <c>research.sessions</c>: the fraction of the window's expected session minutes that carry at
/// least one recorded tick, per conId and overall.
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
/// A conId is included even when it has ZERO ticks in the window, as long as it is a current
/// option-node assignment: a plain <c>GROUP BY con_id</c> over the raw event tables cannot produce
/// a row for a conId that never ticked, which would make a fully-dead subscription — the worst
/// case this whole report exists to catch — invisible instead of showing 0%. Core underlyings
/// (SPX/VIX/SPY) are not tracked in <c>node_assignments</c>, so a fully-dead underlying
/// subscription is not yet covered by this check; that gap is narrower (three fixed, easily
/// resolved symbols vs. up to 54 option nodes) and left as a known residual for now.
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

        var minutesByConId = await QueryMinuteCoverageAsync(connection, windowFrom, windowTo, sessions, cancellationToken);
        var expectedConIds = await QueryExpectedConIdsAsync(connection, cancellationToken);

        // Every conId with ticks, UNION every conId currently expected to be recording — so a node
        // with zero ticks (total recording failure) still gets a 0% row instead of being absent.
        var allConIds = new HashSet<int>(minutesByConId.Keys);
        allConIds.UnionWith(expectedConIds);

        var perConId = allConIds
            .Select(conId =>
            {
                var minutes = minutesByConId.GetValueOrDefault(conId);
                return new ConIdCoverage(conId, minutes, expectedMinutes, (double)minutes / expectedMinutes);
            })
            .OrderBy(row => row.ConId)
            .ToArray();

        var basis = new CoverageBasis(
            CoverageBasisStatus.Measured, calendars, expectedMinutes, persisted.Count, generated.Count, sessions, null);

        return new CoverageReport(
            windowFrom,
            windowTo,
            basis,
            perConId,
            perConId.Length == 0 ? 0d : perConId.Average(row => row.CoverageRatio),
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

    private static async Task<List<int>> QueryExpectedConIdsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT con_id FROM research.node_assignments WHERE assigned_to IS NULL", connection);

        var conIds = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            conIds.Add(reader.GetInt32(0));
        }

        return conIds;
    }

    /// <summary>Distinct in-session minutes carrying at least one tick, per conId, across both raw event tables.</summary>
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
