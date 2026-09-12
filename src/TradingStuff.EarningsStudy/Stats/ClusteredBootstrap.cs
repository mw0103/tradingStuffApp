namespace TradingStuff.EarningsStudy.Stats;

/// <summary>One percentile interval, with the shape of the resampling that produced it.</summary>
public sealed record BootstrapInterval(
    decimal Lower,
    decimal Upper,
    decimal Level,
    int Replications,
    int Clusters,
    int SampleSize);

/// <summary>Both registered statistics, resampled together so each replicate is one draw of clusters.</summary>
public sealed record BootstrapResult(
    BootstrapInterval Median,
    BootstrapInterval ProportionLess,
    int Clusters,
    int Replications,
    int SampleSize);

/// <summary>
/// The pre-registration's inference: "bootstrap clustered by earnings week (same-week events co-move
/// through the market factor), 10,000 reps, 95% CI".
///
/// The unit of resampling is the CLUSTER, not the event. One replicate draws as many clusters as the
/// sample has, with replacement, pools all the events of the drawn clusters, and recomputes both
/// statistics on that pool — so a week is either wholly in or wholly out of a replicate and the
/// within-week common shock is preserved. Resampling events instead would treat same-week events as
/// independent and understate the interval, which is exactly the error the registration names; the
/// test <c>Common_week_shock_makes_the_clustered_interval_materially_wider</c> is the control for it.
///
/// The interval is the percentile interval: endpoints at (1 - level)/2 and 1 - (1 - level)/2 of the
/// replicate distribution, taken as order statistics by <see cref="Statistics.Quantile"/>.
/// </summary>
public static class ClusteredBootstrap
{
    private sealed record Cluster(string Key, decimal[] Ratios, int LessCount);

    /// <summary>
    /// Runs the clustered bootstrap. Null when there is nothing to resample (an empty sample, or a
    /// non-positive replication count) — an absent interval is reported as absent, never as [0, 0].
    /// </summary>
    public static BootstrapResult? Run(IReadOnlyList<SampleEvent> sample, int replications, decimal level, ulong seed)
    {
        if (sample.Count == 0 || replications <= 0) return null;

        // Ordered by key so the draw sequence is a function of the data, not of hash iteration order.
        var clusters = sample
            .GroupBy(e => e.ClusterKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Cluster(g.Key, [.. g.Select(e => e.Ratio)], g.Count(e => e.RfLessThanIm)))
            .ToArray();

        // A replicate draws `clusters.Length` clusters, so the pooled sample cannot exceed that many
        // copies of the largest cluster. Sized once rather than grown per replicate.
        var capacity = (long)clusters.Length * clusters.Max(c => c.Ratios.Length);
        if (capacity > int.MaxValue) throw new InvalidOperationException("the sample is too large to bootstrap in one buffer");
        var buffer = new decimal[(int)capacity];
        var random = new SplitMix64(seed);
        var medians = new decimal[replications];
        var proportions = new decimal[replications];

        for (var r = 0; r < replications; r++)
        {
            var length = 0;
            var less = 0;
            for (var c = 0; c < clusters.Length; c++)
            {
                var drawn = clusters[random.NextBelow(clusters.Length)];
                Array.Copy(drawn.Ratios, 0, buffer, length, drawn.Ratios.Length);
                length += drawn.Ratios.Length;
                less += drawn.LessCount;
            }

            Array.Sort(buffer, 0, length);
            medians[r] = Statistics.MedianOfSorted(buffer, length);
            proportions[r] = (decimal)less / length;
        }

        Array.Sort(medians);
        Array.Sort(proportions);
        var tail = (1m - level) / 2m;
        return new BootstrapResult(
            Interval(medians, tail, level, clusters.Length, sample.Count),
            Interval(proportions, tail, level, clusters.Length, sample.Count),
            clusters.Length,
            replications,
            sample.Count);
    }

    private static BootstrapInterval Interval(decimal[] sortedReplicates, decimal tail, decimal level, int clusters, int sampleSize) =>
        new(Statistics.Quantile(sortedReplicates, tail),
            Statistics.Quantile(sortedReplicates, 1m - tail),
            level,
            sortedReplicates.Length,
            clusters,
            sampleSize);

    /// <summary>
    /// The seed for one named analysis: the registered seed mixed with an FNV-1a hash of the name.
    /// Every split bucket therefore has its own reproducible draw sequence, and adding an analysis
    /// does not move the numbers of the ones already in the memo. <see cref="string.GetHashCode()"/>
    /// is randomised per process and must never be used for this.
    /// </summary>
    public static ulong SeedFor(int registeredSeed, string analysisName)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in analysisName)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash ^ unchecked((ulong)registeredSeed);
    }
}

/// <summary>
/// SplitMix64: a deterministic generator owned by this repository so that the memo reproduces byte
/// for byte. <see cref="Random"/>'s sequence for a given seed is explicitly not guaranteed stable
/// across .NET versions, and "the number is the number" has to survive a runtime upgrade.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong state = seed;

    /// <summary>The next 64 bits.</summary>
    public ulong Next()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// Uniform in [0, <paramref name="exclusiveUpper"/>). Values in the incomplete final block of
    /// 2^64 are rejected and redrawn, so the modulo carries no bias toward the low indices — with
    /// 10,000 replications over a few hundred clusters a biased draw would be invisible and wrong.
    /// </summary>
    public int NextBelow(int exclusiveUpper)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveUpper, 1);
        var bound = (ulong)exclusiveUpper;
        var threshold = unchecked(0UL - bound) % bound; // 2^64 mod bound
        ulong draw;
        do
        {
            draw = Next();
        }
        while (draw < threshold);
        return (int)(draw % bound);
    }
}
