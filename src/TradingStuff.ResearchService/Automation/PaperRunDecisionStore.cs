using Npgsql;
using NpgsqlTypes;

namespace TradingStuff.ResearchService.Automation;

/// <summary>The only scope a decision may carry. Enforced again by migration 023's CHECK.</summary>
public static class PaperRunScopes
{
    /// <summary>
    /// The protocol amends the provenance refusal "for PAPER only, never live". There is deliberately
    /// no live constant here for a caller to reach for.
    /// </summary>
    public const string Paper = "paper";
}

/// <summary>One row of <c>research.paper_run_decisions</c>: an operator's signed authorization.</summary>
/// <param name="RevokedAt">NULL means active. A revoked decision is kept, never deleted.</param>
public sealed record PaperRunDecision(
    long DecisionId,
    DateTimeOffset DecidedAt,
    string Scope,
    string ProtocolRef,
    string Statement,
    string SignedBy,
    DateTimeOffset? RevokedAt,
    string? RevokedReason)
{
    public bool IsActive => RevokedAt is null;
}

/// <summary>The outcome of registering or revoking a decision. A refusal is a value, not an exception.</summary>
public sealed record PaperRunDecisionResult(PaperRunDecision? Decision, string? Refusal)
{
    public static PaperRunDecisionResult Registered(PaperRunDecision decision) => new(decision, null);

    public static PaperRunDecisionResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// The registered-decision table, as its consumers see it.
/// </summary>
/// <remarks>
/// An interface over a single Postgres implementation, for the same reason
/// <see cref="IPaperAutomationStore"/> is one: <see cref="ConstantExposureSignal"/>'s behaviour —
/// that it trades only while an unrevoked row exists and names exactly what is missing otherwise —
/// has to be provable without a database, and it has to be provable that a store which THROWS does
/// not turn into a trade. Neither is a property of SQL.
/// </remarks>
public interface IPaperRunDecisionStore
{
    /// <summary>The unrevoked decision, or null if there is none. Never throws to mean "none".</summary>
    Task<PaperRunDecision?> GetActiveAsync(CancellationToken cancellationToken);

    Task<PaperRunDecisionResult> RegisterAsync(
        string statement, string signedBy, string protocolRef, CancellationToken cancellationToken);

    Task<PaperRunDecisionResult> RevokeActiveAsync(string? reason, CancellationToken cancellationToken);

    /// <summary>Recent decisions, revoked ones included — a withdrawn authorization stays visible.</summary>
    Task<IReadOnlyList<PaperRunDecision>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// <c>research.paper_run_decisions</c> (migration 023).
/// </summary>
/// <remarks>
/// <para>
/// <b>No code path here writes a row except the endpoint an operator calls.</b> There is no seeding,
/// no "create if missing", and no default decision: the protocol's Phase 2 entry condition is a human
/// sign-off, and a decision that software can manufacture authorizes nothing.
/// </para>
/// <para>
/// <b>The single-active rule is enforced twice, on purpose.</b> <see cref="RegisterAsync"/> reads
/// first so the caller gets a refusal naming the decision already in force; the partial unique index
/// is what makes two concurrent registrations impossible. The read alone would be a check-then-act
/// race, and the index alone would surface as a raw constraint violation.
/// </para>
/// </remarks>
public sealed class PaperRunDecisionStore(IConfiguration configuration) : IPaperRunDecisionStore
{
    private const string Columns =
        "decision_id, decided_at, scope, protocol_ref, statement, signed_by, revoked_at, revoked_reason";

    public string? ConnectionString => configuration.GetConnectionString("trading");

    public async Task<PaperRunDecision?> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM research.paper_run_decisions WHERE revoked_at IS NULL", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<PaperRunDecisionResult> RegisterAsync(
        string statement, string signedBy, string protocolRef, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(statement) || string.IsNullOrWhiteSpace(signedBy))
        {
            return PaperRunDecisionResult.Refused(
                "A decision needs both a statement and a signer. An authorization nobody can attribute " +
                "is not an authorization.");
        }

        if (await GetActiveAsync(cancellationToken) is { } existing)
        {
            return PaperRunDecisionResult.Refused(
                $"Decision {existing.DecisionId}, signed by {existing.SignedBy} at " +
                $"{existing.DecidedAt:yyyy-MM-dd HH:mm}Z, is already in force. Revoke it before registering " +
                "another; two live authorizations would make it ambiguous which one the orders were placed under.");
        }

        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO research.paper_run_decisions (decided_at, scope, protocol_ref, statement, signed_by)
             VALUES (now(), $1, $2, $3, $4)
             RETURNING {Columns}
             """,
            connection)
        {
            Parameters =
            {
                new() { Value = PaperRunScopes.Paper },
                new() { Value = protocolRef },
                new() { Value = statement.Trim() },
                new() { Value = signedBy.Trim() },
            },
        };

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            return PaperRunDecisionResult.Registered(Read(reader));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The index caught what the read above could not: another registration committed in
            // between. Reported as the same refusal rather than a 500 — the caller's request was
            // legitimate and simply lost the race.
            return PaperRunDecisionResult.Refused(
                "Another decision was registered concurrently and is now in force. Read " +
                "GET /research/paper-run/decision before registering another.");
        }
    }

    public async Task<PaperRunDecisionResult> RevokeActiveAsync(string? reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"""
             UPDATE research.paper_run_decisions
             SET revoked_at = now(), revoked_reason = $1
             WHERE revoked_at IS NULL
             RETURNING {Columns}
             """,
            connection)
        {
            Parameters = { new() { Value = (object?)reason ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text } },
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // Not an error and not a success. Reporting "revoked" for a table with nothing in it would
            // tell an operator who just pressed stop that they had stopped something.
            return PaperRunDecisionResult.Refused(
                "There is no active decision to revoke. Automation is already refusing entry for want of one.");
        }

        return PaperRunDecisionResult.Registered(Read(reader));
    }

    public async Task<IReadOnlyList<PaperRunDecision>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM research.paper_run_decisions ORDER BY decision_id DESC LIMIT $1",
            connection)
        {
            Parameters = { new() { Value = limit } },
        };

        var rows = new List<PaperRunDecision>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private static PaperRunDecision Read(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        // Throws rather than returning "no decision": an unreachable database means authorization is
        // UNKNOWN, and the signal has to be able to say that instead of reporting it as absent. Absent
        // and unknown both refuse entry, but only one of them is a fault someone has to fix.
        if (ConnectionString is not { Length: > 0 } connectionString)
        {
            throw new InvalidOperationException(
                "Research persistence is not configured (no 'trading' connection string), so no paper-run " +
                "decision can be read or registered.");
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
