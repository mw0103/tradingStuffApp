namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// The sample statistics of the C1 pre-registration. Every choice the registration leaves open is
/// fixed HERE, before any real data is seen, and repeated word for word in the memo
/// (<see cref="MemoWriter"/>) so the reader of the memo can check the code against the text:
///
/// <list type="bullet">
/// <item>mean (the v2 primary statistic): summed in decimal over the sample, divided once;</item>
/// <item>median: odd n is the middle order statistic, EVEN n is the mean of the two middle values;</item>
/// <item>P(RF &lt; IM): strict, ties counted as not-less and reported separately;</item>
/// <item>quantiles: nearest rank, no interpolation;</item>
/// <item>trimmed mean: floor(fraction x n) dropped from EACH tail;</item>
/// <item>quintiles: equal-count buckets by rank, ties broken by event id, extras to the low buckets.</item>
/// </list>
///
/// An undefined statistic returns null. It is never zero, never a default: an honest gap in the
/// record beats a plausible fabrication (docs/LESSONS.md §8).
/// </summary>
public static class Statistics
{
    /// <summary>
    /// The mean of a sample — the PRIMARY statistic under v2. Summed in <c>decimal</c> in the sample's
    /// own order and divided once at the end; no running average, no <c>double</c>, so the value is a
    /// function of the multiset and not of how the addition was chunked. Null for an empty sample.
    /// </summary>
    public static decimal? Mean(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var total = 0m;
        foreach (var value in values) total += value;
        return total / values.Count;
    }

    /// <summary>
    /// The median of a sample. Odd n is the middle order statistic; even n is the mean of the two
    /// middle values — the pre-registration does not say which convention, so this one is fixed here
    /// and stated in the memo. Null for an empty sample.
    ///
    /// DESCRIPTIVE READOUT under v2: reported, never deciding. It measures the shape of the ratio
    /// distribution, which a fairly priced market already satisfies.
    /// </summary>
    public static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return MedianOfSorted(sorted, sorted.Length);
    }

    /// <summary>The median of the first <paramref name="count"/> elements of an already-sorted buffer.</summary>
    public static decimal MedianOfSorted(decimal[] sortedAscending, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var middle = count / 2;
        return count % 2 == 1 ? sortedAscending[middle] : (sortedAscending[middle - 1] + sortedAscending[middle]) / 2m;
    }

    /// <summary>
    /// P(RF &lt; IM), a DESCRIPTIVE READOUT under v2 — reported, never deciding.
    ///
    /// Strictly less: an event whose realized move exactly equals its implied move
    /// counts as NOT less, and is reported separately as a tie. The comparison itself is made on RF
    /// and IM when the sample is built, never on the stored ratio, so a ratio that rounds to exactly
    /// 1 at decimal's precision cannot flip a boundary event. Null for an empty sample.
    /// </summary>
    public static decimal? ProportionLess(IReadOnlyList<SampleEvent> sample) =>
        sample.Count == 0 ? null : (decimal)sample.Count(e => e.RfLessThanIm) / sample.Count;

    /// <summary>Events whose realized move exactly equals their implied move.</summary>
    public static int TieCount(IReadOnlyList<SampleEvent> sample) => sample.Count(e => e.RfEqualsIm);

    /// <summary>
    /// The empirical quantile by the NEAREST-RANK rule: the smallest value whose 1-based rank is at
    /// least p x n, clamped into the sample. No interpolation between order statistics — a
    /// percentile interval must be a value the resampling actually produced, not a weighted average
    /// of two values it produced.
    /// </summary>
    public static decimal Quantile(decimal[] sortedAscending, decimal p)
    {
        if (sortedAscending.Length == 0) throw new ArgumentException("no values to take a quantile of", nameof(sortedAscending));
        var rank = (int)Math.Ceiling(p * sortedAscending.Length);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
    }

    /// <summary>Nearest-rank quantiles of a measured series, for the memo's limitations section.</summary>
    public static QuantileSummary Quantiles(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return new QuantileSummary(0, null, null, null, null, null);
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return new QuantileSummary(
            sorted.Length,
            Quantile(sorted, 0.50m),
            Quantile(sorted, 0.90m),
            Quantile(sorted, 0.95m),
            Quantile(sorted, 0.99m),
            sorted[^1]);
    }

    /// <summary>
    /// The secondary log statistics: mean log(RF/IM), and the symmetrically trimmed mean that drops
    /// floor(<paramref name="trimFraction"/> x n) values from EACH tail of the sorted log ratios.
    ///
    /// An event with RF = 0 has no logarithm and is excluded from these two statistics ONLY — it
    /// stays in the primary median and in P(RF &lt; IM) — and is counted so the exclusion is visible.
    /// n for the trim is the count AFTER that exclusion.
    /// </summary>
    public static LogStatistics Logs(IReadOnlyList<SampleEvent> sample, decimal trimFraction)
    {
        var considered = sample.Count;
        var logs = sample.Where(e => e.Ratio > 0m).Select(e => Log(e.Ratio)).ToArray();
        var zeros = considered - logs.Length;
        if (logs.Length == 0)
        {
            return new LogStatistics(considered, zeros, 0, 0, null, null,
                considered == 0 ? "the sample is empty" : "every event in the sample has RF = 0, which has no logarithm");
        }

        Array.Sort(logs);
        var mean = logs.Average();
        var perTail = (int)Math.Floor(trimFraction * logs.Length);
        if (2 * perTail >= logs.Length)
        {
            return new LogStatistics(considered, zeros, logs.Length, perTail, mean, null,
                $"trimming {perTail} from each tail of {logs.Length} would leave nothing");
        }

        var kept = logs.AsSpan(perTail, logs.Length - (2 * perTail));
        var total = 0d;
        foreach (var value in kept) total += value;
        return new LogStatistics(considered, zeros, logs.Length, perTail, mean, total / kept.Length, null);
    }

    /// <summary>
    /// The study's only <c>double</c>, and the only place one is produced. <c>log</c> is not defined
    /// on <see cref="decimal"/>, and once a logarithm has been taken the extra decimal digits are
    /// gone anyway; the conversion happens here, once, and nothing downstream of it re-enters
    /// decimal arithmetic. This is the boundary in exactly the sense IBApi is (CLAUDE.md), and it
    /// covers the secondary log statistics and nothing else.
    /// </summary>
    private static double Log(decimal ratio) => Math.Log((double)ratio);

    /// <summary>
    /// Equal-count quintiles by rank: bucket = floor(rank x 5 / n) for a 0-based ascending rank,
    /// clamped to 4. When n is not divisible by 5 the bucket sizes differ by at most one and which
    /// buckets carry the extra event is decided by that formula and nothing else — n = 7 gives
    /// 2, 1, 2, 1, 1 and n = 11 gives 3, 2, 2, 2, 2. Fixed here because the registration says
    /// "quintile" and nothing more.
    /// </summary>
    public static int QuintileIndex(int rank, int count) =>
        count <= 0 ? 0 : (int)Math.Min(4L, (long)rank * 5 / count);

    /// <summary>
    /// A quintile split of the sample on <paramref name="value"/>. Events with no value never join a
    /// quintile: they form a separate <c>unknown</c> bucket, which is printed even when it is empty
    /// so that "no unknowns" is a stated zero rather than a missing line. Ranking is within the
    /// sample being reported, as the registration says, with ties broken by event id so that two
    /// equal values land in a defined order rather than an incidental one.
    /// </summary>
    public static SplitDefinition Quintiles(
        IReadOnlyList<SampleEvent> sample,
        string key,
        string title,
        string rule,
        Func<SampleEvent, decimal?> value,
        Func<decimal, string> format)
    {
        var known = sample
            .Where(e => value(e) is not null)
            .OrderBy(e => value(e)!.Value)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ToList();

        var members = new List<SampleEvent>[5];
        for (var q = 0; q < 5; q++) members[q] = [];
        for (var rank = 0; rank < known.Count; rank++) members[QuintileIndex(rank, known.Count)].Add(known[rank]);

        var buckets = new List<SplitBucket>(6);
        for (var q = 0; q < 5; q++)
        {
            var bucket = members[q];
            var range = bucket.Count == 0
                ? null
                : $"{format(value(bucket[0])!.Value)} .. {format(value(bucket[^1])!.Value)}";
            buckets.Add(new SplitBucket($"Q{q + 1}", range, bucket));
        }

        buckets.Add(new SplitBucket("unknown", null, [.. sample.Where(e => value(e) is null)]));
        return new SplitDefinition(key, title, rule, buckets);
    }

    /// <summary>DTE &lt;= threshold vs &gt; threshold, with an <c>unknown</c> bucket for a missing DTE.</summary>
    public static SplitDefinition DteSplit(IReadOnlyList<SampleEvent> sample, int thresholdDays) =>
        new("dte", $"DTE <= {thresholdDays} vs > {thresholdDays}",
            $"Calendar days from entry to the front expiration, from option_measures.dte_calendar_days; a missing DTE is 'unknown'.",
            [
                new SplitBucket($"DTE <= {thresholdDays}", null, [.. sample.Where(e => e.DteCalendarDays is { } d && d <= thresholdDays)]),
                new SplitBucket($"DTE > {thresholdDays}", null, [.. sample.Where(e => e.DteCalendarDays is { } d && d > thresholdDays)]),
                new SplitBucket("unknown", null, [.. sample.Where(e => e.DteCalendarDays is null)])
            ]);

    /// <summary>BMO vs AMC, with every other timing label reported together as <c>other</c>.</summary>
    public static SplitDefinition TimingSplit(IReadOnlyList<SampleEvent> sample) =>
        new("timing", "BMO vs AMC",
            "event_timing.timing_class; any label that is neither BMO nor AMC is reported as 'other' rather than dropped.",
            [
                new SplitBucket("BMO", null, [.. sample.Where(e => IsTiming(e, "BMO"))]),
                new SplitBucket("AMC", null, [.. sample.Where(e => IsTiming(e, "AMC"))]),
                new SplitBucket("other", null, [.. sample.Where(e => !IsTiming(e, "BMO") && !IsTiming(e, "AMC"))])
            ]);

    private static bool IsTiming(SampleEvent e, string label) =>
        string.Equals(e.TimingClass, label, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The straddle hold-through return at mids, over one sample. Events whose exit mid is missing
    /// are COUNTED, not dropped: the diagnostic reports how many it could not measure beside what it
    /// measured.
    /// </summary>
    public static StraddleDiagnostic Straddle(IReadOnlyList<SampleEvent> sample)
    {
        var returns = sample.Where(e => e.StraddleReturn is not null).Select(e => e.StraddleReturn!.Value).ToArray();
        if (returns.Length == 0) return new StraddleDiagnostic(sample.Count, 0, sample.Count, null, null, null, null, null, null);

        Array.Sort(returns);
        var sum = 0m;
        foreach (var value in returns) sum += value;
        return new StraddleDiagnostic(
            sample.Count,
            returns.Length,
            sample.Count - returns.Length,
            MedianOfSorted(returns, returns.Length),
            sum / returns.Length,
            Quantile(returns, 0.10m),
            Quantile(returns, 0.25m),
            Quantile(returns, 0.75m),
            Quantile(returns, 0.90m));
    }
}
