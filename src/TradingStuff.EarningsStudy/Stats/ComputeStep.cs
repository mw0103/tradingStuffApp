namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// WP5. Joins every table, applies the remaining gates in registered order with counts, computes
/// the primary statistics with the week-clustered bootstrap, the secondary statistics, the four
/// pre-registered splits and the straddle diagnostic, and writes <c>c1_event_table.csv</c> and the
/// memo <c>c1_memo.md</c>. The number is the number.
/// </summary>
public sealed class ComputeStep : IStudyStep
{
    public string Verb => "compute";
    public string Description => "Apply the gates, compute the registered statistics and write the memo.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        throw new NotImplementedException("WP5: ComputeStep");
}
