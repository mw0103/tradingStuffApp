using Npgsql;

namespace TradingStuff.ResearchService.Recording;

/// <summary>Per-conId minute coverage over the requested window.</summary>
public sealed record ConIdCoverage(int ConId, int MinutesWithData, int TotalMinutes, double CoverageRatio);

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
public sealed record CoverageReport(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ConIdCoverage> PerConId,
    double OverallCoverageRatio,
    int TotalMinutes,
    IReadOnlyList<RecorderGapSummary> Gaps);

/// <summary>
/// Computes recording coverage directly from the raw event tables: the fraction of one-minute
/// buckets in a window that have at least one recorded tick, per conId and overall.
/// </summary>
/// <remarks>
/// v1 measures a fixed UTC window (defaults to the trailing 24h) rather than an exchange session —
/// <c>SessionCalendarService</c> (Phase 2) will let this align to actual RTH/GTH boundaries.
/// Coverage measured this way is a conservative proxy in the meantime: it counts quiet minutes with
/// genuinely no trading activity the same as minutes with no subscription at all, so real coverage
/// during active hours is at least as good as what this reports.
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
public sealed class CoverageMonitor(IConfiguration configuration, ILogger<CoverageMonitor> logger)
{
    public async Task<CoverageReport> GetCoverageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; coverage cannot be computed.");
            return new CoverageReport(from, to, [], 0, 0, []);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var optionMinutes = await QueryMinuteCoverageAsync(connection, "gateway.option_quote_events", from, to, cancellationToken);
        var underlyingMinutes = await QueryMinuteCoverageAsync(connection, "gateway.underlying_tick_events", from, to, cancellationToken);
        var expectedConIds = await QueryExpectedConIdsAsync(connection, cancellationToken);

        var totalMinutes = Math.Max(1, (int)Math.Ceiling((to - from).TotalMinutes));
        var minutesByConId = new Dictionary<int, int>();

        foreach (var row in optionMinutes.Concat(underlyingMinutes))
        {
            minutesByConId[row.ConId] = row.Minutes;
        }

        // Every conId with ticks, UNION every conId currently expected to be recording — so a node
        // with zero ticks (total recording failure) still gets a 0% row instead of being absent.
        var allConIds = new HashSet<int>(minutesByConId.Keys);
        allConIds.UnionWith(expectedConIds);

        var perConId = allConIds
            .Select(conId =>
            {
                var minutes = minutesByConId.GetValueOrDefault(conId);
                return new ConIdCoverage(conId, minutes, totalMinutes, (double)minutes / totalMinutes);
            })
            .OrderBy(row => row.ConId)
            .ToArray();

        var overall = perConId.Length == 0 ? 0d : perConId.Average(row => row.CoverageRatio);
        var gaps = await QueryGapsAsync(connection, from, to, cancellationToken);

        return new CoverageReport(from, to, perConId, overall, totalMinutes, gaps);
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

    private static async Task<List<(int ConId, int Minutes)>> QueryMinuteCoverageAsync(
        NpgsqlConnection connection, string table, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // `table` is always one of two fixed literals passed by this class, never external input.
        var sql =
            $"SELECT con_id, count(DISTINCT date_trunc('minute', observed_at)) " +
            $"FROM {table} WHERE observed_at >= $1 AND observed_at < $2 GROUP BY con_id";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var rows = new List<(int, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt32(0), (int)reader.GetInt64(1)));
        }

        return rows;
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
