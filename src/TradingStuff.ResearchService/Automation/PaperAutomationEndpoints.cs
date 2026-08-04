namespace TradingStuff.ResearchService.Automation;

/// <param name="Reason">Recorded on the kill-switch status so an operator can see why it is off.</param>
public sealed record KillSwitchRequest(string? Reason);

/// <param name="LimitPrice">
/// The net debit to submit. Required: the manual endpoint exists for the case where no live book can
/// price the spread, and an endpoint that silently fell back to a computed price when the operator
/// omitted one would be a second automated path wearing a manual label.
/// </param>
/// <param name="AcknowledgeOutsideSession">
/// Must be true to act outside a session the calendar can name. Recorded on the row.
/// </param>
public sealed record ManualOrderRequest(decimal LimitPrice, bool AcknowledgeOutsideSession);

/// <summary>
/// The automation surface: one read, one kill, one release, one explicitly-manual order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization is split by direction, not applied uniformly.</b> Everything else under
/// <c>/research/*</c> is anonymous because it is a read-only diagnostic surface for a local-first
/// operator UI. These four are not all reads, so they are decided individually, which is what
/// Program.cs's comment on that prefix requires of anything that mutates:
/// </para>
/// <list type="bullet">
/// <item><c>GET /research/automation</c> — read-only, anonymous, like every other status surface.</item>
/// <item>
/// <c>POST /research/automation/kill</c> — anonymous <b>on purpose</b>. It only ever stops trading. A
/// kill switch that needs a credential is a kill switch that is not pressed in the thirty seconds
/// when it matters, and the worst an unauthorised caller can achieve is that automation does nothing.
/// The UI's button posts here.
/// </item>
/// <item>
/// <c>POST /research/automation/resume</c> — <b>authorized</b>. Re-arming is the dangerous direction.
/// </item>
/// <item>
/// <c>POST /research/automation/manual-order</c> — <b>authorized</b>. It submits a real order to the
/// paper account.
/// </item>
/// </list>
/// </remarks>
public static class PaperAutomationEndpoints
{
    public static void MapPaperAutomationEndpoints(this WebApplication app)
    {
        app.MapGet("/research/automation", async (
                int? recent,
                PaperAutomationService automation,
                CancellationToken cancellationToken) =>
            Results.Ok(await automation.GetStatusAsync(Math.Clamp(recent ?? 50, 1, 500), cancellationToken)));

        app.MapPost("/research/automation/kill", (KillSwitchRequest? request, PaperAutomationService automation) =>
        {
            automation.Kill(request?.Reason);
            return Results.Ok(new { killed = true, reason = request?.Reason });
        });

        app.MapPost("/research/automation/resume", (PaperAutomationService automation) =>
            {
                automation.Resume();
                return Results.Ok(new { killed = false });
            })
            .RequireAuthorization();

        // The manual trigger. Named, shaped and recorded so it cannot be mistaken for the automated
        // path: trigger='manual' on the row, signal_state='not-evaluated', and a limit price whose
        // source is 'operator-supplied'. It exists because the automated path's signal is a genuine
        // no-trade today and the submission path still has to be exercisable — the alternative would
        // be to fabricate a study run, and a fabricated signal in the decision table is precisely what
        // later reads as real.
        //
        // Everything except the signal still applies: arming (a coherent execution plane, a connected
        // DU account), the kill switch, and the per-session cap. This bypasses the reason to trade,
        // not the permission to.
        //
        // It does not bypass the LIFECYCLE either, and an operator needs to know that before pressing
        // it. A manual evaluation runs the same pass as a scheduled one, and the exit branch sits
        // ahead of the entry logic: with a position at or below the DTE threshold this endpoint
        // submits that position's CLOSING order, and the supplied limit price is not used at all — a
        // closing order is priced from live quotes and there is no operator price on that path. With a
        // position open but not yet due, the entry-when-flat guard records a no-trade row and submits
        // nothing. The supplied limit reaches an order only when the account is flat.
        app.MapPost("/research/automation/manual-order", async (
                ManualOrderRequest request,
                PaperAutomationService automation,
                CancellationToken cancellationToken) =>
            {
                if (request.LimitPrice <= 0m)
                {
                    return Results.Problem(
                        title: "A positive net debit is required.",
                        detail: $"limitPrice was {request.LimitPrice}. An operator-priced order from this endpoint " +
                                "is an ENTRY in the configured structure, and the debit vertical it prices is " +
                                "always a debit. It is also only reached when the account is flat: with a position " +
                                "open this evaluation closes it or holds it, and this price is never consulted.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var decision = await automation.EvaluateAsync(
                    AutomationTriggers.Manual,
                    request.LimitPrice,
                    request.AcknowledgeOutsideSession,
                    cancellationToken);

                return Results.Ok(decision);
            })
            .RequireAuthorization();
    }
}
