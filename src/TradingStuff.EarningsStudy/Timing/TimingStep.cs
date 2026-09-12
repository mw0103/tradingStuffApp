namespace TradingStuff.EarningsStudy.Timing;

/// <summary>
/// WP2. Classifies each kept event from its EDGAR acceptance time (Eastern wall clock) as BMO, AMC
/// or INTRADAY, and resolves the measurement dates on the platform NYSE calendar: entry is the last
/// close strictly before the acceptance instant, exit the first close strictly after, pre-entry the
/// trading day before entry. Emits the earnings-week bootstrap key. Intraday and unresolvable
/// acceptances are quarantined here and counted; the price-based QA runs in <c>compute</c> once
/// closes exist, through <see cref="TimingQa"/>.
/// </summary>
public sealed class TimingStep : IStudyStep
{
    public string Verb => "timing";
    public string Description => "Classify BMO/AMC from acceptance time and resolve entry/exit dates on the NYSE calendar.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new NotImplementedException("WP2: TimingStep");
}
