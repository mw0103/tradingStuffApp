using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Stats;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The verb against the hand-written tables in <c>Fixtures/Compute</c> — the same six tables the CLI
/// was run on. Every number asserted here is worked out by hand from those files, so the fixture is
/// an independent statement of what the code should produce rather than a recording of what it did.
///
/// The sorted primary ratios are:
/// 0, 0.25, 0.25, 1/3, 0.4, 0.49, 0.5, 0.5, 0.5, 0.5, 0.5, 0.6, 0.7, 0.75, 0.8, 0.8, 0.8, 5/6, 1, 1,
/// 1.2, 1.5, 1.5, 2 — 24 events, so the median is (0.6 + 0.7) / 2 = 0.65 and P(RF &lt; IM) = 18/24.
/// </summary>
public sealed class ComputeFixtureTests
{
    [Fact]
    public async Task The_fixture_tables_produce_the_hand_computed_statistics()
    {
        using var study = Fixture();

        Assert.Equal(0, await study.RunAsync("--reps", "300"));

        var memo = study.Memo;

        // 32 event rows, one not kept after dedup -> 31 rows in the event table, 30 entering compute.
        Assert.Equal(31, study.EventTable.Count);
        Assert.Contains("Events kept after dedup: 31. Entering `compute`: 30.", memo);

        // Gate by gate: closes (e21), price QA (e22), price (e23), chain (e24), quotable (e25), tradable (e26).
        Assert.Equal(Gates.ClosesPresent, study.Row("e21").StopGate);
        Assert.Equal(Gates.PriceQa, study.Row("e22").StopGate);
        Assert.Equal(Gates.MinimumPrice, study.Row("e23").StopGate);
        Assert.Equal(Gates.ChainFound, study.Row("e24").StopGate);
        Assert.Equal(Gates.Quotable, study.Row("e25").StopGate);
        Assert.Equal(Gates.Tradable, study.Row("e26").StopGate);
        Assert.Equal(Gates.TimingClassified, study.Row("e27").StopGate);

        var counts = study.WrittenGateCounts.Where(c => c.Step == ComputeStep.StepName).ToList();
        Assert.Equal([30, 29, 28, 27, 26, 25], counts.Select(c => c.Considered));
        Assert.Equal([1, 1, 1, 1, 1, 1], counts.Select(c => c.Removed));
        Assert.Equal(24, counts[^1].Remaining);

        Assert.Contains("| events in sample | 24 | |", memo);
        Assert.Contains("| median(RF/IM) | 0.650000 |", memo);
        Assert.Contains("| P(RF < IM) | 0.750000 |", memo);
        Assert.Contains("| ties (RF = IM, counted as not less) | 2 |", memo);
        Assert.Contains("| earnings-week clusters | 9 |", memo);

        // The secondary sample adds the wide-spread event (ratio 2.0): 25 events, median 0.7.
        Assert.Contains("| events in sample | 25 | |", memo);
        Assert.Contains("| median(RF/IM) | 0.700000 |", memo);
        Assert.Contains("| P(RF < IM) | 0.720000 |", memo);

        // (1 quarantined by timing + 1 by the price QA) / 31 considered at gate 07.
        Assert.Contains("Quarantine rate = (1 + 1) / 31 = **0.064516**", memo);

        // One event's realized move is exactly zero, so the logs run on 23 of the 24.
        Assert.True(study.Row("e09").ZeroRealizedMove);
        Assert.Contains("| Primary sample — tradable tier | 23 | 1 | 0 |", memo);

        // Market cap comes from the as-of fact: CIK 1 filed 10,000,000 shares before the entry date and
        // 999,000,000 after it. The later filing must not be used.
        Assert.Equal(10_000_000m, study.Row("e01").SharesOutstanding);
        Assert.Equal(1_000_000_000m, study.Row("e01").MarketCap);
        Assert.Equal("unknown", study.Row("e32").MarketCapQuintilePrimary);

        // The only event quoted off a parity spot, and the only one with no exit mid.
        Assert.Equal("parity", study.Row("e30").SpotSource);
        Assert.Equal(98m, study.Row("e30").SpotForIm);
        Assert.Null(study.Row("e31").StraddleReturn);
        Assert.Contains("feed 23, parity 1", memo);

        // The event with no earnings week is a singleton cluster.
        Assert.Equal("unknown-week:e28", study.Row("e28").ClusterKey);
    }

    [Fact]
    public async Task The_fixture_run_is_reproducible_and_its_verdict_is_stated()
    {
        using var study = Fixture();

        Assert.Equal(0, await study.RunAsync("--reps", "300"));
        var first = File.ReadAllBytes(study.Paths.Memo);

        Assert.Equal(0, await study.RunAsync("--reps", "300"));
        Assert.Equal(first, File.ReadAllBytes(study.Paths.Memo));

        // P(RF < IM) = 0.75 over 9 clusters: the interval sits above 0.5 and the median is 0.65.
        Assert.Contains("**VERDICT: PASS**", study.Memo);
        Assert.Contains("median(RF/IM) = 0.650000 < 1", study.Memo);
    }

    /// <summary>
    /// The fixture directory copied somewhere writable, so the run's outputs never land in the test's
    /// own inputs. The price QA quarantines e22 and nothing else — WP2's rule is not this package's to
    /// write, so the fixture pins the one decision the gate needs.
    /// </summary>
    private static StudyHarness Fixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compute");
        var destination = Path.Combine(Path.GetTempPath(), $"c1-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*.csv"))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        return new StudyHarness(destination, writesTables: false)
        {
            PriceQa = (timing, closes) => timing.EventId == "e22"
                ? new TimingQaResult(true, "entry close already carries the print", 0.15m, 0.001m)
                : new TimingQaResult(false, null, 0.002m, closes.MedianAbsReturn20)
        };
    }
}
