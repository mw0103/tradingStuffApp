namespace TradingStuff.EarningsStudy.Edgar;

/// <summary>
/// WP1. For every eligible universe name, reads the EDGAR submissions JSON (with its paginated
/// continuation files) and emits every 8-K whose items include 2.02, then applies the event-level
/// gates: not an 8-K/A, inside the registered window, one event per CIK-calendar-quarter with the
/// earliest acceptance winning. Also dumps every dei:EntityCommonStockSharesOutstanding fact per CIK
/// to <c>shares_facts.csv</c>; the as-of pick is made downstream against the entry date.
/// Requires <c>EDGAR_USER_AGENT</c> (the SEC's declared-automated-tool policy) and refuses to run without it.
/// </summary>
public sealed class EventsStep : IStudyStep
{
    public string Verb => "events";
    public string Description => "Pull 8-K Item 2.02 events and shares-outstanding facts from EDGAR for the eligible universe.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new NotImplementedException("WP1: EventsStep");
}
