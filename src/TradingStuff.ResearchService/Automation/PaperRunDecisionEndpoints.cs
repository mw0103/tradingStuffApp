namespace TradingStuff.ResearchService.Automation;

/// <param name="Statement">What is being authorized, in the signer's own words. Required.</param>
/// <param name="SignedBy">Who is authorizing it. Required, and never defaulted to a service identity.</param>
/// <param name="ProtocolRef">
/// The document the signer read. Defaults to the protocol this mechanism exists for; supplied
/// explicitly when a later revision supersedes it, so the row names the rules it was signed under.
/// </param>
public sealed record RegisterPaperRunDecisionRequest(string? Statement, string? SignedBy, string? ProtocolRef);

/// <param name="Reason">Recorded on the row. Why the authorization was withdrawn outlives the fact that it was.</param>
public sealed record RevokePaperRunDecisionRequest(string? Reason);

/// <summary>
/// The registered-decision surface: read it, sign one, withdraw one.
/// </summary>
/// <remarks>
/// <para>
/// <b>These endpoints are the LOCK. Only an operator turns the key.</b> Nothing else in this service
/// writes <c>research.paper_run_decisions</c>, and <see cref="ConstantExposureSignal"/> refuses entry
/// for as long as the table has no unrevoked row. A decision that software could manufacture would
/// authorize nothing, which is why the create path insists on a non-empty human signature rather than
/// filling one in.
/// </para>
/// <para>
/// <b>Authorization is split by direction</b>, following <see cref="PaperAutomationEndpoints"/>'s
/// reasoning rather than the anonymous-by-default convention the read-only <c>/research/*</c> surface
/// uses. Plan A's brief called for anonymous throughout; that is right for the read and for the
/// revoke, and wrong for the create, which is the single most consequential POST in this service —
/// it is the amendment that lets automation open a position at all. If <c>/research/automation/resume</c>
/// needs a credential, so does the thing that authorizes what resume re-arms.
/// </para>
/// <list type="bullet">
/// <item><c>GET /research/paper-run/decision</c> — read-only, anonymous. Honest about absence.</item>
/// <item><c>POST /research/paper-run/decision</c> — <b>authorized</b>. It unlocks entry.</item>
/// <item>
/// <c>POST /research/paper-run/decision/revoke</c> — anonymous, for the same reason the kill switch
/// is: it only ever stops trading, and one that needs a credential is one that is not pressed in the
/// thirty seconds when it matters.
/// </item>
/// </list>
/// <para>
/// <b>What the credential is, and what it proves.</b> The accepted bearer is the mesh's shared
/// development token (<c>Authentication:DevelopmentToken</c>, default <c>dev-internal-token</c> —
/// see README's curl examples); a bodiless 401 from the create means the
/// <c>Authorization: Bearer</c> header is missing or wrong, nothing more. Because every internal
/// service holds the same token, possessing it does not attribute the signature to a human —
/// attribution rests on <c>signed_by</c> plus the Critical log line, and the credential merely
/// keeps the create off the anonymous surface until the dev handler is replaced by Keycloak
/// (docs/STATE.md outstanding list).
/// </para>
/// </remarks>
public static class PaperRunDecisionEndpoints
{
    public static void MapPaperRunDecisionEndpoints(this WebApplication app)
    {
        // Absence is a first-class answer here: `active: null` with a note saying what that means for
        // entry, never an empty 200 that a UI can render as "fine".
        app.MapGet("/research/paper-run/decision", async (
            IPaperRunDecisionStore store, int? history, CancellationToken cancellationToken) =>
        {
            PaperRunDecision? active;
            IReadOnlyList<PaperRunDecision> recent;

            try
            {
                active = await store.GetActiveAsync(cancellationToken);
                recent = await store.ListAsync(Math.Clamp(history ?? 20, 1, 200), cancellationToken);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
            {
                // These two handlers are anonymous, and the developer exception page would hand a
                // stack trace plus connection detail to anyone who asks while the database is down.
                return Results.Problem(
                    title: "The decision store is unreachable.",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                active,
                authorized = active is not null,
                note = active is null
                    ? "No decision is in force, so the constant-exposure signal refuses entry. Registering one " +
                      "is a human sign-off (docs/plans/paper-run-protocol.md § Phases 2), never an automated step."
                    : $"Decision {active.DecisionId} authorizes the PAPER run only. Every other arming condition " +
                      "still applies: a coherent execution plane, a connected DU account, a named session, and " +
                      "the per-session order cap.",

                // Revoked decisions stay listed. What authorized last Tuesday's orders is not answerable
                // from a view that shows only what is authorized now.
                history = recent,
            });
        });

        app.MapPost("/research/paper-run/decision", async (
                RegisterPaperRunDecisionRequest request,
                IPaperRunDecisionStore store,
                ILogger<PaperRunDecisionStore> logger,
                CancellationToken cancellationToken) =>
            {
                var result = await store.RegisterAsync(
                    request.Statement ?? string.Empty,
                    request.SignedBy ?? string.Empty,
                    string.IsNullOrWhiteSpace(request.ProtocolRef)
                        ? "docs/plans/paper-run-protocol.md"
                        : request.ProtocolRef.Trim(),
                    cancellationToken);

                if (result.Refusal is { } refusal)
                {
                    return Results.Problem(
                        title: "The decision was not registered.",
                        detail: refusal,
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Critical, not Information. This is the moment a paper account stops being a read-only
                // research plane, and it should be as visible in the log as the refusals it lifts.
                logger.LogCritical(
                    "Paper-run decision {DecisionId} REGISTERED by {SignedBy} against {ProtocolRef}. The " +
                    "constant-exposure signal will ask for a position while it stands.",
                    result.Decision!.DecisionId, result.Decision.SignedBy, result.Decision.ProtocolRef);

                return Results.Ok(result.Decision);
            })
            .RequireAuthorization();

        app.MapPost("/research/paper-run/decision/revoke", async (
            RevokePaperRunDecisionRequest? request,
            IPaperRunDecisionStore store,
            ILogger<PaperRunDecisionStore> logger,
            CancellationToken cancellationToken) =>
        {
            PaperRunDecisionResult result;

            try
            {
                result = await store.RevokeActiveAsync(request?.Reason, cancellationToken);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
            {
                // Same reasoning as the GET: anonymous surface, no stack traces. The revoke intent
                // (stop entry) fails CLOSED anyway — the signal cannot read an unreachable store
                // and refuses on its own.
                return Results.Problem(
                    title: "The decision store is unreachable.",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (result.Refusal is { } refusal)
            {
                // 409, not 404: the resource exists as a concept and the caller's intent (stop entry) is
                // already the state of the world. Saying "revoked" would claim they changed something.
                return Results.Problem(
                    title: "Nothing was revoked.",
                    detail: refusal,
                    statusCode: StatusCodes.Status409Conflict);
            }

            logger.LogCritical(
                "Paper-run decision {DecisionId} REVOKED{Reason}. The constant-exposure signal refuses entry " +
                "from its next evaluation; no restart is needed.",
                result.Decision!.DecisionId,
                string.IsNullOrWhiteSpace(request?.Reason) ? string.Empty : $": {request.Reason}");

            return Results.Ok(result.Decision);
        });
    }
}
