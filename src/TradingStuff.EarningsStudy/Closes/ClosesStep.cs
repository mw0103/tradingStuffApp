using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.ResearchService.Gateway;

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

    public async Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!ClosesOptions.TryParse(args, out var options, out var parseError))
        {
            context.Log($"closes: {parseError}");
            return 2;
        }

        foreach (var (path, label) in new[]
                 {
                     (context.Paths.Universe, "universe"), (context.Paths.Events, "events"),
                     (context.Paths.EventTiming, "event_timing"),
                 })
        {
            if (!File.Exists(path))
            {
                context.Log($"closes: {label} table not found at {path}; run the earlier verbs first.");
                return 1;
            }
        }

        var eligible = CsvFile.Read<UniverseRow>(context.Paths.Universe).Where(u => u.Eligible).ToList();
        var events = CsvFile.Read<EventRow>(context.Paths.Events);
        var timing = CsvFile.Read<EventTimingRow>(context.Paths.EventTiming);

        context.Log(
            $"closes: {eligible.Count} eligible symbols, {timing.Count} event-timing rows, " +
            $"gateway {options.GatewayUrl}, duration '{options.Duration}', concurrency {options.Concurrency}.");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.GatewayUrl),
            // The gateway's own historical-request timeout defaults to 60s (IBKR:HistoricalRequestTimeoutSeconds);
            // this must exceed it or a slow-but-live request gets misread as a client-side failure —
            // the same reasoning ResearchService's Program.cs uses for this client's resilience timeout.
            Timeout = TimeSpan.FromSeconds(90),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

        var gateway = new IbkrGatewayClient(httpClient, NullLogger<IbkrGatewayClient>.Instance);
        var fetcher = new BarsFetcher(gateway, context.Paths, options.Duration, context.Log);
        await fetcher.FetchAllAsync(eligible, options.Concurrency, cancellationToken);

        var symbolByEventId = events.ToDictionary(e => e.EventId, e => e.Symbol, StringComparer.Ordinal);
        var rows = ClosesJoiner.Join(context.Paths, timing, symbolByEventId);
        CsvFile.Write(context.Paths.Closes, rows);

        // Gate 08 (closes present) is tallied by the compute verb from these same Status values, not
        // here — so the count and the rows it is measured on can never disagree. This is a plain
        // count-by-status log line, not a gate row.
        var byStatus = rows
            .GroupBy(r => r.Status, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");
        context.Log($"closes_summary {string.Join(" ", byStatus)} total={rows.Count}");

        return 0;
    }
}
