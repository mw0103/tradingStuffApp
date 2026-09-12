using System.Collections.Concurrent;
using System.Globalization;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.EarningsStudy.Options;

/// <summary>The chains verb's fetch-phase outcome for one event: either a chain ready to measure, or
/// a status/note pair recorded straight into the event's <c>OptionMeasuresRow</c>.</summary>
internal sealed record ChainFetchResult(
    string Root,
    string? FailureStatus,
    string? Note,
    DateOnly? Expiration,
    string SnapshotKind,
    string? SnapshotTimeEt,
    IReadOnlyDictionary<DateOnly, List<ChainQuote>> QuotesByDate,
    IReadOnlyDictionary<DateOnly, decimal> UnderlyingByDate)
{
    public bool Succeeded => FailureStatus is null;

    private static readonly IReadOnlyDictionary<DateOnly, List<ChainQuote>> NoQuotes = new Dictionary<DateOnly, List<ChainQuote>>();
    private static readonly IReadOnlyDictionary<DateOnly, decimal> NoUnderlying = new Dictionary<DateOnly, decimal>();

    public static ChainFetchResult Failure(string root, string status, string? note, string configuredSnapshotKind) =>
        new(root, status, note, null, configuredSnapshotKind, null, NoQuotes, NoUnderlying);
}

/// <summary>
/// WP3b's network and cache layer: lists expirations (with the dot-stripped retry), picks the front
/// expiration via <see cref="ChainSelection.FrontExpiration"/>, and fetches the (root, expiration)
/// chain across pre-entry..exit in one request — EOD primary, minute-snapshot fallback — caching
/// every response under <c>raw/chains</c> so a rerun with a warm cache makes zero HTTP calls. Pure
/// selection semantics (which strike is ATM, which spot to use) live in
/// <see cref="ChainSelection"/> and <c>ChainMeasureBuilder</c>; this class only ever decides
/// eod-vs-minute and root-vs-dotless-root.
/// </summary>
/// <remarks>
/// The EOD report's column set has not been observed by this repository —
/// <see cref="ThetaChainCsv.ParseChainRows"/> parses it tolerantly rather than assuming a shape.
/// <see cref="ThetaDataSubscriptionException"/> on the EOD endpoint permanently switches every later
/// event on this instance to the minute snapshot ("for that and all later events"); any other EOD
/// failure (a non-2xx) only falls back for the one event that hit it. A <see cref="ThetaDataNoDataException"/>
/// on either endpoint, and any parse failure, are NOT caught here — they propagate to the caller's
/// per-event boundary and become <c>FetchStatus "error"</c> there, same as the spec's "no-data" example.
/// <see cref="ThetaDataVersionException"/> is never caught here either: the Terminal is too old for
/// this client for every request, not just one, so the caller must abort the whole verb rather than
/// record it as one event's failure.
/// </remarks>
internal sealed class ThetaChainFetcher(
    ThetaDataClient client, StudyPaths paths, string defaultSnapshotKind, int delayMs, Action<string> log)
{
    // Keyed by root. A concurrent double-fetch of the same root (two events sharing a symbol,
    // racing under --concurrency > 1) is a benign, rare duplicate network call rather than a
    // correctness bug: both writers compute and cache the identical result.
    private readonly ConcurrentDictionary<string, IReadOnlyList<DateOnly>?> _expirationsByRoot = new(StringComparer.Ordinal);

    private volatile bool _forceMinuteOnly;
    private int _loggedExpirationsHeader;
    private int _loggedEodHeader;
    private int _loggedMinuteHeader;

    public async Task<ChainFetchResult> FetchAsync(
        string symbol, DateOnly preEntryDate, DateOnly exitDate, CancellationToken cancellationToken)
    {
        var (root, expirations, rootNote) = await ResolveRootAsync(symbol, cancellationToken);
        if (expirations is not { Count: > 0 })
        {
            var alsoTried = symbol.Contains('.') ? $" (also tried '{symbol.Replace(".", "")}')" : "";
            return ChainFetchResult.Failure(root, ChainsFetchStatus.NoChain,
                Join(rootNote, $"'{symbol}' lists no expirations{alsoTried}."), defaultSnapshotKind);
        }

        var expiration = ChainSelection.FrontExpiration(expirations, exitDate);
        if (expiration is null)
        {
            return ChainFetchResult.Failure(root, ChainsFetchStatus.NoSpanningExpiry,
                Join(rootNote, $"No listed expiration on or after the exit date {Iso(exitDate)} " +
                               $"({expirations.Count} expiration(s) checked)."), defaultSnapshotKind);
        }

        var useMinute = defaultSnapshotKind == "minute" || _forceMinuteOnly;
        ParsedChainResponse? parsed = useMinute ? null : await TryFetchEodAsync(root, expiration.Value, preEntryDate, exitDate, cancellationToken);
        var kind = parsed is not null ? "eod" : "minute";

        parsed ??= await FetchChainAsync(root, expiration.Value, preEntryDate, exitDate, "minute", cancellationToken);

        var snapshotTimeEt = kind == "eod" ? "16:00:00" : ThetaDataClient.TimeOfDay(client.Options.SnapshotTimeOfDay);
        var note = kind == "eod" ? Join(rootNote, "eod snapshot time assumed 16:00:00 ET.") : rootNote;

        return new ChainFetchResult(
            root, null, note, expiration.Value, kind, snapshotTimeEt, parsed.QuotesByDate, parsed.UnderlyingByDate);
    }

    /// <summary>Null return means "fell back": either a non-2xx, or a subscription refusal that also
    /// flips <see cref="_forceMinuteOnly"/> for every later call on this instance.</summary>
    private async Task<ParsedChainResponse?> TryFetchEodAsync(
        string root, DateOnly expiration, DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            return await FetchChainAsync(root, expiration, from, to, "eod", ct);
        }
        catch (ThetaDataSubscriptionException ex)
        {
            _forceMinuteOnly = true;
            log($"chains: {root} EOD subscription refused ({ex.Message}); switching to the minute " +
                "snapshot for this and all later events.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            log($"chains: {root} {Iso(expiration)} EOD request failed ({ex.Message}); falling back " +
                "to the minute snapshot for this event.");
            return null;
        }
    }

    private async Task<ParsedChainResponse> FetchChainAsync(
        string root, DateOnly expiration, DateOnly from, DateOnly to, string kind, CancellationToken ct)
    {
        var directory = Path.Combine(paths.RawChainsDirectory, root, Iso(expiration));
        var cachePath = Path.Combine(directory, $"{Iso(from)}_{Iso(to)}.{kind}.csv");

        CsvTable table;
        if (File.Exists(cachePath))
        {
            table = CsvTable.Parse(File.ReadAllText(cachePath));
        }
        else
        {
            await DelayIfConfiguredAsync(ct);
            table = kind == "eod"
                ? await client.GetAsync(ThetaDataEndpoints.OptionEndOfDay, new Dictionary<string, string>
                  {
                      ["symbol"] = root,
                      ["expiration"] = Iso(expiration),
                      ["start_date"] = Iso(from),
                      ["end_date"] = Iso(to),
                  })
                : await client.GetDailyChainQuotesAsync(
                    root, expiration.ToDateTime(TimeOnly.MinValue),
                    from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue));

            ThetaChainCsv.WriteCacheAtomic(cachePath, ThetaChainCsv.ToRawCsv(table));
        }

        if (kind == "eod") LogHeaderOnce(ref _loggedEodHeader, "eod", table);
        else LogHeaderOnce(ref _loggedMinuteHeader, "minute", table);

        return ThetaChainCsv.ParseChainRows(table, (decimal)client.Options.StrikeDivisor);
    }

    private async Task<(string Root, IReadOnlyList<DateOnly>? Expirations, string? Note)> ResolveRootAsync(
        string symbol, CancellationToken ct)
    {
        var primary = await GetExpirationsAsync(symbol, ct);
        if (primary is { Count: > 0 } || !symbol.Contains('.'))
        {
            return (symbol, primary, null);
        }

        var stripped = symbol.Replace(".", "");
        var secondary = await GetExpirationsAsync(stripped, ct);
        return secondary is { Count: > 0 }
            ? (stripped, secondary, $"root '{symbol}' listed no expirations; '{stripped}' answered instead.")
            : (symbol, null, null);
    }

    private async Task<IReadOnlyList<DateOnly>?> GetExpirationsAsync(string root, CancellationToken ct)
    {
        if (_expirationsByRoot.TryGetValue(root, out var cached))
        {
            return cached;
        }

        var cachePath = Path.Combine(paths.RawChainsDirectory, root, "expirations.csv");
        CsvTable table;
        if (File.Exists(cachePath))
        {
            table = CsvTable.Parse(File.ReadAllText(cachePath));
        }
        else
        {
            await DelayIfConfiguredAsync(ct);
            try
            {
                table = await client.ListExpirationsAsync(root);
            }
            catch (ThetaDataNoDataException)
            {
                // "No data for the specified [root]" IS "the Terminal lists no expirations for it".
                _expirationsByRoot[root] = null;
                return null;
            }
            ThetaChainCsv.WriteCacheAtomic(cachePath, ThetaChainCsv.ToRawCsv(table));
        }

        LogHeaderOnce(ref _loggedExpirationsHeader, "expirations", table);
        var expirations = ThetaChainCsv.ParseExpirations(table);
        _expirationsByRoot[root] = expirations;
        return expirations;
    }

    private void LogHeaderOnce(ref int flag, string label, CsvTable table)
    {
        if (Interlocked.CompareExchange(ref flag, 1, 0) == 0)
        {
            log($"chains: first {label} response header: {string.Join(",", table.ColumnNames)}");
        }
    }

    private async Task DelayIfConfiguredAsync(CancellationToken ct)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct);
        }
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Join(string? a, string? b) =>
        (string.IsNullOrEmpty(a), string.IsNullOrEmpty(b)) switch
        {
            (true, true) => null,
            (true, false) => b,
            (false, true) => a,
            _ => $"{a} {b}",
        };
}
