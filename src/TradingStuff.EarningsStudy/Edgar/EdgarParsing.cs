using System.Globalization;
using System.Text.Json;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Edgar;

/// <summary>One 8-K (or 8-K/A) filing with a 2.02 item, before the event-level gates are applied.</summary>
internal sealed record RawFiling(
    string AccessionNumber,
    string Form,
    string Items,
    DateOnly FilingDate,
    DateOnly? ReportDate,
    DateTime AcceptanceEt);

/// <summary>One entry of a submissions document's <c>filings.files</c> array: an older-filings
/// continuation file and the date range it covers.</summary>
internal sealed record ContinuationFileRef(string Name, DateOnly FilingFrom, DateOnly FilingTo);

/// <summary>
/// The EDGAR submissions/companyfacts JSON parsing rules, factored out of <see cref="EventsStep"/>
/// so the <c>RequiresEdgar</c> live test exercises the exact same code the batch run does rather than
/// a re-implementation that could quietly diverge from it (docs/LESSONS.md: reproduce, don't inspect).
/// </summary>
internal static class EdgarParsing
{
    /// <summary>Item 2.02 selection: the items string split on commas and trimmed must contain the
    /// exact token "2.02" — a substring match would also fire on a hypothetical "12.02" or "2.021",
    /// which this must not do.</summary>
    public static bool HasItem202(string items) =>
        items.Split(',').Any(token => token.Trim() == "2.02");

    /// <summary>
    /// EDGAR's <c>acceptanceDateTime</c> is an ISO-8601 UTC instant ("2024-02-01T21:30:30.000Z") and
    /// the trailing "Z" means exactly what it says. The value is converted once, at
    /// <see cref="EdgarAcceptance.FromUtcInstant"/>, into the America/New_York wall clock the rest of
    /// the study carries; the returned <see cref="DateTimeKind.Unspecified"/> means "Eastern wall
    /// clock", not "timezone unknown", which is the contract
    /// <see cref="EdgarAcceptance.Resolve"/> enforces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Observed live 2026-09-13</b> against <c>data.sec.gov/submissions/CIK0000320193.json</c>:
    /// Apple's 2024-02-01 8-K, accession 0000320193-24-000005, reads
    /// <c>"2024-02-01T21:30:30.000Z"</c>, and its 2026-07-30 8-K reads
    /// <c>"2026-07-30T20:30:28.000Z"</c>. Both are the same 16:30 Eastern acceptance: 21:30Z under
    /// EST (UTC-5), 20:30Z under EDT (UTC-4). A field stamped in a fixed wall clock could not shift
    /// by an hour with the season, so the offset is genuine.
    /// </para>
    /// <para>
    /// This method previously stripped the "Z" and read the digits as Eastern, on a doc comment
    /// claiming that reading had been "verified against Apple's FY24 Q1 8-K … reads 16:30:38". No
    /// live fetch ever returned that string — EDGAR was unreachable from the sandbox that wrote the
    /// claim — and the effect was to place every acceptance four or five hours late, moving an AMC
    /// print past midnight and, with it, the print date and every measurement date derived from it.
    /// The registered <c>Category=RequiresEdgar</c> pin is what caught it, by failing with hour 21
    /// where it expected 16 (docs/LESSONS.md 4: a confident comment is where defects hide, and
    /// "verified against X" written where X was unreachable is a claim, not a verification).
    /// </para>
    /// <para>
    /// The offset marker is therefore load-bearing, so a stamp that is not Z-marked UTC is refused
    /// rather than read under a guess — the same data-shape break as a missing
    /// <c>acceptanceDateTime</c>, and for the same reason (docs/LESSONS.md 8).
    /// </para>
    /// </remarks>
    public static DateTime ParseAcceptanceEt(string raw)
    {
        var trimmed = raw.Trim();
        var digits = trimmed.Length > 0 && trimmed[^1] is 'Z' or 'z' ? trimmed[..^1] : null;

        if (digits is null || !DateTime.TryParseExact(
                digits,
                ["yyyy-MM-dd'T'HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new InvalidDataException(
                $"EDGAR acceptanceDateTime '{raw}' is not a Z-marked UTC instant of the form " +
                "yyyy-MM-ddTHH:mm:ss[.fff]Z. Every acceptanceDateTime EDGAR has been observed to " +
                "emit is; the zone marker decides what the digits mean, so a different shape is " +
                "treated as a data-shape break rather than read under an assumed zone.");
        }

        return EdgarAcceptance.FromUtcInstant(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
    }

    /// <summary>
    /// Reads one "filings block" — the parallel-array shape shared by <c>filings.recent</c> in the
    /// main submissions document and the top level of each continuation file — and returns every
    /// entry whose form is 8-K/8-K-A and whose items contain 2.02. A row missing an
    /// <c>acceptanceDateTime</c> throws rather than guessing one: every real EDGAR filing carries
    /// this field, so its absence means an assumption this parser relies on no longer holds, and
    /// docs/LESSONS.md #8 ("refuse rather than project") says stop rather than fabricate a date.
    /// </summary>
    public static List<RawFiling> ParseFilingsBlock(JsonElement block)
    {
        var accessionNumber = ReadStringArray(block, "accessionNumber");
        var filingDate = ReadStringArray(block, "filingDate");
        var reportDate = ReadStringArray(block, "reportDate");
        var acceptanceDateTime = ReadStringArray(block, "acceptanceDateTime");
        var form = ReadStringArray(block, "form");
        var items = ReadStringArray(block, "items");

        var count = accessionNumber.Count;
        var result = new List<RawFiling>();
        for (var i = 0; i < count; i++)
        {
            var formValue = i < form.Count ? form[i] : "";
            if (formValue is not ("8-K" or "8-K/A")) continue;

            var itemsValue = i < items.Count ? items[i] : "";
            if (!HasItem202(itemsValue)) continue;

            var acceptanceRaw = i < acceptanceDateTime.Count ? acceptanceDateTime[i] : "";
            if (acceptanceRaw.Length == 0)
            {
                throw new InvalidDataException(
                    $"EDGAR filing {accessionNumber[i]} (form {formValue}, items '{itemsValue}') has no acceptanceDateTime; " +
                    "every real EDGAR filing carries one, so this is treated as a data-shape break rather than an event to skip.");
            }

            var filingDateValue = DateOnly.ParseExact(filingDate[i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var reportDateRaw = i < reportDate.Count ? reportDate[i] : "";
            var reportDateValue = reportDateRaw.Length > 0
                ? DateOnly.ParseExact(reportDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : (DateOnly?)null;

            result.Add(new RawFiling(
                accessionNumber[i], formValue, itemsValue, filingDateValue, reportDateValue,
                ParseAcceptanceEt(acceptanceRaw)));
        }
        return result;
    }

    /// <summary>Parses a full <c>submissions/CIK##########.json</c> document into its recent filings
    /// (already filtered to 8-K/2.02 by <see cref="ParseFilingsBlock"/>) and the continuation-file
    /// index for older filings.</summary>
    public static (List<RawFiling> Recent, List<ContinuationFileRef> Files) ParseSubmissions(JsonElement root)
    {
        var filings = root.GetProperty("filings");
        var recent = ParseFilingsBlock(filings.GetProperty("recent"));

        var files = new List<ContinuationFileRef>();
        if (filings.TryGetProperty("files", out var filesArray) && filesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesArray.EnumerateArray())
            {
                files.Add(new ContinuationFileRef(
                    f.GetProperty("name").GetString()!,
                    DateOnly.ParseExact(f.GetProperty("filingFrom").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(f.GetProperty("filingTo").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
        }
        return (recent, files);
    }

    /// <summary>Whether a continuation file's coverage range overlaps the study window at all
    /// (inclusive on both ends) — the only test applied before fetching it.</summary>
    public static bool Intersects(DateOnly rangeFrom, DateOnly rangeTo, DateOnly windowFrom, DateOnly windowTo) =>
        rangeFrom <= windowTo && rangeTo >= windowFrom;

    private static List<string> ReadStringArray(JsonElement block, string propertyName)
    {
        if (!block.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<string>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            result.Add(element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "");
        }
        return result;
    }
}
