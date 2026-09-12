using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class AsOfTests
{
    private const long Cik = 320193;

    private static SharesFactRow Fact(
        string periodEnd, decimal value, string filed, string accession = "0000320193-24-000001") =>
        new(Cik, DateOnly.Parse(periodEnd), value, DateOnly.Parse(filed), "10-Q", accession, null);

    [Fact]
    public void A_fact_filed_after_the_entry_date_is_never_returned()
    {
        var entry = new DateOnly(2024, 2, 1);
        var facts = new[]
        {
            Fact(periodEnd: "2023-09-30", value: 15_552_752_000m, filed: "2023-11-03"),

            // Filed the day after entry. Its period ends before the entry date, which is exactly the
            // trap: selecting on period end would take it, and nothing downstream could see that the
            // number was not public yet.
            Fact(periodEnd: "2023-12-30", value: 15_441_881_000m, filed: "2024-02-02")
        };

        var pick = AsOf.SharesOutstanding(facts, entry);

        Assert.NotNull(pick);
        Assert.Equal(15_552_752_000m, pick.Value);
        Assert.Equal(new DateOnly(2023, 11, 3), pick.Filed);
    }

    [Fact]
    public void A_fact_filed_on_the_entry_date_is_in_force_at_that_close()
    {
        var facts = new[]
        {
            Fact(periodEnd: "2023-09-30", value: 15_552_752_000m, filed: "2023-11-03"),
            Fact(periodEnd: "2023-12-30", value: 15_441_881_000m, filed: "2024-02-01")
        };

        var pick = AsOf.SharesOutstanding(facts, new DateOnly(2024, 2, 1));

        Assert.NotNull(pick);
        Assert.Equal(15_441_881_000m, pick.Value);
    }

    [Fact]
    public void Nothing_filed_yet_is_null_rather_than_the_earliest_fact()
    {
        Assert.Null(AsOf.SharesOutstanding(
            [Fact(periodEnd: "2023-12-30", value: 1m, filed: "2024-02-02")], new DateOnly(2024, 2, 1)));

        Assert.Null(AsOf.SharesOutstanding([], new DateOnly(2024, 2, 1)));
    }

    [Fact]
    public void The_pick_does_not_depend_on_the_order_the_facts_arrive_in()
    {
        var facts = new List<SharesFactRow>
        {
            Fact(periodEnd: "2022-12-31", value: 1m, filed: "2023-02-03"),
            Fact(periodEnd: "2023-12-30", value: 3m, filed: "2024-02-01"),
            Fact(periodEnd: "2023-09-30", value: 2m, filed: "2023-11-03"),
            Fact(periodEnd: "2024-03-30", value: 4m, filed: "2024-05-03")
        };

        var entry = new DateOnly(2024, 2, 1);
        var expected = AsOf.SharesOutstanding(facts, entry);

        Assert.NotNull(expected);
        Assert.Equal(3m, expected.Value);

        foreach (var permutation in Permutations(facts))
        {
            Assert.Equal(expected, AsOf.SharesOutstanding(permutation, entry));
        }
    }

    [Fact]
    public void Facts_filed_the_same_day_break_on_period_end_then_value_then_accession()
    {
        var entry = new DateOnly(2024, 2, 1);

        var byPeriodEnd = new[]
        {
            Fact(periodEnd: "2023-12-30", value: 10m, filed: "2024-01-31"),
            Fact(periodEnd: "2023-09-30", value: 99m, filed: "2024-01-31")
        };

        Assert.Equal(10m, AsOf.SharesOutstanding(byPeriodEnd, entry)!.Value);

        var byValue = new[]
        {
            Fact(periodEnd: "2023-12-30", value: 10m, filed: "2024-01-31", accession: "a"),
            Fact(periodEnd: "2023-12-30", value: 11m, filed: "2024-01-31", accession: "b")
        };

        Assert.Equal(11m, AsOf.SharesOutstanding(byValue, entry)!.Value);

        var byAccession = new[]
        {
            Fact(periodEnd: "2023-12-30", value: 10m, filed: "2024-01-31", accession: "a"),
            Fact(periodEnd: "2023-12-30", value: 10m, filed: "2024-01-31", accession: "b")
        };

        Assert.Equal("b", AsOf.SharesOutstanding(byAccession, entry)!.AccessionNumber);
    }

    private static IEnumerable<List<SharesFactRow>> Permutations(List<SharesFactRow> facts)
    {
        if (facts.Count <= 1)
        {
            yield return facts;
            yield break;
        }

        for (var i = 0; i < facts.Count; i++)
        {
            var rest = facts.Where((_, index) => index != i).ToList();

            foreach (var tail in Permutations(rest))
            {
                yield return [facts[i], .. tail];
            }
        }
    }
}
