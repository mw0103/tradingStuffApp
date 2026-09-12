using System.Collections.Concurrent;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.EarningsStudy.Options;

/// <summary>
/// WP3b. For every event with resolved dates, pulls the front-expiration chain from the local Theta
/// Terminal at the pre-entry, entry and exit dates (EOD report as the primary snapshot, the
/// 15:45 ET minute as the fallback), caches the raw responses under <c>raw/chains</c>, and builds
/// <c>option_measures.csv</c> through <see cref="ChainSelection"/> and <see cref="ChainMeasureBuilder"/>.
/// Every event gets a row with a fetch status; nothing is dropped for lack of data.
/// </summary>
public sealed class ChainsStep : IStudyStep
{
    public string Verb => "chains";
    public string Description => "Fetch front-expiry chains from the Theta Terminal and compute IM, tiers, and the straddle diagnostic.";

    public async Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!ChainsOptions.TryParse(args, out var options, out var parseError))
        {
            context.Log($"chains: {parseError}");
            return 2;
        }

        foreach (var (path, label) in new[]
                 {
                     (context.Paths.EventTiming, "event_timing"), (context.Paths.Events, "events"),
                     (context.Paths.Universe, "universe"),
                 })
        {
            if (!File.Exists(path))
            {
                context.Log($"chains: {label} table not found at {path}; run the earlier verbs first.");
                return 1;
            }
        }

        var timing = CsvFile.Read<EventTimingRow>(context.Paths.EventTiming);
        if (options.Limit is { } limit) timing = timing.Take(limit).ToList();

        var symbols = ResolveSymbols(timing,
            CsvFile.Read<EventRow>(context.Paths.Events), CsvFile.Read<UniverseRow>(context.Paths.Universe));

        context.Log(
            $"chains: {timing.Count} event-timing row(s), theta {options.ThetaUrl}, snapshot " +
            $"{options.SnapshotKind}, concurrency {options.Concurrency}.");

        using var client = new ThetaDataClient(
            new ThetaDataOptions { BaseAddress = options.ThetaUrl, SnapshotTimeOfDay = options.SnapshotTime },
            new HttpClient());
        var fetcher = new ThetaChainFetcher(client, context.Paths, options.SnapshotKind, options.DelayMs, context.Log);

        var rows = new OptionMeasuresRow[timing.Count];
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var processed = 0;
        Exception? abortException = null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, timing.Count),
                new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = linkedCts.Token },
                async (i, token) =>
                {
                    OptionMeasuresRow row;
                    try
                    {
                        row = await ProcessOneAsync(timing[i], symbols[i], options.SnapshotKind, fetcher, token);
                    }
                    catch (ThetaDataVersionException ex)
                    {
                        abortException ??= ex;
                        linkedCts.Cancel();
                        return;
                    }

                    rows[i] = row;
                    counts.AddOrUpdate(row.FetchStatus, 1, (_, n) => n + 1);
                    if (Interlocked.Increment(ref processed) % 50 == 0)
                    {
                        context.Log($"chains: {processed}/{timing.Count} events processed ({Summarize(counts)}).");
                    }
                });
        }
        catch (OperationCanceledException) when (abortException is not null)
        {
            // Expected: a ThetaDataVersionException inside the loop cancelled linkedCts so no
            // further events are attempted against a Terminal too old for this client.
        }

        if (abortException is not null)
        {
            context.Log($"chains: aborting - the Theta Terminal rejected a request as an outdated API version: {abortException.Message}");
            return 1;
        }

        CsvFile.Write(context.Paths.OptionMeasures, rows);
        context.Log($"chains_summary {Summarize(counts)} total={rows.Length}");
        return 0;
    }

    /// <summary>Every timing row's EventId must resolve through events.csv to a symbol, and that
    /// symbol must be in universe.csv — anything else is a pipeline defect, not a data gap.</summary>
    private static string[] ResolveSymbols(
        IReadOnlyList<EventTimingRow> timing, IReadOnlyList<EventRow> events, IReadOnlyList<UniverseRow> universe)
    {
        var symbolByEventId = events.ToDictionary(e => e.EventId, e => e.Symbol, StringComparer.Ordinal);
        var universeBySymbol = universe.ToDictionary(u => u.Symbol, StringComparer.Ordinal);

        var symbols = new string[timing.Count];
        for (var i = 0; i < timing.Count; i++)
        {
            if (!symbolByEventId.TryGetValue(timing[i].EventId, out var symbol))
            {
                throw new InvalidDataException(
                    $"chains: event_timing.csv references event '{timing[i].EventId}', which is not in events.csv.");
            }
            if (!universeBySymbol.TryGetValue(symbol, out var universeRow))
            {
                throw new InvalidDataException(
                    $"chains: event '{timing[i].EventId}' resolves to symbol '{symbol}', which is not in universe.csv.");
            }
            symbols[i] = universeRow.Symbol; // the CBOE symbol as-is; the root the fetcher tries first.
        }
        return symbols;
    }

    private static async Task<OptionMeasuresRow> ProcessOneAsync(
        EventTimingRow timing, string symbol, string configuredSnapshotKind, ThetaChainFetcher fetcher,
        CancellationToken cancellationToken)
    {
        if (timing.PreEntryDate is not { } preEntry || timing.EntryDate is not { } entry || timing.ExitDate is not { } exit)
        {
            return EmptyRow(timing.EventId, symbol, configuredSnapshotKind, ChainsFetchStatus.DatesUnresolved,
                "one or more of pre-entry/entry/exit date is unresolved.");
        }

        try
        {
            var fetch = await fetcher.FetchAsync(symbol, preEntry, exit, cancellationToken);
            return ChainMeasureBuilder.Build(timing.EventId, preEntry, entry, exit, fetch);
        }
        catch (ThetaDataVersionException)
        {
            throw; // handled by the caller: abort the whole verb rather than count one event's row.
        }
        catch (Exception ex)
        {
            return EmptyRow(timing.EventId, symbol, configuredSnapshotKind, ChainsFetchStatus.Error, ex.Message);
        }
    }

    private static OptionMeasuresRow EmptyRow(string eventId, string root, string snapshotKind, string status, string note) =>
        new(eventId, root, null, null, snapshotKind, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, status, note);

    private static string Summarize(ConcurrentDictionary<string, int> counts) =>
        string.Join(" ", ChainsFetchStatus.All.Select(status => $"{status}={counts.GetValueOrDefault(status)}"));
}
