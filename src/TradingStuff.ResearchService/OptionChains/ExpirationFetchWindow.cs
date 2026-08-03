namespace TradingStuff.ResearchService.OptionChains;

/// <summary>
/// Bounds one expiration's quote-history fetch to the window where its quotes can exist, before
/// the range is chunked for the vendor's one-month span cap.
/// </summary>
/// <remarks>
/// Measured live 2026-08-02 (first real drain of jobs 9/10): a no-data chunk of
/// <c>/v3/option/history/quote</c> costs 14–47 seconds — the Terminal consults the vendor's
/// servers even for a range it will answer 472 — and an unbounded 2012→2026 job range chunks
/// into ~191 requests per expiration, of which all but a handful are guaranteed empty: an
/// option has no quotes after its settlement week, and none before it is listed. Unbounded,
/// the first expiration took ~75 minutes for 110 quote rows and the full drain projected to
/// months; bounded, an expiration is a handful of chunks.
///
/// The lookback is deliberately generous. SPXW weeklies list ~6 weeks out, so 120 days more
/// than covers them; SPX monthlies can list far earlier (LEAPS), and quotes further than
/// 120 days before expiry are the one thing this bound genuinely skips. That loss is accepted
/// and recorded here: nothing downstream reads them — the A4 bracket legs are structurally
/// ≤ ~60 days out while monthlies exist, and the hedged-carry menu's longest-dated candidate
/// is a rolled 2–3-month crash bone. Skipped-by-bound is a declared non-goal, not vendor
/// absence: absence semantics for anything within the window are untouched.
/// </remarks>
public static class ExpirationFetchWindow
{
    /// <summary>Days of history fetched before an expiration's settlement date.</summary>
    public const int LookbackDays = 120;

    /// <summary>
    /// Days fetched past the expiration date — a grace week, so a settlement-calendar quirk
    /// (or an off-by-one in the vendor's date keying) cannot silently drop the final sessions.
    /// </summary>
    public const int GraceDays = 7;

    /// <summary>
    /// The job-range ∩ expiration-lifetime window to fetch, or null when they do not overlap
    /// (unreachable for rows the planner created, which filters expirations to the job range —
    /// guarded anyway so a hand-edited row degrades to "empty", not an inverted-range throw).
    /// </summary>
    public static (DateOnly Start, DateOnly End)? For(DateOnly targetFrom, DateOnly targetTo, DateOnly expiration)
    {
        var start = Max(targetFrom, expiration.AddDays(-LookbackDays));
        var end = Min(targetTo, expiration.AddDays(GraceDays));

        return start <= end ? (start, end) : null;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
