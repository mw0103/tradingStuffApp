namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// The one thing that can ask automation to open a position.
/// </summary>
/// <remarks>
/// The seam that lets the arming, cap, session, construction and persistence machinery be tested
/// against a signal that says "trade" without a study, a database, or a fabricated
/// <c>research.dev_vol_residual_runs</c> row. Manufacturing a fake study run to exercise the
/// submission path would put a row in the decision log that later reads as a real signal, which is
/// the fabrication docs/LESSONS.md §8 is about.
/// </remarks>
public interface IAutomationSignal
{
    /// <summary>A short name for the source, shown in status so an operator can see what is driving it.</summary>
    string Name { get; }

    /// <summary>
    /// The <c>PaperAutomation:Signal</c> value this implementation IS, for the coherence check in
    /// <see cref="PaperAutomationArming"/>.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately separate from <see cref="Name"/>, because they are not the same string and a
    /// check written against the wrong one silently never fires.</b>
    /// <c>ConstantExposureSignal.Name</c> is <c>constant-exposure/paper-decision</c> — it names the
    /// source AND what it reads, for an operator looking at a status page — while its key is
    /// <c>constant-exposure</c>, the configuration value that selected it. Comparing
    /// <c>Name == "constant-exposure"</c> would match nothing and the gate would pass every
    /// misconfiguration, in the unsafe direction. This is the same trap
    /// <see cref="PaperAutomationArming.RequiredMarketDataProvider"/> records having walked into
    /// once already, and it is answered the same way: the value compared is the one the resolved
    /// component reports about ITSELF, not a configuration string re-read by the checker.
    /// </remarks>
    string Key { get; }

    Task<SignalResult> EvaluateAsync(CancellationToken cancellationToken);
}
