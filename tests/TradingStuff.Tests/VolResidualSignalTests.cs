using TradingStuff.ResearchService.Automation;
using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests;

/// <summary>
/// The only signal source wired to paper automation, and the mapping that makes it a no-trade path
/// today.
/// </summary>
/// <remarks>
/// Every case here returns <c>Trade: false</c>, which would normally be a suspicious test suite. It
/// is the correct answer: nothing in this service produces a registered run, so there is no input to
/// this function that may open a position. The tests exist to pin WHICH refusal is reported and in
/// what order, because those are what land in <c>research.paper_automation_decisions</c> and are the
/// only thing an operator has to explain an idle day with.
/// </remarks>
public sealed class VolResidualSignalTests
{
    private static VolResidualRunResponse Run(string status, bool isDevelopmentRun, string? insufficientReason = null) =>
        new(
            Guid.NewGuid(),
            isDevelopmentRun,
            DateTimeOffset.UtcNow,
            status,
            insufficientReason,
            new VolResidualDataWindow(new DateOnly(2010, 1, 1), new DateOnly(2023, 12, 31), 0, 0),
            VolResidualHoldoutInfo.Registered,
            "gate",
            [],
            []);

    [Fact]
    public void No_stored_run_is_a_no_trade_that_says_so()
    {
        var result = VolResidualSignal.Interpret(null);

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.NoRun, result.State);
        Assert.Contains("No vol-residual development run has been stored", result.Reason);
    }

    /// <summary>
    /// The live state on 2026-08-01, measured: <c>POST /research/studies/vol-residual/run</c> against
    /// the running stack returned <c>insufficient-data</c> with "0 complete SPX session(s) and 0 VIX
    /// daily close(s) are currently available".
    /// </summary>
    [Fact]
    public void An_insufficient_data_run_is_a_no_trade_carrying_the_studys_own_reason()
    {
        var run = Run(VolResidualRunStatus.InsufficientData, isDevelopmentRun: true,
            "No day in [2010-01-01, 2023-12-31] has a full registered feature row yet.");

        var result = VolResidualSignal.Interpret(run);

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.InsufficientData, result.State);

        // The study's words, not a generic refusal. A decision row that only said "no trade" would
        // make every idle day identical and unattributable.
        Assert.Contains("has a full registered feature row yet", result.Reason);
        Assert.Contains("2010-01-01..2023-12-31", result.Reason);
        Assert.Equal(run.RunId, result.StudyRunId);
    }

    /// <summary>
    /// Status is checked BEFORE provenance, and this is the test that holds the order in place.
    /// </summary>
    /// <remarks>
    /// Every run today is both <c>insufficient-data</c> AND a development run, so a mapping that
    /// examined provenance first would report "development run" forever and the decision log would
    /// never once say that there is no data. Both refusals are real; the informative one goes first.
    /// </remarks>
    [Fact]
    public void Insufficient_data_is_reported_ahead_of_the_development_run_refusal()
    {
        var result = VolResidualSignal.Interpret(Run(VolResidualRunStatus.InsufficientData, isDevelopmentRun: true));

        Assert.Equal(SignalStates.InsufficientData, result.State);
        Assert.NotEqual(SignalStates.DevelopmentRun, result.State);
    }

    /// <summary>
    /// A run that completed cleanly is STILL refused, because a development run is not tradeable.
    /// </summary>
    /// <remarks>
    /// This is the case that would otherwise open a position the moment the backfill reaches far
    /// enough back, without anyone deciding that it should. The study's own pre-registration says a
    /// development run consumes no registered variant slot and has no scripted run behind it.
    /// </remarks>
    [Fact]
    public void An_ok_development_run_is_still_a_no_trade()
    {
        var result = VolResidualSignal.Interpret(Run(VolResidualRunStatus.Ok, isDevelopmentRun: true));

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.DevelopmentRun, result.State);
        Assert.Contains("not tradeable", result.Reason);
    }

    /// <summary>
    /// Even a registered run does not trade, because no entry rule has been defined for one.
    /// </summary>
    /// <remarks>
    /// Unreachable today — nothing in this service produces a non-development run — and asserted
    /// anyway, because the alternative shape (fall through to <c>Trade: true</c> on the last branch)
    /// is one edit away and would turn "we have not written the rule yet" into a position.
    /// </remarks>
    [Fact]
    public void Even_a_registered_run_does_not_trade_without_an_entry_rule()
    {
        var result = VolResidualSignal.Interpret(Run(VolResidualRunStatus.Ok, isDevelopmentRun: false));

        Assert.False(result.Trade);
        Assert.Contains("no entry rule has been defined", result.Reason);
    }

    /// <summary>
    /// Nothing this function can be handed opens a position. Stated as its own assertion because it
    /// is the property the whole automated path currently rests on.
    /// </summary>
    [Fact]
    public void No_input_at_all_produces_a_trade()
    {
        VolResidualRunResponse?[] everyShape =
        [
            null,
            Run(VolResidualRunStatus.InsufficientData, true),
            Run(VolResidualRunStatus.InsufficientData, false),
            Run(VolResidualRunStatus.Ok, true),
            Run(VolResidualRunStatus.Ok, false),
            Run("something-unrecognised", true),
            Run("something-unrecognised", false),
        ];

        Assert.All(everyShape, run => Assert.False(VolResidualSignal.Interpret(run).Trade));
    }

    [Fact]
    public void An_unrecognised_status_is_treated_as_insufficient_data_not_as_ok()
    {
        // Degrading to the refusing branch, the same way every other opt-in in this repository
        // degrades to its safe value on a string nobody recognises.
        var result = VolResidualSignal.Interpret(Run("something-unrecognised", isDevelopmentRun: false));

        Assert.False(result.Trade);
        Assert.Equal(SignalStates.InsufficientData, result.State);
    }
}
