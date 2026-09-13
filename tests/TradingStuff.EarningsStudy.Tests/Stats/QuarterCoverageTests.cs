using TradingStuff.EarningsStudy;
using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The v2 deadline fallback's coverage rule, on its own. The subset it produces is what every
/// statistic in the memo is restricted to, so the rule is pinned here before the end-to-end
/// behaviour is asserted against the compute verb.
/// </summary>
public sealed class QuarterCoverageTests
{
    private static readonly DateOnly From = C1Registration.WindowFrom;
    private static readonly DateOnly To = C1Registration.WindowTo;

    private static CoverageInput Fetched(DateOnly printDate, string chain = "ok", string closes = "ok") =>
        new(printDate, chain, closes);

    private static DateOnly InQuarter(int year, int quarter) => new(year, ((quarter - 1) * 3) + 2, 15);

    [Fact]
    public void The_registered_window_is_sixteen_quarters_and_they_are_named_as_the_memo_names_them()
    {
        var quarters = QuarterCoverage.QuartersIn(From, To);

        Assert.Equal(16, quarters.Count);
        Assert.Equal("2022Q1", quarters[0]);
        Assert.Equal("2025Q4", quarters[^1]);
        Assert.Equal("2024Q1", QuarterCoverage.QuarterOf(new DateOnly(2024, 3, 31)));
        Assert.Equal("2024Q2", QuarterCoverage.QuarterOf(new DateOnly(2024, 4, 1)));
        Assert.Equal("2025Q4", QuarterCoverage.QuarterOf(new DateOnly(2025, 12, 31)));
    }

    [Fact]
    public void A_quarter_with_no_events_is_covered_vacuously_and_its_zero_is_visible()
    {
        var report = QuarterCoverage.Measure([], From, To);

        Assert.True(report.FullWindow);
        Assert.False(report.Provisional);
        Assert.False(report.NothingDeliverable);
        Assert.Equal(16, report.Subset.Count);
        Assert.All(report.Quarters, q => Assert.Equal(0, q.Events));
        Assert.All(report.Quarters, q => Assert.True(q.Covered));
    }

    [Fact]
    public void A_quarter_is_covered_only_when_every_one_of_its_events_has_both_rows()
    {
        var missingChain = QuarterCoverage.Measure(
            [Fetched(InQuarter(2025, 4)), new CoverageInput(InQuarter(2025, 4), null, "ok")], From, To);
        Assert.False(missingChain.Quarters[^1].Covered);
        Assert.Equal(1, missingChain.Quarters[^1].MissingChainRow);
        Assert.Empty(missingChain.Subset);
        Assert.True(missingChain.NothingDeliverable);

        var missingCloses = QuarterCoverage.Measure(
            [new CoverageInput(InQuarter(2025, 4), "ok", null)], From, To);
        Assert.False(missingCloses.Quarters[^1].Covered);
        Assert.Equal(1, missingCloses.Quarters[^1].MissingClosesRow);
    }

    /// <summary>
    /// A recorded failure is a completed fetch. The quarter stays covered and the error is counted,
    /// so the operator can re-run it without the subset rule having withheld a whole quarter for it.
    /// </summary>
    [Fact]
    public void A_recorded_failure_is_a_completed_fetch_and_is_counted_rather_than_withholding_the_quarter()
    {
        var report = QuarterCoverage.Measure(
            [
                Fetched(InQuarter(2025, 4), chain: "error"),
                Fetched(InQuarter(2025, 4), chain: "no_chain"),
                Fetched(InQuarter(2025, 4), closes: "missing_exit")
            ],
            From, To);

        var q = report.Quarters[^1];
        Assert.True(q.Covered);
        Assert.Equal(3, q.Events);
        Assert.Equal(1, q.ChainFetchErrors);
        Assert.Equal(1, q.ClosesNotOk);
        Assert.True(report.FullWindow);

        // Every status is tallied, including the absent-row key, so none of them can hide.
        Assert.Contains(report.ChainStatuses, t => t is { Key: "error", Count: 1 });
        Assert.Contains(report.ChainStatuses, t => t is { Key: "no_chain", Count: 1 });
        Assert.Contains(report.ClosesStatuses, t => t is { Key: "missing_exit", Count: 1 });
    }

    [Fact]
    public void The_subset_is_the_run_ending_at_the_most_recent_quarter_and_a_middle_gap_truncates_it()
    {
        // 2024Q1 is short one chain row; every other quarter is fine. The run ending at 2025Q4 stops
        // at the gap, so 2022 and 2023 are deliverable-adjacent but NOT deliverable.
        var report = QuarterCoverage.Measure(
            [
                Fetched(InQuarter(2022, 1)),
                Fetched(InQuarter(2023, 3)),
                new CoverageInput(InQuarter(2024, 1), null, "ok"),
                Fetched(InQuarter(2024, 2)),
                Fetched(InQuarter(2025, 4))
            ],
            From, To);

        Assert.False(report.FullWindow);
        Assert.True(report.Provisional);
        Assert.Equal(["2024Q2", "2024Q3", "2024Q4", "2025Q1", "2025Q2", "2025Q3", "2025Q4"], report.Subset);
        Assert.False(report.Includes(InQuarter(2022, 1)));
        Assert.False(report.Includes(InQuarter(2024, 1)));
        Assert.True(report.Includes(InQuarter(2024, 2)));
    }

    [Fact]
    public void A_gap_at_the_most_recent_quarter_leaves_nothing_deliverable_however_complete_the_past_is()
    {
        var report = QuarterCoverage.Measure(
            [
                Fetched(InQuarter(2022, 1)),
                Fetched(InQuarter(2024, 2)),
                new CoverageInput(InQuarter(2025, 4), "ok", null)
            ],
            From, To);

        Assert.True(report.NothingDeliverable);
        Assert.False(report.Provisional);
        Assert.False(report.FullWindow);
        Assert.Empty(report.Subset);
    }

    [Fact]
    public void An_event_with_no_print_date_or_a_date_outside_the_window_is_counted_apart()
    {
        var report = QuarterCoverage.Measure(
            [
                new CoverageInput(null, null, null),
                Fetched(new DateOnly(2021, 6, 1)),
                Fetched(InQuarter(2025, 4))
            ],
            From, To);

        Assert.Equal(1, report.EventsWithNoPrintDate);
        Assert.Equal(1, report.EventsOutsideWindowQuarters);

        // Neither one lands in a quarter, so neither withholds one: the last quarter has its single
        // fetched event and the window is covered.
        Assert.Equal(1, report.Quarters[^1].Events);
        Assert.True(report.FullWindow);
    }
}
