using Npgsql;
using NpgsqlTypes;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.OptionChains;

/// <summary>A request row this coordinator instance currently owns the lease on.</summary>
public sealed record ClaimedChainRequest(
    long RequestId, long JobId, DateOnly Expiration, int Attempts);

/// <summary>
/// Every Postgres interaction the option-chain ingestion coordinator makes: job upkeep, expiration
/// planning, race-safe claiming, lease reclamation, quote landing, and status derivation.
/// </summary>
/// <remarks>
/// Deliberately parallel to <c>TradingStuff.ResearchService.Backfill.BackfillStore</c> — same
/// <c>SKIP LOCKED</c> claim shape for the same proven reason (see that class's remarks: a
/// <c>SELECT ... FOR UPDATE</c> followed by a write is NOT race-safe under Postgres 17's Read
/// Committed re-check semantics, verified directly against a live server), same "completion is
/// derived from counts, never inferred from an empty claim" discipline, same reason a job with zero
/// request rows renders as 0% rather than being silently absent from a status report.
/// </remarks>
public sealed class OptionChainStore(IConfiguration configuration, ILogger<OptionChainStore> logger)
{
    /// <summary>Job statuses whose request rows are still eligible to be planned and claimed.</summary>
    /// <remarks>
    /// <c>paused</c> is deliberately NOT in here — that is the status every 'tick' job is created
    /// with (see <see cref="EnsureJobAsync"/>) and the automatic coordinator must never plan or claim
    /// its rows. <c>complete_with_gaps</c> IS included, mirroring BackfillStore, so raising
    /// <c>OptionChains:MaxAttempts</c> makes an exhausted job's rows claimable again.
    /// </remarks>
    private const string ClaimableJobStatuses = "'pending', 'running', 'complete_with_gaps'";

    public string? ConnectionString => configuration.GetConnectionString("trading");

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    // ---- jobs -----------------------------------------------------------------------------

    /// <summary>
    /// Creates the job row if it is missing; refreshes only priority on conflict.
    /// </summary>
    /// <remarks>
    /// <c>underlying</c>, <c>trading_class</c>, the target range, and <c>interval</c> are NEVER
    /// updated on conflict, for the same reason <c>BackfillStore.EnsureJobAsync</c> protects its own
    /// grid-defining columns: quietly changing them would re-plan the job into a second, overlapping
    /// set of request rows the idempotency key cannot collapse.
    /// <para>
    /// A job created with <see cref="OptionChainIntervals.Tick"/> starts — and, since status is never
    /// touched on conflict, STAYS — <c>paused</c>. That is the entire enforcement mechanism for
    /// "tick is never planned automatically": <see cref="GetActiveJobsAsync"/> excludes <c>paused</c>
    /// jobs, so a tick job simply never reaches <c>OptionChainCoordinator.PlanJobAsync</c>. Nothing
    /// unpauses it — bulk tick ingestion is out of scope for this coordinator entirely (see
    /// docs/FOLLOWUP.md §4.5), not merely deferred behind a flag.
    /// </para>
    /// </remarks>
    public async Task<OptionChainJob?> EnsureJobAsync(
        string name, string underlying, string tradingClass, DateOnly targetFrom, DateOnly targetTo,
        string interval, int priority, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO research.option_chain_jobs
              (name, underlying, trading_class, target_from, target_to, interval, priority, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7, CASE WHEN $6 = 'tick' THEN 'paused' ELSE 'pending' END)
            ON CONFLICT (name) DO UPDATE
              SET priority = EXCLUDED.priority,
                  updated_at = now()
            RETURNING job_id, name, underlying, trading_class, target_from, target_to,
                      interval, priority, status
            """,
            connection);

        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(underlying);
        command.Parameters.AddWithValue(tradingClass);
        command.Parameters.AddWithValue(targetFrom);
        command.Parameters.AddWithValue(targetTo);
        command.Parameters.AddWithValue(interval);
        command.Parameters.AddWithValue(priority);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    /// <summary>Jobs the coordinator should plan and drain: not paused, not cleanly finished.</summary>
    public async Task<IReadOnlyList<OptionChainJob>> GetActiveJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT job_id, name, underlying, trading_class, target_from, target_to, " +
            "       interval, priority, status " +
            "FROM research.option_chain_jobs WHERE status IN (" + ClaimableJobStatuses + ") " +
            "ORDER BY priority DESC, job_id",
            connection);

        var jobs = new List<OptionChainJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task<OptionChainJob?> GetJobAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT job_id, name, underlying, trading_class, target_from, target_to, " +
            "       interval, priority, status " +
            "FROM research.option_chain_jobs WHERE job_id = $1",
            connection);
        command.Parameters.AddWithValue(jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    // ---- planning ---------------------------------------------------------------------------

    /// <summary>
    /// Writes planned expiration requests, returning how many were genuinely new.
    /// </summary>
    /// <remarks>
    /// <c>ON CONFLICT DO NOTHING</c> against <c>(job_id, expiration)</c> is the entire resumability
    /// story: re-planning an unchanged job re-derives the identical expiration list (ThetaData's
    /// expiration list for a fixed symbol is deterministic day to day for anything already listed)
    /// and lands zero new request rows.
    /// </remarks>
    public async Task<int> PlanExpirationsAsync(
        long jobId, IReadOnlyList<DateOnly> expirations, CancellationToken cancellationToken)
    {
        if (expirations.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.option_chain_requests (job_id, expiration) " +
            "SELECT $1, e FROM unnest($2::date[]) AS e " +
            "ON CONFLICT DO NOTHING",
            connection);

        command.Parameters.AddWithValue(jobId);
        AddArray(command, expirations.ToArray(), NpgsqlDbType.Date);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- claiming, leases, reclamation --------------------------------------------------------

    /// <summary>
    /// Claims up to <paramref name="limit"/> expiration requests for <paramref name="owner"/> in one
    /// statement. See the class remarks for why <c>SKIP LOCKED</c>, not <c>SELECT ... FOR UPDATE</c>
    /// followed by a write, is the only race-safe shape here.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedChainRequest>> ClaimAsync(
        string owner, TimeSpan lease, int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE research.option_chain_requests AS r " +
            "SET state = 'inflight', " +
            "    claimed_by = $1, " +
            "    lease_expires_at = now() + make_interval(secs => $2), " +
            "    attempts = r.attempts + 1, " +
            "    requested_at = now() " +
            "FROM ( " +
            "    SELECT cr.request_id " +
            "    FROM research.option_chain_requests cr " +
            "    JOIN research.option_chain_jobs cj ON cj.job_id = cr.job_id " +
            "    WHERE cj.status IN (" + ClaimableJobStatuses + ") " +
            "      AND cj.interval <> 'tick' " +
            "      AND cr.attempts < $3 " +
            "      AND ( cr.state = 'pending' " +
            "            OR ( cr.state = 'failed' " +
            "                 AND COALESCE(cr.completed_at, 'epoch'::timestamptz) < " +
            "                     now() - least(interval '30 minutes', " +
            "                                   interval '30 seconds' * power(2, cr.attempts - 1)) ) ) " +
            "    ORDER BY cj.priority DESC, cr.request_id " +
            "    LIMIT $4 " +
            "    FOR UPDATE OF cr SKIP LOCKED " +
            ") AS candidate " +
            "WHERE r.request_id = candidate.request_id AND r.state IN ('pending', 'failed') " +
            "RETURNING r.request_id, r.job_id, r.expiration, r.attempts",
            connection);

        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(lease.TotalSeconds);
        command.Parameters.AddWithValue(maxAttempts);
        command.Parameters.AddWithValue(limit);

        var claimed = new List<ClaimedChainRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedChainRequest(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<DateOnly>(2), reader.GetInt32(3)));
        }

        return claimed;
    }

    /// <summary>Reclaims every <c>inflight</c> row whose lease has expired, returning how many.</summary>
    public async Task<int> ReclaimExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE research.option_chain_requests AS r " +
            "SET state = 'failed', " +
            "    claimed_by = NULL, " +
            "    lease_expires_at = NULL, " +
            "    completed_at = now(), " +
            "    error_message = 'lease expired; reclaimed from ' || COALESCE(r.claimed_by, '(unknown owner)') " +
            "FROM ( " +
            "    SELECT request_id FROM research.option_chain_requests " +
            "    WHERE state = 'inflight' AND lease_expires_at < now() " +
            "    FOR UPDATE SKIP LOCKED " +
            ") AS expired " +
            "WHERE r.request_id = expired.request_id AND r.state = 'inflight'",
            connection);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Puts a claimed request back on the queue without consuming a retry.</summary>
    public Task<bool> ReleaseAsync(long requestId, string owner, CancellationToken cancellationToken) =>
        FinishAsync(
            requestId, owner,
            "SET state = 'pending', claimed_by = NULL, lease_expires_at = NULL, " +
            "    attempts = GREATEST(r.attempts - 1, 0), requested_at = NULL",
            [],
            cancellationToken);

    /// <summary>Records a terminal or retryable outcome that landed no quotes.</summary>
    public Task<bool> MarkOutcomeAsync(
        long requestId, string owner, OptionChainRequestState state, string? errorMessage,
        CancellationToken cancellationToken) =>
        FinishAsync(
            requestId, owner,
            "SET state = $3, claimed_by = NULL, lease_expires_at = NULL, completed_at = now(), " +
            "    quotes_returned = COALESCE(r.quotes_returned, 0), quotes_landed = COALESCE(r.quotes_landed, 0), " +
            "    error_message = $4",
            [
                new NpgsqlParameter { Value = StateName(state), NpgsqlDbType = NpgsqlDbType.Text },
                new NpgsqlParameter { Value = (object?)errorMessage ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text },
            ],
            cancellationToken);

    /// <summary>
    /// Lands one expiration's quotes and marks the request succeeded, atomically.
    /// </summary>
    /// <returns>False when the lease was lost mid-flight; nothing is written in that case.</returns>
    /// <remarks>
    /// One transaction, for the identical reason <c>BackfillStore.LandBarsAsync</c> is: splitting
    /// the insert from the completion write admits a crash window where the checkpoint says
    /// <c>succeeded</c> and the quotes are not there, which is the one failure this table's whole
    /// design exists to make impossible.
    /// <para>
    /// <c>landedRows</c> — the INSERT's own row count, taken BEFORE the primary key deduplicates —
    /// is the honest "genuinely new" figure, exactly as <c>BackfillStore.LandBarsAsync</c> documents:
    /// a rerun of an already-landed expiration returns the same rows and lands zero of them, which is
    /// the idempotent-rerun guarantee this package's tests pin directly.
    /// </para>
    /// </remarks>
    public async Task<bool> LandQuotesAsync(
        ClaimedChainRequest request,
        string owner,
        IReadOnlyList<OptionChainQuoteRow> rows,
        string vendorSymbol,
        string interval,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var landedRows = 0;

        if (rows.Count > 0)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO research.option_chain_quotes
                  (underlying, trading_class, expiration, strike, option_right, observed_at, trading_date,
                   bid, ask, bid_size, ask_size, bid_exchange, ask_exchange,
                   vendor, vendor_symbol, vendor_endpoint, interval, request_id)
                SELECT $1, $2, $3, t.strike, t.option_right, t.observed_at, t.trading_date,
                       t.bid, t.ask, t.bid_size, t.ask_size, t.bid_exchange, t.ask_exchange,
                       'thetadata', $4, $5, $6, $7
                FROM unnest($8::numeric[], $9::char(1)[], $10::timestamptz[], $11::date[],
                            $12::numeric[], $13::numeric[], $14::numeric[], $15::numeric[], $16::smallint[], $17::smallint[])
                     AS t(strike, option_right, observed_at, trading_date, bid, ask, bid_size, ask_size, bid_exchange, ask_exchange)
                ON CONFLICT (underlying, trading_class, expiration, strike, option_right, observed_at) DO NOTHING
                """,
                connection, transaction);

            insert.Parameters.AddWithValue(rows[0].Underlying);
            insert.Parameters.AddWithValue(rows[0].TradingClass);
            insert.Parameters.AddWithValue(request.Expiration);
            insert.Parameters.AddWithValue(vendorSymbol);
            insert.Parameters.AddWithValue(OptionChainQuoteCsvParser.Endpoint);
            insert.Parameters.AddWithValue(interval);
            insert.Parameters.AddWithValue(request.RequestId);

            AddArray(insert, rows.Select(r => r.Strike).ToArray(), NpgsqlDbType.Numeric);
            AddArray(insert, rows.Select(r => r.Right.ToString()).ToArray(), NpgsqlDbType.Char);
            AddArray(insert, rows.Select(r => r.ObservedAt).ToArray(), NpgsqlDbType.TimestampTz);
            AddArray(insert, rows.Select(r => r.TradingDate).ToArray(), NpgsqlDbType.Date);
            AddNullableArray(insert, rows.Select(r => r.Bid).ToArray(), NpgsqlDbType.Numeric);
            AddNullableArray(insert, rows.Select(r => r.Ask).ToArray(), NpgsqlDbType.Numeric);
            AddNullableArray(insert, rows.Select(r => r.BidSize).ToArray(), NpgsqlDbType.Numeric);
            AddNullableArray(insert, rows.Select(r => r.AskSize).ToArray(), NpgsqlDbType.Numeric);
            AddNullableArray(insert, rows.Select(r => (short?)r.BidExchange).ToArray(), NpgsqlDbType.Smallint);
            AddNullableArray(insert, rows.Select(r => (short?)r.AskExchange).ToArray(), NpgsqlDbType.Smallint);

            landedRows = await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var complete = new NpgsqlCommand(
            "UPDATE research.option_chain_requests AS r " +
            "SET state = 'succeeded', claimed_by = NULL, lease_expires_at = NULL, completed_at = now(), " +
            "    quotes_returned = $3, quotes_landed = $4, error_message = NULL " +
            "WHERE r.request_id = $1 AND r.state = 'inflight' AND r.claimed_by = $2",
            connection, transaction);

        complete.Parameters.AddWithValue(request.RequestId);
        complete.Parameters.AddWithValue(owner);
        complete.Parameters.AddWithValue(rows.Count);
        complete.Parameters.AddWithValue(landedRows);

        if (await complete.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning(
                "Lease on request {RequestId} was lost before its {Count} landed quote row(s) could be " +
                "committed; rolling back so a re-fetch, not a duplicate, is what happens next.",
                request.RequestId, landedRows);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // ---- status -------------------------------------------------------------------------------

    /// <summary>
    /// Every job's progress, derived from the checkpoint table via <c>LEFT JOIN LATERAL</c> rather
    /// than a <c>GROUP BY</c> starting from <c>research.option_chain_requests</c> — the same
    /// absent-row discipline <c>BackfillStore.GetStatusAsync</c> documents: a job whose planning has
    /// not yet run must render with a total of 0, not be missing from the report entirely.
    /// </summary>
    public async Task<IReadOnlyList<OptionChainJobStatus>> GetStatusAsync(
        int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT j.job_id, j.name, j.underlying, j.trading_class, j.target_from, j.target_to,
                   j.interval, j.priority, j.status,
                   COALESCE(r.total, 0), COALESCE(r.pending, 0), COALESCE(r.inflight, 0),
                   COALESCE(r.succeeded, 0), COALESCE(r.empty, 0), COALESCE(r.retryable, 0),
                   COALESCE(r.exhausted, 0), COALESCE(r.permanent, 0),
                   COALESCE(r.quotes_landed, 0), COALESCE(r.quotes_returned, 0)
            FROM research.option_chain_jobs j
            LEFT JOIN LATERAL (
                SELECT count(*) AS total,
                       count(*) FILTER (WHERE cr.state = 'pending') AS pending,
                       count(*) FILTER (WHERE cr.state = 'inflight') AS inflight,
                       count(*) FILTER (WHERE cr.state = 'succeeded') AS succeeded,
                       count(*) FILTER (WHERE cr.state = 'empty') AS empty,
                       count(*) FILTER (WHERE cr.state = 'failed' AND cr.attempts < $1) AS retryable,
                       count(*) FILTER (WHERE cr.state = 'failed' AND cr.attempts >= $1) AS exhausted,
                       count(*) FILTER (WHERE cr.state = 'permanent') AS permanent,
                       COALESCE(sum(cr.quotes_landed), 0) AS quotes_landed,
                       COALESCE(sum(cr.quotes_returned), 0) AS quotes_returned
                FROM research.option_chain_requests cr WHERE cr.job_id = j.job_id
            ) AS r ON true
            ORDER BY j.priority DESC, j.job_id
            """,
            connection);

        command.Parameters.AddWithValue(maxAttempts);

        var rows = new List<OptionChainJobStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var total = (int)reader.GetInt64(9);
            var succeeded = (int)reader.GetInt64(12);
            var empty = (int)reader.GetInt64(13);
            var exhausted = (int)reader.GetInt64(15);
            var permanent = (int)reader.GetInt64(16);
            var resolved = succeeded + empty + permanent;

            rows.Add(new OptionChainJobStatus(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4),
                reader.GetFieldValue<DateOnly>(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8),
                total,
                (int)reader.GetInt64(10),
                (int)reader.GetInt64(11),
                succeeded,
                empty,
                (int)reader.GetInt64(14),
                exhausted,
                permanent,
                reader.GetInt64(17),
                reader.GetInt64(18),
                total == 0 ? 0d : (double)resolved / total));
        }

        return rows;
    }

    /// <summary>
    /// Re-derives a job's status from its checkpoint counts and writes it if it changed. Never
    /// touches <c>paused</c> or <c>failed</c> — both are decisions, not counts.
    /// </summary>
    public async Task<string?> RefreshJobStatusAsync(long jobId, int maxAttempts, CancellationToken cancellationToken)
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
                FROM research.option_chain_requests
                WHERE job_id = $1
            ), derived AS (
                SELECT CASE
                           WHEN c.outstanding > 0 THEN 'running'
                           WHEN c.exhausted > 0   THEN 'complete_with_gaps'
                           ELSE                        'complete'
                       END AS status,
                       c.total
                FROM counts c
            )
            UPDATE research.option_chain_jobs j
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

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? reader.GetString(0) : null;
    }

    // ---- capability probes ----------------------------------------------------------------------

    /// <summary>
    /// Records one runtime-verified ThetaData capability fact into the SAME registry the IBKR
    /// gateway uses (<c>research.capability_probes</c> — see docs/DECISIONS.md's provenance entries
    /// and migration 002). <c>con_id</c>, <c>tws_server_version</c> and <c>market_data_type</c> are
    /// IBKR-shaped columns that do not apply to a ThetaData probe and are left NULL rather than
    /// repurposed — the vendor identity lives in the probe key and the notes/result instead.
    /// </summary>
    public async Task RecordCapabilityProbeAsync(
        string probeKey, bool succeeded, string resultJson, string? notes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.capability_probes (probe_key, ran_at, succeeded, result, notes) " +
            "VALUES ($1, now(), $2, $3::jsonb, $4)",
            connection);

        command.Parameters.AddWithValue(probeKey);
        command.Parameters.AddWithValue(succeeded);
        command.Parameters.AddWithValue(resultJson);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)notes ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<bool> FinishAsync(
        long requestId, string owner, string setClause, NpgsqlParameter[] extraParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"UPDATE research.option_chain_requests AS r {setClause} " +
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
        command.Parameters.Add(new NpgsqlParameter { Value = values, NpgsqlDbType = NpgsqlDbType.Array | elementType });

    private static void AddNullableArray<T>(NpgsqlCommand command, T?[] values, NpgsqlDbType elementType)
        where T : struct =>
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = values.Select(v => (object?)v ?? DBNull.Value).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | elementType,
        });

    public static string StateName(OptionChainRequestState state) => state switch
    {
        OptionChainRequestState.Pending => OptionChainRequestStates.Pending,
        OptionChainRequestState.Inflight => OptionChainRequestStates.Inflight,
        OptionChainRequestState.Succeeded => OptionChainRequestStates.Succeeded,
        OptionChainRequestState.Empty => OptionChainRequestStates.Empty,
        OptionChainRequestState.Failed => OptionChainRequestStates.Failed,
        _ => OptionChainRequestStates.Permanent,
    };

    private static OptionChainJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetFieldValue<DateOnly>(4),
        reader.GetFieldValue<DateOnly>(5),
        reader.GetString(6),
        reader.GetInt32(7),
        reader.GetString(8));
}
