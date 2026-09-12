using System.Text.Json;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Edgar;

/// <summary>
/// WP1. Seeds the universe from the frozen CBOE optionable directory, joins the SEC ticker/CIK/exchange
/// file, and applies the name-level gates (CIK mapped; primary listing NYSE, Nasdaq or NYSE American).
/// Writes <c>universe.csv</c> with every seed symbol present, eligible or not, and appends gate counts.
/// </summary>
public sealed class UniverseStep(HttpMessageHandler? handler = null) : IStudyStep
{
    private const string SecTickerExchangeUrl = "https://www.sec.gov/files/company_tickers_exchange.json";

    public string Verb => "universe";
    public string Description => "Seed symbols from the frozen CBOE directory, map to CIKs, apply name-level gates.";

    /// <summary>One SEC ticker-file row that matched a seed symbol's ticker string.</summary>
    private readonly record struct SecListing(long Cik, string Exchange, string Name);

    /// <summary>The outcome of resolving one seed symbol against the SEC ticker index.</summary>
    private readonly record struct Resolution(long? Cik, string? SecTicker, string? SecName, string? Exchange, IReadOnlyList<long>? AmbiguousCiks);

    public async Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var seedPath = ReadOption(args, "--seed") ?? context.Paths.UniverseSeed;
        var seed = ReadSeed(seedPath);
        context.Log($"universe: {seed.Count} seed rows from {seedPath}");

        var cachePath = Path.Combine(context.Paths.RawEdgarDirectory, "company_tickers_exchange.json");
        var userAgent = Environment.GetEnvironmentVariable("EDGAR_USER_AGENT");
        if (string.IsNullOrEmpty(userAgent) && !File.Exists(cachePath))
        {
            context.Log(
                "universe: EDGAR_USER_AGENT is not set and no cached company_tickers_exchange.json exists. " +
                "The SEC's fair-access policy refuses undeclared automated requests with 403; this fetch will " +
                "likely fail. Set EDGAR_USER_AGENT, or pre-populate the cache, to avoid that.");
        }

        string body;
        using (var edgar = new EdgarHttpClient(userAgent, handler))
        {
            body = await edgar.GetCachedAsync(SecTickerExchangeUrl, cachePath, cancellationToken);
        }

        var index = ParseSecTickers(body);

        // Stage 1: resolve each seed symbol independently. A symbol's CIK mapping never depends on
        // any other symbol's outcome.
        var resolved = new List<(string Symbol, string CompanyName, Resolution Resolution)>(seed.Count);
        var noMatch = 0;
        var ambiguous = 0;
        foreach (var (symbol, companyName) in seed)
        {
            var resolution = Resolve(symbol, index);
            resolved.Add((symbol, companyName, resolution));
            if (resolution.Cik is null)
            {
                if (resolution.AmbiguousCiks is { Count: > 0 }) ambiguous++; else noMatch++;
            }
        }

        // Stage 2: a CIK claimed by more than one distinct seed symbol is real (dual-class shares —
        // e.g. GOOG/GOOGL both file under one CIK) and every such symbol keeps its own mapping; it is
        // reported, not silently picked, per the WP1 brief.
        var symbolsByCik = resolved
            .Where(r => r.Resolution.Cik is not null)
            .GroupBy(r => r.Resolution.Cik!.Value)
            .Where(g => g.Select(r => r.Symbol).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Symbol).ToList());

        // Stage 3: gate 03 (primary listing), only over CIK-mapped rows, plus the per-exchange-value
        // tally the brief asks the gate-03 note to carry.
        var exchangeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var listingConsidered = 0;
        var listingRemoved = 0;
        var rows = new List<UniverseRow>(resolved.Count);

        foreach (var (symbol, companyName, resolution) in resolved)
        {
            if (resolution.Cik is null)
            {
                var note = resolution.AmbiguousCiks is { Count: > 0 } candidates
                    ? $"ticker '{resolution.SecTicker}' maps to {candidates.Count} CIKs in SEC data ({string.Join(",", candidates)}); ambiguous, not picked"
                    : "no ticker match in SEC company_tickers_exchange.json";
                rows.Add(new UniverseRow(symbol, companyName, null, resolution.SecTicker, null, null, false, Gates.CikMapped, note));
                continue;
            }

            listingConsidered++;
            var exchange = resolution.Exchange ?? "";
            var exchangeKey = exchange.Length > 0 ? exchange : "(empty)";
            exchangeCounts[exchangeKey] = exchangeCounts.GetValueOrDefault(exchangeKey) + 1;

            string? sharedNote = null;
            if (symbolsByCik.TryGetValue(resolution.Cik.Value, out var siblings))
            {
                var others = siblings.Where(s => s != symbol);
                sharedNote = $"CIK {resolution.Cik} shared with {string.Join(",", others)}";
            }

            var eligible = exchange.Length > 0 && C1Registration.AdmittedExchanges.Contains(exchange);
            if (!eligible)
            {
                listingRemoved++;
                var listingNote = $"exchange '{exchangeKey}' not in admitted set";
                rows.Add(new UniverseRow(symbol, companyName, resolution.Cik, resolution.SecTicker, resolution.SecName, resolution.Exchange,
                    false, Gates.PrimaryListing, Join(sharedNote, listingNote)));
                continue;
            }

            rows.Add(new UniverseRow(symbol, companyName, resolution.Cik, resolution.SecTicker, resolution.SecName, resolution.Exchange,
                true, null, sharedNote));
        }

        CsvFile.Write(context.Paths.Universe, rows);

        var totalSeed = seed.Count;
        var cikRemoved = noMatch + ambiguous;
        var gate02Note = $"no_match: {noMatch}; ambiguous_multiple_ciks: {ambiguous}" +
            (symbolsByCik.Count > 0
                ? $"; cik_shared_by_multiple_symbols: {symbolsByCik.Sum(kv => kv.Value.Count)} symbols across {symbolsByCik.Count} CIKs"
                : "");
        var gate03Note = string.Join(", ", OrderExchangeCounts(exchangeCounts).Select(kv => $"{kv.Key}: {kv.Value}"));

        CsvFile.Append(context.Paths.GateCounts,
        [
            new GateCountRow(Verb, 1, Gates.SeedOptionable, totalSeed, 0, totalSeed, null),
            new GateCountRow(Verb, 2, Gates.CikMapped, totalSeed, cikRemoved, totalSeed - cikRemoved, gate02Note),
            new GateCountRow(Verb, 3, Gates.PrimaryListing, listingConsidered, listingRemoved, listingConsidered - listingRemoved, gate03Note)
        ]);

        var eligibleCount = rows.Count(r => r.Eligible);
        context.Log($"universe: {eligibleCount}/{totalSeed} eligible after CIK mapping and primary-listing gates");
        return 0;
    }

    /// <summary>Exact match first, then with '.' replaced by '-' (the SEC spells class shares like
    /// BRK-B), else no CIK. A ticker string mapping to more than one CIK in the SEC file is ambiguous
    /// and is not picked, at whichever stage (exact or dashed) it is found.</summary>
    private static Resolution Resolve(string symbol, IReadOnlyDictionary<string, List<SecListing>> index)
    {
        var key = symbol.ToUpperInvariant();
        if (index.TryGetValue(key, out var exact))
        {
            return FromGroup(key, exact);
        }

        var dashed = key.Replace('.', '-');
        if (dashed != key && index.TryGetValue(dashed, out var viaDash))
        {
            return FromGroup(dashed, viaDash);
        }

        return new Resolution(null, null, null, null, null);

        static Resolution FromGroup(string ticker, List<SecListing> group)
        {
            var distinctCiks = group.Select(g => g.Cik).Distinct().ToList();
            if (distinctCiks.Count > 1)
            {
                return new Resolution(null, ticker, null, null, distinctCiks);
            }
            var listing = group[0];
            return new Resolution(listing.Cik, ticker, listing.Name, listing.Exchange, null);
        }
    }

    private static string? Join(string? a, string? b) => a is null ? b : (b is null ? a : $"{a}; {b}");

    /// <summary>Deterministic display order for the gate-03 note: the admitted exchanges first (in
    /// the order they are admitted), then the other known non-admitted values, then anything else by
    /// descending count, with the empty-exchange bucket always last.</summary>
    private static IEnumerable<KeyValuePair<string, int>> OrderExchangeCounts(Dictionary<string, int> counts)
    {
        string[] priority = ["NYSE", "Nasdaq", "NYSE American", "CBOE", "OTC"];
        foreach (var name in priority)
        {
            if (counts.TryGetValue(name, out var count)) yield return new(name, count);
        }
        foreach (var kv in counts
                     .Where(kv => !priority.Contains(kv.Key) && kv.Key != "(empty)")
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return kv;
        }
        if (counts.TryGetValue("(empty)", out var empty)) yield return new("(empty)", empty);
    }

    /// <summary>Parses <c>company_tickers_exchange.json</c> (<c>{"fields":[...],"data":[[...]]}</c>)
    /// into a ticker -> listings index, grouping rather than assuming uniqueness so an ambiguous
    /// ticker (multiple CIKs) is visible to <see cref="Resolve"/> rather than picked by whichever
    /// row happened to parse last.</summary>
    private static Dictionary<string, List<SecListing>> ParseSecTickers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var fields = root.GetProperty("fields").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        var cikIdx = fields.IndexOf("cik");
        var nameIdx = fields.IndexOf("name");
        var tickerIdx = fields.IndexOf("ticker");
        var exchangeIdx = fields.IndexOf("exchange");
        if (cikIdx < 0 || tickerIdx < 0 || exchangeIdx < 0)
        {
            throw new InvalidDataException(
                $"company_tickers_exchange.json: expected fields cik/ticker/exchange, found [{string.Join(",", fields)}]");
        }

        var index = new Dictionary<string, List<SecListing>>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("data").EnumerateArray())
        {
            var ticker = row[tickerIdx].GetString() ?? "";
            if (ticker.Length == 0) continue;
            var cik = row[cikIdx].GetInt64();
            var exchange = row[exchangeIdx].ValueKind == JsonValueKind.String ? row[exchangeIdx].GetString() ?? "" : "";
            var name = nameIdx >= 0 && row[nameIdx].ValueKind == JsonValueKind.String ? row[nameIdx].GetString() ?? "" : "";

            var key = ticker.ToUpperInvariant();
            if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add(new SecListing(cik, exchange, name));
        }
        return index;
    }

    /// <summary>Reads the frozen CBOE directory: header <c>Company Name, Stock Symbol, DPM Name,
    /// Post/Station, Global Trading Hours DPM</c> (note the space after every comma — header field
    /// names are trimmed before matching), quoted fields, one row per optionable symbol.</summary>
    private static List<(string Symbol, string CompanyName)> ReadSeed(string path)
    {
        var records = CsvFile.ParseRecords(File.ReadAllText(path));
        if (records.Count == 0) return [];

        var header = records[0].Select(h => h.Trim()).ToArray();
        var symbolIdx = Array.IndexOf(header, "Stock Symbol");
        var nameIdx = Array.IndexOf(header, "Company Name");
        if (symbolIdx < 0 || nameIdx < 0)
        {
            throw new InvalidDataException($"{path}: expected 'Company Name' and 'Stock Symbol' columns, found [{string.Join(",", header)}]");
        }

        var seed = new List<(string, string)>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var row = records[r];
            if (row.Length == 1 && row[0].Length == 0) continue; // trailing blank line
            seed.Add((row[symbolIdx].Trim(), row[nameIdx].Trim()));
        }
        return seed;
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name && i + 1 < args.Count) return args[i + 1];
        }
        return null;
    }
}
