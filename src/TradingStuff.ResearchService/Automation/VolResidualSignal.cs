using Npgsql;
using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// Reads the latest volatility-forecast-residual run and turns it into a trade / no-trade answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>This signal cannot currently return "trade", and that is the correct behaviour rather than a
/// gap.</b> Two independent refusals stand in front of it, checked in this order so both are visible
/// in the decision log rather than one masking the other forever:
/// </para>
/// <list type="number">
/// <item>
/// <b>Status.</b> The latest run reports <c>insufficient-data</c>. That is real, not a placeholder:
/// every SPX bar backfilled so far falls inside the reserved 2024–2026 holdout, and zero VIX daily
/// bars have landed. There is nothing to forecast from.
/// </item>
/// <item>
/// <b>Provenance.</b> Even a run that reported <c>ok</c> would be a DEVELOPMENT run —
/// <see cref="VolResidualRunResponse.IsDevelopmentRun"/> — which by the study's own pre-registration
/// (<c>docs/research/volatility-forecast-residual-study.md</c>) is not a result anything may be
/// traded on. It is computed on demand over whatever data happens to be present, with no registered
/// variant slot and no scripted run behind it. Trading a development run would be exactly the
/// "plausible fabrication" docs/LESSONS.md §8 rules out, one level up from the data.
/// </list>
/// <para>
/// So the honest MVP answer is that the automated path is a no-trade path today, and the status
/// endpoint says so in those words rather than reporting an idle loop as a healthy one. Wiring a
/// tradeable rule in here is Phase 5/6 work with a gate and a leakage review in front of it, not
/// something to improvise so that the loop has something to do.
/// </para>
/// </remarks>
public sealed class VolResidualSignal(VolResidualStudyStore store, ILogger<VolResidualSignal> logger)
    : IAutomationSignal
{
    public string Name => "vol-residual/latest";

    public string Key => PaperAutomationOptions.Signals.VolResidual;

    public async Task<SignalResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        VolResidualRunResponse? latest;

        try
        {
            latest = await store.GetLatestAsync(cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning(ex, "Could not read the latest vol-residual run; treating the signal as no-trade.");

            // A read failure is a no-trade, never a trade and never silence. The reason string carries
            // the exception so the decision row distinguishes "the study says no" from "nobody asked
            // the study" — those look identical in a row that only records the outcome.
            return new SignalResult(
                SignalStates.NoRun,
                $"The latest development run could not be read: {ex.Message}",
                Trade: false);
        }

        return Interpret(latest);
    }

    /// <summary>
    /// The run-to-decision mapping, separated from the read so it can be tested without a database.
    /// </summary>
    /// <remarks>
    /// The ORDER of these checks is the part worth pinning. Status is examined before provenance so
    /// that an <c>insufficient-data</c> run reports as insufficient data rather than being swallowed
    /// by the development-run refusal that would also catch it — today every run trips both, and a
    /// decision log that only ever said "development run" would hide the fact that there is no data.
    /// </remarks>
    internal static SignalResult Interpret(VolResidualRunResponse? latest)
    {
        if (latest is null)
        {
            return new SignalResult(
                SignalStates.NoRun,
                "No vol-residual development run has been stored. POST /research/studies/vol-residual/run first.",
                Trade: false);
        }

        if (!string.Equals(latest.Status, VolResidualRunStatus.Ok, StringComparison.Ordinal))
        {
            return new SignalResult(
                SignalStates.InsufficientData,
                $"The latest run ({latest.RunId}) reports status '{latest.Status}'" +
                $"{(string.IsNullOrWhiteSpace(latest.InsufficientReason) ? string.Empty : $": {latest.InsufficientReason}")}. " +
                $"Data window {latest.DataWindow.From:yyyy-MM-dd}..{latest.DataWindow.To:yyyy-MM-dd}, " +
                $"{latest.DataWindow.SessionsUsed} of {latest.DataWindow.SessionsAvailable} sessions usable.",
                Trade: false,
                latest.RunId);
        }

        if (latest.IsDevelopmentRun)
        {
            return new SignalResult(
                SignalStates.DevelopmentRun,
                $"Run {latest.RunId} completed, but it is a DEVELOPMENT run and is not tradeable: it consumes no " +
                "registered variant slot and has no scripted run behind it. A registered run is a Phase 5/6 " +
                "prerequisite, not something automation may substitute for.",
                Trade: false,
                latest.RunId);
        }

        // Deliberately unreachable today: nothing in this service produces a non-development run. Left
        // as an explicit refusal rather than an entry point, so that whoever adds registered runs has
        // to write the rule that turns a forecast into a position, in front of a gate and a leakage
        // review, instead of inheriting one that was improvised here to make the loop fire.
        return new SignalResult(
            SignalStates.NoRun,
            $"Run {latest.RunId} is a registered run, but no entry rule has been defined for it yet. " +
            "Automation does not invent one.",
            Trade: false,
            latest.RunId);
    }
}
