using Npgsql;
using TradingStuff.Contracts;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.ResearchService.Universe;

/// <summary>
/// Resolves the registered SPX option-node grid (seeded in migration 003) to live conIds and
/// records the assignment.
/// </summary>
/// <remarks>
/// v1 is bootstrap-only: strikes are picked by a fixed moneyness offset per node role (the
/// "VIX-scaled vol guess" the roadmap describes, simplified to fixed per-bucket offsets), never by
/// delta — there is no delta to target before anything has streamed. Delta-based drift detection
/// and reassignment (re-evaluating a node once its own recorded Greeks show it has drifted from its
/// target) is explicitly deferred to a follow-up; see docs/STATE.md. Node continuity is preserved
/// regardless: every assignment is a `node_assignments` row, and re-running the bootstrap is
/// idempotent — a node whose currently-assigned conId still matches is left untouched.
/// </remarks>
public sealed class NodeSelector(IConfiguration configuration, IbkrGatewayClient gateway, ILogger<NodeSelector> logger)
{
    private const short SelectorVersion = 1;
    private const int ChainWindow = 20;
    private const string Surface = "SPX";
    private const string Underlying = "SPX";

    private sealed record OptionNodeRow(
        short NodeId, string Role, int MinDte, int MaxDte, string TradingClass, OptionRight Right, decimal StrikeTarget);

    /// <summary>Resolves every node whose current assignment is missing or stale. Returns how many were (re)assigned.</summary>
    public async Task<int> BootstrapAssignmentsAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("No 'trading' connection string; node selection is disabled.");
            return 0;
        }

        var nodes = await LoadNodesAsync(connectionString, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var assigned = 0;

        foreach (var bucket in nodes.GroupBy(n => (n.MinDte, n.MaxDte, n.TradingClass)))
        {
            var (minDte, maxDte, tradingClass) = bucket.Key;
            var targetExpiration = today.AddDays((minDte + maxDte) / 2);

            var candidates = await gateway.GetChainAsync(Underlying, targetExpiration, tradingClass, ChainWindow, cancellationToken);

            if (candidates.Count == 0)
            {
                logger.LogWarning(
                    "No chain candidates for {TradingClass} near {Expiration} (DTE {MinDte}-{MaxDte}); " +
                    "{Count} node(s) left unassigned this pass.",
                    tradingClass, targetExpiration, minDte, maxDte, bucket.Count());
                continue;
            }

            // The chain endpoint's window is already centred on spot, so the median strike it
            // returns is a serviceable spot proxy without a dedicated spot-price lookup.
            var strikes = candidates.Select(c => c.Strike).Distinct().OrderBy(s => s).ToArray();
            var spotProxy = strikes[strikes.Length / 2];

            var picks = new List<(OptionNodeRow Node, OptionContract Contract)>();

            foreach (var node in bucket)
            {
                var targetStrike = spotProxy * (1 + node.StrikeTarget);

                var best = candidates
                    .Where(c => c.Right == node.Right)
                    .OrderBy(c => Math.Abs(c.Strike - targetStrike))
                    .FirstOrDefault();

                if (best is null)
                {
                    logger.LogWarning("No {Right} candidate near strike {Strike:F2} for node {Role}.", node.Right, targetStrike, node.Role);
                    continue;
                }

                picks.Add((node, best));
            }

            var resolved = await gateway.ResolveContractsAsync([.. picks.Select(p => p.Contract)], cancellationToken);

            foreach (var (node, contract) in picks)
            {
                if (!resolved.TryGetValue(contract.Key(), out var conId))
                {
                    logger.LogWarning("Could not resolve a conId for node {Role} ({Contract}).", node.Role, contract);
                    continue;
                }

                if (await UpsertAssignmentAsync(connectionString, node.NodeId, conId, NodeAssignmentReasons.Bootstrap, cancellationToken))
                {
                    assigned++;
                }
            }
        }

        logger.LogInformation("Node selection assigned or confirmed {Count} of {Total} registered nodes.", assigned, nodes.Count);
        return assigned;
    }

    /// <summary>Current (assigned_to IS NULL) node -> conId mapping, for orchestration and coverage.</summary>
    public async Task<IReadOnlyList<NodeAssignment>> GetCurrentAssignmentsAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT assignment_id, node_id, con_id, assigned_from, assigned_to, reason, selector_version " +
            "FROM research.node_assignments WHERE assigned_to IS NULL ORDER BY node_id",
            connection);

        var results = new List<NodeAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new NodeAssignment(
                reader.GetInt64(0), reader.GetInt16(1), reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5), reader.GetInt16(6)));
        }

        return results;
    }

    private static async Task<IReadOnlyList<OptionNodeRow>> LoadNodesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT node_id, role, min_dte, max_dte, trading_class, option_right, strike_target " +
            "FROM research.option_nodes WHERE surface = @surface ORDER BY node_id",
            connection);
        command.Parameters.AddWithValue("surface", Surface);

        var rows = new List<OptionNodeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var right = reader.GetString(5) == "C" ? OptionRight.Call : OptionRight.Put;

            rows.Add(new OptionNodeRow(
                reader.GetInt16(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), right, reader.GetDecimal(6)));
        }

        return rows;
    }

    /// <summary>Returns true when the assignment changed (or was newly created); false when the node already pointed at this conId.</summary>
    internal async Task<bool> UpsertAssignmentAsync(
        string connectionString, short nodeId, int conId, string reason, CancellationToken cancellationToken)
    {
        // One retry: research.node_assignments carries a partial UNIQUE INDEX on (node_id) WHERE
        // assigned_to IS NULL as the real guarantee (see migration 003's remarks — SELECT ... FOR
        // UPDATE alone does not prevent two "current" rows under Read Committed's blocked-lock
        // re-check semantics, verified against live Postgres). A concurrent writer for the same
        // node_id can make this transaction's INSERT lose that race; retrying re-reads the
        // now-committed state and makes the correct decision the second time. Only one caller
        // exists today (RecorderOrchestrator's sequential loop), so this path is not expected to
        // ever actually retry — it exists so a future concurrent caller fails safely, not silently.
        try
        {
            return await TryUpsertAssignmentAsync(connectionString, nodeId, conId, reason, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogWarning("Concurrent assignment update for node {NodeId}; retrying once.", nodeId);
            return await TryUpsertAssignmentAsync(connectionString, nodeId, conId, reason, cancellationToken);
        }
    }

    private async Task<bool> TryUpsertAssignmentAsync(
        string connectionString, short nodeId, int conId, string reason, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int? currentConId = null;

        await using (var current = new NpgsqlCommand(
            "SELECT con_id FROM research.node_assignments WHERE node_id = $1 AND assigned_to IS NULL FOR UPDATE",
            connection, transaction))
        {
            current.Parameters.AddWithValue(nodeId);
            var result = await current.ExecuteScalarAsync(cancellationToken);

            if (result is int existing)
            {
                currentConId = existing;
            }
        }

        if (currentConId == conId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (currentConId is not null)
        {
            await using var close = new NpgsqlCommand(
                "UPDATE research.node_assignments SET assigned_to = now() WHERE node_id = $1 AND assigned_to IS NULL",
                connection, transaction);
            close.Parameters.AddWithValue(nodeId);
            await close.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO research.node_assignments (node_id, con_id, assigned_from, assigned_to, reason, selector_version) " +
            "VALUES ($1, $2, now(), NULL, $3, $4)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(nodeId);
            insert.Parameters.AddWithValue(conId);
            insert.Parameters.AddWithValue(reason);
            insert.Parameters.AddWithValue(SelectorVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Node {NodeId} assigned to conId {ConId} ({Reason}), replacing {PreviousConId}.",
            nodeId, conId, reason, currentConId?.ToString() ?? "(none)");

        return true;
    }
}
