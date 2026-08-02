using System.Text.Json;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// Runs the ThetaData capability probes this platform's design decisions actually lean on, and
/// persists every finding into <c>research.capability_probes</c> — the same registry the IBKR
/// gateway uses (migration 002), so "what does the account actually serve" has one place to look
/// regardless of vendor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The as-of probe is the one that matters most.</b> "An as-of chain reconstruction is
/// survivorship-free" is a negative claim (docs/DECISIONS.md §16 class (c)): the review has to name
/// the check that would DETECT a violation, and "we used a vendor that serves expired contracts" is
/// not that check — a vendor could serve expired contracts while still answering a strike-list query
/// with today's currently-listed strikes filtered by expiration, which would silently reintroduce
/// survivorship bias into every reconstructed chain. <see cref="RunAsOfProbeAsync"/> is the actual
/// check: it compares the strike range ThetaData reports for a genuinely old, already-expired series
/// against the strike range it reports for a series expiring soon, and requires the old series' range
/// to sit far below the recent one's — the two could not coincide if the "old" answer were secretly
/// built from today's strike universe, because today's SPX is roughly 5-6x its 2012 level.
/// </para>
/// <para>
/// Every number here is re-measured against the live Terminal at call time, never copied from a
/// prompt or a comment — the whole point of a capability probe is that documentation is not evidence.
/// </para>
/// </remarks>
public sealed class OptionChainCapabilityProbes(
    ThetaDataClient client, OptionChainStore store, ILogger<OptionChainCapabilityProbes> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public sealed record AsOfProbeResult(
        string Symbol,
        DateOnly HistoricalExpiration,
        int HistoricalStrikeCount,
        decimal HistoricalMinStrike,
        decimal HistoricalMaxStrike,
        int HistoricalQuoteRowCount,
        DateOnly RecentExpiration,
        int RecentStrikeCount,
        decimal RecentMinStrike,
        decimal RecentMaxStrike,
        bool IsGenuinelyAsOf,
        string Explanation);

    public sealed record ProbeReport(bool Succeeded, string Detail);

    /// <summary>Runs every probe this ingestion package depends on and persists each result.</summary>
    public async Task<IReadOnlyList<(string ProbeKey, ProbeReport Report)>> RunAllAsync(CancellationToken cancellationToken)
    {
        var results = new List<(string, ProbeReport)>();

        foreach (var symbol in new[] { "SPXW", "SPX", "VIX" })
        {
            results.Add(($"thetadata:options:{symbol}:expirations", await RunExpirationProbeAsync(symbol)));
        }

        results.Add(("thetadata:options:quote_schema", await RunQuoteSchemaProbeAsync()));
        results.Add(("thetadata:index:subscription", await RunSubscriptionProbeAsync(
            "index", () => client.GetIndexPriceAsync("SPX", DateTime.UtcNow.Date.AddDays(-5), DateTime.UtcNow.Date.AddDays(-5), TimeSpan.FromMinutes(1)))));
        results.Add(("thetadata:stock:subscription", await RunSubscriptionProbeAsync(
            "stock", () => client.GetStockOhlcAsync("SPY", DateTime.UtcNow.Date.AddDays(-5), DateTime.UtcNow.Date.AddDays(-5), TimeSpan.FromMinutes(1)))));

        var asOf = await RunAsOfProbeAsync(cancellationToken);
        results.Add(asOf);

        return results;
    }

    private async Task<ProbeReport> RunExpirationProbeAsync(string symbol)
    {
        try
        {
            var table = await client.ListExpirationsAsync(symbol);
            var column = table.RequireColumn("expiration");

            var expirations = table.Rows
                .Select(row => DateOnly.Parse(CsvTable.GetString(row, column)))
                .OrderBy(e => e)
                .ToList();

            var result = new
            {
                symbol,
                count = expirations.Count,
                firstExpiration = expirations.Count > 0 ? expirations[0].ToString("yyyy-MM-dd") : null,
                lastExpiration = expirations.Count > 0 ? expirations[^1].ToString("yyyy-MM-dd") : null,
            };

            var json = JsonSerializer.Serialize(result, Json);
            await store.RecordCapabilityProbeAsync(
                $"thetadata:options:{symbol}:expirations", true, json,
                $"{expirations.Count} expirations listed for {symbol}.", CancellationToken.None);

            return new ProbeReport(true, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync($"thetadata:options:{symbol}:expirations", ex);
        }
    }

    private async Task<ProbeReport> RunQuoteSchemaProbeAsync()
    {
        try
        {
            // A recent trading day, far enough back that the session has settled and is not "today,
            // possibly still open" — the schema probe cares about columns, not freshness.
            var day = PreviousWeekday(DateTime.UtcNow.Date.AddDays(-3));
            var expirations = await client.ListExpirationsAsync("SPXW");
            var expirationColumn = expirations.RequireColumn("expiration");

            var nearestExpiration = expirations.Rows
                .Select(row => DateOnly.Parse(CsvTable.GetString(row, expirationColumn)))
                .Where(e => e >= DateOnly.FromDateTime(day))
                .OrderBy(e => e)
                .First();

            var table = await client.GetDailyChainQuotesAsync(
                "SPXW", nearestExpiration.ToDateTime(TimeOnly.MinValue), day, day);

            var columns = table.ColumnNames.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
            var expectedNbboColumns = new[] { "bid", "ask", "bid_size", "ask_size", "bid_exchange", "ask_exchange" };
            var hasFullNbbo = expectedNbboColumns.All(table.HasColumn);

            var result = new { columns, hasFullNbbo, sampleRowCount = table.Count };
            var json = JsonSerializer.Serialize(result, Json);

            await store.RecordCapabilityProbeAsync(
                "thetadata:options:quote_schema", true, json,
                hasFullNbbo
                    ? "Full NBBO both sides confirmed (bid/ask/size/exchange)."
                    : "Some expected NBBO columns were missing — see result.columns.",
                CancellationToken.None);

            return new ProbeReport(true, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync("thetadata:options:quote_schema", ex);
        }
    }

    private async Task<ProbeReport> RunSubscriptionProbeAsync(string assetClass, Func<Task<CsvTable>> probe)
    {
        try
        {
            var table = await probe();
            var json = JsonSerializer.Serialize(new { assetClass, subscribed = true, rows = table.Count }, Json);
            await store.RecordCapabilityProbeAsync(
                $"thetadata:{assetClass}:subscription", true, json,
                $"{assetClass} history is available on this subscription.", CancellationToken.None);
            return new ProbeReport(true, json);
        }
        catch (ThetaDataSubscriptionException ex)
        {
            var json = JsonSerializer.Serialize(new { assetClass, subscribed = false }, Json);
            await store.RecordCapabilityProbeAsync(
                $"thetadata:{assetClass}:subscription", true, json,
                $"{assetClass} is not covered by this subscription (403), reported cleanly: {ex.Message}",
                CancellationToken.None);
            return new ProbeReport(true, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync($"thetadata:{assetClass}:subscription", ex);
        }
    }

    /// <summary>
    /// THE negative-claim check: see the class remarks. Compares the strike range of a genuinely old,
    /// already-expired SPXW series against a series expiring soon, and requires the old range to sit
    /// far below the recent one — which could not happen if the "old" answer were secretly built from
    /// today's strike universe.
    /// </summary>
    private async Task<(string, ProbeReport)> RunAsOfProbeAsync(CancellationToken cancellationToken)
    {
        const string ProbeKey = "thetadata:options:asof_strike_list";

        try
        {
            var expirationsTable = await client.ListExpirationsAsync("SPXW");
            var column = expirationsTable.RequireColumn("expiration");

            var allExpirations = expirationsTable.Rows
                .Select(row => DateOnly.Parse(CsvTable.GetString(row, column)))
                .OrderBy(e => e)
                .ToList();

            // The earliest genuinely historical series with a real trading history behind it — the
            // very first listed expiration has the least time for a market to have developed, so the
            // second one (still within the first week SPXW existed) is used instead.
            var historicalExpiration = allExpirations[1];

            // A near-term expiration relative to TODAY, i.e. the vendor's own "recent" answer,
            // resolved live rather than hardcoded — the whole point is to compare against whatever
            // the Terminal says right now.
            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var recentExpiration = allExpirations.First(e => e >= today.AddDays(14));

            var historicalStrikes = await StrikeRangeAsync("SPXW", historicalExpiration);
            var recentStrikes = await StrikeRangeAsync("SPXW", recentExpiration);

            // Quotes, not just a metadata stub: the old series must have real NBBO history, not merely
            // appear in the expiration/strike lists. A trading weekday five calendar days before
            // expiry — not a bare AddDays(-5), which can land on a weekend and be reported as
            // "no data" for a reason that has nothing to do with the as-of question being asked.
            var quoteDay = PreviousWeekday(historicalExpiration.ToDateTime(TimeOnly.MinValue).AddDays(-5));
            int quoteRowCount;
            try
            {
                var quoteTable = await client.GetDailyChainQuotesAsync(
                    "SPXW", historicalExpiration.ToDateTime(TimeOnly.MinValue), quoteDay, quoteDay);
                quoteRowCount = quoteTable.Count;
            }
            catch (ThetaDataNoDataException)
            {
                quoteRowCount = 0;
            }

            // A strike range that is contaminated by today's currently-listed strikes would put the
            // historical maximum within striking distance of the recent maximum — SPX has moved
            // roughly 5-6x since SPXW's 2012 start, so a genuinely as-of answer for a 2012-era series
            // must top out far below a 2026-era one. Twice is a conservative bar given that ratio.
            var isGenuinelyAsOf = historicalStrikes.Max * 2m < recentStrikes.Max && quoteRowCount > 0;

            var explanation =
                $"Historical {historicalExpiration:yyyy-MM-dd} strikes span {historicalStrikes.Min:F0}-" +
                $"{historicalStrikes.Max:F0} ({historicalStrikes.Count} strikes, {quoteRowCount} quote rows " +
                $"confirmed on {quoteDay:yyyy-MM-dd}); recent {recentExpiration:yyyy-MM-dd} strikes span " +
                $"{recentStrikes.Min:F0}-{recentStrikes.Max:F0} ({recentStrikes.Count} strikes). " +
                (isGenuinelyAsOf
                    ? "The historical range sits far below the recent range and carries real quote " +
                      "history, which a today's-list-filtered-by-expiration answer could not produce — " +
                      "this is evidence the vendor's expiration/strike lists are genuinely as-of, not a " +
                      "current universe filtered after the fact."
                    : "The historical range does NOT sit clearly below the recent range, or no quote " +
                      "history was found for it — this does NOT confirm as-of correctness and the " +
                      "survivorship-free claim should be treated as UNVERIFIED until investigated.");

            var result = new AsOfProbeResult(
                "SPXW", historicalExpiration, historicalStrikes.Count, historicalStrikes.Min, historicalStrikes.Max,
                quoteRowCount, recentExpiration, recentStrikes.Count, recentStrikes.Min, recentStrikes.Max,
                isGenuinelyAsOf, explanation);

            var json = JsonSerializer.Serialize(result, Json);
            await store.RecordCapabilityProbeAsync(ProbeKey, isGenuinelyAsOf, json, explanation, cancellationToken);

            if (!isGenuinelyAsOf)
            {
                logger.LogError("As-of probe FAILED: {Explanation}", explanation);
            }
            else
            {
                logger.LogInformation("As-of probe passed: {Explanation}", explanation);
            }

            return (ProbeKey, new ProbeReport(isGenuinelyAsOf, json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var report = await FailAsync(ProbeKey, ex);
            return (ProbeKey, report);
        }
    }

    private async Task<(int Count, decimal Min, decimal Max)> StrikeRangeAsync(string symbol, DateOnly expiration)
    {
        var table = await client.ListStrikesAsync(symbol, expiration.ToDateTime(TimeOnly.MinValue));
        var column = table.RequireColumn("strike");

        var strikes = table.Rows
            .Select(row => (decimal)CsvTable.GetDouble(row, column))
            .Where(s => s > 0m)
            .ToList();

        return (strikes.Count, strikes.Count > 0 ? strikes.Min() : 0m, strikes.Count > 0 ? strikes.Max() : 0m);
    }

    private static DateTime PreviousWeekday(DateTime date)
    {
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            date = date.AddDays(-1);
        }

        return date;
    }

    private async Task<ProbeReport> FailAsync(string probeKey, Exception ex)
    {
        logger.LogWarning(ex, "Probe {ProbeKey} failed.", probeKey);
        var json = JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex.Message }, Json);
        await store.RecordCapabilityProbeAsync(probeKey, false, json, ex.Message, CancellationToken.None);
        return new ProbeReport(false, json);
    }
}
