namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// Splits a date range into chunks no wider than ThetaData's own bulk-history limit.
/// </summary>
/// <remarks>
/// Measured live against the Terminal 2026-08-02, and NOT documented anywhere the client's
/// original author had seen: <c>/v3/option/history/quote</c> answers a <c>[start_date, end_date]</c>
/// span wider than about a month with <c>HTTP 400 "Bulk history requests are limited to no more
/// than 1 month."</c> — discovered because a two-and-a-half-month ingestion job failed every single
/// one of its 24 request rows on the first live drain. <see cref="OptionChainCoordinator"/> chunks
/// every request's date range through this class rather than assuming a job's whole
/// <c>[target_from, target_to]</c> answers in one call, which is what the vendor's own bulk-quote
/// example in <c>ThetaDataClient.GetDailyChainQuotesAsync</c>'s doc comment implicitly assumed.
/// </remarks>
public static class MonthlyDateRangeChunker
{
    /// <summary>
    /// 28 days rather than a calendar month: safely under "no more than 1 month" regardless of
    /// whether the vendor means a calendar month (28-31 days) or a fixed 30/31-day span, without
    /// needing to special-case February or a leap year.
    /// </summary>
    public const int MaxDaysPerChunk = 28;

    public static IReadOnlyList<(DateTime Start, DateTime End)> Split(DateTime from, DateTime to)
    {
        if (to < from)
        {
            throw new ArgumentException($"'to' ({to:yyyy-MM-dd}) is before 'from' ({from:yyyy-MM-dd}).");
        }

        var chunks = new List<(DateTime, DateTime)>();
        var chunkStart = from;

        while (chunkStart <= to)
        {
            var chunkEnd = chunkStart.AddDays(MaxDaysPerChunk - 1);
            if (chunkEnd > to)
            {
                chunkEnd = to;
            }

            chunks.Add((chunkStart, chunkEnd));
            chunkStart = chunkEnd.AddDays(1);
        }

        return chunks;
    }
}
