using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Stats;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The compute verb end to end: the gates in registered order, the accounting that must reconcile,
/// and the memo. The rule these tests exist to enforce is that an event which contributes nothing
/// still leaves a row saying why — a study whose exclusions are invisible reports the cleanest
/// numbers it will ever produce (docs/LESSONS.md §3).
/// </summary>
public sealed class ComputeStepTests
{
    [Fact]
    public async Task Every_event_that_reached_compute_appears_in_the_event_table_with_the_gate_it_stopped_at()
    {
        using var study = new StudyHarness();
        study.PriceQa = (timing, _) => timing.EventId == "qa"
            ? new TimingQaResult(true, "entry close already carries the move", 0.001m, 0.090m)
            : new TimingQaResult(false, null, 0.001m, 0.020m);

        study.AddEvent("closes-missing", withCloses: false);
        study.AddEvent("closes-status", closesStatus: "missing_exit_bar");
        study.AddEvent("qa");
        study.AddEvent("cheap", closeEntryOverride: 9.99m);
        study.AddEvent("chain-missing", withMeasures: false);
        study.AddEvent("chain-status", fetchStatus: "no_chain");
        study.AddEvent("no-bid", putBid: 0m);
        study.AddEvent("wide", combinedSpread: 4m);
        study.AddEvent("good", week: "2024-W06");
        study.AddEvent("timing-quarantined", quarantinedAtTiming: true);
        study.AddEvent("no-timing-row", withTiming: false);
        study.AddEvent("not-kept", keptAfterDedup: false);

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        var table = study.EventTable;

        // Every kept event has a row; the one the events verb dropped is that verb's business.
        Assert.Equal(11, table.Count);
        Assert.DoesNotContain(table, r => r.EventId == "not-kept");

        Assert.Equal(Gates.ClosesPresent, study.Row("closes-missing").StopGate);
        Assert.Equal(Gates.ClosesPresent, study.Row("closes-status").StopGate);
        Assert.Equal(Gates.PriceQa, study.Row("qa").StopGate);
        Assert.Equal(Gates.MinimumPrice, study.Row("cheap").StopGate);
        Assert.Equal(Gates.ChainFound, study.Row("chain-missing").StopGate);
        Assert.Equal(Gates.ChainFound, study.Row("chain-status").StopGate);
        Assert.Equal(Gates.Quotable, study.Row("no-bid").StopGate);
        Assert.Equal(Gates.Tradable, study.Row("wide").StopGate);
        Assert.Equal(Gates.TimingClassified, study.Row("timing-quarantined").StopGate);
        Assert.Equal(Gates.TimingClassified, study.Row("no-timing-row").StopGate);
        Assert.Equal("", study.Row("good").StopGate);

        // Each removed row says why, in its own words.
        Assert.Contains("no row in closes.csv", study.Row("closes-missing").Note);
        Assert.Contains("entry close below 10", study.Row("cheap").Note);
        Assert.Contains("the ATM put bid is zero", study.Row("no-bid").Note);
        Assert.Contains("wider than the tradable tier", study.Row("wide").Note);
        Assert.Contains("quarantined by the timing step", study.Row("timing-quarantined").Note);
        Assert.Contains("no row in event_timing.csv", study.Row("no-timing-row").Note);
    }

    [Fact]
    public async Task The_gate_counts_are_the_registered_gates_in_order_and_they_reconcile()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b").AddEvent("wide", combinedSpread: 4m).AddEvent("cheap", closeEntryOverride: 5m);

        Assert.Equal(0, await study.RunAsync("--reps", "50"));

        var counts = study.WrittenGateCounts.Where(c => c.Step == ComputeStep.StepName).ToList();

        Assert.Equal([8, 9, 10, 11, 12, 13], counts.Select(c => c.Order));
        Assert.Equal(
            [Gates.ClosesPresent, Gates.PriceQa, Gates.MinimumPrice, Gates.ChainFound, Gates.Quotable, Gates.Tradable],
            counts.Select(c => c.Gate));

        foreach (var count in counts)
        {
            Assert.Equal(count.Considered - count.Removed, count.Remaining);
        }

        for (var i = 1; i < counts.Count; i++)
        {
            Assert.Equal(counts[i - 1].Remaining, counts[i].Considered);
        }

        Assert.Equal(4, counts[0].Considered);
        Assert.Equal(2, counts[^1].Remaining);
    }

    [Fact]
    public async Task Compute_replaces_its_own_gate_rows_on_a_rerun_and_leaves_the_other_steps_alone()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));
        var afterFirst = File.ReadAllBytes(study.Paths.GateCounts);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));
        var afterSecond = File.ReadAllBytes(study.Paths.GateCounts);

        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(6, study.WrittenGateCounts.Count(c => c.Step == ComputeStep.StepName));
        Assert.Equal(7, study.WrittenGateCounts.Count(c => c.Step != ComputeStep.StepName));
    }

    [Fact]
    public async Task The_same_inputs_and_seed_write_a_byte_identical_memo()
    {
        using var study = new StudyHarness();
        for (var i = 0; i < 12; i++) study.AddEvent($"e{i}", rf: 0.02m + (i * 0.01m), im: 0.08m, week: $"2024-W{i % 4:00}");

        Assert.Equal(0, await study.RunAsync("--reps", "200"));
        var first = File.ReadAllBytes(study.Paths.Memo);

        Assert.Equal(0, await study.RunAsync("--reps", "200"));
        Assert.Equal(first, File.ReadAllBytes(study.Paths.Memo));

        // A different seed draws different clusters, so the intervals — and the memo — move.
        Assert.Equal(0, await study.RunAsync("--reps", "200", "--seed", "7"));
        Assert.NotEqual(first, File.ReadAllBytes(study.Paths.Memo));
    }

    [Fact]
    public async Task The_injected_price_qa_decides_gate_09_and_its_numbers_reach_the_event_table()
    {
        using var study = new StudyHarness();
        study.PriceQa = (timing, closes) => timing.EventId == "late"
            ? new TimingQaResult(true, "pre-entry move is 9 daily moves", 0.09m, 0.01m)
            : new TimingQaResult(false, null, 0.002m, closes.MedianAbsReturn20);

        study.AddEvent("late").AddEvent("clean");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var late = study.Row("late");
        Assert.Equal("quarantined", late.PriceQa);
        Assert.Equal("pre-entry move is 9 daily moves", late.PriceQaReason);
        Assert.Equal(0.09m, late.PriceQaPreEntryMove);
        Assert.Equal(0.01m, late.PriceQaEventMove);

        var clean = study.Row("clean");
        Assert.Equal("ok", clean.PriceQa);
        Assert.Equal(0.002m, clean.PriceQaPreEntryMove);
        Assert.Equal(0.02m, clean.PriceQaEventMove);
        Assert.True(clean.InPrimary);
    }

    [Fact]
    public async Task The_injected_as_of_delegate_decides_the_market_cap_and_a_missing_fact_is_unknown()
    {
        using var study = new StudyHarness();
        study.AddEvent("known", cik: 7, shares: null);
        study.AddEvent("unknown-shares", cik: 8, shares: null);

        // Entry is 2024-01-31: the later filing must not be picked, and CIK 8 has no fact at all.
        study.SharesFacts.Add(new SharesFactRow(7, new DateOnly(2023, 12, 31), 2_000_000m, new DateOnly(2024, 1, 10), "10-K", "a", null));
        study.SharesFacts.Add(new SharesFactRow(7, new DateOnly(2024, 3, 31), 9_000_000m, new DateOnly(2024, 3, 25), "10-Q", "b", null));

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var known = study.Row("known");
        Assert.Equal(2_000_000m, known.SharesOutstanding);
        Assert.Equal(new DateOnly(2024, 1, 10), known.SharesAsOfFiled);
        Assert.Equal(200_000_000m, known.MarketCap);
        Assert.Equal("Q1", known.MarketCapQuintilePrimary);

        var missing = study.Row("unknown-shares");
        Assert.Null(missing.MarketCap);
        Assert.Equal("unknown", missing.MarketCapQuintilePrimary);
    }

    [Fact]
    public async Task The_primary_sample_is_a_subset_of_the_secondary_and_both_flags_are_written()
    {
        using var study = new StudyHarness();
        study.AddEvent("tradable").AddEvent("quotable-only", combinedSpread: 4m);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var tradable = study.Row("tradable");
        Assert.True(tradable.InPrimary);
        Assert.True(tradable.InSecondary);
        Assert.Equal("primary", tradable.Membership);

        var quotable = study.Row("quotable-only");
        Assert.False(quotable.InPrimary);
        Assert.True(quotable.InSecondary);
        Assert.Equal("secondary", quotable.Membership);

        Assert.Contains("(secondary) sample: 2 events", study.Memo);
        Assert.Contains("(primary) sample: 1 events", study.Memo);
    }

    [Fact]
    public async Task The_measures_are_the_registered_ones_and_the_spot_prefers_the_feed()
    {
        using var study = new StudyHarness();
        study.AddEvent("feed", rf: 0.05m, im: 0.08m, spotFeed: 100m, spotParity: 90m);
        study.AddEvent("parity", rf: 0.05m, spotFeed: null, spotParity: 80m, straddleMidOverride: 8m);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var feed = study.Row("feed");
        Assert.Equal("feed", feed.SpotSource);
        Assert.Equal(100m, feed.SpotForIm);
        Assert.Equal(0.05m, feed.RealizedMove);
        Assert.Equal(0.08m, feed.ImpliedMove);
        Assert.Equal(0.625m, feed.Ratio);
        Assert.True(feed.RfLessThanIm);
        Assert.Equal(0.1m, feed.ParityCloseDeviation);   // |90 - 100| / 100

        var parity = study.Row("parity");
        Assert.Equal("parity", parity.SpotSource);
        Assert.Equal(80m, parity.SpotForIm);
        Assert.Equal(0.1m, parity.ImpliedMove);          // 8 / 80
        Assert.Equal(0.5m, parity.Ratio);

        Assert.Contains("feed 1, parity 1", study.Memo);
    }

    [Fact]
    public async Task A_zero_realized_move_stays_in_the_median_and_leaves_only_the_log_statistics()
    {
        using var study = new StudyHarness();
        study.AddEvent("zero", rf: 0m, im: 0.08m);
        study.AddEvent("one", rf: 0.08m, im: 0.08m);
        study.AddEvent("half", rf: 0.04m, im: 0.08m);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.True(study.Row("zero").ZeroRealizedMove);
        Assert.True(study.Row("zero").InPrimary);
        Assert.Equal(0m, study.Row("zero").Ratio);

        // Ratios 0, 0.5, 1 -> median 0.5, P(RF < IM) = 2/3, one tie. Logs keep only 0.5 and 1.
        Assert.Contains("| median(RF/IM) | 0.500000 |", study.Memo);
        Assert.Contains("| P(RF < IM) | 0.666667 |", study.Memo);
        Assert.Contains("| ties (RF = IM, counted as not less) | 1 |", study.Memo);
        Assert.Contains("| Primary sample — tradable tier | 2 | 1 | 0 |", study.Memo);
    }

    [Fact]
    public async Task An_event_in_the_sample_with_no_computable_ratio_is_listed_rather_than_dropped()
    {
        using var study = new StudyHarness();

        // Quotable (both bids positive) but no straddle mid: IM cannot be formed. The event is a
        // member of the sample, is excluded from n, and is named in the memo.
        study.AddEvent("no-mid", straddleMidOverride: null, callBid: 3m, putBid: 3m, combinedSpread: 0.1m);
        study.Measures[0] = study.Measures[0] with { StraddleMidEntry = null };
        study.AddEvent("normal");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var row = study.Row("no-mid");
        Assert.True(row.InSecondary);
        Assert.False(row.RatioMeasurable);
        Assert.Null(row.Ratio);
        Assert.Contains("no computable RF/IM", study.Memo);
        Assert.Contains("`no-mid`", study.Memo);
        Assert.Contains("| of which RF/IM computable (n) | 1 |", study.Memo);
    }

    [Fact]
    public async Task Each_quarantine_rate_is_reported_against_its_own_denominator()
    {
        using var study = new StudyHarness();
        study.PriceQa = (timing, _) => new TimingQaResult(timing.EventId == "q", "post-print entry", null, null);
        study.AddEvent("q").AddEvent("a").AddEvent("b").AddEvent("no-closes", withCloses: false);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        // The ledger says 4 quarantined by timing out of 100 considered. Gate 08 then removed the
        // event with no closes, so gate 09 decided on 3 and quarantined 1 of THOSE — not 1 of 100.
        Assert.Contains("- Timing QA (gate 07): **4** quarantined of **100** considered = **0.040000**.", study.Memo);
        Assert.Contains("- Price QA (gate 09): **1** quarantined of **3** considered = **0.333333**.", study.Memo);

        // The combined figure may stay, but only labelled as the share of the gate-07 denominator.
        Assert.Contains("Combined quarantine share of the gate-07 denominator = (4 + 1) / 100 = **0.050000**.", study.Memo);
        Assert.Contains("NOT the rate at which either rule fires", study.Memo);
    }

    [Fact]
    public async Task Without_a_gate_07_row_the_timing_rate_is_absent_rather_than_zero_and_the_price_qa_rate_still_stands()
    {
        using var study = new StudyHarness();
        study.GateCounts.RemoveAll(c => c.Gate == Gates.TimingClassified);
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("- Timing QA (gate 07): **0** quarantined; the rate is **not computable**.", study.Memo);
        Assert.Contains("- Price QA (gate 09): **0** quarantined of **2** considered = **0.000000**.", study.Memo);
        Assert.Contains("It is absent, not zero.", study.Memo);
        Assert.DoesNotContain("Combined quarantine share", study.Memo);
        Assert.Contains("| 7 | `07_timing_classified_bmo_or_amc` | (not recorded) |", study.Memo);
        Assert.Contains("registered gate(s) have no row in gate_counts.csv", study.Memo);
    }

    [Fact]
    public async Task With_nothing_reaching_gate_09_the_price_qa_rate_is_absent_rather_than_zero()
    {
        using var study = new StudyHarness();
        study.AddEvent("no-closes", withCloses: false);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("- Price QA (gate 09): **0** quarantined; the rate is **not computable**.", study.Memo);
        Assert.Contains("gate 09 considered no events, so the price-QA rate has no denominator.", study.Memo);
    }

    [Fact]
    public async Task The_im_collapse_diagnostic_is_computed_per_event_and_carried_with_its_two_pre_entry_inputs()
    {
        using var study = new StudyHarness();
        study.AddEvent("collapsed").AddEvent("steady").AddEvent("not-computable");

        // IM(entry) = 8/100 = 0.08. Pre-entry straddle 20 on a spot of 100 -> IM(pre-entry) = 0.20,
        // so the ratio is 0.4: the straddle crushed into the entry snapshot.
        SetPreEntry(study, "collapsed", 20m);
        SetPreEntry(study, "steady", 8m);
        SetPreEntry(study, "not-computable", null);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var collapsed = study.Row("collapsed");
        Assert.Equal(20m, collapsed.StraddleMidPreEntry);
        Assert.Equal(100m, collapsed.SpotParityPreEntry);
        Assert.Equal(0.4m, collapsed.ImCollapseRatio);

        Assert.Equal(1m, study.Row("steady").ImCollapseRatio);

        // No pre-entry straddle: the diagnostic is null, not a default, and the column says so.
        Assert.Null(study.Row("not-computable").ImCollapseRatio);
        Assert.Null(study.Row("not-computable").StraddleMidPreEntry);

        // The column is the same number TimingQa computes; this verb keeps no second copy of the rule.
        Assert.Equal(TimingQa.ImCollapseDiagnostic(study.Measures[0]), collapsed.ImCollapseRatio);
    }

    [Fact]
    public async Task The_im_collapse_cross_tab_counts_match_a_hand_built_table()
    {
        using var study = new StudyHarness();
        study.PriceQa = (timing, _) => new TimingQaResult(
            timing.EventId.StartsWith('q'), "late filing suspected", 0.09m, 0.01m);

        // Gate 09 sees six events. Hand-built, with IM(entry) = 0.08 throughout and the reference 0.60:
        //   q-collapsed      quarantined,     pre-entry IM 0.20 -> 0.400000  collapsed
        //   q-steady         quarantined,     pre-entry IM 0.08 -> 1.000000  not collapsed
        //   q-unknown        quarantined,     no pre-entry straddle -> null  not computable
        //   k-collapsed      not quarantined, pre-entry IM 0.16 -> 0.500000  collapsed
        //   k-steady-a       not quarantined, pre-entry IM 0.10 -> 0.800000  not collapsed
        //   k-steady-b       not quarantined, pre-entry IM 0.08 -> 1.000000  not collapsed
        // and one event that never reaches gate 09 at all, which must not appear in the table.
        study.AddEvent("q-collapsed").AddEvent("q-steady").AddEvent("q-unknown")
             .AddEvent("k-collapsed").AddEvent("k-steady-a").AddEvent("k-steady-b")
             .AddEvent("never-reaches-gate-09", withCloses: false);

        SetPreEntry(study, "q-collapsed", 20m);
        SetPreEntry(study, "q-steady", 8m);
        SetPreEntry(study, "q-unknown", null);
        SetPreEntry(study, "k-collapsed", 16m);
        SetPreEntry(study, "k-steady-a", 10m);
        SetPreEntry(study, "k-steady-b", 8m);
        SetPreEntry(study, "never-reaches-gate-09", 20m);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("IM collapse vs the gate-09 decision, over the 6 event(s) gate 09 decided on.", study.Memo);
        Assert.Contains("Not computable for 1 of them", study.Memo);
        Assert.Contains("| gate 09 | IM collapse < 0.600000 | IM collapse >= 0.600000 | not computable | total |", study.Memo);
        Assert.Contains("| quarantined | 1 | 1 | 1 | 3 |", study.Memo);
        Assert.Contains("| not quarantined | 1 | 2 | 0 | 3 |", study.Memo);

        // The cross-tab is recomputable from the event table, which is the point of carrying the columns.
        var decided = study.EventTable.Where(r => r.PriceQa is "ok" or "quarantined").ToList();
        Assert.Equal(6, decided.Count);
        Assert.Equal(1, decided.Count(r => r.PriceQa == "quarantined" && r.ImCollapseRatio < 0.60m));
        Assert.Equal(1, decided.Count(r => r.PriceQa == "quarantined" && r.ImCollapseRatio >= 0.60m));
        Assert.Equal(1, decided.Count(r => r.PriceQa == "quarantined" && r.ImCollapseRatio is null));
        Assert.Equal(1, decided.Count(r => r.PriceQa == "ok" && r.ImCollapseRatio < 0.60m));
        Assert.Equal(2, decided.Count(r => r.PriceQa == "ok" && r.ImCollapseRatio >= 0.60m));
        Assert.Equal(0, decided.Count(r => r.PriceQa == "ok" && r.ImCollapseRatio is null));
    }

    [Fact]
    public async Task Gate_09_removing_an_event_puts_its_selection_effect_in_the_limitations()
    {
        using var study = new StudyHarness();
        study.PriceQa = (timing, _) => new TimingQaResult(timing.EventId == "late", "late filing suspected", 0.09m, 0.01m);
        study.AddEvent("late").AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("Gate-09 selection effect: the price QA removed 1 event(s) from this study", study.Memo);
        Assert.Contains("which of those it weighs is the rule quoted above, not restated here", study.Memo);
        Assert.Contains("The IM-collapse cross-tab below is the independent, options-side", study.Memo);

        // The limitation states the rule's INPUTS, and leaves the predicate to the quoted sentence: a
        // paraphrase here would be a second definition, and it is the paraphrase that drifts.
        Assert.Contains("is given is the event's timing row and its row of official closes", study.Memo);
        Assert.Contains("name's own trailing 20-day median absolute daily return", study.Memo);
        Assert.Contains("No option price reaches it", study.Memo);

        // The predicate appears in the memo exactly once, inside the quoted rule, and nowhere else.
        Assert.DoesNotContain("larger than the absolute event move", study.Memo.Replace(TimingQa.Describe(), ""));
    }

    [Fact]
    public async Task A_run_where_gate_09_removed_nothing_claims_no_selection_effect()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.DoesNotContain("Gate-09 selection effect", study.Memo);

        // The cross-tab is printed either way: a witness shown only when it agrees is not a witness.
        Assert.Contains("IM collapse vs the gate-09 decision, over the 2 event(s) gate 09 decided on.", study.Memo);
    }

    [Fact]
    public async Task The_memo_states_what_the_registered_criterion_does_not_establish()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains(
            "For any move distribution whose median absolute move is below its mean absolute move — which is every\n" +
            "symmetric one — a straddle priced at exactly the mean absolute move, carrying no premium at all, already\n" +
            "gives median(RF/IM) < 1 and P(RF < IM) > 0.5, so the registered PASS condition is satisfied by a fairly\n" +
            "priced market and is not by itself evidence of a premium in expectation. This memo executes the\n" +
            "registered rule as written and does not reinterpret it; the mean-based straddle hold-through diagnostic\n" +
            "in section 7 is the readout that speaks to a premium in expectation.",
            study.Memo);
    }

    [Fact]
    public async Task The_memo_names_the_source_of_the_official_closes()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b").AddEvent("other-vendor").AddEvent("no-closes", withCloses: false);
        study.Closes[2] = study.Closes[2] with { Source = "polygon" };

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        // Four events entering compute, and the tally sums to four: the event with no closes row is a
        // named bucket rather than a row that quietly leaves the denominator.
        Assert.Contains("Official close source (`closes.source`), over the 4 event(s) entering `compute`: " +
                        "(no source recorded) 1, ibkr 2, polygon 1.", study.Memo);
    }

    /// <summary>
    /// Gives one event the pre-entry snapshot the IM-collapse diagnostic needs. Both parity spots are
    /// set: the diagnostic divides IM by IM, so it needs a spot at BOTH snapshots, and the harness's
    /// default event is quoted off a feed spot with no parity spot at all.
    /// </summary>
    private static void SetPreEntry(StudyHarness study, string eventId, decimal? straddleMidPreEntry)
    {
        var index = study.Measures.FindIndex(m => m.EventId == eventId);
        study.Measures[index] = study.Measures[index] with
        {
            StraddleMidPreEntry = straddleMidPreEntry,
            SpotParityPreEntry = 100m,
            SpotParityEntry = 100m
        };
    }

    [Fact]
    public async Task The_exclusion_table_carries_every_earlier_step_in_the_registered_order()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var rows = study.Memo.Split('\n').Where(l => l.StartsWith("| ") && l.Contains("` | ")).ToList();
        var gates = rows.Select(l => l.Split('`')[1]).Where(g => ComputeStep.GateOrder(g) > 0).ToList();

        Assert.Equal(Gates.InOrder, gates);
        Assert.Contains("| 1 | `01_seed_current_optionable_list` | universe |", study.Memo);
        Assert.Contains("| 7 | `07_timing_classified_bmo_or_amc` | timing |", study.Memo);
    }

    [Fact]
    public async Task A_gate_row_that_is_not_a_registered_gate_is_reproduced_rather_than_discarded()
    {
        using var study = new StudyHarness();
        study.GateCounts.Add(new GateCountRow("chains", 99, "99_experimental_gate", 10, 3, 7, "not in the registered order"));
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Contains("not part of the registered order", study.Memo);
        Assert.Contains("`99_experimental_gate`", study.Memo);
    }

    [Fact]
    public async Task The_memo_sections_appear_in_the_pre_registrations_deliverable_order()
    {
        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var headings = study.Memo.Split('\n').Where(l => l.StartsWith("## ")).ToList();
        Assert.Equal(
        [
            "## 1. Universe rule",
            "## 2. Exclusion table",
            "## 3. Quarantine rate",
            "## 4. Primary statistics",
            "## 5. Verdict",
            "## 6. Pre-registered splits",
            "## 7. Straddle hold-through diagnostic",
            "## 8. Secondary statistics",
            "## 9. Limitations"
        ], headings);

        var subheadings = study.Memo.Split('\n').Where(l => l.StartsWith("### ")).ToList();
        Assert.Equal(
        [
            "### 1.1 Definitions fixed before compute",
            "### 1.2 Inputs",
            "### 4.1 Primary sample — tradable tier",
            "### 4.2 Secondary sample — all quotable",
            "### 6.1 Market-cap quintile",
            "### 6.2 ATM-spread quintile",
            "### 6.3 DTE <= 7 vs > 7",
            "### 6.4 BMO vs AMC",
            "### 9.1 Registered v0 biases",
            "### 9.2 Measured in this run",
            "### 9.3 What the registered criterion does and does not establish"
        ], subheadings);
    }

    [Fact]
    public async Task A_pass_and_a_fail_are_both_reachable_and_the_memo_says_which()
    {
        // Twelve weeks, every event's realized move well inside its implied move: P(RF < IM) = 1, so
        // the interval is [1, 1] and the median is 0.25.
        using var pass = new StudyHarness();
        for (var i = 0; i < 24; i++) pass.AddEvent($"e{i}", rf: 0.02m, im: 0.08m, week: $"2024-W{i % 12:00}");

        Assert.Equal(0, await pass.RunAsync("--reps", "300"));
        Assert.Contains("**VERDICT: PASS**", pass.Memo);
        Assert.Contains("excludes 0.5 (above it)", pass.Memo);
        Assert.Contains("proceed to C2 preparation", pass.Memo);

        // Half the weeks under, half over: the median is below 1 but the interval straddles 0.5.
        using var fail = new StudyHarness();
        for (var i = 0; i < 24; i++)
        {
            var under = i % 2 == 0;
            fail.AddEvent($"e{i}", rf: under ? 0.02m : 0.10m, im: 0.08m, week: $"2024-W{i % 12:00}");
        }

        Assert.Equal(0, await fail.RunAsync("--reps", "300"));
        Assert.Contains("**VERDICT: FAIL**", fail.Memo);
        Assert.Contains("contains 0.5", fail.Memo);
        Assert.Contains("the program stops", fail.Memo);
    }

    [Fact]
    public async Task An_event_with_no_earnings_week_is_its_own_cluster_and_the_count_is_reported()
    {
        using var study = new StudyHarness();
        study.AddEvent("a", week: "2024-W05").AddEvent("b", week: "2024-W05").AddEvent("loose", week: null);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Equal("unknown-week:loose", study.Row("loose").ClusterKey);
        Assert.Equal("2024-W05", study.Row("a").ClusterKey);
        Assert.Contains("| earnings-week clusters | 2 |", study.Memo);
        Assert.Contains("1 event(s) had no earnings week recorded", study.Memo);
    }

    [Fact]
    public async Task A_missing_exit_mid_is_counted_by_the_straddle_diagnostic_and_not_dropped()
    {
        using var study = new StudyHarness();
        study.AddEvent("has-exit", straddleMidExit: 4m, straddleMidOverride: 8m);
        study.AddEvent("no-exit", withStraddleExit: false);

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Null(study.Row("no-exit").StraddleReturn);
        Assert.Equal(-0.5m, study.Row("has-exit").StraddleReturn);
        Assert.True(study.Row("no-exit").InPrimary);
        Assert.Contains("| Primary sample — tradable tier | 2 | 1 | 1 | -0.500000 |", study.Memo);
    }

    [Fact]
    public async Task Splits_are_readouts_so_every_bucket_of_every_split_sums_back_to_the_sample()
    {
        using var study = new StudyHarness();
        for (var i = 0; i < 11; i++)
        {
            study.AddEvent($"e{i}", rf: 0.01m + (i * 0.01m), im: 0.08m, week: $"2024-W{i % 3:00}",
                cik: i + 1, dte: i % 2 == 0 ? 3 : 30, timingClass: i % 3 == 0 ? "BMO" : "AMC", shares: (i + 1) * 1_000_000m);
        }

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        var table = study.EventTable.Where(r => r.InPrimary).ToList();
        Assert.Equal(11, table.Count);

        // Quintiles of 11 by the registered rule, bucket = floor(rank x 5 / 11): 3, 2, 2, 2, 2.
        var quintiles = table.GroupBy(r => r.MarketCapQuintilePrimary!).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(["Q1", "Q2", "Q3", "Q4", "Q5"], quintiles.Select(g => g.Key));
        Assert.Equal([3, 2, 2, 2, 2], quintiles.Select(g => g.Count()));

        // The split tables are the same events, counted again: no bucket loses or invents one.
        Assert.Equal(11, quintiles.Sum(g => g.Count()));
        Assert.Equal(11, table.Count(r => r.DteBucket is "DTE <= 7" or "DTE > 7"));
        Assert.Equal(11, table.Count(r => r.TimingBucket is "BMO" or "AMC"));
    }

    [Fact]
    public async Task Missing_inputs_and_duplicate_keys_are_refused_rather_than_worked_around()
    {
        using var missing = new StudyHarness();
        missing.AddEvent("a");
        missing.Write();
        File.Delete(missing.Paths.Closes);

        var code = await new ComputeStep(missing.PriceQa, missing.SharesAsOf, missing.DescribePriceQa)
            .RunAsync(missing.Context, [], CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains("required input closes.csv is missing", missing.Output.ToString());
        Assert.False(File.Exists(missing.Paths.Memo));

        using var duplicate = new StudyHarness();
        duplicate.AddEvent("a").AddEvent("a");

        Assert.Equal(1, await duplicate.RunAsync());
        Assert.Contains("more than one row for event_id 'a'", duplicate.Output.ToString());
        Assert.False(File.Exists(duplicate.Paths.Memo));
    }

    [Fact]
    public async Task An_empty_study_reports_an_empty_sample_rather_than_a_verdict()
    {
        using var study = new StudyHarness();

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        Assert.Empty(study.EventTable);
        Assert.Contains("| events in sample | 0 | |", study.Memo);
        Assert.Contains("| median(RF/IM) | n/a | n/a |", study.Memo);
        Assert.Contains("**VERDICT: NOT COMPUTED**", study.Memo);
        Assert.Equal(0, study.WrittenGateCounts.Single(c => c.Gate == Gates.ClosesPresent).Considered);
    }

    [Fact]
    public async Task With_no_gate_ledger_at_all_every_earlier_gate_reads_as_not_recorded()
    {
        using var study = new StudyHarness();
        study.GateCounts.Clear();
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));

        foreach (var gate in Gates.InOrder.Take(7))
        {
            Assert.Contains($"| `{gate}` | (not recorded) |", study.Memo);
        }

        Assert.Contains("this gate's tally is absent, not zero", study.Memo);
        Assert.Contains("- Timing QA (gate 07): **0** quarantined; the rate is **not computable**.", study.Memo);
        Assert.Equal(6, study.WrittenGateCounts.Count);
    }

    [Fact]
    public async Task An_unreadable_option_is_refused_with_the_usage_rather_than_ignored()
    {
        using var study = new StudyHarness();
        study.AddEvent("a");

        Assert.Equal(2, await study.RunAsync("--reps", "0"));
        Assert.Contains("cannot read the option", study.Output.ToString());
        Assert.False(File.Exists(study.Paths.Memo));
    }

    [Fact]
    public async Task Reps_and_seed_default_to_the_registered_values_and_are_printed_in_the_memo()
    {
        Assert.True(ComputeOptions.TryParse([], out var defaults, out _));
        Assert.Equal(C1Registration.BootstrapReplications, defaults.Replications);
        Assert.Equal(C1Registration.BootstrapSeed, defaults.Seed);

        Assert.True(ComputeOptions.TryParse(["--reps", "250", "--seed", "-3"], out var given, out _));
        Assert.Equal(250, given.Replications);
        Assert.Equal(-3, given.Seed);
        Assert.False(ComputeOptions.TryParse(["--unverified-price-qa"], out _, out var usage));
        Assert.Contains("cannot read the option", usage);

        using var study = new StudyHarness();
        study.AddEvent("a").AddEvent("b");
        Assert.Equal(0, await study.RunAsync("--reps", "17", "--seed", "99"));
        Assert.Contains("17 replications, seed 99", study.Memo);
    }

    [Fact]
    public async Task The_price_qa_rule_is_quoted_into_the_memo_verbatim()
    {
        using var study = new StudyHarness();
        study.DescribePriceQa = () => "QUARANTINE when |pre-entry move| > 4 x median |daily move|.";
        study.AddEvent("a").AddEvent("b");

        Assert.Equal(0, await study.RunAsync("--reps", "20"));
        Assert.Contains("QUARANTINE when |pre-entry move| > 4 x median |daily move|.", study.Memo);
    }

    [Fact]
    public async Task The_real_timing_qa_and_as_of_selection_are_what_the_verb_applies_by_default()
    {
        using var study = new StudyHarness();
        // A clean event, and one whose move landed the day BEFORE the entry close — the late-filing
        // signature WP2's rule exists for: pre-entry move 8 % against a 0.5 % event move and a 1 %
        // trailing scale.
        study.AddEvent("clean").AddEvent("late", closePreEntry: 92m, closeEntryOverride: 100m, closeExitOverride: 100.5m);

        Assert.Equal(0, await study.RunWithRealDefaultsAsync("--reps", "20"));

        Assert.Contains(TimingQa.Describe(), study.Memo);
        Assert.Contains("VERDICT:", study.Memo);
        Assert.Equal("quarantined", study.Row("late").PriceQa);
        Assert.Equal("ok", study.Row("clean").PriceQa);
        Assert.Equal(1, study.WrittenGateCounts.Single(c => c.Gate == Gates.PriceQa).Removed);

        // The real as-of pick found the harness's fact (filed 2024-01-15, entry 2024-01-31).
        Assert.NotNull(study.Row("clean").MarketCap);
    }
}
