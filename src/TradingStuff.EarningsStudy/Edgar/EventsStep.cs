using System.Globalization;
using System.Net;
using System.Text.Json;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Edgar;

/// <summary>
/// WP1. For every eligible universe name, reads the EDGAR submissions JSON (with its paginated
/// continuation files) and emits every 8-K whose items include 2.02, then applies the event-level
/// gates: not an 8-K/A, inside the registered window, one event per CIK-calendar-quarter with the
/// earliest acceptance winning. Also dumps every dei:EntityCommonStockSharesOutstanding fact per CIK
/// to <c>shares_facts.csv</c>; the as-of pick is made downstream against the entry date.
/// Requires <c>EDGAR_USER_AGENT</c> (the SEC's declared-automated-tool policy) and refuses to run without it.
/// </summary>
/// <param name="handler">Injected for tests so no suite ever touches the network.</param>
/// <param name="userAgentOverride">Injected for tests so the refusal/inclusion of a User-Agent does
/// not depend on mutating the real process environment (test isolation). Production leaves this
/// null and reads the real <c>EDGAR_USER_AGENT</c> environment variable.</param>
/// <param name="requestSpacing">Overrides <see cref="EdgarHttpClient"/>'s request spacing for tests.</param>
/// <param name="retryBaseDelay">Overrides <see cref="EdgarHttpClient"/>'s retry backoff base for tests.</param>
public sealed class EventsStep(
    HttpMessageHandler? handler = null,
    string? userAgentOverride = null,
    TimeSpan? requestSpacing = null,
    TimeSpan? retryBaseDelay = null) : IStudyStep
{
    private const string SubmissionsHost = "https://data.sec.gov";
    private const string SubmissionsBaseUrl = "https://data.sec.gov/submissions/";
    private const string CompanyFactsBaseUrl = "https://data.sec.gov/api/xbrl/companyfacts/";

    public string Verb => "events";
    public string Description => "Pull 8-K Item 2.02 events and shares-outstanding facts from EDGAR for the eligible universe.";

    public async Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var userAgent = userAgentOverride ?? Environment.GetEnvironmentVariable("EDGAR_USER_AGENT");
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            context.Output.WriteLine(
                "events: refusing to run. EDGAR_USER_AGENT is not set. The SEC's fair-access policy " +
                "requires every automated requester to declare a User-Agent naming the operator and a " +
                "contact (e.g. \"Research Bot contact@example.com\"); requests without one are refused " +
                "with 403 \"Undeclared Automated Tool\". Set EDGAR_USER_AGENT and re-run — this tool " +
                "will not invent a value for you.");
            return 2;
        }

        var universe = CsvFile.Read<UniverseRow>(context.Paths.Universe);
        var eligible = universe.Where(u => u.Eligible && u.Cik is not null).ToList();
        context.Log($"events: {eligible.Count} eligible names from {context.Paths.Universe}");

        using var edgar = new EdgarHttpClient(userAgent, handler, requestSpacing, retryBaseDelay);

        var events = new List<EventRow>();
        var namesWithZeroInWindowOriginal = 0;
        var submissionsUnavailable = 0;

        foreach (var name in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cik = name.Cik!.Value;
            var (filings, submissionsOk) = await FetchAllFilingsAsync(edgar, context, cik, cancellationToken);

            if (!submissionsOk)
            {
                submissionsUnavailable++;
                namesWithZeroInWindowOriginal++;
                context.Log($"events: submissions unavailable (404) for {name.Symbol} (CIK {cik})");
                continue;
            }

            var hasInWindowOriginal = false;
            foreach (var filing in filings)
            {
                var acceptanceDate = DateOnly.FromDateTime(filing.AcceptanceEt);
                var inWindow = acceptanceDate >= C1Registration.WindowFrom && acceptanceDate <= C1Registration.WindowTo;
                var isAmendment = filing.Form == "8-K/A";
                if (inWindow && !isAmendment) hasInWindowOriginal = true;

                events.Add(new EventRow(
                    $"{cik}:{filing.AccessionNumber}",
                    cik,
                    name.Symbol,
                    filing.AccessionNumber,
                    filing.Form,
                    filing.Items,
                    filing.FilingDate,
                    filing.ReportDate,
                    filing.AcceptanceEt,
                    inWindow,
                    false, // dedup (gate 06) is resolved below, once every name's rows are collected
                    inWindow ? (isAmendment ? "8-K/A ignored" : null) : "out of window"));
            }
            if (!hasInWindowOriginal) namesWithZeroInWindowOriginal++;
        }

        ResolveDedup(events);
        CsvFile.Write(context.Paths.Events, events);

        var (sharesFacts, companyFactsAttempted, companyFactsMissing) =
            await FetchSharesFactsAsync(edgar, context, eligible, cancellationToken);
        CsvFile.Write(context.Paths.SharesFacts, sharesFacts);

        AppendGateCounts(context.Paths.GateCounts, eligible.Count, namesWithZeroInWindowOriginal, submissionsUnavailable,
            companyFactsAttempted, companyFactsMissing, events);

        var keptEvents = events.Count(e => e.KeptAfterDedup);
        context.Log($"events: {events.Count} filing rows, {keptEvents} unique events after dedup, {sharesFacts.Count} shares-outstanding facts");
        return 0;
    }

    /// <summary>
    /// Gate 06: one event per (CIK, calendar quarter of the acceptance date), earliest acceptance
    /// instant wins — per the frozen pre-registration this is CIK-scoped, not security-scoped, so two
    /// eligible symbols that share one CIK (dual-class shares, e.g. GOOG/GOOGL both filing under one
    /// company) collapse to a single kept event for that CIK-quarter even though each symbol has its
    /// own option chain; the loser's DedupNote still names the winner's accession (which, in that
    /// specific case, equals the loser's own AccessionNumber — same filing, different Symbol row).
    /// Mutates by list index rather than an EventId-keyed lookup because EventId
    /// ($"{cik}:{accessionNumber}") is exactly the case that collides for that shared-CIK scenario.
    /// Ties (identical acceptance instants) are broken by <c>OrderBy</c>'s stable sort, i.e. by
    /// whichever row was encountered first — first filing wins, extended to an exact tie.
    /// </summary>
    private static void ResolveDedup(List<EventRow> events)
    {
        var candidates = events
            .Select((e, index) => (Event: e, Index: index))
            .Where(x => x.Event.InWindow && x.Event.Form != "8-K/A")
            .GroupBy(x => (x.Event.Cik, x.Event.AcceptanceEt.Year, Quarter: (x.Event.AcceptanceEt.Month - 1) / 3 + 1));

        foreach (var group in candidates)
        {
            var ordered = group.OrderBy(x => x.Event.AcceptanceEt).ToList();
            var kept = ordered[0];
            events[kept.Index] = kept.Event with { KeptAfterDedup = true, DedupNote = null };
            foreach (var loser in ordered.Skip(1))
            {
                events[loser.Index] = loser.Event with { KeptAfterDedup = false, DedupNote = $"dedup: kept {kept.Event.AccessionNumber}" };
            }
        }
    }

    private void AppendGateCounts(
        string gateCountsPath, int eligibleNameCount, int namesWithZeroInWindowOriginal, int submissionsUnavailable,
        int companyFactsAttempted, int companyFactsMissing, List<EventRow> events)
    {
        var gate04Note =
            $"submissions_unavailable: {submissionsUnavailable}; " +
            $"shares_outstanding_fact_missing: {companyFactsMissing} of {companyFactsAttempted} CIKs";

        var inWindowRows = events.Where(e => e.InWindow).ToList();
        var amendmentsRemoved = inWindowRows.Count(e => e.Form == "8-K/A");

        var dedupCandidates = inWindowRows.Where(e => e.Form != "8-K/A").ToList();
        var dedupRemoved = dedupCandidates.Count(e => !e.KeptAfterDedup);

        CsvFile.Append(gateCountsPath,
        [
            new GateCountRow(Verb, 4, Gates.HasItem202, eligibleNameCount, namesWithZeroInWindowOriginal,
                eligibleNameCount - namesWithZeroInWindowOriginal, gate04Note),
            new GateCountRow(Verb, 5, Gates.NotAmendment, inWindowRows.Count, amendmentsRemoved,
                inWindowRows.Count - amendmentsRemoved, null),
            new GateCountRow(Verb, 6, Gates.Dedup, dedupCandidates.Count, dedupRemoved,
                dedupCandidates.Count - dedupRemoved, null)
        ]);
    }

    /// <summary>Fetches (cached) the submissions document for one CIK plus every continuation file
    /// whose range intersects the study window, returning every selected (8-K/8-K-A, item 2.02)
    /// filing found across all of them. <c>SubmissionsOk=false</c> means the submissions endpoint
    /// itself 404'd for this CIK — a documented, non-crashing outcome the caller counts at gate 04.</summary>
    private static async Task<(List<RawFiling> Filings, bool SubmissionsOk)> FetchAllFilingsAsync(
        EdgarHttpClient edgar, StudyContext context, long cik, CancellationToken cancellationToken)
    {
        var cikPadded = cik.ToString("D10", CultureInfo.InvariantCulture);
        var url = $"{SubmissionsBaseUrl}CIK{cikPadded}.json";
        var cachePath = Path.Combine(context.Paths.RawEdgarDirectory, "submissions", $"CIK{cikPadded}.json");

        string body;
        try
        {
            body = await edgar.GetCachedAsync(url, cachePath, cancellationToken);
        }
        catch (EdgarHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return ([], false);
        }

        using var doc = JsonDocument.Parse(body);
        var (recent, files) = EdgarParsing.ParseSubmissions(doc.RootElement);

        var all = new List<RawFiling>(recent);
        foreach (var file in files)
        {
            if (!EdgarParsing.Intersects(file.FilingFrom, file.FilingTo, C1Registration.WindowFrom, C1Registration.WindowTo))
            {
                continue;
            }

            var contUrl = $"{SubmissionsHost}/submissions/{file.Name}";
            var contCachePath = Path.Combine(context.Paths.RawEdgarDirectory, "submissions", file.Name);
            var contBody = await edgar.GetCachedAsync(contUrl, contCachePath, cancellationToken);
            using var contDoc = JsonDocument.Parse(contBody);
            all.AddRange(EdgarParsing.ParseFilingsBlock(contDoc.RootElement));
        }

        // Defensive de-duplication on accession number: SEC's recent/continuation split should never
        // overlap, but nothing downstream should crash (a Dictionary keyed on accession number would
        // throw on a duplicate key) if it ever does.
        return (all.DistinctBy(f => f.AccessionNumber).ToList(), true);
    }

    /// <summary>
    /// Fetches (cached) companyfacts per DISTINCT CIK — not per eligible name — because the fact is a
    /// property of the filer, not of a specific optionable ticker; fetching per name would duplicate
    /// every row for a CIK shared by more than one eligible symbol (see <see cref="ResolveDedup"/>).
    /// </summary>
    private static async Task<(List<SharesFactRow> Rows, int Attempted, int Missing)> FetchSharesFactsAsync(
        EdgarHttpClient edgar, StudyContext context, List<UniverseRow> eligible, CancellationToken cancellationToken)
    {
        var rows = new List<SharesFactRow>();
        var attempted = 0;
        var missing = 0;

        foreach (var cik in eligible.Select(e => e.Cik!.Value).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            var cikPadded = cik.ToString("D10", CultureInfo.InvariantCulture);
            var url = $"{CompanyFactsBaseUrl}CIK{cikPadded}.json";
            var cachePath = Path.Combine(context.Paths.RawEdgarDirectory, "companyfacts", $"CIK{cikPadded}.json");

            string body;
            try
            {
                body = await edgar.GetCachedAsync(url, cachePath, cancellationToken);
            }
            catch (EdgarHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                missing++;
                continue;
            }

            using var doc = JsonDocument.Parse(body);
            if (!TryGetSharesOutstandingArray(doc.RootElement, out var shares))
            {
                missing++;
                continue;
            }

            foreach (var entry in shares.EnumerateArray())
            {
                rows.Add(new SharesFactRow(
                    cik,
                    DateOnly.ParseExact(entry.GetProperty("end").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    entry.GetProperty("val").GetDecimal(),
                    DateOnly.ParseExact(entry.GetProperty("filed").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    entry.GetProperty("form").GetString() ?? "",
                    entry.GetProperty("accn").GetString() ?? "",
                    entry.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.String ? frame.GetString() : null));
            }
        }

        return (rows, attempted, missing);
    }

    private static bool TryGetSharesOutstandingArray(JsonElement root, out JsonElement shares)
    {
        shares = default;
        return root.TryGetProperty("facts", out var facts) &&
               facts.TryGetProperty("dei", out var dei) &&
               dei.TryGetProperty("EntityCommonStockSharesOutstanding", out var fact) &&
               fact.TryGetProperty("units", out var units) &&
               units.TryGetProperty("shares", out shares) &&
               shares.ValueKind == JsonValueKind.Array;
    }
}
