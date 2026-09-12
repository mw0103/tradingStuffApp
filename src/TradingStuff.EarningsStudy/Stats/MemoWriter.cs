using System.Globalization;
using System.Text;
using TradingStuff.EarningsStudy.Model;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// Renders <c>c1_memo.md</c> from a <see cref="C1Report"/> that is already fully computed. Nothing
/// here decides anything: every number arrives from <see cref="ComputeStep"/> unrounded and is
/// formatted only as it is written, so no comparison in this study is ever made on a rounded value.
///
/// The section order is the pre-registration's deliverable order — universe rule reference,
/// exclusion table, quarantine rate, primary statistics with CIs, verdict, the four splits,
/// straddle-return diagnostic, secondary statistics, limitations — and it is pinned by a test. The
/// memo is the only artifact the reader sees, and the code that computes the verdict is the code
/// that writes it, so everything the verdict depends on is printed beside it: the definitions fixed
/// before the data was seen, the gate counts, and what could not be measured.
///
/// Nothing time-dependent is written: the same inputs and seed must produce a byte-identical memo.
/// </summary>
public static class MemoWriter
{
    /// <summary>The frozen rule this memo is an execution of. Quoted, not paraphrased.</summary>
    public const string PreRegistrationPath = "docs/research/c1-preregistration-v0.md";

    /// <summary>Quoted from that file's header line "Frozen: Saturday 2026-09-12, prior to any query execution."</summary>
    public const string PreRegistrationFrozen = "Saturday 2026-09-12";

    /// <summary>
    /// The "Known v0 biases (logged, with direction)" section of the pre-registration, verbatim. The
    /// limitations section must not paraphrase a registered bias into something weaker, so the text
    /// is copied and a test compares it against the file.
    /// </summary>
    public const string RegisteredBiases = """
- Survivorship (universe seeded from current optionable list):
  delisted blowups missing -> RF understated -> biases TOWARD PASS.
  Therefore: v0 PASS is provisional pending survivorship-complete
  rerun; v0 FAIL is strong evidence.
- EOD entry proxy vs true last-pre-print snapshot (age logged).
- Recent-window regime coverage (2018/2020 regimes absent from v0).
""";

    private const string Absent = "n/a";

    public static string Render(C1Report report)
    {
        var memo = new StringBuilder();

        Line(memo, "# C1 — implied vs realized earnings moves (v0)");
        Line(memo);

        Line(memo, "The number is the number. Every figure below is computed by the `compute` verb from the tables");
        Line(memo, "listed in 1.2 and written by the same run; nothing in this memo is typed by hand.");
        Line(memo);

        UniverseSection(memo, report);
        ExclusionSection(memo, report);
        QuarantineSection(memo, report);
        PrimaryStatisticsSection(memo, report);
        VerdictSection(memo, report);
        SplitsSection(memo, report);
        StraddleSection(memo, report);
        SecondarySection(memo, report);
        LimitationsSection(memo, report);

        return memo.ToString();
    }

    private static void UniverseSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 1. Universe rule");
        Line(memo);
        Line(memo, $"Pre-registration: `{PreRegistrationPath}`, frozen {PreRegistrationFrozen} prior to any query");
        Line(memo, "execution. That file defines the universe, the gates, the measures, the statistics and the");
        Line(memo, "PASS/FAIL rule; this memo executes it and adds nothing to it.");
        Line(memo);
        Line(memo, $"- Registered window: {Date(C1Registration.WindowFrom)} to {Date(C1Registration.WindowTo)} (v0 subsets by TIME only).");
        Line(memo, $"- Minimum entry close: {Money(C1Registration.MinimumEntryPrice)}. Tradable tier: combined ATM spread <= {Num(C1Registration.TradableSpreadFraction)} x straddle mid.");
        Line(memo, $"- Calendar: {C1Registration.Calendar}. Admitted primary listings: {string.Join(", ", C1Registration.AdmittedExchanges.Order(StringComparer.Ordinal))}.");
        Line(memo, $"- Universe table: {report.Measured.UniverseRows} seed symbols, {report.Measured.UniverseEligible} eligible after the name-level gates.");
        Line(memo, $"- Events in window and kept after dedup: {report.EventsInWindowAndKept} — the same set the timing step considers at gate 07.");
        Line(memo, $"- Entering `compute`: {report.EventsEnteringCompute}. Quarantined by the timing step: {report.EventsQuarantinedByTimingStep} " +
                   "(those ARE the gate-07 removals, counted there and not again here, and still listed in the event table).");
        Line(memo, $"- With no row at all in `event_timing.csv`: {report.EventsWithNoTimingRow}. Such an event is a pipeline");
        Line(memo, "  discontinuity, not a gate-07 quarantine; it is never folded into that tally, because a missing row");
        Line(memo, "  presented as a gate decision is how an absent record reads as a clean one.");
        Line(memo);

        Line(memo, "### 1.1 Definitions fixed before compute");
        Line(memo);
        Line(memo, "The pre-registration leaves these open. Each was fixed in code before any real data was read,");
        Line(memo, "and is stated here so the memo and the code can be checked against each other:");
        Line(memo);
        foreach (var definition in Definitions()) Line(memo, definition);
        Line(memo);

        Line(memo, "### 1.2 Inputs");
        Line(memo);
        Line(memo, "| table | rows | sha256 |");
        Line(memo, "|---|---:|---|");
        foreach (var input in report.Inputs)
        {
            Line(memo, $"| `{input.Name}` | {input.Rows} | `{input.Sha256}` |");
        }
        Line(memo);
        Line(memo, "`gate_counts.csv` is not hashed here: `compute` appends its own rows to it, so it is an output of");
        Line(memo, "this run as well as an input, and a hash of it would change on a re-run that changed nothing.");
        Line(memo);
    }

    private static IEnumerable<string> Definitions() =>
    [
        "1. **Median, even n** — the mean of the two middle order statistics. Odd n is the middle value.",
        "2. **P(RF < IM)** — strict. An event with RF exactly equal to IM counts as NOT less and is reported",
        "   separately as a tie. The comparison is made on RF and IM, never on the stored ratio, so a ratio",
        "   that rounds to exactly 1 cannot flip a boundary event.",
        "3. **Bootstrap cluster** — `event_timing.earnings_week`. An event with no week recorded is its own",
        "   singleton cluster (`unknown-week:<event_id>`), counted and reported per sample: singletons narrow",
        "   the interval, so the count is printed rather than buried.",
        "4. **Bootstrap draw** — clusters with replacement, as many draws as the sample has clusters; the drawn",
        "   clusters' events are pooled and both statistics recomputed on the pool.",
        "5. **Interval** — percentile method at (1 - level)/2 and 1 - (1 - level)/2, endpoints taken as order",
        "   statistics by the nearest-rank rule (the smallest replicate whose 1-based rank is at least p x reps).",
        "   No interpolation: an endpoint is a value the resampling actually produced.",
        "6. **Verdict boundaries** — `median < 1` is strict, so a median of exactly 1 FAILS. \"Excludes 0.5\" means",
        "   0.5 lies outside the CLOSED interval, so an endpoint of exactly 0.5 does NOT exclude it and FAILS.",
        "7. **Trim** — floor(0.01 x n) values dropped from EACH tail of the sorted log ratios, n counted AFTER the",
        "   RF = 0 exclusion. If the two tails would leave nothing, the trimmed mean is reported as not computable.",
        "8. **RF = 0** — no logarithm exists, so such events are excluded from the secondary log statistics ONLY,",
        "   and counted. They stay in the median and in P(RF < IM).",
        "9. **Quintiles** — equal-count buckets by rank WITHIN the sample being reported: ascending, ties broken by",
        "   event id, bucket = floor(rank x 5 / n) clamped to 4. When n is not divisible by 5 the bucket sizes differ",
        "   by at most one and which buckets carry the extra event is decided by that formula and nothing else",
        "   (n = 7 gives 2, 1, 2, 1, 1; n = 11 gives 3, 2, 2, 2, 2). An event with no value never joins a quintile;",
        "   it goes to the `unknown` bucket, which is printed even when empty.",
        "10. **Spot for IM** — `spot_feed_entry` when present, otherwise `spot_parity_entry`. The source is recorded",
        "    per event and tallied in 9.2, with the parity-vs-close deviation quantiles beside it.",
        "11. **Sample nesting** — the primary (tradable) sample is a SUBSET of the secondary (all-quotable) sample.",
        "    An event in the primary sample is counted in both; `in_primary` / `in_secondary` in the event table are",
        "    the authoritative flags.",
        "12. **Unmeasurable events** — an event that passes the quotable gate but has no computable RF/IM is counted",
        "    and listed by id, never dropped and never defaulted. It counts toward sample membership, not toward n.",
        "13. **Straddle hold-through** — diagnostic only, never an input to the verdict. A missing exit mid is counted,",
        "    not dropped.",
        "14. **Random draws** — SplitMix64 seeded from the registered seed mixed with an FNV-1a hash of each analysis",
        "    name, so every interval reproduces exactly and adding an analysis does not move the others.",
        "15. **Rounding** — every comparison is made on unrounded `decimal` values. Numbers are formatted only when",
        "    written: 6 decimal places for ratios, proportions and moves, 2 for prices, 0 for market capitalisations.",
        "16. **Market cap** — as-of shares outstanding (the latest fact filed on or before the entry date) x the entry",
        "    close. No fact on or before that date means the `unknown` bucket, never an interpolated figure."
    ];

    private static void ExclusionSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 2. Exclusion table");
        Line(memo);
        Line(memo, "Every gate of the universe rule, in the order applied, assembled from `gate_counts.csv` (the");
        Line(memo, "steps' own records) rather than from anything this run remembers. `considered - removed = remaining`.");
        Line(memo);
        Line(memo, "| # | gate | step | considered | removed | remaining | note |");
        Line(memo, "|---:|---|---|---:|---:|---:|---|");
        foreach (var row in report.ExclusionTable)
        {
            Line(memo, $"| {row.Order} | `{row.Gate}` | {row.Step} | {row.Considered} | {row.Removed} | {row.Remaining} | {Text(row.Note)} |");
        }
        foreach (var gate in report.GatesWithoutCounts)
        {
            Line(memo, $"| {ComputeStep.GateOrder(gate)} | `{gate}` | (not recorded) | | | | **no row in gate_counts.csv — this gate's tally is absent, not zero** |");
        }
        Line(memo);

        if (report.UnrecognisedGateRows.Count > 0)
        {
            Line(memo, "Rows in `gate_counts.csv` whose gate is not part of the registered order, reproduced so that");
            Line(memo, "nothing in that ledger is silently discarded:");
            Line(memo);
            Line(memo, "| gate | step | considered | removed | remaining | note |");
            Line(memo, "|---|---|---:|---:|---:|---|");
            foreach (var row in report.UnrecognisedGateRows)
            {
                Line(memo, $"| `{row.Gate}` | {row.Step} | {row.Considered} | {row.Removed} | {row.Remaining} | {Text(row.Note)} |");
            }
            Line(memo);
        }

        Line(memo, $"Survivors of gate 12 are the all-quotable (secondary) sample: {report.Secondary.Members} events.");
        Line(memo, $"Survivors of gate 13 are the tradable (primary) sample: {report.Primary.Members} events.");
        Line(memo);
    }

    private static void QuarantineSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 3. Quarantine rate");
        Line(memo);
        Line(memo, "Two quarantines, each reported against ITS OWN denominator. Gate 09 decides only on the events that");
        Line(memo, "reached it, which is a strictly smaller set than gate 07 considered, so one rate over the gate-07");
        Line(memo, "denominator would understate how often the price QA fires on the events it actually sees.");
        Line(memo);

        var q = report.Quarantine;
        Line(memo, q.TimingComputable
            ? $"- Timing QA (gate 07): **{q.TimingQuarantined}** quarantined of **{q.TimingConsidered}** considered = **{Num(q.TimingRate)}**."
            : $"- Timing QA (gate 07): **{q.TimingQuarantined}** quarantined; the rate is **not computable**.");
        Line(memo, q.PriceQaComputable
            ? $"- Price QA (gate 09): **{q.PriceQaQuarantined}** quarantined of **{q.PriceQaConsidered}** considered = **{Num(q.PriceQaRate)}**."
            : $"- Price QA (gate 09): **{q.PriceQaQuarantined}** quarantined; the rate is **not computable**.");
        Line(memo);

        if (q.CombinedRate is not null)
        {
            Line(memo, $"Combined quarantine share of the gate-07 denominator = ({q.TimingQuarantined} + {q.PriceQaQuarantined}) / {q.TimingConsidered} = **{Num(q.CombinedRate)}**.");
            Line(memo, "That figure adds two numerators measured on different sets, so it is the share of the gate-07");
            Line(memo, "denominator that either quarantine removed and NOT the rate at which either rule fires. The two");
            Line(memo, "rates above are the rates.");
            Line(memo);
        }

        if (q.Note is { Length: > 0 })
        {
            Line(memo, $"**Not fully computable.** {Text(q.Note)}");
            Line(memo);
        }
    }

    private static void PrimaryStatisticsSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 4. Primary statistics");
        Line(memo);
        Line(memo, $"Week-clustered bootstrap: {report.Replications} replications, seed {report.Seed}, " +
                   $"{Percent(C1Registration.ConfidenceLevel)} percentile intervals.");
        Line(memo);
        SampleStatistics(memo, "### 4.1", report.Primary);
        SampleStatistics(memo, "### 4.2", report.Secondary);
    }

    private static void SampleStatistics(StringBuilder memo, string heading, SampleReport sample)
    {
        Line(memo, $"{heading} {sample.Title}");
        Line(memo);
        Line(memo, sample.Definition);
        Line(memo);
        Line(memo, "| statistic | value | clustered interval |");
        Line(memo, "|---|---:|---|");
        Line(memo, $"| events in sample | {sample.Members} | |");
        Line(memo, $"| of which RF/IM computable (n) | {sample.Measurable} | |");
        Line(memo, $"| earnings-week clusters | {sample.Clusters} | |");
        Line(memo, $"| median(RF/IM) | {Num(sample.Median)} | {Interval(sample.Bootstrap?.Median)} |");
        Line(memo, $"| P(RF < IM) | {Num(sample.ProportionLess)} | {Interval(sample.Bootstrap?.ProportionLess)} |");
        Line(memo, $"| events with RF < IM | {sample.LessCount} | |");
        Line(memo, $"| ties (RF = IM, counted as not less) | {sample.TieCount} | |");
        Line(memo);

        if (sample.SingletonUnknownWeeks > 0)
        {
            Line(memo, $"{sample.SingletonUnknownWeeks} event(s) had no earnings week recorded and were bootstrapped as singleton clusters.");
            Line(memo);
        }

        if (sample.UnmeasurableEvents.Count > 0)
        {
            Line(memo, $"{sample.UnmeasurableEvents.Count} event(s) in this sample have no computable RF/IM and are excluded from the");
            Line(memo, "statistics above — counted here, never dropped, and carried in the event table with the reason:");
            Line(memo);
            foreach (var note in sample.UnmeasurableEvents) Line(memo, $"- {note}");
            Line(memo);
        }
    }

    private static void VerdictSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 5. Verdict");
        Line(memo);
        Line(memo, "PASS = median(RF/IM) < 1 AND the week-clustered 95% CI for P(RF < IM) excludes 0.5. FAIL = otherwise.");
        Line(memo, "Measured on the PRIMARY (tradable) sample.");
        Line(memo);
        Line(memo, report.Verdict.Line);
        Line(memo);
        if (report.Verdict.Computed)
        {
            Line(memo, report.Verdict.Pass
                ? "Per the pre-registration: PASS is provisional pending the survivorship-complete and full-window reruns; proceed to C2 preparation."
                : "Per the pre-registration: the program stops. The full-window and survivorship checks run once before the stop is final.");
        }
        else
        {
            Line(memo, "No consequence is triggered by a verdict that was not computed.");
        }
        Line(memo);
    }

    private static void SplitsSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 6. Pre-registered splits");
        Line(memo);
        Line(memo, "Readouts, not gates: no event is excluded by a split. Each bucket carries its own week-clustered");
        Line(memo, "interval, computed on that bucket's clusters alone. The buckets cover the events with a computable");
        Line(memo, "RF/IM, so they sum to n rather than to the sample's membership; section 4 names any difference.");
        Line(memo);

        for (var i = 0; i < report.Primary.Splits.Count; i++)
        {
            var split = report.Primary.Splits[i];
            Line(memo, $"### 6.{i + 1} {split.Title}");
            Line(memo);
            Line(memo, split.Rule);
            Line(memo);
            SplitTable(memo, report.Primary.Title, split);
            if (i < report.Secondary.Splits.Count) SplitTable(memo, report.Secondary.Title, report.Secondary.Splits[i]);
        }
    }

    private static void SplitTable(StringBuilder memo, string sampleTitle, SplitReport split)
    {
        Line(memo, $"{sampleTitle}:");
        Line(memo);
        Line(memo, "| bucket | range | n | clusters | median | median CI | P(RF < IM) | P CI |");
        Line(memo, "|---|---|---:|---:|---:|---|---:|---|");
        foreach (var bucket in split.Buckets)
        {
            Line(memo, $"| {bucket.Label} | {Text(bucket.Range)} | {bucket.Members} | {bucket.Clusters} | " +
                       $"{Num(bucket.Median)} | {Interval(bucket.MedianInterval)} | {Num(bucket.ProportionLess)} | {Interval(bucket.ProportionInterval)} |");
        }
        Line(memo);
    }

    private static void StraddleSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 7. Straddle hold-through diagnostic");
        Line(memo);
        Line(memo, "(straddle mid at exit - straddle mid at entry) / straddle mid at entry, at mids, held through the");
        Line(memo, "print. Diagnostic only: it is not part of the claim and does not enter the verdict. Events whose exit");
        Line(memo, "mid is missing are counted, not dropped.");
        Line(memo);
        Line(memo, "| sample | n (RF/IM computable) | with a return | missing exit mid | median | mean | p10 | p25 | p75 | p90 |");
        Line(memo, "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var sample in new[] { report.Primary, report.Secondary })
        {
            var s = sample.Straddle;
            Line(memo, $"| {sample.Title} | {s.Members} | {s.WithReturn} | {s.NullExit} | {Num(s.Median)} | {Num(s.Mean)} | " +
                       $"{Num(s.P10)} | {Num(s.P25)} | {Num(s.P75)} | {Num(s.P90)} |");
        }
        Line(memo);
    }

    private static void SecondarySection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 8. Secondary statistics");
        Line(memo);
        Line(memo, "mean log(RF/IM) and the 1% symmetrically trimmed mean of the same log ratios. Labelled secondary");
        Line(memo, "because the registration says trimming appears only here: the primary statistics keep every outlier.");
        Line(memo);
        Line(memo, "| sample | n with a logarithm | RF = 0, excluded | trimmed per tail | mean log(RF/IM) | 1% trimmed mean log |");
        Line(memo, "|---|---:|---:|---:|---:|---:|");
        foreach (var sample in new[] { report.Primary, report.Secondary })
        {
            var log = sample.Log;
            Line(memo, $"| {sample.Title} | {log.Used} | {log.ZeroRealizedExcluded} | {log.TrimPerTail} | {Dbl(log.MeanLog)} | {Dbl(log.TrimmedMeanLog)} |");
        }
        Line(memo);

        var notes = new[] { report.Primary, report.Secondary }.Where(s => s.Log.Note is { Length: > 0 }).ToList();
        if (notes.Count == 0) return;

        foreach (var sample in notes) Line(memo, $"- {sample.Title}: {sample.Log.Note}.");
        Line(memo);
    }

    private static void LimitationsSection(StringBuilder memo, C1Report report)
    {
        Line(memo, "## 9. Limitations");
        Line(memo);
        Line(memo, "### 9.1 Registered v0 biases");
        Line(memo);
        Line(memo, $"Verbatim from `{PreRegistrationPath}`, section \"Known v0 biases (logged, with direction)\":");
        Line(memo);
        Line(memo, "```");
        Line(memo, RegisteredBiases);
        Line(memo, "```");
        Line(memo);

        Line(memo, "### 9.2 Measured in this run");
        Line(memo);
        Line(memo, $"- Bootstrap: {report.Replications} replications, registered seed {report.Seed}, SplitMix64, percentile intervals at {Percent(C1Registration.ConfidenceLevel)}.");
        Line(memo, $"- Spot used for IM — {report.Primary.Title}: {TallyText(report.Primary.SpotSources)}. {report.Secondary.Title}: {TallyText(report.Secondary.SpotSources)}.");
        Line(memo, $"- Official close source (`closes.source`), over the {report.EventsEnteringCompute} event(s) entering `compute`: {TallyText(report.Measured.PriceSources)}. " +
                   "RF and the minimum-price gate are measured on these closes, so the price vendor is part of what the number depends on.");

        var parity = report.Measured.ParityCloseDeviation;
        Line(memo, $"- |parity spot - entry close| / entry close, over the events entering `compute` that have a parity spot " +
                   $"(n = {parity.Count}): p50 {Num(parity.P50)}, p90 {Num(parity.P90)}, p95 {Num(parity.P95)}, p99 {Num(parity.P99)}, max {Num(parity.Max)}.");
        Line(memo, $"- Option snapshot kind: {TallyText(report.Measured.SnapshotKinds)}.");
        Line(memo, $"- Option snapshot time (ET), the entry-proxy age the registration asks to be logged: {TallyText(report.Measured.SnapshotTimes)}.");
        Line(memo, $"- Timing quarantine reasons recorded by the timing step: {TallyText(report.Measured.TimingQuarantineReasons)}.");

        if (report.Measured.TimingGateNotes.Count > 0)
        {
            Line(memo, "- Timing gate notes, verbatim from `gate_counts.csv` (non-trading-day acceptances are recorded here if the timing step counted them):");
            foreach (var note in report.Measured.TimingGateNotes) Line(memo, $"  - `{note}`");
        }
        else
        {
            Line(memo, "- Timing gate notes: the timing step recorded none, so no non-trading-day acceptance count is available from it.");
        }

        if (report.Measured.OrphanRows.Count > 0)
        {
            Line(memo, $"- Rows in the input tables that reference an event not kept after dedup, ignored by the statistics but counted here: {TallyText(report.Measured.OrphanRows)}.");
        }

        Line(memo, "- Price QA rule, quoted verbatim from `TimingQa.Describe()`:");
        Line(memo);
        Line(memo, "  ```");
        foreach (var line in report.PriceQaDescription.Replace("\r\n", "\n").Split('\n')) Line(memo, $"  {line}");
        Line(memo, "  ```");
        Line(memo);

        GateNineSelectionEffect(memo, report);
        ImCollapseSection(memo, report);

        foreach (var warning in report.Warnings) Line(memo, $"- {warning}");
        Line(memo);

        CriterionSection(memo);
    }

    /// <summary>
    /// What gate 09 did to the sample, printed only when it removed something. The rule itself is
    /// quoted just above from <c>TimingQa.Describe()</c> and is deliberately NOT restated here: a
    /// paraphrase of a gate beside the gate's own words is a second, drifting definition, and the one
    /// that drifts is always the paraphrase. What this says instead is the count, the INPUT SURFACE,
    /// and where the independent witness is. The input surface is a guarantee of the delegate type
    /// the gate is handed — a timing row and a closes row — so it holds whatever rule is plugged in,
    /// which is what lets this paragraph be written without knowing the predicate.
    /// </summary>
    private static void GateNineSelectionEffect(StringBuilder memo, C1Report report)
    {
        var removed = report.Quarantine.PriceQaQuarantined;
        if (removed <= 0) return;

        Line(memo, $"- Gate-09 selection effect: the price QA removed {removed} event(s) from this study, so the sample the");
        Line(memo, "  verdict is measured on is the set that survived a cut, not the registered universe. What that decision");
        Line(memo, "  is given is the event's timing row and its row of official closes — the resolved measurement dates,");
        Line(memo, "  the official closes at those dates, and the name's own trailing 20-day median absolute daily return;");
        Line(memo, "  which of those it weighs is the rule quoted above, not restated here. No option price reaches it, so");
        Line(memo, "  the ratio RF/IM this study reports is never itself the quantity selected on; but the closes that ARE");
        Line(memo, "  inputs are correlated with volatility and so with IM, which is why these removals cannot be assumed");
        Line(memo, "  random with respect to the claim. The IM-collapse cross-tab below is the independent, options-side");
        Line(memo, "  witness: it is computed from option prices the gate never reads, so agreement is evidence the cut");
        Line(memo, "  landed on events that were already priced out, and disagreement is evidence that it did not.");
    }

    /// <summary>
    /// The agreement cross-tab. Reported whether or not the two agree — a witness produced only when
    /// it corroborates is not a witness, it is a decoration.
    /// </summary>
    private static void ImCollapseSection(StringBuilder memo, C1Report report)
    {
        var a = report.Measured.ImCollapse;
        Line(memo, $"- IM collapse vs the gate-09 decision, over the {a.Considered} event(s) gate 09 decided on. The diagnostic is");
        Line(memo, "  `TimingQa.ImCollapseDiagnostic`: IM at the entry snapshot divided by IM at the pre-entry snapshot, from");
        Line(memo, $"  option prices only. Below the reference {Num(a.Threshold)} the straddle had already collapsed INTO the entry");
        Line(memo, "  snapshot — what a print that has already happened looks like from the options side. It gates nothing and");
        Line(memo, $"  is not part of the registered rule. Not computable for {a.NotComputable} of them, which is a column here rather");
        Line(memo, "  than a silent drop:");
        Line(memo);
        Line(memo, $"| gate 09 | IM collapse < {Num(a.Threshold)} | IM collapse >= {Num(a.Threshold)} | not computable | total |");
        Line(memo, "|---|---:|---:|---:|---:|");
        Line(memo, $"| quarantined | {a.QuarantinedCollapsed} | {a.QuarantinedNotCollapsed} | {a.QuarantinedNotComputable} | {a.Quarantined} |");
        Line(memo, $"| not quarantined | {a.KeptCollapsed} | {a.KeptNotCollapsed} | {a.KeptNotComputable} | {a.Kept} |");
        Line(memo);
    }

    /// <summary>
    /// The arithmetic property of the registered PASS condition itself. Printed on every run, passing
    /// or failing: it is a fact about the rule, not about the data, and it is the one caveat a reader
    /// cannot recover from any number in this memo.
    /// </summary>
    private static void CriterionSection(StringBuilder memo)
    {
        Line(memo, "### 9.3 What the registered criterion does and does not establish");
        Line(memo);
        Line(memo, "For any move distribution whose median absolute move is below its mean absolute move — which is every");
        Line(memo, "symmetric one — a straddle priced at exactly the mean absolute move, carrying no premium at all, already");
        Line(memo, "gives median(RF/IM) < 1 and P(RF < IM) > 0.5, so the registered PASS condition is satisfied by a fairly");
        Line(memo, "priced market and is not by itself evidence of a premium in expectation. This memo executes the");
        Line(memo, "registered rule as written and does not reinterpret it; the mean-based straddle hold-through diagnostic");
        Line(memo, "in section 7 is the readout that speaks to a premium in expectation.");
        Line(memo);
    }

    private static string TallyText(IReadOnlyList<Tally> tallies) =>
        tallies.Count == 0 ? "none" : string.Join(", ", tallies.Select(t => $"{t.Key} {t.Count}"));

    private static void Line(StringBuilder memo, string text = "")
    {
        // '\n' explicitly, never Environment.NewLine: the memo must be byte-identical wherever it runs.
        memo.Append(text).Append('\n');
    }

    /// <summary>A note written by another step goes into a table cell: a pipe would end the column and a newline the row.</summary>
    private static string Text(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Num(decimal? value) => value is null ? Absent : value.Value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal? value) => value is null ? Absent : value.Value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Dbl(double? value) => value is null ? Absent : value.Value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Interval(BootstrapInterval? interval) =>
        interval is null ? Absent : $"[{Num(interval.Lower)}, {Num(interval.Upper)}]";

    private static string Percent(decimal level) => (level * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>Market capitalisations are printed whole; a cent of a billion-dollar cap is noise.</summary>
    internal static string BigNumber(decimal value) => value.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>Spread fractions and other ratios print at the same six places as every other ratio here.</summary>
    internal static string Ratio(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
