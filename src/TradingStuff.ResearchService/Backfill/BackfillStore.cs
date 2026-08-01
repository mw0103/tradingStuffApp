using Npgsql;
using NpgsqlTypes;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>
/// One <c>research.backfill_requests</c> row reduced to what <c>GapDetector</c> needs: enough to
/// derive its nominal [start, end) window and to explain a shortfall found under it. Deliberately not
/// <see cref="ClaimedSlice"/> — that record carries claim/lease fields no read-only report should
/// touch, and this one omits <c>request_id</c> because gap analysis never writes back to a row.
/// </summary>
public sealed record BackfillRequestWindowRow(DateTimeOffset EndTimeUtc, string Duration, string State, int Attempts);

/// <summary>A slice this coordinator instance currently owns the lease on.</summary>
public sealed record ClaimedSlice(
    long RequestId,
    long JobId,
    int ConId,
    DateTimeOffset EndTimeUtc,
    string Duration,
    string WhatToShow,
    string BarSize,
    bool UseRth,
    int Attempts);

/// <summary>A canonical instrument row, which is where a request's contract shape comes from.</summary>
public sealed record InstrumentRow(
    short InstrumentId, string Symbol, string Kind, string? OptionTradingClass, string Exchange, string Currency)
{
    /// <summary>Maps <c>research.instruments.kind</c> onto a TWS security type.</summary>
    public string SecType => Kind switch
    {
        "index" => "IND",
        "stock" => "STK",
        "option_class" => "OPT",
        "future_family" => "FUT",
        _ => "STK",
    };

    /// <summary>
    /// The contract to request history for, given a conId the caller already knows.
    /// </summary>
    /// <remarks>
    /// Built from the instrument row plus the request's own conId rather than from a per-job
    /// hard-coded template, so a job whose slices come from somewhere else entirely — the ES
    /// per-expired-contract walk of package 2e, which inserts one request row per rolled contract —
    /// is drained by this coordinator with no code change here. <c>IncludeExpired</c> follows from
    /// the security type for the same reason: an expired future is exactly what that walk requests,
    /// and it is harmless on a live one.
    /// </remarks>
    public HistoricalContractSpecDto ContractFor(int conId) => new(
        Symbol,
        SecType,
        Exchange,
        Currency,
        TradingClass: OptionTradingClass,
        ConId: conId,
        IncludeExpired: SecType == "FUT");
}

/// <summary>
/// Per-job progress, derived entirely from <c>research.backfill_requests</c>.
/// </summary>
/// <param name="BarsLanded">
/// Rows this job's requests actually inserted into <c>research.bars</c>. This is the honest figure
/// and the one to render: overlap is designed into three separate places in this pipeline (the
/// leading historical slice, the 4x top-up window, the daily forward re-request), so it is strictly
/// less than <paramref name="BarsReturned"/> by an amount no reader can infer.
/// </param>
/// <param name="BarsReturned">
/// Bars TWS handed back, summed BEFORE <c>research.bars</c>' primary key deduplicated them. Kept
/// alongside rather than dropped because the ratio between the two is the only visible measure of
/// how much of the paced request budget is being spent re-fetching bars already held — but it must
/// never be presented as a count of data owned.
/// </param>
/// <param name="PercentComplete">
/// Fraction of this job's slices with a CONFIRMED outcome: succeeded, empty (TWS says there is
/// nothing there), or permanent (TWS says retrying cannot help). Exhausted slices are deliberately
/// NOT in the numerator even though <see cref="IsJobSettledAsync"/> counts them as settled — an
/// exhausted slice is a hole with no explanation behind it, and a progress bar that reaches 100%
/// over one is the exact misreading this figure exists to prevent. Consequently a job with a single
/// dead slice sits just short of 1.0 forever, which is the truth about it; whether that state is
/// final is what <see cref="Status"/> (<c>complete_with_gaps</c>) answers.
/// </param>
public sealed record BackfillJobStatus(
    long JobId,
    string Name,
    string Kind,
    short InstrumentId,
    int? ConId,
    string WhatToShow,
    string BarSize,
    bool UseRth,
    DateTimeOffset TargetFrom,
    DateTimeOffset TargetTo,
    int Priority,
    string Status,
    int TotalSlices,
    int PendingCount,
    int InflightCount,
    int SucceededCount,
    int EmptyCount,
    int RetryableCount,
    int ExhaustedCount,
    int PermanentCount,
    int NowAnchoredCount,
    long BarsLanded,
    long BarsReturned,
    double PercentComplete,
    DateTimeOffset? LowWaterMarkUtc,
    DateTimeOffset? HighWaterMarkUtc,
    DateTimeOffset? EarliestLeaseExpiry);

/// <summary>What <c>GET /research/backfill</c> answers with.</summary>
public sealed record BackfillStatusReport(
    bool Enabled, string OwnerId, int MaxAttempts, IReadOnlyList<BackfillJobStatus> Jobs);

/// <summary>
/// Every Postgres interaction the backfill coordinator makes: job upkeep, slice planning writes,
/// race-safe claiming, lease reclamation, bar landing, and status derivation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claiming is engine-enforced, not coordinated in application code.</b> The Phase 1 review
/// disproved <c>SELECT ... FOR UPDATE</c> followed by a write against live Postgres 17: under Read
/// Committed a blocked <c>FOR UPDATE</c> re-checks its WHERE against the row's NEW committed version
/// once the lock releases, and a row that no longer matches is silently excluded, so both callers
/// conclude "no row" and both write. Here that same re-check is harmless — a row excluded because it
/// is no longer <c>pending</c> was genuinely claimed by someone else — but the claim still never
/// relies on it, because <see cref="ClaimAsync"/> is a single <c>UPDATE ... RETURNING</c> whose
/// candidate subquery takes <c>FOR UPDATE ... SKIP LOCKED</c>. <c>SKIP LOCKED</c> never blocks, so
/// the "unblock and silently re-evaluate" window does not exist; a contended row is passed over
/// rather than contended for, and the decision and the write are the same statement in the same
/// transaction, so nothing can change between them.
/// </para>
/// <para>
/// The consequence of getting this wrong is what makes it worth the care: the losing claimer would
/// see zero pending rows, and "zero pending rows" is indistinguishable from "the job is done". A
/// double-claimed slice would not surface as a conflict; it would surface as a permanently
/// unfinished backfill reporting itself complete. For that reason completion is NEVER inferred from
/// an empty claim anywhere in this class — <see cref="GetStatusAsync"/> counts non-terminal rows
/// explicitly, and a job with no rows at all reports 0%, not silence.
/// </para>
/// </remarks>
public sealed class BackfillStore(IConfiguration configuration, ILogger<BackfillStore> logger)
{
    /// <summary>Every state a slice can rest in and never be attempted again (given the attempt cap).</summary>
    private const string TerminalStates = "'succeeded', 'empty', 'permanent'";

    /// <summary>
    /// Job statuses whose request rows are still eligible to be claimed and executed.
    /// </summary>
    /// <remarks>
    /// <c>complete_with_gaps</c> is in here on purpose, and it is the operator's way back into a job
    /// that has stalled on exhausted slices. Raising <c>Backfill:MaxAttempts</c> makes those rows
    /// claimable again by <see cref="ClaimAsync"/>'s <c>attempts &lt; $3</c> predicate — but while the
    /// terminal status was plain <c>complete</c>, the JOB was filtered out here and the newly-eligible
    /// rows were unreachable anyway. The job returns to <c>running</c> by itself on the next
    /// <see cref="RefreshJobStatusAsync"/>, because status is derived from the counts, not latched.
    /// <para>
    /// <c>complete</c> stays out: a job with no exhausted rows has nothing an attempt-cap change could
    /// reach, and re-admitting it would put every finished job back through the claim query forever.
    /// </para>
    /// </remarks>
    private const string ClaimableJobStatuses = "'pending', 'running', 'complete_with_gaps'";

    public string? ConnectionString => configuration.GetConnectionString("trading");

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    // ---- jobs -------------------------------------------------------------------------------

    /// <summary>
    /// Creates the job row if it is missing and refreshes only the fields that are safe to refresh.
    /// </summary>
    /// <remarks>
    /// <c>bar_size</c>, <c>what_to_show</c>, <c>use_rth</c>, and the target range are deliberately
    /// NOT updated on conflict. They define the slice grid, so quietly changing them on an existing
    /// job would re-plan it into a second, overlapping set of request rows that the idempotency key
    /// has no way to collapse — the job would look like it had doubled in size overnight. Changing
    /// them is an operator action against the row, taken knowingly.
    /// <para>
    /// A historical job's <c>target_to</c> defaults to the UTC midnight of its creation day and then
    /// never moves. That fixed far end is what every slice boundary is measured from, so it is also
    /// what makes lowering <c>target_from</c> later a pure extension of the existing sequence.
    /// </para>
    /// </remarks>
    public async Task<BackfillJob?> EnsureJobAsync(BackfillJobDefinition definition, int? conId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.backfill_jobs " +
            "  (name, instrument_id, con_id, what_to_show, bar_size, use_rth, target_from, target_to, " +
            "   priority, kind, slice_duration) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, COALESCE($8, date_trunc('day', now())), $9, $10, $11) " +
            "ON CONFLICT (name) DO UPDATE " +
            "  SET con_id = COALESCE(EXCLUDED.con_id, research.backfill_jobs.con_id), " +
            "      priority = EXCLUDED.priority, " +
            "      updated_at = now() " +
            "RETURNING job_id, name, instrument_id, con_id, what_to_show, bar_size, use_rth, " +
            "          target_from, target_to, priority, status, kind, slice_duration",
            connection);

        command.Parameters.AddWithValue(definition.Name);
        command.Parameters.AddWithValue(definition.InstrumentId);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)conId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.AddWithValue(definition.WhatToShow);
        command.Parameters.AddWithValue(definition.BarSize);
        command.Parameters.AddWithValue(definition.UseRth);
        command.Parameters.AddWithValue(definition.TargetFrom);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)definition.TargetTo ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
        });
        command.Parameters.AddWithValue(definition.Priority);
        command.Parameters.AddWithValue(definition.Kind);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)definition.SliceDuration ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    /// <summary>
    /// Jobs the coordinator should be working: not paused, not cleanly complete.
    /// </summary>
    /// <remarks>
    /// Deliberately the SAME status set <see cref="ClaimAsync"/> filters on. These two must agree:
    /// this list is what <c>BackfillCoordinator.ExecuteSliceAsync</c> rebuilds its job cache from, so
    /// a status that can be claimed but is missing here produces a claimed slice for an "unknown job"
    /// that is released and re-claimed on a loop.
    /// </remarks>
    public async Task<IReadOnlyList<BackfillJob>> GetActiveJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT job_id, name, instrument_id, con_id, what_to_show, bar_size, use_rth, " +
            "       target_from, target_to, priority, status, kind, slice_duration " +
            "FROM research.backfill_jobs WHERE status IN (" + ClaimableJobStatuses + ") " +
            "ORDER BY priority DESC, job_id",
            connection);

        var jobs = new List<BackfillJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    /// <returns>True when the status actually changed, so callers can log a transition rather than a tick.</returns>
    public async Task<bool> SetJobStatusAsync(long jobId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE research.backfill_jobs SET status = $2, updated_at = now() " +
            "WHERE job_id = $1 AND status <> $2",
            connection);
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(status);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<InstrumentRow?> GetInstrumentAsync(short instrumentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT instrument_id, symbol, kind, option_trading_class, exchange, currency " +
            "FROM research.instruments WHERE instrument_id = $1",
            connection);
        command.Parameters.AddWithValue(instrumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InstrumentRow(
            reader.GetInt16(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5));
    }

    // ---- planning ---------------------------------------------------------------------------

    /// <summary>
    /// Writes planned slices, returning how many were genuinely new.
    /// </summary>
    /// <remarks>
    /// <c>ON CONFLICT DO NOTHING</c> against the (job_id, con_id, end_time_utc, duration,
    /// what_to_show, bar_size, use_rth) key is the entire resumability story: re-planning an
    /// unchanged job re-derives the identical slices and lands zero rows. That guarantee is only as
    /// good as the planner's determinism, which is why <see cref="BackfillPlanner"/> never reads the
    /// clock for historical work — and why the integration test for this asserts on
    /// <c>research.backfill_requests</c> rather than on <c>research.bars</c>, whose primary key would
    /// mask a duplicated request entirely.
    /// </remarks>
    public async Task<int> InsertSlicesAsync(IReadOnlyList<BackfillSlice> slices, CancellationToken cancellationToken)
    {
        if (slices.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.backfill_requests " +
            "  (job_id, con_id, end_time_utc, duration, what_to_show, bar_size, use_rth) " +
            "SELECT $1, $2, t.end_time_utc, $3, $4, $5, $6 " +
            "FROM unnest($7::timestamptz[]) AS t(end_time_utc) " +
            "ON CONFLICT DO NOTHING",
            connection);

        var inserted = 0;

        // Grouped by everything but the end instant so one statement covers a whole job's plan.
        foreach (var group in slices.GroupBy(s => (s.JobId, s.ConId, s.Duration, s.WhatToShow, s.BarSize, s.UseRth)))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue(group.Key.JobId);
            command.Parameters.AddWithValue(group.Key.ConId);
            command.Parameters.AddWithValue(group.Key.Duration);
            command.Parameters.AddWithValue(group.Key.WhatToShow);
            command.Parameters.AddWithValue(group.Key.BarSize);
            command.Parameters.AddWithValue(group.Key.UseRth);
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = group.Where(s => s.EndTimeUtc.HasValue).Select(s => s.EndTimeUtc!.Value).ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
            });

            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return inserted;
    }

    // ---- claiming, leases, and reclamation ----------------------------------------------------

    /// <summary>
    /// Claims up to <paramref name="limit"/> slices for <paramref name="owner"/> in one statement.
    /// </summary>
    /// <remarks>
    /// See the class remarks for why this shape, and not a read followed by a write, is the only one
    /// that holds under Read Committed. Three details carry weight beyond the <c>SKIP LOCKED</c>:
    /// <list type="bullet">
    /// <item><c>attempts</c> is incremented AT CLAIM, not at outcome — a process that dies mid-flight
    /// must still burn an attempt, or a slice that reliably kills its coordinator is reclaimed and
    /// re-attempted forever.</item>
    /// <item><c>end_time_utc IS NOT NULL</c>: a NULL-anchored ("now") row cannot be turned back into
    /// a reproducible request, and executing one would resurrect exactly the top-up collision
    /// migration 005 resolves. Such rows are reported by <see cref="GetStatusAsync"/> instead of
    /// being silently skipped.</item>
    /// <item><c>COALESCE(completed_at, 'epoch')</c> on the retry-backoff comparison: a failed row that
    /// somehow lost its <c>completed_at</c> becomes immediately eligible rather than permanently
    /// unclaimable. Silent permanent ineligibility is the failure mode this whole file is written
    /// against.</item>
    /// </list>
    /// </remarks>
    public async Task<IReadOnlyList<ClaimedSlice>> ClaimAsync(
        string owner, TimeSpan lease, int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE research.backfill_requests AS r " +
            "SET state = 'inflight', " +
            "    claimed_by = $1, " +
            "    lease_expires_at = now() + make_interval(secs => $2), " +
            "    attempts = r.attempts + 1, " +
            "    requested_at = now() " +
            "FROM ( " +
            "    SELECT br.request_id " +
            "    FROM research.backfill_requests br " +
            "    JOIN research.backfill_jobs bj ON bj.job_id = br.job_id " +
            "    WHERE bj.status IN (" + ClaimableJobStatuses + ") " +
            "      AND br.end_time_utc IS NOT NULL " +
            "      AND br.end_time_utc <= now() " +
            "      AND br.attempts < $3 " +
            "      AND ( br.state = 'pending' " +
            "            OR ( br.state = 'failed' " +
            "                 AND COALESCE(br.completed_at, 'epoch'::timestamptz) < " +
            "                     now() - least(interval '30 minutes', " +
            "                                   interval '30 seconds' * power(2, br.attempts - 1)) ) ) " +
            "    ORDER BY bj.priority DESC, br.end_time_utc DESC " +
            "    LIMIT $4 " +
            "    FOR UPDATE OF br SKIP LOCKED " +
            ") AS candidate " +
            "WHERE r.request_id = candidate.request_id AND r.state IN ('pending', 'failed') " +
            "RETURNING r.request_id, r.job_id, r.con_id, r.end_time_utc, r.duration, " +
            "          r.what_to_show, r.bar_size, r.use_rth, r.attempts",
            connection);

        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(lease.TotalSeconds);
        command.Parameters.AddWithValue(maxAttempts);
        command.Parameters.AddWithValue(limit);

        var claimed = new List<ClaimedSlice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedSlice(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetInt32(8)));
        }

        return claimed;
    }

    /// <summary>
    /// Reclaims every <c>inflight</c> row whose lease has expired, returning how many.
    /// </summary>
    /// <remarks>
    /// This is the answer to the crash path migration 004 had no schema for. A coordinator that dies
    /// between claiming a slice and writing its outcome leaves the row <c>inflight</c> forever; no
    /// query distinguishes it from a request still legitimately in the air, so without a lease the
    /// slice is simply never retried and the hole it leaves is invisible.
    /// <para>
    /// Reclaimed rows land in <c>failed</c>, not back in <c>pending</c>, and keep their incremented
    /// <c>attempts</c>. Both details are deliberate: <c>failed</c> puts the row on the same
    /// exponential-backoff path as every other retryable outcome (one retry path, not two), and
    /// keeping the attempt means a slice whose processing reliably kills the coordinator exhausts
    /// the cap and stops rather than crash-looping the service forever. <c>completed_at</c> is
    /// stamped so the backoff has something to measure from.
    /// </para>
    /// </remarks>
    public async Task<int> ReclaimExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE research.backfill_requests AS r " +
            "SET state = 'failed', " +
            "    claimed_by = NULL, " +
            "    lease_expires_at = NULL, " +
            "    completed_at = now(), " +
            "    error_code = NULL, " +
            "    error_message = 'lease expired; reclaimed from ' || COALESCE(r.claimed_by, '(unknown owner)') " +
            "FROM ( " +
            "    SELECT request_id FROM research.backfill_requests " +
            "    WHERE state = 'inflight' AND lease_expires_at < now() " +
            "    FOR UPDATE SKIP LOCKED " +
            ") AS expired " +
            "WHERE r.request_id = expired.request_id AND r.state = 'inflight'",
            connection);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Puts a claimed slice back on the queue without consuming a retry — for outcomes where the
    /// request never reached TWS at all (pacing rejection, gateway not connected).
    /// </summary>
    /// <remarks>
    /// The attempt decrement undoes <see cref="ClaimAsync"/>'s increment. Without it a busy pacing
    /// window would walk every queued slice up to the attempt cap and retire the whole backlog for a
    /// reason that has nothing to do with the slices.
    /// </remarks>
    public Task<bool> ReleaseAsync(long requestId, string owner, CancellationToken cancellationToken) =>
        FinishAsync(
            requestId, owner,
            "SET state = 'pending', claimed_by = NULL, lease_expires_at = NULL, " +
            "    attempts = GREATEST(r.attempts - 1, 0), requested_at = NULL",
            [],
            cancellationToken);

    /// <summary>Records a terminal or retryable outcome that landed no bars.</summary>
    public Task<bool> MarkOutcomeAsync(
        long requestId, string owner, BackfillRequestState state, int? errorCode, string? errorMessage,
        CancellationToken cancellationToken) =>
        FinishAsync(
            requestId, owner,
            "SET state = $3, claimed_by = NULL, lease_expires_at = NULL, completed_at = now(), " +
            "    bars_returned = COALESCE(r.bars_returned, 0), bars_landed = COALESCE(r.bars_landed, 0), " +
            "    error_code = $4, error_message = $5",
            [
                new NpgsqlParameter { Value = StateName(state), NpgsqlDbType = NpgsqlDbType.Text },
                new NpgsqlParameter { Value = (object?)errorCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
                new NpgsqlParameter { Value = (object?)errorMessage ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            ],
            cancellationToken);

    /// <summary>
    /// Lands a slice's bars and marks the request succeeded, atomically.
    /// </summary>
    /// <returns>False when the lease was lost mid-flight; nothing is written in that case.</returns>
    /// <remarks>
    /// One transaction, deliberately. Splitting them admits a crash window in which the checkpoint
    /// says <c>succeeded</c> and the bars are not there — the one failure this table's whole design
    /// is meant to make impossible, because a succeeded slice is never re-requested.
    /// <para>
    /// If the completion UPDATE matches no row, this instance no longer owns the lease (its claim
    /// expired and a reaper took the row back) and the transaction is rolled back rather than
    /// force-written. Discarding the fetched bars costs one re-issued request; committing them under
    /// a <c>request_id</c> that another owner is about to re-run would leave <c>research.bars</c>
    /// rows whose lineage points at a request row that ultimately says <c>failed</c>.
    /// </para>
    /// </remarks>
    public async Task<bool> LandBarsAsync(
        ClaimedSlice slice,
        string owner,
        short instrumentId,
        IReadOnlyList<HistoricalBarDto> bars,
        string source,
        CancellationToken cancellationToken)
    {
        var timestamps = new List<DateTimeOffset>(bars.Count);
        var tradingDates = new List<DateOnly?>(bars.Count);
        var opens = new List<decimal>(bars.Count);
        var highs = new List<decimal>(bars.Count);
        var lows = new List<decimal>(bars.Count);
        var closes = new List<decimal>(bars.Count);
        var volumes = new List<decimal?>(bars.Count);
        var waps = new List<decimal?>(bars.Count);
        var counts = new List<int?>(bars.Count);

        foreach (var bar in bars)
        {
            // ts_utc is NOT NULL and is the partition key. A daily bar carries only a trading date
            // (TWS returns a bare yyyyMMdd even under formatDate=2), so its instant is that date's
            // UTC midnight and trading_date stays authoritative — exactly the split migration 004
            // documents. A bar with neither is unusable and is dropped loudly rather than guessed at.
            if (bar.Timestamp is { } instant)
            {
                timestamps.Add(instant.ToUniversalTime());
                tradingDates.Add(null);
            }
            else if (bar.TradingDate is { } date)
            {
                timestamps.Add(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
                tradingDates.Add(date);
            }
            else
            {
                logger.LogWarning(
                    "Dropping a bar with no timestamp and no trading date from request {RequestId}.", slice.RequestId);
                continue;
            }

            opens.Add(bar.Open);
            highs.Add(bar.High);
            lows.Add(bar.Low);
            closes.Add(bar.Close);

            // TWS reports -1 for "this instrument has no volume" (index TRADES bars). Storing that
            // as a real -1 would corrupt any later aggregate; NULL is what migration 004 reserves.
            volumes.Add(bar.Volume < 0 ? null : bar.Volume);
            waps.Add(bar.Wap < 0 ? null : bar.Wap);
            counts.Add(bar.Count < 0 ? null : bar.Count);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // How many of this request's bars were genuinely new, as opposed to how many TWS returned.
        // The INSERT's own row count is the exact answer and the only cheap one — research.bars has
        // no index on request_id — and the difference is not a rounding error: three separate parts
        // of this pipeline overlap on purpose, so summing bars_returned per job reports a job that
        // landed a quarter of its data as if it were full. See migration 009.
        var landedRows = 0;

        if (timestamps.Count > 0)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.bars " +
                "  (con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, trading_date, " +
                "   open, high, low, close, volume, wap, bar_count, source, request_id) " +
                "SELECT $1, $2, $3, $4, $5, t.ts_utc, t.trading_date, t.open, t.high, t.low, t.close, " +
                "       t.volume, t.wap, t.bar_count, $6, $7 " +
                "FROM unnest($8::timestamptz[], $9::date[], $10::numeric[], $11::numeric[], $12::numeric[], " +
                "            $13::numeric[], $14::numeric[], $15::numeric[], $16::integer[]) " +
                "     AS t(ts_utc, trading_date, open, high, low, close, volume, wap, bar_count) " +
                "ON CONFLICT (con_id, what_to_show, bar_size, use_rth, ts_utc) DO NOTHING",
                connection, transaction);

            insert.Parameters.AddWithValue(slice.ConId);
            insert.Parameters.AddWithValue(instrumentId);
            insert.Parameters.AddWithValue(slice.BarSize);
            insert.Parameters.AddWithValue(slice.WhatToShow);
            insert.Parameters.AddWithValue(slice.UseRth);
            insert.Parameters.AddWithValue(source);
            insert.Parameters.AddWithValue(slice.RequestId);
            AddArray(insert, timestamps.ToArray(), NpgsqlDbType.TimestampTz);
            AddArray(insert, tradingDates.ToArray(), NpgsqlDbType.Date);
            AddArray(insert, opens.ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, highs.ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, lows.ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, closes.ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, volumes.ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, waps.ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, counts.ToArray(), NpgsqlDbType.Integer);

            landedRows = await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var complete = new NpgsqlCommand(
            "UPDATE research.backfill_requests AS r " +
            "SET state = 'succeeded', claimed_by = NULL, lease_expires_at = NULL, completed_at = now(), " +
            "    bars_returned = $3, bars_landed = $6, first_bar_utc = $4, last_bar_utc = $5, " +
            "    error_code = NULL, error_message = NULL " +
            "WHERE r.request_id = $1 AND r.state = 'inflight' AND r.claimed_by = $2",
            connection, transaction);

        complete.Parameters.AddWithValue(slice.RequestId);
        complete.Parameters.AddWithValue(owner);
        complete.Parameters.AddWithValue(timestamps.Count);
        AddNullable(complete, timestamps.Count == 0 ? null : timestamps.Min(), NpgsqlDbType.TimestampTz);
        AddNullable(complete, timestamps.Count == 0 ? null : timestamps.Max(), NpgsqlDbType.TimestampTz);
        complete.Parameters.AddWithValue(landedRows);

        if (await complete.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Whether a slice reported empty sits between slices on the SAME contract that DID return data.
    /// </summary>
    /// <remarks>
    /// Defence against a known, unfixed upstream ambiguity: TWS raises error 162 both for "this
    /// query returned no data" and for some pacing violations, and the gateway's classifier
    /// distinguishes them by message text alone. A pacing 162 misread as no-data would retire a
    /// slice that has data, permanently and silently — the resulting hole is unrecoverable by any
    /// gap report, because the checkpoint says the slice was legitimately empty.
    /// <para>
    /// A confirmed-empty slice with data-bearing neighbours on BOTH sides is not proof of anything,
    /// but it is cheap suspicion: one indexed lookup on the leading columns of the table's existing
    /// uniqueness key. The coordinator spends one extra request on such a slice before accepting the
    /// verdict. On a first pass nothing is suspicious (the neighbours have not run yet), so this
    /// costs nothing until a re-walk, which is exactly when it matters.
    /// </para>
    /// </remarks>
    public async Task<bool> HasDataBearingNeighboursAsync(
        long jobId, int conId, DateTimeOffset endTimeUtc, TimeSpan proximity, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT " +
            "  count(*) FILTER (WHERE end_time_utc > $3 AND end_time_utc <= $4) AS newer, " +
            "  count(*) FILTER (WHERE end_time_utc < $3 AND end_time_utc >= $5) AS older " +
            "FROM research.backfill_requests " +
            "WHERE job_id = $1 AND con_id = $2 AND state = 'succeeded' AND COALESCE(bars_returned, 0) > 0 " +
            "  AND end_time_utc BETWEEN $5 AND $4",
            connection);

        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(conId);
        command.Parameters.AddWithValue(endTimeUtc);
        command.Parameters.AddWithValue(endTimeUtc + proximity);
        command.Parameters.AddWithValue(endTimeUtc - proximity);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) > 0 && reader.GetInt64(1) > 0;
    }

    // ---- head timestamps, cached in the capability registry -----------------------------------

    /// <summary>
    /// The most recently probed head timestamp for a probe key, and when it was probed.
    /// </summary>
    /// <remarks>
    /// Head timestamps live in <c>research.capability_probes</c> rather than a table of their own.
    /// That is what the registry is for — "every design decision leaning on a capability should point
    /// at the probe row that verified it" — and it means planning survives a gateway outage instead
    /// of stalling behind one paced request per instrument on every restart.
    /// </remarks>
    public async Task<(DateTimeOffset Head, DateTimeOffset ProbedAt)?> GetCachedHeadTimestampAsync(
        string probeKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT result ->> 'headTimestampUtc', ran_at FROM research.capability_probes " +
            "WHERE probe_key = $1 AND succeeded ORDER BY ran_at DESC LIMIT 1",
            connection);
        command.Parameters.AddWithValue(probeKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var head)
            ? (head.ToUniversalTime(), reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    /// <summary>
    /// The most recent head-timestamp probe for a key, including one that concluded there is no data.
    /// </summary>
    /// <remarks>
    /// A separate reader from <see cref="GetCachedHeadTimestampAsync"/> rather than a widened one, so
    /// the coordinator and the gap detector keep the two-state answer they were written against while
    /// <see cref="EsContractWalker"/> gets the three-state one it needs. The distinction it adds is
    /// <c>Head is null</c> on a row that EXISTS: TWS was asked and said there is no history here,
    /// which is a conclusion about the contract. No row at all means nobody has asked yet, which is
    /// not — and collapsing those two is what kept a listed-but-untraded ES quarter permanently in
    /// the "could not plan it this pass" bucket, re-probed every scan and holding its job open forever.
    /// </remarks>
    public async Task<(DateTimeOffset? Head, DateTimeOffset ProbedAt)?> GetCachedHeadProbeAsync(
        string probeKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT result ->> 'headTimestampUtc', ran_at FROM research.capability_probes " +
            "WHERE probe_key = $1 AND succeeded ORDER BY ran_at DESC LIMIT 1",
            connection);
        command.Parameters.AddWithValue(probeKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var probedAt = reader.GetFieldValue<DateTimeOffset>(1);

        if (reader.IsDBNull(0))
        {
            return (null, probedAt);
        }

        // An unparseable stored value is treated as "never probed", not as "no data": the second
        // would silently retire a contract on a formatting bug.
        return DateTimeOffset.TryParse(
            reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var head)
            ? (head.ToUniversalTime(), probedAt)
            : null;
    }

    public async Task RecordHeadTimestampAsync(
        string probeKey, int conId, DateTimeOffset head, string notes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.capability_probes (probe_key, con_id, ran_at, succeeded, result, notes) " +
            "VALUES ($1, $2, now(), true, jsonb_build_object('headTimestampUtc', $3::text), $4)",
            connection);

        command.Parameters.AddWithValue(probeKey);
        command.Parameters.AddWithValue(conId);
        command.Parameters.AddWithValue(head.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(notes);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records that a head-timestamp probe ran and TWS answered that there is no history for this
    /// contract.
    /// </summary>
    /// <remarks>
    /// <c>succeeded</c> is true because the PROBE succeeded — an answer was obtained — and the head
    /// is stored as an explicit JSON null so <see cref="GetCachedHeadProbeAsync"/> can tell "asked,
    /// and there is nothing" from "never asked". A failed probe (paced, disconnected) writes no row
    /// at all, which is what keeps it retried on the next scan.
    /// </remarks>
    public async Task RecordNoHeadTimestampAsync(
        string probeKey, int conId, string notes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.capability_probes (probe_key, con_id, ran_at, succeeded, result, notes) " +
            "VALUES ($1, $2, now(), true, jsonb_build_object('headTimestampUtc', NULL, 'noData', true), $3)",
            connection);

        command.Parameters.AddWithValue(probeKey);
        command.Parameters.AddWithValue(conId);
        command.Parameters.AddWithValue(notes);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- status -------------------------------------------------------------------------------

    /// <summary>
    /// Every job's progress, derived from the checkpoint table.
    /// </summary>
    /// <remarks>
    /// <c>LEFT JOIN LATERAL ... ON true</c>, not a <c>GROUP BY</c> over the request table. A grouped
    /// query cannot emit a row for a job that has no request rows, so a job whose planning never ran
    /// would be absent from this report — and absence reads as health. Three of the Phase 1 review's
    /// eight confirmed defects shared exactly that root cause. Here such a job renders with a total
    /// of 0 and a completion of 0%, which is the truth about it.
    /// <para>
    /// For the same reason <c>PercentComplete</c> is 0 rather than 100 (or NaN) when there is nothing
    /// to do: "no slices" must never round up to "finished" — and, since the same review that found
    /// that also found its mirror image, an exhausted slice does not count toward completion either.
    /// See <see cref="BackfillJobStatus.PercentComplete"/>.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<BackfillJobStatus>> GetStatusAsync(int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT j.job_id, j.name, j.kind, j.instrument_id, j.con_id, j.what_to_show, j.bar_size, j.use_rth, " +
            "       j.target_from, j.target_to, j.priority, j.status, " +
            "       COALESCE(r.total, 0), COALESCE(r.pending, 0), COALESCE(r.inflight, 0), COALESCE(r.succeeded, 0), " +
            "       COALESCE(r.empty, 0), COALESCE(r.retryable, 0), COALESCE(r.exhausted, 0), COALESCE(r.permanent, 0), " +
            "       COALESCE(r.now_anchored, 0), COALESCE(r.bars_landed, 0), COALESCE(r.bars_returned, 0), " +
            "       r.low_water, r.high_water, r.earliest_lease " +
            "FROM research.backfill_jobs j " +
            "LEFT JOIN LATERAL ( " +
            "    SELECT count(*) AS total, " +
            "           count(*) FILTER (WHERE br.state = 'pending') AS pending, " +
            "           count(*) FILTER (WHERE br.state = 'inflight') AS inflight, " +
            "           count(*) FILTER (WHERE br.state = 'succeeded') AS succeeded, " +
            "           count(*) FILTER (WHERE br.state = 'empty') AS empty, " +
            "           count(*) FILTER (WHERE br.state = 'failed' AND br.attempts < $1) AS retryable, " +
            "           count(*) FILTER (WHERE br.state = 'failed' AND br.attempts >= $1) AS exhausted, " +
            "           count(*) FILTER (WHERE br.state = 'permanent') AS permanent, " +
            "           count(*) FILTER (WHERE br.end_time_utc IS NULL) AS now_anchored, " +
            "           COALESCE(sum(br.bars_landed), 0) AS bars_landed, " +
            "           COALESCE(sum(br.bars_returned), 0) AS bars_returned, " +
            "           min(br.first_bar_utc) AS low_water, " +
            "           max(br.last_bar_utc) AS high_water, " +
            "           min(br.lease_expires_at) FILTER (WHERE br.state = 'inflight') AS earliest_lease " +
            "    FROM research.backfill_requests br WHERE br.job_id = j.job_id " +
            ") AS r ON true " +
            "ORDER BY j.priority DESC, j.job_id",
            connection);

        command.Parameters.AddWithValue(maxAttempts);

        var rows = new List<BackfillJobStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var total = (int)reader.GetInt64(12);
            var succeeded = (int)reader.GetInt64(15);
            var empty = (int)reader.GetInt64(16);
            var exhausted = (int)reader.GetInt64(18);
            var permanent = (int)reader.GetInt64(19);

            // Exhausted is settled (IsJobSettledAsync counts it, and that is deliberate — such a
            // slice is genuinely never coming back) but it is NOT resolved: nothing confirmed the
            // data is absent, the coordinator simply stopped asking. Including it here is what let a
            // job with dead slices in it render at 100% on a green progress bar.
            var resolved = succeeded + empty + permanent;

            rows.Add(new BackfillJobStatus(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt16(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetInt32(10),
                reader.GetString(11),
                total,
                (int)reader.GetInt64(13),
                (int)reader.GetInt64(14),
                succeeded,
                empty,
                (int)reader.GetInt64(17),
                exhausted,
                permanent,
                (int)reader.GetInt64(20),
                reader.GetInt64(21),
                reader.GetInt64(22),
                total == 0 ? 0d : (double)resolved / total,
                reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
                reader.IsDBNull(24) ? null : reader.GetFieldValue<DateTimeOffset>(24),
                reader.IsDBNull(25) ? null : reader.GetFieldValue<DateTimeOffset>(25)));
        }

        return rows;
    }

    /// <summary>
    /// Whether a job has at least one request row and no non-terminal ones left.
    /// </summary>
    /// <remarks>
    /// The <c>total &gt; 0</c> half is not a formality. Without it a job whose planning has never run
    /// — the gateway was down, its conId never resolved — has zero non-terminal rows and would be
    /// marked complete on the coordinator's first pass, which is the absent-row failure mode wearing
    /// a different hat. Completion is never inferred from an empty claim either; that would report
    /// the loser of a claim race as a finished job.
    /// </remarks>
    public async Task<bool> IsJobSettledAsync(long jobId, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) AS total, " +
            "       count(*) FILTER (WHERE state NOT IN (" + TerminalStates + ") " +
            "                          AND NOT (state = 'failed' AND attempts >= $2)) AS outstanding " +
            "FROM research.backfill_requests WHERE job_id = $1",
            connection);

        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(maxAttempts);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) > 0 && reader.GetInt64(1) == 0;
    }

    /// <summary>
    /// Re-derives a job's status from its checkpoint counts and writes it if it changed.
    /// </summary>
    /// <param name="planningComplete">
    /// False when the CALLER knows its own planning was partial — a contract whose head timestamp it
    /// could not resolve this pass, say. Such a job is forced to <c>running</c> no matter what the
    /// counts say, because the counts cannot see it: a contract that produced no request rows lowers
    /// no total and no outstanding count, so "nothing outstanding" is a statement about the slices
    /// that WERE planned and nothing at all about the ones that were not. That inference is right for
    /// a single-conId job, where one planner call derives the whole range, and wrong for a job whose
    /// planning is routinely partial by design (<see cref="EsContractWalker"/>).
    /// </param>
    /// <returns>The new status when it changed, else null — so a caller logs a transition, not a tick.</returns>
    /// <remarks>
    /// One statement, deriving and writing together, for the same reason <see cref="ClaimAsync"/> is:
    /// the caller's cached <c>BackfillJob.Status</c> can be stale by the time it acts on it, and a
    /// status decided from one read and written by another can move a job the writer never saw.
    /// <para>
    /// <c>paused</c> and <c>failed</c> are never overwritten. Both are decisions somebody made — an
    /// operator pause, or the planner refusing a job whose slice duration it cannot put on a grid —
    /// and a count-derived status has no business relitigating either. A job with zero request rows
    /// is left alone as well: <c>pending</c> is the truth about it, and calling it <c>running</c>
    /// would claim work exists that nothing has planned.
    /// </para>
    /// </remarks>
    public async Task<string?> RefreshJobStatusAsync(
        long jobId, int maxAttempts, bool planningComplete, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            WITH counts AS (
                SELECT count(*) AS total,
                       count(*) FILTER (
                           WHERE state NOT IN ('succeeded', 'empty', 'permanent')
                             AND NOT (state = 'failed' AND attempts >= $2)) AS outstanding,
                       count(*) FILTER (WHERE state = 'failed' AND attempts >= $2) AS exhausted
                FROM research.backfill_requests
                WHERE job_id = $1
            ), derived AS (
                SELECT CASE
                           WHEN NOT $3 OR c.outstanding > 0 THEN 'running'
                           WHEN c.exhausted > 0             THEN 'complete_with_gaps'
                           ELSE                                  'complete'
                       END AS status,
                       c.total
                FROM counts c
            )
            UPDATE research.backfill_jobs j
            SET status = d.status, updated_at = now()
            FROM derived d
            WHERE j.job_id = $1
              AND d.total > 0
              AND j.status IN ('pending', 'running', 'complete', 'complete_with_gaps')
              AND j.status IS DISTINCT FROM d.status
            RETURNING j.status
            """,
            connection);

        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(maxAttempts);
        command.Parameters.AddWithValue(planningComplete);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? reader.GetString(0) : null;
    }

    /// <summary>
    /// Every distinct <c>con_id</c> this job has request rows for.
    /// </summary>
    /// <remarks>
    /// The durable half of the ES walker's coverage check. A contract that a previous scan planned
    /// and this scan's family enumeration did not return is not evidence that the contract stopped
    /// mattering — it is evidence that this enumeration was incomplete — and without this query the
    /// walker has no memory of a contract outside the list it was just handed.
    /// </remarks>
    public async Task<HashSet<int>> GetPlannedConIdsAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT con_id FROM research.backfill_requests WHERE job_id = $1", connection);
        command.Parameters.AddWithValue(jobId);

        var conIds = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            conIds.Add(reader.GetInt32(0));
        }

        return conIds;
    }

    /// <summary>
    /// The newest <c>end_time_utc</c> this job has a request row for, or NULL when it has none.
    /// </summary>
    /// <remarks>
    /// Where <see cref="EsContractWalker"/>'s forward-coverage claim is measured, and it is measured
    /// on <c>research.backfill_requests</c> deliberately: the claim is "nothing after the job's frozen
    /// <c>target_to</c> went unrequested", which is a statement about request rows, not about landed
    /// bars — bars are also absent for a Sunday, and a check that could not tell those apart would
    /// cry wolf every weekend.
    /// <para>
    /// NULL is returned rather than an epoch sentinel so the caller has to decide what "this job has
    /// requested nothing at all" means. It means a shortfall. An aggregate over an empty set is the
    /// canonical shape of absence rendering as health, which is exactly the failure this query exists
    /// to catch, so it must not be papered over here.
    /// </para>
    /// </remarks>
    public async Task<DateTimeOffset?> GetNewestPlannedEndAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT max(end_time_utc) FROM research.backfill_requests WHERE job_id = $1", connection);
        command.Parameters.AddWithValue(jobId);

        // Read through the reader rather than ExecuteScalar so the value arrives as a DateTimeOffset
        // by the same conversion every other timestamptz in this class uses, instead of a boxed
        // DateTime whose Kind this method would have to assume.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0)
            ? reader.GetFieldValue<DateTimeOffset>(0).ToUniversalTime()
            : null;
    }

    // ---- gap detection --------------------------------------------------------------------------

    /// <summary>
    /// Every <c>research.backfill_requests</c> row for a job, reduced to what <c>GapArithmetic</c>
    /// needs to derive its nominal window and outcome.
    /// </summary>
    /// <remarks>
    /// NULL-anchored rows (<c>end_time_utc IS NULL</c>) are excluded: only a hand-inserted row is ever
    /// NULL-anchored (the coordinator refuses to write one — see migration 005), and there is no
    /// window to derive from "whenever this runs". Excluding it is the conservative direction — such a
    /// row contributes nothing toward "this range is covered", so at worst a range shows up as
    /// <see cref="GapBasis.NotRequested"/> rather than being wrongly cleared by a request that cannot
    /// actually be replayed. <c>GET /research/backfill</c>'s <c>NowAnchoredCount</c> already surfaces
    /// such a row so it is never simply invisible.
    /// </remarks>
    public async Task<IReadOnlyList<BackfillRequestWindowRow>> GetRequestWindowsAsync(
        long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT end_time_utc, duration, state, attempts FROM research.backfill_requests " +
            "WHERE job_id = $1 AND end_time_utc IS NOT NULL",
            connection);
        command.Parameters.AddWithValue(jobId);

        var rows = new List<BackfillRequestWindowRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BackfillRequestWindowRow(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return rows;
    }

    /// <summary>
    /// Trading dates in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) that have at least one
    /// landed daily bar for this exact (con_id, what_to_show, bar_size, use_rth) key.
    /// </summary>
    /// <remarks>
    /// Filters on <c>ts_utc</c>, not <c>trading_date</c>, even though the caller wants dates: a daily
    /// bar's <c>ts_utc</c> is exactly its trading date's UTC midnight (see migration 004 and
    /// <see cref="LandBarsAsync"/>), so the two filters select an identical row set, but <c>ts_utc</c>
    /// is the trailing column of <c>research.bars</c>'s primary key and <c>trading_date</c> is not —
    /// this is what lets the equality prefix (con_id, what_to_show, bar_size, use_rth) plus a genuine
    /// index range scan answer the query, rather than a full filter pass over every row for the key.
    /// This alone does not decide which dates are MISSING — the caller compares the returned set
    /// against the full expected trading-date list, which is where the absent-row check actually
    /// happens (a date this query never mentions is exactly a date with nothing landed).
    /// </remarks>
    public async Task<HashSet<DateOnly>> GetLandedTradingDatesAsync(
        int conId, string whatToShow, string barSize, bool useRth,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT trading_date FROM research.bars " +
            "WHERE con_id = $1 AND what_to_show = $2 AND bar_size = $3 AND use_rth = $4 " +
            "  AND trading_date IS NOT NULL AND ts_utc >= $5 AND ts_utc < $6",
            connection);
        command.Parameters.AddWithValue(conId);
        command.Parameters.AddWithValue(whatToShow);
        command.Parameters.AddWithValue(barSize);
        command.Parameters.AddWithValue(useRth);
        command.Parameters.AddWithValue(fromUtc);
        command.Parameters.AddWithValue(toUtc);

        var dates = new HashSet<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            dates.Add(reader.GetFieldValue<DateOnly>(0));
        }

        return dates;
    }

    /// <summary>
    /// Landed bar counts for this exact (con_id, what_to_show, bar_size, use_rth) key, one count per
    /// caller-supplied [from, to) window, in the SAME order the windows were given.
    /// </summary>
    /// <remarks>
    /// <b>This is where the negative claim for intraday bar sizes is measured, and this is the
    /// absent-row check.</b> <c>LEFT JOIN</c>, not a <c>GROUP BY</c> starting from
    /// <c>research.bars</c>: the expected set — every session window the calendar says should have
    /// bars — is materialised FIRST via <c>unnest(...) WITH ORDINALITY</c>, and reality is joined onto
    /// it. A window with zero landed bars still produces a row (<c>count = 0</c> via <c>count(b.ts_utc)</c>,
    /// which ignores the LEFT JOIN's NULL-extended columns) at its own ordinal position; a
    /// <c>GROUP BY</c> starting from the bars table cannot emit a row for a window that landed nothing,
    /// which is precisely the worst case a gap report exists to catch. The caller never needs to
    /// special-case "missing from the result" because there is no such case: the returned array always
    /// has exactly <c>windowFrom.Count</c> entries.
    /// </remarks>
    public async Task<int[]> GetLandedBarCountsAsync(
        int conId, string whatToShow, string barSize, bool useRth,
        IReadOnlyList<DateTimeOffset> windowFrom, IReadOnlyList<DateTimeOffset> windowTo,
        CancellationToken cancellationToken)
    {
        if (windowFrom.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT s.idx, count(b.ts_utc)
            FROM unnest($1::timestamptz[], $2::timestamptz[]) WITH ORDINALITY AS s(from_utc, to_utc, idx)
            LEFT JOIN research.bars b
                   ON b.con_id = $3 AND b.what_to_show = $4 AND b.bar_size = $5 AND b.use_rth = $6
                  AND b.ts_utc >= s.from_utc AND b.ts_utc < s.to_utc
            GROUP BY s.idx
            """,
            connection);

        AddArray(command, windowFrom.ToArray(), NpgsqlDbType.TimestampTz);
        AddArray(command, windowTo.ToArray(), NpgsqlDbType.TimestampTz);
        command.Parameters.AddWithValue(conId);
        command.Parameters.AddWithValue(whatToShow);
        command.Parameters.AddWithValue(barSize);
        command.Parameters.AddWithValue(useRth);

        var counts = new int[windowFrom.Count];
        var seen = new bool[windowFrom.Count];
        var returned = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // WITH ORDINALITY is 1-based.
            var index = (int)reader.GetInt64(0) - 1;
            counts[index] = (int)reader.GetInt64(1);
            seen[index] = true;
            returned++;
        }

        // The negative claim, asserted rather than assumed. A caller-allocated array is zero-filled,
        // so a query shape that emits NO row for a window with no bars — a GROUP BY starting from
        // research.bars, the exact regression the SQL above is written against — produces an
        // indistinguishable, entirely plausible 0. The array's length proves nothing either: it is
        // fixed by C#, not by the database. Only counting which ordinals the engine actually returned
        // can tell "measured, and it is zero" from "never measured".
        //
        // The query above cannot trip this, which is the point: it is the invariant that makes the
        // zero trustworthy, and it fires the moment a rewrite stops guaranteeing it. The
        // corresponding test therefore pins the DIFFERENCE — it runs the naive shape against the same
        // fixture and shows it answers with fewer rows — because the old test asserted only the
        // array's length and its middle value, both of which the naive shape satisfies too.
        if (returned != windowFrom.Count)
        {
            throw new InvalidOperationException(
                $"The landed-bar-count query returned {returned} row(s) for {windowFrom.Count} window(s); " +
                $"the first unmeasured window starts at {windowFrom[Array.IndexOf(seen, false)]:O}. Every window " +
                "must produce a row, including one with no bars — otherwise absence is reported as a count of zero.");
        }

        return counts;
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// The one shared shape for every claim-releasing write: it applies only while this owner still
    /// holds the lease, so a coordinator that was reaped mid-flight cannot overwrite the reclaim.
    /// </summary>
    private async Task<bool> FinishAsync(
        long requestId, string owner, string setClause, NpgsqlParameter[] extraParameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"UPDATE research.backfill_requests AS r {setClause} " +
            "WHERE r.request_id = $1 AND r.state = 'inflight' AND r.claimed_by = $2",
            connection);

        command.Parameters.AddWithValue(requestId);
        command.Parameters.AddWithValue(owner);

        foreach (var parameter in extraParameters)
        {
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static void AddArray<T>(NpgsqlCommand command, T[] values, NpgsqlDbType elementType) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = values,
            NpgsqlDbType = NpgsqlDbType.Array | elementType,
        });

    private static void AddNullable(NpgsqlCommand command, DateTimeOffset? value, NpgsqlDbType type) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)value ?? DBNull.Value,
            NpgsqlDbType = type,
        });

    /// <summary>The database spelling of a state — lower-case, matching migration 004's CHECK constraint.</summary>
    public static string StateName(BackfillRequestState state) => state switch
    {
        BackfillRequestState.Pending => "pending",
        BackfillRequestState.Inflight => "inflight",
        BackfillRequestState.Succeeded => "succeeded",
        BackfillRequestState.Empty => "empty",
        BackfillRequestState.Failed => "failed",
        _ => "permanent",
    };

    private static BackfillJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetInt16(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetBoolean(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetInt32(9),
        reader.GetString(10),
        reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12));
}
