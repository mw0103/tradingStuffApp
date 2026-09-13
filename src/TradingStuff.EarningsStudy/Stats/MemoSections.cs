using System.Text;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// The sections of <c>c1_memo.md</c> that the v2 amendment added or rewrote, and the list of
/// definitions fixed in code before any real data was read. Split from <see cref="MemoWriter"/> only
/// for size; it is the same class and the same rule — nothing here decides anything, every number
/// arrives already computed.
/// </summary>
public static partial class MemoWriter
{
    /// <summary>
    /// Section 1.1. Every choice the two registrations leave open, fixed in code before any real data
    /// was read and repeated here so the memo and the code can be checked against each other. Items
    /// 1-3 are the v2 amendment; the rest are v0's, unchanged by v2 section 6.
    /// </summary>
    private static IEnumerable<string> Definitions() =>
    [
        "1. **Primary statistic (v2)** — mean(RF/IM) over the sample: the ratios summed in `decimal` and divided",
        "   once by n. The mean is the statistic the verdict reads; under fair per-event pricing (IM_i = E[RF_i])",
        "   its null value is exactly 1 by the tower property, which is what makes it a test of a premium.",
        "2. **Demoted to descriptive readouts (v2)** — median(RF/IM) and P(RF < IM). Computed, printed, and",
        "   reaching no decision: they measure the shape of the ratio distribution, and a fairly priced market",
        "   already satisfies both (section 9.3). mean log(RF/IM) and its 1% trimmed form stay secondary as in v0.",
        "3. **Verdict boundaries (v2)** — `mean(RF/IM) < 1` is strict, so a mean of exactly 1 FAILS. \"Excludes 1\"",
        "   means 1 lies outside the CLOSED interval, so an endpoint of exactly 1 does NOT exclude it and FAILS.",
        "   The memo reports which side of 1 the interval lies on.",
        "4. **Straddle cross-check sign (v2)** — the hold-through return is measured on the LONG straddle,",
        "   (exit mid - entry mid) / entry mid. A premium makes the long side lose on average, so a NEGATIVE mean",
        "   agrees with PASS and a non-negative mean agrees with FAIL. It is a cross-check, never an input.",
        "5. **Fetch coverage of a quarter (v2 section 3)** — every event in window and kept after dedup whose print",
        "   date falls in that calendar quarter has a row in `option_measures.csv` AND a row in `closes.csv`. Any",
        "   status counts: a recorded failure is a fetch that completed, and the error counts are columns of the",
        "   coverage table so they can be re-run. A quarter with no events is covered vacuously, and its event",
        "   count is printed so a zero is visible rather than implied.",
        "6. **Deliverable subset (v2 section 3)** — the maximal run of consecutive covered quarters ending at the",
        "   most recent quarter of the registered window. Applied at gate 07b as a TIME-ONLY cut before any measure",
        "   is read, so every statistic and split is restricted to it by construction. Symbol-partial subsets are",
        "   prohibited in every form. A subset short of the full window makes this memo PROVISIONAL; an empty run",
        "   means no verdict at all and a non-zero exit.",
        "7. **Median, even n** — the mean of the two middle order statistics. Odd n is the middle value.",
        "8. **P(RF < IM)** — strict. An event with RF exactly equal to IM counts as NOT less and is reported",
        "   separately as a tie. The comparison is made on RF and IM, never on the stored ratio, so a ratio",
        "   that rounds to exactly 1 cannot flip a boundary event.",
        "9. **Bootstrap cluster** — `event_timing.earnings_week`. An event with no week recorded is its own",
        "   singleton cluster (`unknown-week:<event_id>`), counted and reported per sample: singletons narrow",
        "   the interval, so the count is printed rather than buried.",
        "10. **Bootstrap draw** — clusters with replacement, as many draws as the sample has clusters; the drawn",
        "    clusters' events are pooled and every statistic recomputed on the pool, so the mean, the median and",
        "    P(RF < IM) describe the same replicates.",
        "11. **Interval** — percentile method at (1 - level)/2 and 1 - (1 - level)/2, endpoints taken as order",
        "    statistics by the nearest-rank rule (the smallest replicate whose 1-based rank is at least p x reps).",
        "    No interpolation: an endpoint is a value the resampling actually produced.",
        "12. **Trim** — floor(0.01 x n) values dropped from EACH tail of the sorted log ratios, n counted AFTER the",
        "    RF = 0 exclusion. If the two tails would leave nothing, the trimmed mean is reported as not computable.",
        "13. **RF = 0** — no logarithm exists, so such events are excluded from the secondary log statistics ONLY,",
        "    and counted. They stay in the mean, in the median and in P(RF < IM).",
        "14. **Quintiles** — equal-count buckets by rank WITHIN the sample being reported: ascending, ties broken by",
        "    event id, bucket = floor(rank x 5 / n) clamped to 4. When n is not divisible by 5 the bucket sizes differ",
        "    by at most one and which buckets carry the extra event is decided by that formula and nothing else",
        "    (n = 7 gives 2, 1, 2, 1, 1; n = 11 gives 3, 2, 2, 2, 2). An event with no value never joins a quintile;",
        "    it goes to the `unknown` bucket, which is printed even when empty.",
        "15. **Spot for IM** — `spot_feed_entry` when present, otherwise `spot_parity_entry`. The source is recorded",
        "    per event and tallied in 9.2, with the parity-vs-close deviation quantiles beside it.",
        "16. **Sample nesting** — the primary (tradable) sample is a SUBSET of the secondary (all-quotable) sample.",
        "    An event in the primary sample is counted in both; `in_primary` / `in_secondary` in the event table are",
        "    the authoritative flags.",
        "17. **Unmeasurable events** — an event that passes the quotable gate but has no computable RF/IM is counted",
        "    and listed by id, never dropped and never defaulted. It counts toward sample membership, not toward n.",
        "18. **Random draws** — SplitMix64 seeded from the registered seed mixed with an FNV-1a hash of each analysis",
        "    name, so every interval reproduces exactly and adding an analysis does not move the others.",
        "19. **Rounding** — every comparison is made on unrounded `decimal` values. Numbers are formatted only when",
        "    written: 6 decimal places for ratios, proportions and moves, 2 for prices, 0 for market capitalisations.",
        "20. **Market cap** — as-of shares outstanding (the latest fact filed on or before the entry date) x the entry",
        "    close. No fact on or before that date means the `unknown` bucket, never an interpolated figure.",
        "21. **Retired v0 two-signal rule** — computed per event as a POST-HOC diagnostic and carried in the event",
        "    table as `v0_two_signal_would_quarantine`. It never affects inclusion (v2 section 2); section 9.2 reports",
        "    what it would have removed and that set's own mean(RF/IM)."
    ];

    /// <summary>
    /// Section 1.3: what was fetched, which quarters are deliverable, and what the subset rule
    /// therefore cut. Printed on EVERY run, covered or not — a coverage table produced only when
    /// something is missing would leave the reader of a final memo unable to tell that anyone looked.
    /// </summary>
    private static void CoverageSection(StringBuilder memo, C1Report report)
    {
        var coverage = report.Coverage;
        Line(memo, "### 1.3 Fetch coverage and the deliverable subset");
        Line(memo);
        Line(memo, "v2 section 3 pre-commits the deadline fallback: when the full universe x window has not completed,");
        Line(memo, "the deliverable is the most recent run of complete calendar quarters with 100% fetch coverage, and");
        Line(memo, "the memo labels itself PROVISIONAL pending the full-window rerun. A quarter is COVERED when every");
        Line(memo, "event in window and kept after dedup whose print date falls in it has a row in `option_measures.csv`");
        Line(memo, "AND a row in `closes.csv`. Any status counts — a recorded failure is a fetch that completed — so the");
        Line(memo, "error counts are columns below rather than grounds for withholding a quarter. A quarter with no");
        Line(memo, "events is covered vacuously; its event count is printed so that zero is visible on its face.");
        Line(memo);
        Line(memo, $"The subset is the maximal run of consecutive covered quarters ending at {coverage.Quarters[^1].Quarter}, the most recent");
        Line(memo, "quarter of the registered window. It is applied at gate 07b, before any measure is read, and it is a");
        Line(memo, "cut on the print date and nothing else: symbol-partial subsets are prohibited in every form.");
        Line(memo);

        if (coverage.NothingDeliverable)
        {
            Line(memo, $"**Deliverable subset: NONE.** {coverage.Quarters[^1].Quarter} is not fully fetched, so the run ending there is empty,");
            Line(memo, "every event was removed at gate 07b and this memo states NO VERDICT.");
        }
        else if (coverage.FullWindow)
        {
            Line(memo, $"**Deliverable subset: {coverage.Subset[0]}..{coverage.Subset[^1]} — the full registered window ({coverage.Subset.Count} quarters). This memo is FINAL.**");
        }
        else
        {
            Line(memo, $"**Deliverable subset: {coverage.Subset[0]}..{coverage.Subset[^1]} ({coverage.Subset.Count} of {coverage.Quarters.Count} quarters). This memo is PROVISIONAL.**");
            Line(memo, "Every statistic and split below is restricted to those quarters, and the full-window run remains owed");
            Line(memo, "whatever the verdict says.");
        }

        Line(memo);
        Line(memo, "| quarter | events | no option_measures row | no closes row | chain fetch_status=error | closes status not ok | covered | in subset |");
        Line(memo, "|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var quarter in coverage.Quarters)
        {
            var inSubset = coverage.Subset.Contains(quarter.Quarter, StringComparer.Ordinal);
            Line(memo, $"| {quarter.Quarter} | {quarter.Events} | {quarter.MissingChainRow} | {quarter.MissingClosesRow} | " +
                       $"{quarter.ChainFetchErrors} | {quarter.ClosesNotOk} | {YesNo(quarter.Covered)} | {YesNo(inSubset)} |");
        }
        Line(memo);

        Line(memo, $"- Events in window and kept after dedup with NO print date, so in no quarter's denominator: {coverage.EventsWithNoPrintDate}.");
        Line(memo, $"- Events in window and kept after dedup whose print date falls outside every quarter of the registered window: {coverage.EventsOutsideWindowQuarters}.");
        Line(memo, $"- `option_measures.fetch_status` over those events: {TallyText(coverage.ChainStatuses)}.");
        Line(memo, $"- `closes.status` over those events: {TallyText(coverage.ClosesStatuses)}.");
        Line(memo, $"  `{QuarterCoverage.NoRow}` is an event with no row in that table at all, which is the only thing that withholds a quarter.");
        Line(memo);
    }

    /// <summary>
    /// The economic cross-check of v2 section 1, printed under the verdict. The convention is stated
    /// in full every time: a sign agreement is only evidence if the reader can see which sign was
    /// expected, and "agrees" without the convention beside it is a claim the reader cannot check.
    /// </summary>
    private static void CrossCheckSection(StringBuilder memo, C1Report report)
    {
        var check = report.StraddleCrossCheck;
        Line(memo, "**Economic cross-check (v2 section 1).** The ATM straddle hold-through return at mids is measured on");
        Line(memo, "the LONG straddle: (exit mid - entry mid) / entry mid. A premium means the option was dear, so the long");
        Line(memo, "side loses on average — a NEGATIVE mean hold-through return agrees with PASS, and a non-negative one");
        Line(memo, "agrees with FAIL. Exactly zero agrees with FAIL, for the same reason a mean(RF/IM) of exactly 1 fails.");
        Line(memo, "It is a cross-check and never an input: the verdict above is unchanged by what this says.");
        Line(memo);

        if (check.Agreement == StraddleCrossCheck.NotComputable)
        {
            Line(memo, report.Verdict.Computed
                ? "Mean hold-through return over the primary sample: **not computable** (no event in the sample has both an entry and an exit mid), so the cross-check is **not computable**."
                : "There is no verdict to cross-check, so the sign comparison is **not computable**.");
        }
        else
        {
            Line(memo, $"Mean hold-through return over the primary sample = **{Num(check.Mean)}**, which **{check.Agreement}** with " +
                       $"the {(check.Pass ? "PASS" : "FAIL")} above.");
        }

        Line(memo);
    }

    /// <summary>
    /// Section 9.2's post-hoc reading of the retired v0 two-signal rule (v2 section 2). The count it
    /// would have removed and THAT SET'S OWN mean(RF/IM) beside the sample's is the measurement
    /// docs/LESSONS.md section 13 prescribes for a selection rule, made here on real data: a rule
    /// whose removed set sits far from the population it was cut from selected on something, and the
    /// only way to see that is to print both numbers.
    /// </summary>
    private static void V0TwoSignalSection(StringBuilder memo, C1Report report)
    {
        var d = report.V0TwoSignal;
        Line(memo, "- Retired v0 two-signal rule, computed as a POST-HOC DIAGNOSTIC and applied to nothing (v2 section 2).");
        Line(memo, "  Quoted verbatim from `V0TwoSignalRule.Describe()`:");
        Line(memo);
        Line(memo, "  ```");
        foreach (var line in V0TwoSignalRule.Describe().Replace("\r\n", "\n").Split('\n')) Line(memo, $"  {line}");
        Line(memo, "  ```");
        Line(memo);
        Line(memo, $"  Over the {d.Considered} event(s) gate 09 decided on, it would have removed **{d.WouldQuarantine}**; it could not be");
        Line(memo, $"  evaluated for {d.NotEvaluable} of them, which is a count here rather than a silent \"would not quarantine\".");

        if (d.PrimaryFlagged > 0)
        {
            Line(memo, $"  Over the primary sample's {d.PrimaryMembers} event(s) with a computable RF/IM it flags **{d.PrimaryFlagged}**, whose");
            Line(memo, $"  mean(RF/IM) is **{Num(d.PrimaryFlaggedMean)}** against the sample's **{Num(d.PrimaryMean)}**. A flagged set whose mean sits");
            Line(memo, "  well away from the sample's is the selection effect the retired rule was withdrawn for; the two numbers");
            Line(memo, "  are printed together so the comparison is the reader's to make.");
        }
        else
        {
            Line(memo, $"  Over the primary sample's {d.PrimaryMembers} event(s) with a computable RF/IM it flags none, so there is no");
            Line(memo, $"  removed-set mean to compare against the sample's {Num(d.PrimaryMean)}.");
        }

        Line(memo, "  Inclusion in this study is decided by gate 09 and gate 09 alone; this line removed nothing.");
    }

    /// <summary>
    /// What the v2 criterion establishes and what the retired v0 criterion did not. Printed on every
    /// run, passing or failing: it is a fact about the rules, not about the data, and it is the one
    /// caveat a reader cannot recover from any number in this memo.
    /// </summary>
    private static void CriterionSection(StringBuilder memo)
    {
        Line(memo, "### 9.3 What the criterion does and does not establish");
        Line(memo);
        Line(memo, "**The retired v0 criterion was satisfied by a fairly priced market.** For any move distribution whose");
        Line(memo, "median absolute move is below its mean absolute move — which is every symmetric one, and absolute");
        Line(memo, "returns are right-skewed — a straddle priced at exactly the mean absolute move, carrying no premium at");
        Line(memo, "all, already gives median(RF/IM) < 1 and P(RF < IM) > 0.5. Both of v0's conditions therefore measured");
        Line(memo, "the SHAPE of the ratio distribution and neither carried the claim. That was proved on a zero-premium");
        Line(memo, "synthetic population run through this same compute step, before any real data was seen, and it is why");
        Line(memo, "v2 amended the criterion rather than the result.");
        Line(memo);
        Line(memo, "**The v2 criterion has null value exactly 1.** Under fair per-event pricing — IM_i = E[RF_i] for every");
        Line(memo, "event — the tower property gives E[RF_i/IM_i] = E[E[RF_i | IM_i]/IM_i] = 1, so mean(RF/IM) estimates 1");
        Line(memo, "with no premium, whatever the shape of the move distribution. A premium is a statement about means, and");
        Line(memo, "this is the statistic that makes it one. The interval excluding 1 is what carries the claim.");
        Line(memo);
        Line(memo, "**median(RF/IM) and P(RF < IM) are shape readouts only.** They are reported in section 4 because C2");
        Line(memo, "sizing needs the win rate and the typical ratio, and they decide nothing here. The straddle");
        Line(memo, "hold-through return in section 7 speaks to the same premium in dollars rather than in ratios, and its");
        Line(memo, "sign is cross-checked against the verdict in section 5.");
        Line(memo);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
