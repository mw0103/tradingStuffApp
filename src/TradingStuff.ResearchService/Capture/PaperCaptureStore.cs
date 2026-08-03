using Npgsql;
using NpgsqlTypes;

namespace TradingStuff.ResearchService.Capture;

/// <summary>
/// The raw paper-capture tables: <c>research.paper_fills</c> and
/// <c>research.paper_account_snapshots</c> (migration 024).
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and idempotent per trading date.</b> Both tables carry database triggers that
/// reject UPDATE and DELETE, so this store only ever inserts. Re-running a date is the intended
/// recovery path rather than an anomaly: the partial unique index on <c>trading_date</c> makes the
/// second snapshot a no-op, and the unique <c>exec_id</c> makes a replayed execution a no-op too.
/// Both are enforced by the SCHEMA, not by a read-then-write here — two passes racing (a restart
/// overlapping the previous process's shutdown) would otherwise both see "nothing captured" and
/// both write.
/// </para>
/// <para>
/// <b>Snapshot and fills go in one transaction.</b> The snapshot row carries <c>fill_count</c>, and
/// a snapshot claiming five fills with three rows behind it is a reconciliation problem for whoever
/// reads it later. Either the whole pass is on the record or none of it is, and the absence of a
/// snapshot row is what makes the next pass retry.
/// </para>
/// </remarks>
public sealed class PaperCaptureStore(IConfiguration configuration)
{
    public string? ConnectionString => configuration.GetConnectionString("trading");

    /// <summary>True when this trading date already has a successful capture (refusals do not count).</summary>
    /// <remarks>
    /// A cheap pre-check so a captured date costs no gateway traffic at all. It is NOT the
    /// idempotency guarantee — the unique index is; see the class remarks.
    /// </remarks>
    public async Task<bool> HasCaptureAsync(DateOnly tradingDate, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM research.paper_account_snapshots " +
            "WHERE trading_date = $1 AND refusal_kind IS NULL)",
            connection)
        {
            Parameters = { new() { Value = tradingDate, NpgsqlDbType = NpgsqlDbType.Date } },
        };

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Writes one capture pass: the snapshot row and every fill it pulled.</summary>
    public async Task<PaperCaptureOutcome> SaveAsync(PaperAccountCapture capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);

        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Fills first, so fill_count on the snapshot is a count of rows that are already in the
        // transaction rather than an intention. ON CONFLICT DO NOTHING performs no UPDATE, so the
        // append-only trigger is never reached — a replayed execution is skipped, not rewritten.
        var written = 0;

        foreach (var fill in capture.Fills)
        {
            written += await InsertFillAsync(connection, transaction, fill, cancellationToken);
        }

        await using var snapshot = new NpgsqlCommand(
            """
            INSERT INTO research.paper_account_snapshots (
                trading_date, snapshot_at, account_id,
                net_liquidation, maintenance_margin, init_margin, excess_liquidity,
                available_funds, buying_power, gross_position_value, currency,
                summary, positions, position_count, fill_count, capture_source)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            ON CONFLICT DO NOTHING
            RETURNING snapshot_id
            """,
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = capture.TradingDate, NpgsqlDbType = NpgsqlDbType.Date },
                new() { Value = capture.SnapshotAt },
                new() { Value = capture.AccountId },
                Money(capture.NetLiquidation),
                Money(capture.MaintenanceMargin),
                Money(capture.InitMargin),
                Money(capture.ExcessLiquidity),
                Money(capture.AvailableFunds),
                Money(capture.BuyingPower),
                Money(capture.GrossPositionValue),
                Text(capture.Currency),
                new() { Value = capture.SummaryJson, NpgsqlDbType = NpgsqlDbType.Jsonb },
                new() { Value = capture.PositionsJson, NpgsqlDbType = NpgsqlDbType.Jsonb },
                new() { Value = capture.PositionCount },
                new() { Value = capture.Fills.Count },
                new() { Value = capture.CaptureSource },
            },
        };

        var stored = await snapshot.ExecuteScalarAsync(cancellationToken) is not null;

        await transaction.CommitAsync(cancellationToken);

        return new PaperCaptureOutcome(stored, written, capture.Fills.Count);
    }

    /// <summary>
    /// Records that a capture pass could not read the broker, and why.
    /// </summary>
    /// <remarks>
    /// The absence-renders-as-absence half of this table. An evening with the gateway down must not
    /// be indistinguishable from an evening with a flat account, so the pass writes a row saying so.
    /// Deduplicated by reason rather than suppressed: "the gateway was unreachable for this date" is
    /// one fact however many retries observed it, and a different reason is a different fact.
    /// </remarks>
    /// <returns>True when this reason was new for the date; false when it was already on the record.</returns>
    public async Task<bool> RecordRefusalAsync(
        DateOnly tradingDate,
        DateTimeOffset observedAt,
        string refusalKind,
        string refusal,
        string captureSource,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO research.paper_account_snapshots (
                trading_date, snapshot_at, refusal_kind, refusal, capture_source)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT DO NOTHING
            RETURNING snapshot_id
            """,
            connection)
        {
            Parameters =
            {
                new() { Value = tradingDate, NpgsqlDbType = NpgsqlDbType.Date },
                new() { Value = observedAt },
                new() { Value = refusalKind },
                new() { Value = refusal },
                new() { Value = captureSource },
            },
        };

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<PaperAccountSnapshotRow>> ListSnapshotsAsync(
        int limit, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT snapshot_id, trading_date, snapshot_at, account_id,
                   net_liquidation, maintenance_margin, init_margin, excess_liquidity,
                   available_funds, buying_power, gross_position_value, currency,
                   summary::text, positions::text, position_count, fill_count,
                   refusal_kind, refusal, capture_source
            FROM research.paper_account_snapshots
            ORDER BY trading_date DESC, snapshot_at DESC
            LIMIT $1
            """,
            connection)
        {
            Parameters = { new() { Value = limit } },
        };

        var rows = new List<PaperAccountSnapshotRow>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PaperAccountSnapshotRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateOnly>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.GetString(18)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<PaperFillRow>> ListFillsAsync(
        DateOnly? tradingDate, int limit, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT fill_id, captured_at, trading_date, account_id, exec_id, perm_id, ibkr_order_id,
                   con_id, symbol, sec_type, expiration, strike, option_right, side, quantity, price,
                   executed_at_raw, executed_at, commission, commission_currency, capture_source
            FROM research.paper_fills
            WHERE ($1::date IS NULL OR trading_date = $1)
            ORDER BY trading_date DESC, executed_at DESC NULLS LAST, fill_id DESC
            LIMIT $2
            """,
            connection)
        {
            Parameters =
            {
                new() { Value = (object?)tradingDate ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date },
                new() { Value = limit },
            },
        };

        var rows = new List<PaperFillRow>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PaperFillRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateOnly>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateOnly>(10),
                reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.GetDecimal(14),
                reader.GetDecimal(15),
                reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                reader.IsDBNull(18) ? null : reader.GetDecimal(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetString(20)));
        }

        return rows;
    }

    private static async Task<int> InsertFillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaperFill fill,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO research.paper_fills (
                trading_date, account_id, exec_id, perm_id, ibkr_order_id, client_id,
                con_id, symbol, sec_type, expiration, strike, option_right, trading_class, multiplier,
                side, quantity, price, executed_at_raw, executed_at, exchange,
                commission, commission_currency, realized_pnl, capture_source)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14,
                    $15, $16, $17, $18, $19, $20, $21, $22, $23, $24)
            ON CONFLICT (exec_id) DO NOTHING
            """,
            connection, transaction)
        {
            Parameters =
            {
                new() { Value = fill.TradingDate, NpgsqlDbType = NpgsqlDbType.Date },
                new() { Value = fill.AccountId },
                new() { Value = fill.ExecId },
                new() { Value = (object?)fill.PermId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint },
                new() { Value = (object?)fill.IbkrOrderId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
                new() { Value = (object?)fill.ClientId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
                new() { Value = fill.ConId },
                new() { Value = fill.Symbol },
                new() { Value = fill.SecType },
                new() { Value = (object?)fill.Expiration ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date },
                Money(fill.Strike),
                Text(fill.OptionRight),
                Text(fill.TradingClass),
                new() { Value = (object?)fill.Multiplier ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer },
                new() { Value = fill.Side },
                new() { Value = fill.Quantity, NpgsqlDbType = NpgsqlDbType.Numeric },
                new() { Value = fill.Price, NpgsqlDbType = NpgsqlDbType.Numeric },
                new() { Value = fill.ExecutedAtRaw },
                new()
                {
                    Value = (object?)fill.ExecutedAt ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.TimestampTz,
                },
                Text(fill.Exchange),
                Money(fill.Commission),
                Text(fill.CommissionCurrency),
                Money(fill.RealizedPnL),
                new() { Value = fill.CaptureSource },
            },
        };

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Money(decimal? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric };

    private static NpgsqlParameter Text(string? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private string Required() =>
        ConnectionString is { Length: > 0 } connectionString
            ? connectionString
            : throw new InvalidOperationException(
                "Research persistence is not configured (no 'trading' connection string), so the paper " +
                "account capture has nowhere to land. A capture that is not written is not a capture.");
}
