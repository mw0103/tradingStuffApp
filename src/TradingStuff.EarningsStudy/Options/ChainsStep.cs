namespace TradingStuff.EarningsStudy.Options;

/// <summary>
/// WP3b. For every event with resolved dates, pulls the front-expiration chain from the local Theta
/// Terminal at the pre-entry, entry and exit dates (EOD report as the primary snapshot, the
/// 15:45 ET minute as the fallback), caches the raw responses under <c>raw/chains</c>, and builds
/// <c>option_measures.csv</c> through <see cref="ChainSelection"/>. Every event gets a row with a
/// fetch status; nothing is dropped for lack of data.
/// </summary>
public sealed class ChainsStep : IStudyStep
{
    public string Verb => "chains";
    public string Description => "Fetch front-expiry chains from the Theta Terminal and compute IM, tiers, and the straddle diagnostic.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new NotImplementedException("WP3b: ChainsStep");
}
