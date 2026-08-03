using TradingStuff.ResearchService.OptionChains;

namespace TradingStuff.Tests.OptionChains;

/// <summary>
/// Pins the fetch-window bound that keeps one expiration's drain to a handful of vendor calls.
/// Measured live 2026-08-02: a no-data chunk costs the Terminal 14–47 seconds, so fetching a
/// 2012 expiration across an unbounded 2012→2026 job range (~191 chunks, all but ~6 empty) took
/// ~75 minutes for 110 quote rows and projected the full drain into months.
/// </summary>
public sealed class ExpirationFetchWindowTests
{
    private static readonly DateOnly JobFrom = new(2012, 1, 1);
    private static readonly DateOnly JobTo = new(2026, 8, 1);

    [Fact]
    public void A_mid_range_expiration_gets_lookback_days_before_and_grace_days_after()
    {
        var window = ExpirationFetchWindow.For(JobFrom, JobTo, new DateOnly(2018, 6, 15));

        Assert.NotNull(window);
        Assert.Equal(new DateOnly(2018, 6, 15).AddDays(-ExpirationFetchWindow.LookbackDays), window.Value.Start);
        Assert.Equal(new DateOnly(2018, 6, 22), window.Value.End);
    }

    [Fact]
    public void An_expiration_near_the_job_start_is_clamped_to_the_job_range()
    {
        var window = ExpirationFetchWindow.For(JobFrom, JobTo, new DateOnly(2012, 2, 17));

        Assert.NotNull(window);
        Assert.Equal(JobFrom, window.Value.Start);
    }

    [Fact]
    public void An_expiration_at_the_job_end_is_clamped_so_the_grace_week_never_leaves_the_range()
    {
        var window = ExpirationFetchWindow.For(JobFrom, JobTo, new DateOnly(2026, 7, 31));

        Assert.NotNull(window);
        Assert.Equal(JobTo, window.Value.End);
    }

    [Fact]
    public void The_window_never_reaches_more_than_the_grace_week_past_the_expiration()
    {
        var window = ExpirationFetchWindow.For(JobFrom, JobTo, new DateOnly(2012, 6, 1));

        Assert.NotNull(window);
        Assert.Equal(new DateOnly(2012, 6, 8), window.Value.End);
    }

    [Fact]
    public void An_expiration_with_no_overlap_degrades_to_null_rather_than_an_inverted_range()
    {
        // Unreachable through the planner (it filters expirations to the job range) — this pins
        // the guard for a hand-edited request row.
        var window = ExpirationFetchWindow.For(JobFrom, JobTo, new DateOnly(2027, 6, 18));

        Assert.Null(window);
    }
}
