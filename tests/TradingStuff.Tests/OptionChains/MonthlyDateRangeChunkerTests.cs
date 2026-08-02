using TradingStuff.ResearchService.OptionChains;

namespace TradingStuff.Tests.OptionChains;

/// <summary>
/// Pins the vendor constraint discovered live 2026-08-02: <c>/v3/option/history/quote</c> rejects a
/// <c>[start_date, end_date]</c> span wider than about a month with HTTP 400 "Bulk history requests
/// are limited to no more than 1 month." A 2.5-month ingestion job failed all 24 of its request rows
/// on first drain before this class existed.
/// </summary>
public sealed class MonthlyDateRangeChunkerTests
{
    [Fact]
    public void A_range_no_wider_than_the_limit_is_a_single_chunk()
    {
        var chunks = MonthlyDateRangeChunker.Split(new DateTime(2024, 1, 1), new DateTime(2024, 1, 20));

        var chunk = Assert.Single(chunks);
        Assert.Equal(new DateTime(2024, 1, 1), chunk.Start);
        Assert.Equal(new DateTime(2024, 1, 20), chunk.End);
    }

    [Fact]
    public void A_single_day_range_is_a_single_chunk()
    {
        var chunks = MonthlyDateRangeChunker.Split(new DateTime(2024, 1, 1), new DateTime(2024, 1, 1));

        var chunk = Assert.Single(chunks);
        Assert.Equal(chunk.Start, chunk.End);
    }

    [Fact]
    public void A_two_and_a_half_month_range_is_split_into_multiple_chunks_none_wider_than_the_limit()
    {
        var chunks = MonthlyDateRangeChunker.Split(new DateTime(2016, 5, 1), new DateTime(2016, 7, 15));

        Assert.True(chunks.Count > 1, "expected more than one chunk for a 2.5-month range");

        foreach (var (start, end) in chunks)
        {
            var span = (end - start).Days + 1;
            Assert.True(span <= MonthlyDateRangeChunker.MaxDaysPerChunk,
                $"chunk {start:yyyy-MM-dd}..{end:yyyy-MM-dd} spans {span} days, over the {MonthlyDateRangeChunker.MaxDaysPerChunk}-day limit");
        }
    }

    [Fact]
    public void Chunks_are_contiguous_and_cover_the_whole_range_with_no_gaps_or_overlaps()
    {
        var from = new DateTime(2016, 5, 1);
        var to = new DateTime(2016, 7, 15);
        var chunks = MonthlyDateRangeChunker.Split(from, to);

        Assert.Equal(from, chunks[0].Start);
        Assert.Equal(to, chunks[^1].End);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].End.AddDays(1), chunks[i].Start);
        }
    }

    [Fact]
    public void An_inverted_range_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            MonthlyDateRangeChunker.Split(new DateTime(2024, 1, 20), new DateTime(2024, 1, 1)));
    }
}
