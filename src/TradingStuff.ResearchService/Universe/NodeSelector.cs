using System.Collections.Concurrent;
using System.Globalization;
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
/// <para>
/// <b>Selector version 2 exists because version 1's bootstrap was broken end to end, and silently.</b>
/// It asked the gateway for a chain with <c>window: 20</c>, which the gateway reads as a half-count
/// of strikes — the 41 strikes nearest spot, about ±1.3% of SPX at 7,440 — while this code reasoned
/// about it as if it covered a moneyness range. Every seeded target beyond ±2.5% therefore fell
/// outside the window, and an unbounded <c>OrderBy(|strike − target|).FirstOrDefault()</c> answered
/// with the window's edge strike. Nine roles per DTE bucket collapsed onto four contracts; 54 roles
/// onto roughly 24 conIds. Nothing caught it: the edge strikes are real, liquid contracts that tick
/// normally, so all 54 nodes reported near-100% coverage and cleared the ≥95% gate, and nothing
/// anywhere compared an assigned strike against the target it had been chosen for. Two adversarial
/// reviews passed over it.
/// </para>
/// <para>
/// So the three properties this class now holds, in the order they matter:
/// <list type="number">
/// <item><b>The reference price is read, never inferred.</b> Targets are computed from the spot the
/// gateway actually used to cut the window (<see cref="ChainWindow.ReferencePrice"/>). The previous
/// version derived a "spot proxy" as the median strike of the response, whose stated precondition —
/// that the window is centred on spot — was exactly what the gateway's degraded path silently
/// dropped.</item>
/// <item><b>A pick outside its target's neighbourhood is refused, not approximated.</b> The target
/// must be bracketed by the window's listed strikes AND the chosen strike must land within
/// <see cref="MaxStrikeDeviation"/> of it. The bracket test is the load-bearing one: an edge clamp
/// cannot satisfy "there is a listed strike on both sides of the target", whatever the window width
/// or strike increment, so the specific failure above cannot recur silently even if every constant
/// here is later tuned wrong.</item>
/// <item><b>One conId cannot play two roles.</b> Enforced here rather than in the schema; migration
/// 011 records why.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class NodeSelector(
    IConfiguration configuration,
    IbkrGatewayClient gateway,
    OptionChainClient chains,
    ILogger<NodeSelector> logger)
{
    // Bumped from 1 with the fixes above. The registry is version-stamped precisely so a later study
    // can tell assignments made by a broken selector from assignments made by this one; leaving it
    // at 1 would fold the collapsed grid and the corrected grid into one indistinguishable series.
    private const short SelectorVersion = 2;

    /// <summary>
    /// Chain window half-width, as a FRACTION of spot — never a strike count.
    /// </summary>
    /// <remarks>
    /// The deepest target seeded by migration 003 is −0.150, so 0.20 spans every node with room to
    /// spare. Sized in moneyness because sizing it in strikes is what broke: the reach of N strikes
    /// depends on the local increment, which is 5 points near the money for SPX, so the strike count
    /// needed to cover ±15% is ~423 per side at a 7,440 spot (measured live 2026-08-01) and would
    /// change with the index level, the expiration, and the listing regime.
    /// <para>
    /// The cost of the wider window is close to zero, and lower than the narrow one's: the gateway
    /// serves it from a single <c>reqContractDetails</c> per bucket that also warms its conId cache,
    /// so this pass now spends 6 paced requests instead of 6 + 54. No market-data line is taken —
    /// lines are spent by <c>RecorderOrchestrator</c> on the 54 assigned nodes, and that count is
    /// fixed by the grid, not by the window. (Under the old selector the collapse actually WASTED
    /// lines: ~30 of the 80-line research budget went on double-subscribing contracts already being
    /// recorded under another role.)
    /// </para>
    /// </remarks>
    private const decimal ChainMoneynessHalfWidth = 0.20m;

    /// <summary>
    /// How far a chosen strike may sit from its target, as a fraction of the reference price.
    /// </summary>
    /// <remarks>
    /// Grounded in what TWS actually lists, not picked for roundness. Measured live 2026-08-01 with
    /// SPX at 7437.63, over the real per-expiration ladders: the worst deviation of a genuinely
    /// nearest listed strike was 0.75% of spot (SPXW 2026-09-14, +11% target 8255.8 → nearest listed
    /// 8200; that expiration lists 25- and 100-point increments, not 5). 1.5% is double the worst
    /// observed sparse ladder and an order of magnitude below the failure it is meant to reject —
    /// the collapse put the 5Δ put 13.6% of spot from its target. A tighter bound would start
    /// refusing legitimate thin expirations; a looser one would start accepting nodes that are not
    /// what their role says.
    /// </remarks>
    private const decimal MaxStrikeDeviation = 0.015m;

    private const string Surface = "SPX";
    private const string Underlying = "SPX";

    // Why a node has no assignment, from the most recent pass, for /research/nodes. In memory
    // because it is a fact about the last pass rather than about the assignment history: the
    // orchestrator re-runs every 2 minutes, so a restart repopulates this within one cycle, and
    // persisting per-pass refusals would put a row-per-pass churn into the table that Phase 4 reads
    // as role -> conId ground truth.
    private readonly ConcurrentDictionary<short, (string Reason, string Detail)> _refusals = new();

    internal sealed record OptionNodeRow(
        short NodeId, string Role, int MinDte, int MaxDte, string TradingClass, OptionRight Right, decimal StrikeTarget);

    /// <summary>What the selection produced for one node, before conIds and duplicates are settled.</summary>
    internal sealed record StrikePick(
        OptionContract? Contract,
        decimal TargetStrike,
        decimal Deviation,
        string? Refusal,
        string? RefusalDetail);

    private sealed record NodePick(
        OptionNodeRow Node,
        OptionContract Contract,
        decimal TargetStrike,
        decimal ReferencePrice,
        decimal Deviation);

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
        var refusals = new Dictionary<short, (string Reason, string Detail)>();
        var picks = new List<NodePick>();

        foreach (var bucket in nodes.GroupBy(n => (n.MinDte, n.MaxDte, n.TradingClass)))
        {
            var (minDte, maxDte, tradingClass) = bucket.Key;
            var targetExpiration = today.AddDays((minDte + maxDte) / 2);

            var window = await chains.GetChainAsync(
                Underlying, targetExpiration, tradingClass, ChainMoneynessHalfWidth, cancellationToken);

            // One check, not two: a window that is not spot-centred carries no reference price and no
            // contracts, so there is no half-usable middle state to reason about. Refusing the whole
            // bucket here is what the old code could not do — it had no way to tell a degraded chain
            // from a healthy one, so it went on to invent a spot proxy from the median strike of
            // whatever came back.
            if (!window.SpotCentred || window.ReferencePrice is not { } referencePrice || window.Contracts.Count == 0)
            {
                var detail =
                    $"No spot-centred chain window for {tradingClass} near {targetExpiration:yyyy-MM-dd} " +
                    $"(DTE {minDte}-{maxDte}): {window.Unavailable ?? "the gateway returned no contracts"}.";

                logger.LogWarning(
                    "{Count} node(s) left unassigned this pass. {Detail}", bucket.Count(), detail);

                foreach (var node in bucket)
                {
                    refusals[node.NodeId] = (NodeUnassignedReasons.ChainUnavailable, detail);
                }

                continue;
            }

            if (window.Expiration is { } listed && !IsInBucket(listed, today, minDte, maxDte))
            {
                // Not a refusal. TWS lists what it lists — the SPX monthly series genuinely had no
                // expiration inside the 76-105 DTE bucket on 2026-08-01 (10-15 was 75 DTE, 11-19 was
                // 110) — and refusing would leave nine nodes dark over a one-day boundary miss. The
                // real hazard when two buckets land on one expiration is that they then select the
                // same contracts, and THAT is caught as a duplicate conId below. This is reported
                // per node so the mismatch between a role's label and its tenure is visible.
                logger.LogWarning(
                    "The nearest listed {TradingClass} expiration to DTE {MinDte}-{MaxDte} is {Expiration} " +
                    "({Dte} DTE), outside the bucket. Its {Count} node(s) are labelled for a term they do not hold.",
                    tradingClass, minDte, maxDte, listed, listed.DayNumber - today.DayNumber, bucket.Count());
            }

            foreach (var node in bucket)
            {
                var pick = PickStrike(referencePrice, node.StrikeTarget, node.Right, window.Contracts);

                if (pick.Contract is null)
                {
                    logger.LogWarning(
                        "Node {Role} left unassigned ({Reason}): {Detail}",
                        node.Role, pick.Refusal, pick.RefusalDetail);

                    refusals[node.NodeId] = (pick.Refusal!, pick.RefusalDetail!);
                    continue;
                }

                picks.Add(new NodePick(node, pick.Contract, pick.TargetStrike, referencePrice, pick.Deviation));
            }
        }

        // Resolution is a cache hit at the gateway: the chain window came from a reqContractDetails
        // over the whole expiration, which already told it every conId in play.
        var resolved = picks.Count == 0
            ? new Dictionary<OptionContractKey, int>()
            : await gateway.ResolveContractsAsync([.. picks.Select(p => p.Contract)], cancellationToken);

        var resolvedPicks = new List<(NodePick Pick, int ConId)>(picks.Count);

        foreach (var pick in picks)
        {
            if (!resolved.TryGetValue(pick.Contract.Key(), out var conId))
            {
                var detail = $"The broker did not resolve {Describe(pick.Contract)} to a conId.";
                logger.LogWarning("Node {Role} left unassigned ({Reason}): {Detail}",
                    pick.Node.Role, NodeUnassignedReasons.UnresolvedContract, detail);

                refusals[pick.Node.NodeId] = (NodeUnassignedReasons.UnresolvedContract, detail);
                continue;
            }

            resolvedPicks.Add((pick, conId));
        }

        var current = await GetCurrentAssignmentsAsync(cancellationToken);
        var accepted = ResolveRoleCollisions(resolvedPicks, current, refusals, logger);

        var assigned = 0;

        foreach (var (pick, conId) in accepted)
        {
            var provenance = new AssignmentProvenance(
                pick.Contract.Expiration, pick.Contract.Strike, pick.TargetStrike, pick.ReferencePrice);

            if (await UpsertAssignmentAsync(
                    connectionString, pick.Node.NodeId, conId, NodeAssignmentReasons.Bootstrap, cancellationToken, provenance))
            {
                assigned++;
            }
        }

        _refusals.Clear();

        foreach (var (nodeId, refusal) in refusals)
        {
            _refusals[nodeId] = refusal;
        }

        // Bound and Distinct are printed side by side deliberately: they are equal iff every role has
        // its own contract, and the one number nobody ever looked at under the old selector would
        // have read "54 bound over 24 distinct conId(s)" every single pass.
        logger.LogInformation(
            "Node selection bound {Bound} of {Total} registered nodes over {Distinct} distinct conId(s) " +
            "({Changed} changed this pass); {Refused} node(s) refused.",
            accepted.Count, nodes.Count, accepted.Select(a => a.ConId).Distinct().Count(), assigned, refusals.Count);

        return assigned;
    }

    /// <summary>
    /// Picks the listed contract for one node role, or refuses and says why.
    /// </summary>
    /// <remarks>
    /// Pure, and separated out so the property that matters is testable without a socket or a
    /// database: given a realistic SPX ladder and a window that does not reach a node's target, this
    /// returns a refusal rather than the nearest thing to hand.
    /// </remarks>
    internal static StrikePick PickStrike(
        decimal referencePrice,
        decimal moneynessTarget,
        OptionRight right,
        IReadOnlyList<OptionContract> candidates)
    {
        var targetStrike = referencePrice * (1m + moneynessTarget);

        var ofRight = candidates.Where(c => c.Right == right).ToArray();

        if (ofRight.Length == 0)
        {
            return new StrikePick(
                null, targetStrike, 0m, NodeUnassignedReasons.NoCandidates,
                $"The chain window holds no {right} contracts.");
        }

        var low = ofRight.Min(c => c.Strike);
        var high = ofRight.Max(c => c.Strike);

        // The structural check. "Nearest strike to the target" is only meaningful when the target is
        // inside the ladder; outside it, the nearest strike is by definition the ladder's edge, and
        // an edge is what nine different targets all resolve to when the window is too narrow. This
        // cannot be satisfied by a clamp, at any window width or strike increment, which is what
        // makes the collapse structurally unrepeatable rather than merely unlikely.
        if (targetStrike < low || targetStrike > high)
        {
            return new StrikePick(
                null, targetStrike, 0m, NodeUnassignedReasons.TargetOutsideWindow,
                string.Create(CultureInfo.InvariantCulture,
                    $"Target strike {targetStrike:F2} ({moneynessTarget:+0.###;-0.###;0} of {referencePrice:F2}) is " +
                    $"outside the window's {right} strikes {low:F2}-{high:F2}, so the nearest listed strike would " +
                    $"be the window's edge rather than a match."));
        }

        var best = ofRight.OrderBy(c => Math.Abs(c.Strike - targetStrike)).ThenBy(c => c.Strike).First();
        var deviation = (best.Strike - targetStrike) / referencePrice;

        if (Math.Abs(deviation) > MaxStrikeDeviation)
        {
            return new StrikePick(
                null, targetStrike, deviation, NodeUnassignedReasons.StrikeDeviation,
                string.Create(CultureInfo.InvariantCulture,
                    $"The nearest listed {right} strike to {targetStrike:F2} is {best.Strike:F2}, " +
                    $"{deviation:P2} of spot away — beyond the {MaxStrikeDeviation:P2} tolerance."));
        }

        return new StrikePick(best, targetStrike, deviation, null, null);
    }

    /// <summary>
    /// Settles the "one conId, one role" invariant across this pass's picks and the assignments that
    /// already exist, refusing the losers rather than writing a contract under two roles.
    /// </summary>
    /// <remarks>
    /// Continuity wins first: a node already recording a conId keeps it, so a contested contract
    /// never causes a needless tenure break in <c>node_assignments</c>. Otherwise the better fit for
    /// its own target wins, because that is the role the contract genuinely belongs to; node id
    /// breaks the remaining tie so the outcome does not depend on enumeration order.
    /// </remarks>
    private static List<(NodePick Pick, int ConId)> ResolveRoleCollisions(
        IReadOnlyList<(NodePick Pick, int ConId)> picks,
        IReadOnlyList<NodeAssignment> current,
        Dictionary<short, (string Reason, string Detail)> refusals,
        ILogger logger)
    {
        var currentByNode = current.ToDictionary(a => a.NodeId, a => a.ConId);

        // A conId still held after this pass by a node that is NOT re-picking it stays taken: the
        // pass only closes a node's tenure when it writes that node a new assignment.
        var repicking = picks.Select(p => p.Pick.Node.NodeId).ToHashSet();

        var held = current
            .Where(a => !repicking.Contains(a.NodeId))
            .GroupBy(a => a.ConId)
            .ToDictionary(g => g.Key, g => g.First().NodeId);

        var accepted = new List<(NodePick Pick, int ConId)>(picks.Count);

        foreach (var contest in picks.GroupBy(p => p.ConId))
        {
            var claimants = contest
                .OrderByDescending(c => currentByNode.TryGetValue(c.Pick.Node.NodeId, out var existing) && existing == contest.Key)
                .ThenBy(c => Math.Abs(c.Pick.Deviation))
                .ThenBy(c => c.Pick.Node.NodeId)
                .ToArray();

            int start;

            if (held.TryGetValue(contest.Key, out var holder))
            {
                // Somebody outside this pass owns it; every claimant loses.
                start = claimants.Length;

                foreach (var loser in claimants)
                {
                    Refuse(loser, $"conId {contest.Key} is already assigned to node {holder}, which did not re-pick this pass.");
                }
            }
            else
            {
                accepted.Add(claimants[0]);
                start = 1;
            }

            for (var i = start; i < claimants.Length; i++)
            {
                Refuse(claimants[i],
                    $"conId {contest.Key} was selected by {claimants.Length} roles this pass; " +
                    $"node {claimants[0].Pick.Node.Role} is the better fit for it.");
            }
        }

        return accepted;

        void Refuse((NodePick Pick, int ConId) claimant, string detail)
        {
            logger.LogWarning(
                "Node {Role} left unassigned ({Reason}): {Detail}",
                claimant.Pick.Node.Role, NodeUnassignedReasons.DuplicateConId, detail);

            refusals[claimant.Pick.Node.NodeId] = (NodeUnassignedReasons.DuplicateConId, detail);
        }
    }

    private static bool IsInBucket(DateOnly expiration, DateOnly today, int minDte, int maxDte)
    {
        var dte = expiration.DayNumber - today.DayNumber;
        return dte >= minDte && dte <= maxDte;
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

    /// <summary>
    /// Every registered node with its target, its assignment, and the distance between them.
    /// </summary>
    /// <remarks>
    /// Reports the whole registered grid rather than just the assigned rows, for the same reason
    /// CoverageMonitor had to be taught to union in nodes with zero ticks: a query over
    /// <c>node_assignments</c> cannot emit a row for a node that has no assignment, so an unassigned
    /// node renders as absence, and absence renders as health.
    /// </remarks>
    public async Task<NodeGridReport> GetGridReportAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("trading");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new NodeGridReport(0, 0, 0, 0, []);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT n.node_id, n.role, n.trading_class, n.option_right, n.min_dte, n.max_dte, n.strike_target, " +
            "       a.con_id, a.expiration, a.strike, a.target_strike, a.reference_price, " +
            "       a.assigned_from, a.reason, a.selector_version " +
            "FROM research.option_nodes n " +
            "LEFT JOIN research.node_assignments a ON a.node_id = n.node_id AND a.assigned_to IS NULL " +
            "WHERE n.surface = @surface ORDER BY n.node_id",
            connection);
        command.Parameters.AddWithValue("surface", Surface);

        var rows = new List<NodeGridEntry>();
        var conIdCounts = new Dictionary<int, int>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var nodeId = reader.GetInt16(0);
                var minDte = reader.GetInt32(4);
                var maxDte = reader.GetInt32(5);

                int? conId = reader.IsDBNull(7) ? null : reader.GetInt32(7);
                DateOnly? expiration = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8);
                decimal? strike = reader.IsDBNull(9) ? null : reader.GetDecimal(9);
                decimal? targetStrike = reader.IsDBNull(10) ? null : reader.GetDecimal(10);
                decimal? referencePrice = reader.IsDBNull(11) ? null : reader.GetDecimal(11);

                if (conId is { } id)
                {
                    conIdCounts[id] = conIdCounts.GetValueOrDefault(id) + 1;
                }

                var deviation = strike is { } s && targetStrike is { } t && referencePrice is { } r && r > 0m
                    ? (s - t) / r
                    : (decimal?)null;

                _refusals.TryGetValue(nodeId, out var refusal);

                rows.Add(new NodeGridEntry(
                    nodeId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3) == "C" ? OptionRight.Call : OptionRight.Put,
                    minDte,
                    maxDte,
                    reader.GetDecimal(6),
                    conId,
                    expiration,
                    strike,
                    targetStrike,
                    referencePrice,
                    deviation,
                    reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetInt16(14),
                    // An unassigned node has no expiration to judge, so it is not reported as a
                    // bucket mismatch on top of being unassigned.
                    expiration is not { } e || IsInBucket(e, today, minDte, maxDte),
                    DuplicateConId: false,
                    conId is null ? refusal.Reason : null,
                    conId is null ? refusal.Detail : null));
            }
        }

        var nodes = rows
            .Select(row => row.ConId is { } id && conIdCounts[id] > 1 ? row with { DuplicateConId = true } : row)
            .ToArray();

        var assigned = nodes.Count(n => n.ConId is not null);

        return new NodeGridReport(
            nodes.Length, assigned, nodes.Length - assigned, conIdCounts.Count, nodes);
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

    /// <summary>What the assignment was selected for, recorded beside it (migration 011).</summary>
    internal sealed record AssignmentProvenance(
        DateOnly Expiration, decimal Strike, decimal TargetStrike, decimal ReferencePrice);

    /// <summary>Returns true when the assignment changed (or was newly created); false when the node already pointed at this conId.</summary>
    internal async Task<bool> UpsertAssignmentAsync(
        string connectionString,
        short nodeId,
        int conId,
        string reason,
        CancellationToken cancellationToken,
        AssignmentProvenance? provenance = null)
    {
        // One retry: research.node_assignments carries a partial UNIQUE INDEX on (node_id) WHERE
        // assigned_to IS NULL as the real guarantee (see migration 003's remarks — SELECT ... FOR
        // UPDATE alone does not prevent two "current" rows under Read Committed's blocked-lock
        // re-check semantics, verified against live Postgres). A concurrent writer for the same
        // node_id can make this transaction's INSERT lose that race; retrying re-reads the
        // now-committed state and makes the correct decision the second time. Only one caller
        // exists today (RecorderOrchestrator's sequential loop), so this path is not expected to
        // ever actually retry — it exists so a future concurrent caller fails safely, not silently.
        //
        // Note this is why "one conId, one role" is NOT a database constraint: it would raise the
        // same SQLSTATE for a permanent condition and be retried into the same failure. See
        // migration 011.
        try
        {
            return await TryUpsertAssignmentAsync(connectionString, nodeId, conId, reason, provenance, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogWarning("Concurrent assignment update for node {NodeId}; retrying once.", nodeId);
            return await TryUpsertAssignmentAsync(connectionString, nodeId, conId, reason, provenance, cancellationToken);
        }
    }

    private async Task<bool> TryUpsertAssignmentAsync(
        string connectionString,
        short nodeId,
        int conId,
        string reason,
        AssignmentProvenance? provenance,
        CancellationToken cancellationToken)
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
            "INSERT INTO research.node_assignments " +
            "(node_id, con_id, assigned_from, assigned_to, reason, selector_version, " +
            " expiration, strike, target_strike, reference_price) " +
            "VALUES ($1, $2, now(), NULL, $3, $4, $5, $6, $7, $8)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(nodeId);
            insert.Parameters.AddWithValue(conId);
            insert.Parameters.AddWithValue(reason);
            insert.Parameters.AddWithValue(SelectorVersion);
            insert.Parameters.AddWithValue((object?)provenance?.Expiration ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)provenance?.Strike ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)provenance?.TargetStrike ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)provenance?.ReferencePrice ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Node {NodeId} assigned to conId {ConId} ({Reason}), replacing {PreviousConId}. {Selection}",
            nodeId, conId, reason, currentConId?.ToString() ?? "(none)",
            provenance is null
                ? "(no selection provenance recorded)"
                : $"strike {provenance.Strike} for target {provenance.TargetStrike:F2} at spot {provenance.ReferencePrice}");

        return true;
    }

    private static string Describe(OptionContract contract) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{contract.TradingClass ?? contract.Underlying} {contract.Expiration:yyyy-MM-dd} {contract.Strike} {contract.Right}");
}
