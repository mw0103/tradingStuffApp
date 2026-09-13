using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The inference the verdict rests on. Two of these are the controls that matter: with one event per
/// week the clustered interval must agree with an ordinary event bootstrap, and when same-week events
/// share a common shock it must be MATERIALLY WIDER than one. A bootstrap that resampled events
/// would pass the first and fail the second, which is exactly the error the pre-registration's
/// "clustered by earnings week" exists to prevent.
///
/// The comparison bootstrap below is written independently, with <see cref="Random"/> rather than the
/// study's generator, so the two implementations cannot agree by sharing code.
/// </summary>
public sealed class ClusteredBootstrapTests
{
    private const decimal Level = 0.95m;

    /// <summary>
    /// The interval the v2 verdict reads. Two properties pin it: a degenerate sample where every
    /// replicate mean is forced to one value, so both endpoints must be that value and nothing else;
    /// and a sample where the mean and the median are far apart, so an interval accidentally built
    /// from the median replicates could not pass.
    /// </summary>
    [Fact]
    public void The_mean_interval_is_built_from_the_replicate_means_and_not_from_any_other_statistic()
    {
        // Every event identical: every replicate mean is 0.8 whatever clusters are drawn.
        var flat = Enumerable.Range(0, 30)
            .Select(i => new SampleEvent($"e{i}", $"w{i % 6}", 0.8m, true, false, null, null, null, "AMC", null))
            .ToList();

        var degenerate = ClusteredBootstrap.Run(flat, 500, Level, 4242UL)!;
        Assert.Equal(0.8m, degenerate.Mean.Lower);
        Assert.Equal(0.8m, degenerate.Mean.Upper);
        Assert.Equal(6, degenerate.Clusters);
        Assert.Equal(30, degenerate.SampleSize);

        // Four clusters of five. Three sit at 0.5 and one at 5.5, so the sample mean is 1.75 and the
        // median is 0.5: an interval taken off the median replicates would sit nowhere near the mean.
        var skewed = new List<SampleEvent>();
        for (var cluster = 0; cluster < 4; cluster++)
        {
            for (var i = 0; i < 5; i++)
            {
                var ratio = cluster == 3 ? 5.5m : 0.5m;
                skewed.Add(new SampleEvent($"c{cluster}e{i}", $"w{cluster}", ratio, ratio < 1m, false, null, null, null, "AMC", null));
            }
        }

        var result = ClusteredBootstrap.Run(skewed, 4000, Level, 777UL)!;
        Assert.Equal(1.75m, Statistics.Mean([.. skewed.Select(e => e.Ratio)]));
        Assert.Equal(0.5m, Statistics.Median([.. skewed.Select(e => e.Ratio)]));

        // A replicate draws four clusters with replacement, so its mean is 0.5 + 1.25k for k high
        // clusters drawn: 0.5, 1.75, 3, 4.25 or 5.5. Both endpoints must be one of those five values,
        // and the interval must cover the sample mean.
        decimal[] possible = [0.5m, 1.75m, 3m, 4.25m, 5.5m];
        Assert.Contains(result.Mean.Lower, possible);
        Assert.Contains(result.Mean.Upper, possible);
        Assert.True(result.Mean.Lower <= 1.75m && result.Mean.Upper >= 1.75m);

        // The median interval is a different interval on the same replicates, and stays on the
        // median's own scale rather than drifting toward the mean.
        Assert.Equal(0.5m, result.Median.Lower);
        Assert.True(result.Median.Upper <= 5.5m);
    }

    [Fact]
    public void One_event_per_cluster_matches_an_ordinary_event_bootstrap()
    {
        // 200 events, each its own week: clustering has nothing to bind, so the clustered interval is
        // an event bootstrap and the two must land on top of each other up to Monte Carlo error.
        var sample = Enumerable.Range(0, 200)
            .Select(i => new SampleEvent($"e{i}", $"w{i}", 0.4m + (i * 0.006m), 0.4m + (i * 0.006m) < 1m, false, null, null, null, "AMC", null))
            .ToList();

        var clustered = ClusteredBootstrap.Run(sample, 4000, Level, 12345UL)!;
        var (lower, upper) = EventBootstrapProportion(sample, 4000, seed: 99);

        Assert.Equal((double)lower, (double)clustered.ProportionLess.Lower, 2);
        Assert.Equal((double)upper, (double)clustered.ProportionLess.Upper, 2);
        Assert.Equal(200, clustered.Clusters);
    }

    [Fact]
    public void Common_week_shock_makes_the_clustered_interval_materially_wider()
    {
        // 40 weeks x 10 events. Every event in a week moves the same way: in half the weeks realized
        // came in under implied, in the other half over. Resampling EVENTS treats 400 independent
        // draws and reports an interval about sqrt(10) too narrow; resampling WEEKS keeps the shock.
        var sample = new List<SampleEvent>();
        for (var week = 0; week < 40; week++)
        {
            var low = week % 2 == 0;
            for (var i = 0; i < 10; i++)
            {
                var ratio = (low ? 0.5m : 1.5m) + (i * 0.01m);
                sample.Add(new SampleEvent($"w{week}e{i}", $"2024-W{week:00}", ratio, low, false, null, null, null, "AMC", null));
            }
        }

        var clustered = ClusteredBootstrap.Run(sample, 4000, Level, 777UL)!;
        var (lower, upper) = EventBootstrapProportion(sample, 4000, seed: 4242);

        var clusteredWidth = clustered.ProportionLess.Upper - clustered.ProportionLess.Lower;
        var eventWidth = upper - lower;

        Assert.Equal(40, clustered.Clusters);
        Assert.Equal(400, clustered.SampleSize);
        Assert.True(clusteredWidth > 2m * eventWidth,
            $"the clustered interval must be materially wider than the event bootstrap: clustered {clusteredWidth}, event {eventWidth}");
    }

    [Fact]
    public void The_same_seed_reproduces_the_interval_exactly_and_a_different_seed_does_not()
    {
        // 60 singleton clusters with 60 distinct ratios, so the replicate distribution is fine-grained
        // enough that two seeds landing on identical endpoints would mean identical draws.
        var sample = Enumerable.Range(0, 60)
            .Select(i => new SampleEvent($"e{i}", $"w{i}", 0.10m + (i * 0.037m), 0.10m + (i * 0.037m) < 1m, false, null, null, null, "AMC", null))
            .ToList();

        var first = ClusteredBootstrap.Run(sample, 2000, Level, 20260912UL)!;
        var again = ClusteredBootstrap.Run(sample, 2000, Level, 20260912UL)!;
        var other = ClusteredBootstrap.Run(sample, 2000, Level, 20260913UL)!;

        Assert.Equal(first.ProportionLess, again.ProportionLess);
        Assert.Equal(first.Median, again.Median);
        Assert.True(first.Median != other.Median || first.ProportionLess != other.ProportionLess,
            "a different seed must draw a different set of clusters");
    }

    [Fact]
    public void A_seed_is_derived_from_the_registered_seed_and_the_analysis_name_without_string_hashing()
    {
        // Stable across processes: string.GetHashCode is randomised per process and would make the
        // memo irreproducible from one run to the next.
        Assert.Equal(ClusteredBootstrap.SeedFor(20260912, "primary:overall"), ClusteredBootstrap.SeedFor(20260912, "primary:overall"));
        Assert.NotEqual(ClusteredBootstrap.SeedFor(20260912, "primary:overall"), ClusteredBootstrap.SeedFor(20260912, "secondary:overall"));
        Assert.NotEqual(ClusteredBootstrap.SeedFor(20260912, "primary:overall"), ClusteredBootstrap.SeedFor(20260913, "primary:overall"));
    }

    [Fact]
    public void A_single_cluster_gives_a_degenerate_interval_rather_than_a_crash()
    {
        var sample = Enumerable.Range(0, 5)
            .Select(i => new SampleEvent($"e{i}", "one-week", 0.5m + (i * 0.1m), true, false, null, null, null, "AMC", null))
            .ToList();

        var result = ClusteredBootstrap.Run(sample, 200, Level, 1UL)!;

        Assert.Equal(1, result.Clusters);
        Assert.Equal(result.Median.Lower, result.Median.Upper);
        Assert.Equal(0.7m, result.Median.Lower);
    }

    [Fact]
    public void An_empty_sample_has_no_interval_at_all()
    {
        Assert.Null(ClusteredBootstrap.Run([], 100, Level, 1UL));
        Assert.Null(ClusteredBootstrap.Run(Sample(), 0, Level, 1UL));
    }

    [Fact]
    public void The_interval_endpoints_are_replicates_the_resampling_actually_produced()
    {
        // Two clusters of one event each, ratios 0.5 and 1.5: a replicate's median is 0.5, 1.0 or 1.5
        // and nothing else, so an interpolating quantile rule would produce a value off that list.
        List<SampleEvent> sample =
        [
            new("a", "w1", 0.5m, true, false, null, null, null, "AMC", null),
            new("b", "w2", 1.5m, false, false, null, null, null, "AMC", null)
        ];

        var result = ClusteredBootstrap.Run(sample, 1000, Level, 5UL)!;

        Assert.Contains(result.Median.Lower, new[] { 0.5m, 1.0m, 1.5m });
        Assert.Contains(result.Median.Upper, new[] { 0.5m, 1.0m, 1.5m });
        Assert.Contains(result.ProportionLess.Lower, new[] { 0m, 0.5m, 1m });
    }

    [Fact]
    public void Bounded_draws_cover_every_index()
    {
        // A rejection-sampled modulo must still reach the last index; a bound-1 bug is invisible in an
        // interval but shifts every resample.
        var random = new SplitMix64(42);
        var seen = new HashSet<int>();
        for (var i = 0; i < 1000; i++) seen.Add(random.NextBelow(7));

        Assert.Equal([0, 1, 2, 3, 4, 5, 6], seen.Order());
        Assert.Equal(0, new SplitMix64(1).NextBelow(1));
    }

    private static List<SampleEvent> Sample() =>
    [
        .. Enumerable.Range(0, 60).Select(i =>
            new SampleEvent($"e{i}", $"2024-W{i % 12:00}", 0.3m + (i * 0.02m), 0.3m + (i * 0.02m) < 1m, false, null, null, null, "AMC", null))
    ];

    /// <summary>
    /// An ordinary (unclustered) percentile bootstrap of P(RF &lt; IM), written here so the comparison
    /// is against a separate implementation rather than the same code with a different argument.
    /// </summary>
    private static (decimal Lower, decimal Upper) EventBootstrapProportion(IReadOnlyList<SampleEvent> sample, int replications, int seed)
    {
        var random = new Random(seed);
        var values = new decimal[replications];
        for (var r = 0; r < replications; r++)
        {
            var less = 0;
            for (var i = 0; i < sample.Count; i++)
            {
                if (sample[random.Next(sample.Count)].RfLessThanIm) less++;
            }

            values[r] = (decimal)less / sample.Count;
        }

        Array.Sort(values);
        return (values[(int)Math.Ceiling(0.025 * replications) - 1], values[(int)Math.Ceiling(0.975 * replications) - 1]);
    }
}
