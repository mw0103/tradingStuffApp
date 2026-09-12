using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// Known-answer tests for the registered statistics. Every expected value here is worked out by hand
/// in the test name or the comment, so a change in the implementation has to argue with arithmetic
/// rather than with another copy of itself.
/// </summary>
public sealed class StatisticsTests
{
    private static SampleEvent Event(decimal ratio, string cluster = "w1", string id = "", bool? less = null, bool equal = false) =>
        new(id.Length == 0 ? $"e{ratio}" : id, cluster, ratio, less ?? ratio < 1m, equal, null, null, null, "AMC", null);

    [Fact]
    public void Median_of_odd_n_is_the_middle_value()
    {
        Assert.Equal(3m, Statistics.Median([5m, 1m, 3m, 9m, 2m]));
        Assert.Equal(7m, Statistics.Median([7m]));
    }

    [Fact]
    public void Median_of_even_n_is_the_mean_of_the_two_middle_values()
    {
        // 1, 2, 3, 9 -> (2 + 3) / 2 = 2.5. The lower-middle convention would give 2.
        Assert.Equal(2.5m, Statistics.Median([9m, 1m, 3m, 2m]));

        // A case where the two middle values are far apart, so a mis-picked convention is unmissable.
        Assert.Equal(0.75m, Statistics.Median([0.5m, 1.0m, 0.1m, 4.0m]));
    }

    [Fact]
    public void Median_of_an_empty_sample_is_absent_not_zero()
    {
        Assert.Null(Statistics.Median([]));
        Assert.Null(Statistics.ProportionLess([]));
    }

    [Fact]
    public void Proportion_less_is_strict_and_ties_are_counted_as_not_less()
    {
        // Five events: two strictly less, one tie (RF == IM), two greater. P = 2/5, ties = 1.
        List<SampleEvent> sample =
        [
            Event(0.5m, id: "a", less: true),
            Event(0.8m, id: "b", less: true),
            Event(1.0m, id: "c", less: false, equal: true),
            Event(1.2m, id: "d", less: false),
            Event(2.0m, id: "e", less: false)
        ];

        Assert.Equal(0.4m, Statistics.ProportionLess(sample));
        Assert.Equal(1, Statistics.TieCount(sample));
    }

    [Fact]
    public void Quantile_is_the_nearest_rank_order_statistic_with_no_interpolation()
    {
        decimal[] sorted = [1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m];

        // ceil(0.025 * 10) = 1 -> the 1st value; ceil(0.975 * 10) = 10 -> the 10th.
        Assert.Equal(1m, Statistics.Quantile(sorted, 0.025m));
        Assert.Equal(10m, Statistics.Quantile(sorted, 0.975m));

        // ceil(0.5 * 10) = 5 -> the 5th value. An interpolating rule would return 5.5.
        Assert.Equal(5m, Statistics.Quantile(sorted, 0.5m));

        // ceil(0.25 * 10) = 3, ceil(0.9 * 10) = 9.
        Assert.Equal(3m, Statistics.Quantile(sorted, 0.25m));
        Assert.Equal(9m, Statistics.Quantile(sorted, 0.90m));
    }

    [Fact]
    public void Quantile_clamps_rather_than_walking_off_either_end()
    {
        decimal[] sorted = [1m, 2m, 3m];
        Assert.Equal(1m, Statistics.Quantile(sorted, 0m));
        Assert.Equal(3m, Statistics.Quantile(sorted, 1m));
    }

    [Fact]
    public void The_trimmed_mean_drops_exactly_floor_fraction_times_n_from_each_tail()
    {
        // n = 100 -> floor(0.01 * 100) = 1 per tail. Two distinct small ratios and 98 ratios of 1, so
        // that dropping 0, 1 or 2 per tail each give a DIFFERENT answer and the count is pinned:
        //   k = 0 -> (log 0.0001 + log 0.001) / 100
        //   k = 1 -> (log 0.001) / 98            <- the registered 1%
        //   k = 2 -> 0
        List<SampleEvent> sample = [Event(0.0001m, id: "tiny"), Event(0.001m, id: "small")];
        for (var i = 0; i < 98; i++) sample.Add(Event(1m, id: $"one{i}"));

        var stats = Statistics.Logs(sample, 0.01m);

        Assert.Equal(100, stats.Used);
        Assert.Equal(1, stats.TrimPerTail);
        Assert.Equal(0, stats.ZeroRealizedExcluded);
        Assert.Equal((Math.Log(0.0001) + Math.Log(0.001)) / 100d, stats.MeanLog!.Value, 12);
        Assert.Equal(Math.Log(0.001) / 98d, stats.TrimmedMeanLog!.Value, 12);
    }

    [Fact]
    public void The_trim_count_is_a_floor_so_it_is_zero_below_one_hundred_events()
    {
        Assert.Equal(0, Trim(99));
        Assert.Equal(1, Trim(100));
        Assert.Equal(1, Trim(199));
        Assert.Equal(2, Trim(200));

        static int Trim(int n) =>
            Statistics.Logs([.. Enumerable.Range(0, n).Select(i => Event(1m + (i * 0.001m), id: $"e{i}"))], 0.01m).TrimPerTail;
    }

    [Fact]
    public void A_trimmed_mean_that_would_trim_everything_is_reported_as_not_computable()
    {
        var stats = Statistics.Logs([Event(1.5m, id: "a"), Event(2.5m, id: "b")], 0.5m);

        Assert.Equal(1, stats.TrimPerTail);
        Assert.Null(stats.TrimmedMeanLog);
        Assert.NotNull(stats.MeanLog);
        Assert.Contains("would leave nothing", stats.Note);
    }

    [Fact]
    public void An_event_with_a_zero_realized_move_is_excluded_from_the_log_statistics_and_counted()
    {
        // Three events, one with RF = 0 (ratio 0). log(0) does not exist, so n for the logs is 2 and
        // the exclusion is reported rather than silently changing the denominator.
        var stats = Statistics.Logs([Event(0m, id: "zero"), Event(1m, id: "one"), Event(1m, id: "two")], 0.01m);

        Assert.Equal(3, stats.Considered);
        Assert.Equal(1, stats.ZeroRealizedExcluded);
        Assert.Equal(2, stats.Used);
        Assert.Equal(0d, stats.MeanLog!.Value, 12);
    }

    [Fact]
    public void Mean_log_is_the_mean_of_the_logarithms_not_the_log_of_the_mean()
    {
        // ratios 0.5 and 2 -> mean log = (log 0.5 + log 2) / 2 = 0, while log(mean) = log(1.25) > 0.
        var stats = Statistics.Logs([Event(0.5m, id: "a"), Event(2m, id: "b")], 0.01m);
        Assert.Equal(0d, stats.MeanLog!.Value, 12);
    }

    [Fact]
    public void Quintiles_with_n_not_divisible_by_five_put_the_extra_events_in_the_low_buckets()
    {
        // n = 7 -> floor(rank * 5 / 7) gives sizes 2, 1, 2, 1, 1.
        var sample = Enumerable.Range(0, 7).Select(i => Sized(i, i + 1)).ToList();
        var split = Statistics.Quintiles(sample, "k", "t", "r", e => e.MarketCap, v => v.ToString("F0"));

        Assert.Equal(["Q1", "Q2", "Q3", "Q4", "Q5", "unknown"], split.Buckets.Select(b => b.Label));
        Assert.Equal([2, 1, 2, 1, 1, 0], split.Buckets.Select(b => b.Events.Count));

        // Ascending: the smallest caps are in Q1, the largest in Q5.
        Assert.Equal(["e0", "e1"], split.Buckets[0].Events.Select(e => e.EventId));
        Assert.Equal(["e6"], split.Buckets[4].Events.Select(e => e.EventId));
        Assert.Equal("1 .. 2", split.Buckets[0].Range);
    }

    [Fact]
    public void Quintiles_of_ten_events_are_two_apiece()
    {
        var split = Statistics.Quintiles([.. Enumerable.Range(0, 10).Select(i => Sized(i, i + 1))], "k", "t", "r", e => e.MarketCap, v => v.ToString("F0"));
        Assert.Equal([2, 2, 2, 2, 2, 0], split.Buckets.Select(b => b.Events.Count));
    }

    [Fact]
    public void Events_without_a_value_go_to_the_unknown_bucket_and_never_into_a_quintile()
    {
        List<SampleEvent> sample = [Sized(0, 1), Sized(1, 2), Sized(2, null), Sized(3, null)];
        var split = Statistics.Quintiles(sample, "k", "t", "r", e => e.MarketCap, v => v.ToString("F0"));

        Assert.Equal(2, split.Buckets[^1].Events.Count);
        Assert.Equal(["e2", "e3"], split.Buckets[^1].Events.Select(e => e.EventId));
        Assert.Equal(2, split.Buckets.Take(5).Sum(b => b.Events.Count));
        Assert.Null(split.Buckets[^1].Range);
    }

    [Fact]
    public void An_empty_unknown_bucket_is_still_printed_so_that_none_is_a_stated_zero()
    {
        var split = Statistics.Quintiles([Sized(0, 1)], "k", "t", "r", e => e.MarketCap, v => v.ToString("F0"));
        Assert.Equal("unknown", split.Buckets[^1].Label);
        Assert.Empty(split.Buckets[^1].Events);
    }

    [Fact]
    public void The_dte_split_is_inclusive_at_the_threshold_and_keeps_a_bucket_for_a_missing_dte()
    {
        List<SampleEvent> sample =
        [
            Dte("short", 7),
            Dte("long", 8),
            Dte("zero", 0),
            Dte("none", null)
        ];

        var split = Statistics.DteSplit(sample, 7);
        Assert.Equal(["short", "zero"], split.Buckets[0].Events.Select(e => e.EventId).Order());
        Assert.Equal(["long"], split.Buckets[1].Events.Select(e => e.EventId));
        Assert.Equal(["none"], split.Buckets[2].Events.Select(e => e.EventId));
    }

    [Fact]
    public void The_timing_split_reports_every_other_label_as_other_rather_than_dropping_it()
    {
        List<SampleEvent> sample = [Timed("a", "BMO"), Timed("b", "amc"), Timed("c", "INTRADAY"), Timed("d", "")];

        var split = Statistics.TimingSplit(sample);
        Assert.Equal(["a"], split.Buckets[0].Events.Select(e => e.EventId));
        Assert.Equal(["b"], split.Buckets[1].Events.Select(e => e.EventId));
        Assert.Equal(["c", "d"], split.Buckets[2].Events.Select(e => e.EventId));
        Assert.Equal(sample.Count, split.Buckets.Sum(b => b.Events.Count));
    }

    [Fact]
    public void The_straddle_diagnostic_counts_the_events_whose_exit_mid_is_missing()
    {
        List<SampleEvent> sample =
        [
            new("a", "w", 1m, false, false, null, null, null, "AMC", -0.4m),
            new("b", "w", 1m, false, false, null, null, null, "AMC", -0.2m),
            new("c", "w", 1m, false, false, null, null, null, "AMC", null)
        ];

        var straddle = Statistics.Straddle(sample);
        Assert.Equal(3, straddle.Members);
        Assert.Equal(2, straddle.WithReturn);
        Assert.Equal(1, straddle.NullExit);
        Assert.Equal(-0.3m, straddle.Median);
        Assert.Equal(-0.3m, straddle.Mean);
    }

    private static SampleEvent Sized(int index, decimal? cap) =>
        new($"e{index}", "w", 1m, false, false, cap, null, null, "AMC", null);

    private static SampleEvent Dte(string id, int? dte) =>
        new(id, "w", 1m, false, false, null, null, dte, "AMC", null);

    private static SampleEvent Timed(string id, string timing) =>
        new(id, "w", 1m, false, false, null, null, null, timing, null);
}
