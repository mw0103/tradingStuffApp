using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Timing;

/// <summary>
/// WP2. Classifies each kept event from its EDGAR acceptance time (Eastern wall clock) as BMO, AMC
/// or INTRADAY, and resolves the measurement dates on the platform NYSE calendar: entry is the last
/// close strictly before the acceptance instant, exit the first close strictly after, pre-entry the
/// trading day before entry. Emits the earnings-week bootstrap key. Intraday and unresolvable
/// acceptances are quarantined here and counted; the price-based QA runs in <c>compute</c> once
/// closes exist, through <see cref="TimingQa"/>.
/// </summary>
/// <remarks>
/// All the judgement lives in <see cref="TimingResolver"/> and <see cref="EdgarAcceptance"/>; this
/// class is the I/O around it. Every event read is written back with a class, so an event that could
/// not be resolved is a row saying so rather than a row that is not there — the exclusion table is
/// assembled from records, and a query cannot emit a row for a case that has none
/// (docs/LESSONS.md, 3).
/// </remarks>
public sealed class TimingStep : IStudyStep
{
    public string Verb => "timing";

    public string Description => "Classify BMO/AMC from acceptance time and resolve entry/exit dates on the NYSE calendar.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!File.Exists(context.Paths.Events))
        {
            context.Log($"timing: {context.Paths.Events} does not exist. Run the events verb first.");
            return Task.FromResult(2);
        }

        var events = CsvFile.Read<EventRow>(context.Paths.Events);

        // The gates before this one (in window, not an amendment, deduped) are the events verb's to
        // tally; this verb's considered set is what survived them, so gate 07's considered equals
        // gate 06's remaining and the exclusion table reconciles.
        var considered = events.Where(row => row is { InWindow: true, KeptAfterDedup: true }).ToList();

        var duplicateIds = considered
            .GroupBy(row => row.EventId, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);

        if (duplicateIds > 0)
        {
            context.Log($"timing: WARNING {duplicateIds} event ids appear more than once in {context.Paths.Events}.");
        }

        var rows = new List<EventTimingRow>(considered.Count);
        var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nonTradingDayAcceptances = 0;
        var dstAnomalies = 0;

        foreach (var row in considered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolution = TimingResolver.Resolve(
                context.Clock, C1Registration.Calendar, row.EventId, row.AcceptanceEt);

            rows.Add(resolution.Row);
            classCounts[resolution.Row.TimingClass] = classCounts.GetValueOrDefault(resolution.Row.TimingClass) + 1;

            if (!resolution.AcceptanceDateIsTradingDay)
            {
                nonTradingDayAcceptances++;
            }

            if (resolution.AcceptanceAnomaly is not null)
            {
                dstAnomalies++;
                context.Log($"timing: {row.EventId}: {resolution.AcceptanceAnomaly}");
            }
        }

        CsvFile.Write(context.Paths.EventTiming, rows);

        var removed = rows.Count(row => row.Quarantined);
        var note = string.Join("; ",
        [
            $"bmo={classCounts.GetValueOrDefault(TimingResolver.Bmo)}",
            $"amc={classCounts.GetValueOrDefault(TimingResolver.Amc)}",
            $"intraday={classCounts.GetValueOrDefault(TimingResolver.Intraday)}",
            $"unresolved={classCounts.GetValueOrDefault(TimingResolver.Unresolved)}",
            $"acceptance_on_non_trading_day={nonTradingDayAcceptances}",
            $"dst_anomalies={dstAnomalies}"
        ]);

        CsvFile.Append(context.Paths.GateCounts,
        [
            new GateCountRow(
                Verb,
                GateOrder(Gates.TimingClassified),
                Gates.TimingClassified,
                considered.Count,
                removed,
                considered.Count - removed,
                note)
        ]);

        context.Log(
            $"timing: {events.Count} event rows read, {considered.Count} carried in, {removed} quarantined " +
            $"({note}) -> {context.Paths.EventTiming}");

        return Task.FromResult(0);
    }

    /// <summary>The gate's 1-based position in <see cref="Gates.InOrder"/>, so the exclusion table cannot drift from the gate list.</summary>
    internal static int GateOrder(string gate)
    {
        for (var index = 0; index < Gates.InOrder.Count; index++)
        {
            if (string.Equals(Gates.InOrder[index], gate, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(gate), gate, "not a member of Gates.InOrder.");
    }
}
