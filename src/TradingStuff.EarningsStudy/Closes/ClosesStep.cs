namespace TradingStuff.EarningsStudy.Closes;

/// <summary>
/// WP4. Pulls daily TRADES bars for every eligible symbol through the gateway's
/// <c>POST /ibkr/history/bars</c> (one multi-year request per name, honouring 429 + Retry-After),
/// stores each series under <c>raw/bars</c> with an atomic completion marker so a resume never
/// trusts a partial file, and joins the pre-entry, entry and exit closes plus the trailing 20-day
/// median absolute daily return into <c>closes.csv</c>, one row per event whatever was found.
/// </summary>
public sealed class ClosesStep : IStudyStep
{
    public string Verb => "closes";
    public string Description => "Pull daily closes from the IBKR gateway and join them to each event's dates.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new NotImplementedException("WP4: ClosesStep");
}
