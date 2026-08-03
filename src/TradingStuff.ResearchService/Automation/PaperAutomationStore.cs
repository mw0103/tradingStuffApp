using Npgsql;
using NpgsqlTypes;

namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// The decision log, as its one consumer sees it.
/// </summary>
/// <remarks>
/// An interface over a single Postgres implementation, for one reason: the loop's own behaviour —
/// what it records when it refuses to arm, when it is killed, when the cap is spent, when the signal
/// says no — has to be provable without a database, and it has to be provable that a FAILING store
/// does not turn into a silent success. Both are properties of <see cref="PaperAutomationService"/>,
/// not of SQL.
/// </remarks>
public interface IPaperAutomationStore
{
    Task<long> RecordAsync(AutomationDecision decision, CancellationToken cancellationToken);

    Task<int> CountSubmittedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutomationDecision>> RecentAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutomationDecision>> SubmittedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken);

    /// <summary>
    /// The exit keys already handed a closing order on this trading date.
    /// </summary>
    /// <remarks>
    /// The durable half of the exit claim — the same shape as
    /// <see cref="CountSubmittedOnAsync"/> and for the same reason. An in-memory set of what this
    /// process has closed is emptied by a restart, and a loop that came back would submit a second
    /// closing order for a position whose first one is resting at the venue. Measured on the table, it
    /// is a fact about what was ordered.
    /// </remarks>
    Task<IReadOnlyList<string>> ExitKeysOrderedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken);
}

/// <summary>
/// The decision log: <c>research.paper_automation_decisions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every evaluation writes a row, including — especially — the ones that did nothing.</b> A table
/// that records only submissions is empty on a day automation refused to arm, was killed, or saw no
/// signal, and an empty table renders as health: docs/LESSONS.md §3, which is the dominant defect
/// class in this repository. "What did automation do today?" has to be answerable by counting rows,
/// and "nothing, because X" has to BE a row. The volume is accepted: at the default 5-minute
/// interval a full day is a few hundred rows.
/// </para>
/// <para>
/// <b>The per-session order count is derived from this table, not held in memory.</b> A counter in a
/// field resets on every restart, so a process that crashed after submitting its cap would come back
/// with a full budget — the cap would be a cap on uptime rather than on orders. Deriving it from the
/// rows makes it a fact about what was submitted. It also means an unreachable database refuses to
/// arm rather than arming with an unknown count, which is the correct direction.
/// </para>
/// </remarks>
public sealed class PaperAutomationStore(IConfiguration configuration) : IPaperAutomationStore
{
    public string? ConnectionString => configuration.GetConnectionString("trading");

    /// <summary>Writes one decision and returns its id.</summary>
    /// <remarks>
    /// Throws on failure rather than swallowing. An unwritten decision is an unrecorded one, and the
    /// caller must not report an action it could not log — that is the difference between an audit
    /// trail and a plausible story.
    /// </remarks>
    public async Task<long> RecordAsync(AutomationDecision decision, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.paper_automation_decisions (" +
            "  decided_at, trigger, armed, arm_state, arm_reason, session_calendar, session_label, " +
            "  session_trading_date, in_session, signal_state, signal_reason, study_run_id, action, " +
            "  action_reason, order_submitted, order_id, correlation_id, lifecycle_status, limit_price, " +
            "  limit_price_source, orders_this_session, order_cap, detail) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23::jsonb) " +
            "RETURNING decision_id",
            connection)
        {
            Parameters =
            {
                new() { Value = decision.DecidedAt },
                new() { Value = decision.Trigger },
                new() { Value = decision.Armed },
                new() { Value = decision.ArmState },
                new() { Value = decision.ArmReason },
                Nullable(decision.SessionCalendar),
                Nullable(decision.SessionLabel),
                new() { Value = (object?)decision.SessionTradingDate ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date },
                new() { Value = decision.InSession },
                new() { Value = decision.SignalState },
                new() { Value = decision.SignalReason },
                new() { Value = (object?)decision.StudyRunId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new() { Value = decision.Action },
                new() { Value = decision.ActionReason },
                new() { Value = decision.OrderSubmitted },
                new() { Value = (object?)decision.OrderId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                new() { Value = (object?)decision.CorrelationId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid },
                Nullable(decision.LifecycleStatus),
                new() { Value = (object?)decision.LimitPrice ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric },
                Nullable(decision.LimitPriceSource),
                new() { Value = decision.OrdersThisSession },
                new() { Value = decision.OrderCap },
                new() { Value = (object?)decision.Detail ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb },
            },
        };

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// How many orders this trading date has already had submitted, measured on the table.
    /// </summary>
    /// <remarks>
    /// <c>order_submitted</c> means an order id was established, and the schema's CHECK constraint
    /// ties that flag to the presence of one. It is NOT the whole set of orders that may exist: an
    /// order handed to ExecutionService whose outcome never came back may well be live at the venue
    /// with no id recorded here. Those rows are <c>action = 'outcome-unknown'</c> and they are counted
    /// too — a cap that ignored the ambiguous case would let a gateway timeout buy an extra order,
    /// which is the one direction a safety rail must not fail in.
    /// <para>
    /// <b>Closing orders are counted here as well</b>, by the same two clauses:
    /// <c>exit-submitted</c> rows carry an order id so <c>order_submitted</c> already covers them, and
    /// <c>exit-outcome-unknown</c> is named alongside its entry counterpart. This is a count of orders
    /// this loop put at the venue, and an exit is one — the operator's "how many orders today?" must
    /// not be answerable only for the half of the lifecycle that opens a position. What the count does
    /// NOT do any more is stop an exit: see <c>PaperAutomationArming.PermitsExit</c>.
    /// </para>
    /// </remarks>
    public async Task<int> CountSubmittedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.paper_automation_decisions " +
            "WHERE (order_submitted OR action IN ('outcome-unknown', 'exit-outcome-unknown')) " +
            "  AND session_trading_date = $1",
            connection)
        {
            Parameters = { new() { Value = tradingDate, NpgsqlDbType = NpgsqlDbType.Date } },
        };

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The exit keys already handed a closing order on this trading date.
    /// </summary>
    /// <remarks>
    /// Read out of <c>detail</c> rather than a column of its own, because this plan adds no migration
    /// (023 and 024 are spoken for) and <c>detail</c> is the jsonb column that exists for facts a
    /// decision carries that the fixed columns do not name. Both order-bearing exit actions are
    /// included for the reason the cap counts both: an <c>exit-outcome-unknown</c> closing order may
    /// well be resting at the venue, and re-submitting one because its outcome was ambiguous is the
    /// same defect as re-entering because a response was lost.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ExitKeysOrderedOnAsync(
        DateOnly tradingDate, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT detail->>'exitKey' FROM research.paper_automation_decisions " +
            "WHERE session_trading_date = $1 " +
            "  AND action IN ('exit-submitted', 'exit-outcome-unknown') " +
            "  AND detail->>'exitKey' IS NOT NULL",
            connection)
        {
            Parameters = { new() { Value = tradingDate, NpgsqlDbType = NpgsqlDbType.Date } },
        };

        var keys = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public Task<IReadOnlyList<AutomationDecision>> RecentAsync(int limit, CancellationToken cancellationToken) =>
        QueryAsync("ORDER BY decided_at DESC, decision_id DESC LIMIT $1", [new() { Value = limit }], cancellationToken);

    public Task<IReadOnlyList<AutomationDecision>> SubmittedOnAsync(DateOnly tradingDate, CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE (order_submitted OR action IN ('outcome-unknown', 'exit-outcome-unknown')) " +
            "  AND session_trading_date = $1 " +
            "ORDER BY decided_at DESC",
            [new() { Value = tradingDate, NpgsqlDbType = NpgsqlDbType.Date }],
            cancellationToken);

    private async Task<IReadOnlyList<AutomationDecision>> QueryAsync(
        string tail, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Required());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT decision_id, decided_at, trigger, armed, arm_state, arm_reason, session_calendar, " +
            "       session_label, session_trading_date, in_session, signal_state, signal_reason, " +
            "       study_run_id, action, action_reason, order_submitted, order_id, correlation_id, " +
            "       lifecycle_status, limit_price, limit_price_source, orders_this_session, order_cap, " +
            "       detail::text " +
            "FROM research.paper_automation_decisions " + tail,
            connection);

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var rows = new List<AutomationDecision>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AutomationDecision(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
                reader.GetBoolean(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetBoolean(15),
                reader.IsDBNull(16) ? null : reader.GetGuid(16),
                reader.IsDBNull(17) ? null : reader.GetGuid(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetDecimal(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetInt32(21),
                reader.GetInt32(22),
                reader.IsDBNull(23) ? null : reader.GetString(23)));
        }

        return rows;
    }

    private static NpgsqlParameter Nullable(string? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private string Required() =>
        ConnectionString is { Length: > 0 } connectionString
            ? connectionString
            : throw new InvalidOperationException(
                "Research persistence is not configured (no 'trading' connection string), so no automation " +
                "decision can be recorded. Automation does not act without an audit trail.");
}
