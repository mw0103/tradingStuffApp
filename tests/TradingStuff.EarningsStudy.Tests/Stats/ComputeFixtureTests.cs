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
/// Their sum is 17.70666... and the v2 primary statistic, the mean, is that over 24 = 0.737778 at
/// six places. The secondary sample adds the ratio-2.0 event: (17.70666... + 2) / 25 = 0.788267.
/// Every fixture event prints in 2024Q1 and every one has a row in both fetch tables, so the quarter
/// is covered; the other fifteen quarters of the registered window hold no events and are covered
/// vacuously, which makes this a FINAL memo over the full window.
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
        Assert.Contains("Events in window and kept after dedup: 31 — the same set the timing step considers at gate 07.", memo);
        Assert.Contains("Entering `compute`: 30. Quarantined by the timing step: 1", memo);
        Assert.Contains("With no row at all in `event_timing.csv`: 0.", memo);

        // Gate 07's ledger row and this run agree, so the fixture raises no accounting warning.
        Assert.DoesNotContain("event(s) remaining, but", memo);
        Assert.DoesNotContain("have NO row in event_timing.csv", memo);

        // Gate by gate: closes (e21), price QA (e22), price (e23), chain (e24), quotable (e25), tradable (e26).
        Assert.Equal(Gates.ClosesPresent, study.Row("e21").StopGate);
        Assert.Equal(Gates.PriceQa, study.Row("e22").StopGate);
        Assert.Equal(Gates.MinimumPrice, study.Row("e23").StopGate);
        Assert.Equal(Gates.ChainFound, study.Row("e24").StopGate);
        Assert.Equal(Gates.Quotable, study.Row("e25").StopGate);
        Assert.Equal(Gates.Tradable, study.Row("e26").StopGate);
        Assert.Equal(Gates.TimingClassified, study.Row("e27").StopGate);

        var counts = study.WrittenGateCounts.Where(c => c.Step == ComputeStep.StepName).ToList();
        Assert.Equal([30, 30, 29, 28, 27, 26, 25], counts.Select(c => c.Considered));
        Assert.Equal([0, 1, 1, 1, 1, 1, 1], counts.Select(c => c.Removed));
        Assert.Equal(24, counts[^1].Remaining);

        // The v2 subset gate considered every event and removed none: the window is fully fetched.
        Assert.Equal(Gates.QuarterCoverage, counts[0].Gate);
        Assert.Contains("**Deliverable subset: 2022Q1..2025Q4 — the full registered window (16 quarters). This memo is FINAL.**", memo);
        Assert.DoesNotContain($" — {MemoWriter.ProvisionalLabel}", memo.Split('\n')[0]);
        Assert.Equal("# C1 — implied vs realized earnings moves (v2)", memo.Split('\n')[0]);
        Assert.Contains("| 2024Q1 | 31 | 0 | 0 | 0 | 1 | yes | yes |", memo);
        Assert.Contains("| 2025Q4 | 0 | 0 | 0 | 0 | 0 | yes | yes |", memo);

        Assert.Contains("| events in sample | 24 | |", memo);
        Assert.Contains("| **mean(RF/IM)** — PRIMARY, decides the verdict | **0.737778** |", memo);
        Assert.Contains("| median(RF/IM) — DESCRIPTIVE READOUT | 0.650000 |", memo);
        Assert.Contains("| P(RF < IM) — DESCRIPTIVE READOUT | 0.750000 |", memo);
        Assert.Contains("| ties (RF = IM, counted as not less) | 2 |", memo);
        Assert.Contains("| earnings-week clusters | 9 |", memo);

        // The secondary sample adds the wide-spread event (ratio 2.0): 25 events, median 0.7, mean 0.788267.
        Assert.Contains("| events in sample | 25 | |", memo);
        Assert.Contains("| **mean(RF/IM)** — PRIMARY, decides the verdict | **0.788267** |", memo);
        Assert.Contains("| median(RF/IM) — DESCRIPTIVE READOUT | 0.700000 |", memo);
        Assert.Contains("| P(RF < IM) — DESCRIPTIVE READOUT | 0.720000 |", memo);

        // Each rate on its own denominator: gate 07 saw 31, gate 09 only the 29 that got past gate 08.
        Assert.Contains("- Timing QA (gate 07): **1** quarantined of **31** considered = **0.032258**.", memo);
        Assert.Contains("- Price QA (gate 09): **1** quarantined of **29** considered = **0.034483**.", memo);
        Assert.Contains("Combined quarantine share of the gate-07 denominator = (1 + 1) / 31 = **0.064516**.", memo);

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

        // The IM-collapse witness, worked out by hand from option_measures.csv. Gate 09 decided on the
        // 29 events that got past gate 08. Twelve of those carry a parity spot at BOTH snapshots and so
        // have a computable diagnostic; the other 17 do not, and are a column rather than a drop:
        //   e22  6.00/99.95 over 12.00/100.00 = 0.500250  collapsed, and gate 09 quarantined it
        //   e09  6.00/99.95 over 15.00/100.00 = 0.400200  collapsed, and gate 09 did NOT
        //   e01-e08, e10, e30                  >= 0.96    steady, and gate 09 did NOT
        Assert.Contains("IM collapse vs the gate-09 decision, over the 29 event(s) gate 09 decided on.", memo);
        Assert.Contains("Not computable for 17 of them", memo);
        Assert.Contains("| quarantined | 1 | 0 | 0 | 1 |", memo);
        Assert.Contains("| not quarantined | 1 | 10 | 17 | 28 |", memo);
        Assert.Equal(0.500250m, Math.Round(study.Row("e22").ImCollapseRatio!.Value, 6));
        Assert.Equal(0.400200m, Math.Round(study.Row("e09").ImCollapseRatio!.Value, 6));
        Assert.Null(study.Row("e11").ImCollapseRatio);

        // Gate 09 removed one event, so the memo owes the reader its selection effect.
        Assert.Contains("Gate-09 selection effect: the price QA removed 1 event(s) from this study", memo);

        // The price source, and the fact that every close in this fixture came from one vendor.
        Assert.Contains("over the 30 event(s) entering `compute`: ibkr 30.", memo);
    }

    [Fact]
    public async Task The_fixture_run_is_reproducible_and_its_verdict_is_stated()
    {
        using var study = Fixture();

        Assert.Equal(0, await study.RunAsync("--reps", "300"));
        var first = File.ReadAllBytes(study.Paths.Memo);

        Assert.Equal(0, await study.RunAsync("--reps", "300"));
        Assert.Equal(first, File.ReadAllBytes(study.Paths.Memo));

        // mean(RF/IM) = 0.737778 over 9 clusters: the interval sits entirely below 1.
        Assert.Contains("**VERDICT: PASS**", study.Memo);
        Assert.Contains("mean(RF/IM) = 0.737778 < 1", study.Memo);
        Assert.Contains("excludes 1 (below it)", study.Memo);

        // The memo cites v2 as governing and v0 as the superseded record, by path.
        Assert.Contains("# C1 — implied vs realized earnings moves (v2)", study.Memo);
        Assert.Contains("Governing registration: `docs/research/c1-preregistration-v2.md`", study.Memo);
        Assert.Contains("Superseded record: `docs/research/c1-preregistration-v0.md`", study.Memo);
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
