using System.Collections.Concurrent;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.EarningsStudy.Closes;

/// <summary>How one symbol's fetch attempt this run ended, for the run's progress tally.</summary>
internal enum FetchOutcome
{
    /// <summary>Bars landed and the marker says so.</summary>
    Fetched,

    /// <summary>TWS confirmed no data; marker says "empty".</summary>
    Empty,

    /// <summary>TWS rejected the contract or request; marker says "rejected: &lt;detail&gt;".</summary>
    Rejected,

    /// <summary>Bounded retries exhausted on a transient outcome; no marker written, a rerun retries it.</summary>
    Failed,

    /// <summary>A <c>.done</c> marker already existed; no request was sent this run.</summary>
    Skipped,
}

internal sealed record FetchSummary(int Fetched, int Empty, int Rejected, int Failed, int Skipped)
{
    public int Total => Fetched + Empty + Rejected + Failed + Skipped;

    public override string ToString() =>
        $"fetched={Fetched} empty={Empty} rejected={Rejected} failed={Failed} skipped={Skipped} total={Total}";
}

/// <summary>
/// Pulls one daily-bar TRADES series per eligible symbol from the gateway's
/// <c>POST /ibkr/history/bars</c> and lands it under <c>raw/bars/</c> with an atomic completion
/// marker, honouring the gateway's pacing governor exactly as <c>BackfillCoordinator</c> does: a
/// <see cref="GatewayOutcome.Paced"/> answer sleeps for the stated <c>Retry-After</c> and retries the
/// SAME request without spending a retry, because the slice never reached TWS.
/// </summary>
/// <remarks>
/// Reuses <see cref="IbkrGatewayClient"/> rather than re-deriving its outcome classification: that
/// class already turns the gateway's documented status-code surface (429/400/503/504/5xx/network
/// failure/200-with-HasData-false) into <see cref="GatewayOutcome"/>, tested against real transport
/// failures in <c>BackfillGatewayClientTests</c>. Restating that mapping a third time (the gateway has
/// its own, ResearchService has this one) would be a second place for the 200-vs-empty-vs-failed
/// distinction to drift out of sync with the one that matters at 3am.
/// </remarks>
internal sealed class BarsFetcher(
    IbkrGatewayClient gateway,
    StudyPaths paths,
    string duration,
    Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>
    /// NotConnected/Transient/Unreachable attempts before a symbol is left unmarked for a rerun.
    /// Internal (rather than private) so the bound tested in <c>BarsFetcherTests</c> is the real
    /// constant, not a copy that can silently drift out of sync with it.
    /// </summary>
    internal const int MaxTransientAttempts = 5;

    /// <summary>
    /// A defensive ceiling on consecutive pacing rejections. The brief is explicit that a paced
    /// answer is never a failure and is retried unconditionally — this exists only so a
    /// misconfigured governor that paces every single request cannot hang a run forever; a real
    /// budget replenishes long before this is reached.
    /// </summary>
    internal const int MaxPacedRetries = 20;

    internal const int ProgressLogInterval = 25;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<FetchSummary> FetchAllAsync(
        IReadOnlyList<UniverseRow> eligible, int concurrency, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RawBarsDirectory);

        var counts = new ConcurrentDictionary<FetchOutcome, int>();
        var processed = 0;

        await Parallel.ForEachAsync(
            eligible,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (row, token) =>
            {
                var outcome = await FetchOneAsync(row, token);
                counts.AddOrUpdate(outcome, 1, (_, n) => n + 1);
                var done = Interlocked.Increment(ref processed);
                if (done % ProgressLogInterval == 0)
                {
                    log($"closes: {done}/{eligible.Count} symbols processed ({Summarize(counts)}).");
                }
            });

        var summary = new FetchSummary(
            counts.GetValueOrDefault(FetchOutcome.Fetched),
            counts.GetValueOrDefault(FetchOutcome.Empty),
            counts.GetValueOrDefault(FetchOutcome.Rejected),
            counts.GetValueOrDefault(FetchOutcome.Failed),
            counts.GetValueOrDefault(FetchOutcome.Skipped));

        log($"closes: fetch complete — {summary}.");
        return summary;
    }

    private static string Summarize(ConcurrentDictionary<FetchOutcome, int> counts) =>
        string.Join(" ", Enum.GetValues<FetchOutcome>().Select(o => $"{o}={counts.GetValueOrDefault(o)}"));

    internal async Task<FetchOutcome> FetchOneAsync(UniverseRow row, CancellationToken cancellationToken)
    {
        var csvPath = Path.Combine(paths.RawBarsDirectory, $"{row.Symbol}.csv");
        var markerPath = Path.Combine(paths.RawBarsDirectory, $"{row.Symbol}.done");

        // The ONLY resume signal. A bars file with no marker is a partial write from a killed run
        // (or one still in flight under another owner) and must never be trusted — see BarsMarker.
        if (File.Exists(markerPath))
        {
            return FetchOutcome.Skipped;
        }

        var ibkrSymbol = SymbolMapping.ToIbkrSymbol(row.Symbol);
        var primaryExchange = SymbolMapping.ToPrimaryExchange(
            row.Exchange, unrecognised => log($"closes: {row.Symbol} has unrecognised exchange '{unrecognised}'; sending it unmapped."));

        var request = new HistoricalBarsRequestDto(
            new HistoricalContractSpecDto(
                Symbol: ibkrSymbol,
                SecType: "STK",
                Exchange: "SMART",
                Currency: "USD",
                PrimaryExchange: primaryExchange),
            EndDateTime: null,
            Duration: duration,
            BarSize: "1 day",
            WhatToShow: "TRADES",
            UseRth: true);

        var transientAttempts = 0;
        var pacedAttempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await gateway.GetHistoricalBarsAsync(request, cancellationToken);

            switch (result.Outcome)
            {
                case GatewayOutcome.Ok:
                {
                    var bars = result.Bars
                        .Where(b => b.TradingDate is not null)
                        .Select(b => new BarRow(b.TradingDate!.Value, b.Open, b.High, b.Low, b.Close, b.Volume))
                        .OrderBy(b => b.TradingDate)
                        .ToList();

                    if (bars.Count == 0)
                    {
                        // TWS said HasData:true but every bar came back undated — never observed for
                        // "1 day" bars, but treating it as empty rather than fabricating an "ok" with
                        // zero rows keeps the marker's meaning honest (LESSONS.md #8).
                        log($"closes: {row.Symbol} returned HasData=true with no dated daily bars; treating as empty.");
                        WriteMarkerAtomically(markerPath, BarsMarker.BuildEmpty(ibkrSymbol, primaryExchange));
                        return FetchOutcome.Empty;
                    }

                    WriteBarsAtomically(csvPath, bars);
                    WriteMarkerAtomically(
                        markerPath, BarsMarker.BuildOk(bars.Count, bars[^1].TradingDate, ibkrSymbol, primaryExchange));
                    return FetchOutcome.Fetched;
                }

                case GatewayOutcome.Empty:
                    WriteMarkerAtomically(markerPath, BarsMarker.BuildEmpty(ibkrSymbol, primaryExchange));
                    return FetchOutcome.Empty;

                case GatewayOutcome.Permanent:
                    WriteMarkerAtomically(
                        markerPath, BarsMarker.BuildRejected(result.Detail ?? "no detail", ibkrSymbol, primaryExchange));
                    return FetchOutcome.Rejected;

                case GatewayOutcome.Paced:
                {
                    pacedAttempts++;
                    if (pacedAttempts > MaxPacedRetries)
                    {
                        log($"closes: {row.Symbol} paced {MaxPacedRetries} times in a row; leaving unmarked for a rerun.");
                        return FetchOutcome.Failed;
                    }

                    var wait = result.RetryAfter ?? TimeSpan.FromSeconds(60);
                    log($"closes: {row.Symbol} paced; retrying the same request in {wait.TotalSeconds:F0}s.");
                    await _delay(wait, cancellationToken);
                    continue;
                }

                default: // NotConnected, Transient, Unreachable — all "may or may not have reached TWS, bounded retry"
                {
                    transientAttempts++;
                    if (transientAttempts > MaxTransientAttempts)
                    {
                        log($"closes: {row.Symbol} failed after {MaxTransientAttempts} attempts " +
                            $"({result.Outcome}): {result.Detail}. Leaving unmarked for a rerun.");
                        return FetchOutcome.Failed;
                    }

                    var backoff = TransientBackoff(transientAttempts);
                    log($"closes: {row.Symbol} {result.Outcome} on attempt {transientAttempts} " +
                        $"({result.Detail}); retrying in {backoff.TotalSeconds:F0}s.");
                    await _delay(backoff, cancellationToken);
                    continue;
                }
            }
        }
    }

    private static TimeSpan TransientBackoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 5 * attempt));

    private static void WriteBarsAtomically(string path, IReadOnlyList<BarRow> bars)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        CsvFile.Write(tmp, bars);
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteMarkerAtomically(string path, string content)
    {
        // Defensive, not redundant: FetchOneAsync is independently callable (and independently
        // tested) and must not depend on FetchAllAsync having created the directory first.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
