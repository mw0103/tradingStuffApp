namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// The one thing that can ask automation to open a position.
/// </summary>
/// <remarks>
/// An interface with a single implementation, deliberately. It is not speculative extensibility: it
/// is the seam that lets the arming, cap, session, construction and persistence machinery be tested
/// against a signal that says "trade" without a study, a database, or a fabricated
/// <c>research.dev_vol_residual_runs</c> row. Manufacturing a fake study run to exercise the
/// submission path would put a row in the decision log that later reads as a real signal, which is
/// the fabrication docs/LESSONS.md §8 is about.
/// </remarks>
public interface IAutomationSignal
{
    /// <summary>A short name for the source, shown in status so an operator can see what is driving it.</summary>
    string Name { get; }

    Task<SignalResult> EvaluateAsync(CancellationToken cancellationToken);
}
