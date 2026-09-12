using System.Globalization;
using System.Text.Json;

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
    /// EDGAR's <c>acceptanceDateTime</c> is shaped like ISO-8601 UTC ("2024-02-01T16:30:38.000Z")
    /// but the trailing "Z" is NOT a UTC offset marker: EDGAR stamps this field in Eastern wall-clock
    /// time regardless of the filer's own location. Verified against Apple's FY24 Q1 8-K (CIK
    /// 320193): it was released after the 2024-02-01 close, and this field reads "16:30:38" — a true
    /// UTC timestamp for an after-the-close release would read roughly 21:00-22:00 (accounting for
    /// EST/EDT), not 16:30. So: strip the "Z", parse what remains as a plain local-looking timestamp,
    /// and stamp the result Unspecified — here "Unspecified" specifically means "Eastern wall clock",
    /// by the convention of this one EDGAR field, not "timezone unknown".
    /// </summary>
    public static DateTime ParseAcceptanceEt(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z')) trimmed = trimmed[..^1];
        var parsed = DateTime.ParseExact(
            trimmed,
            ["yyyy-MM-dd'T'HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
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
