namespace TradingStuff.EarningsStudy.Edgar;

/// <summary>
/// WP1. Seeds the universe from the frozen CBOE optionable directory, joins the SEC ticker/CIK/exchange
/// file, and applies the name-level gates (CIK mapped; primary listing NYSE, Nasdaq or NYSE American).
/// Writes <c>universe.csv</c> with every seed symbol present, eligible or not, and appends gate counts.
/// </summary>
public sealed class UniverseStep : IStudyStep
{
    public string Verb => "universe";
    public string Description => "Seed symbols from the frozen CBOE directory, map to CIKs, apply name-level gates.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new NotImplementedException("WP1: UniverseStep");
}
