using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Timing;

namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// WP5. Joins every table, applies the remaining gates in registered order with counts, computes the
/// primary statistics with the week-clustered bootstrap, the secondary statistics, the four
/// pre-registered splits and the straddle diagnostic, and writes <c>c1_event_table.csv</c> and the
/// memo <c>c1_memo.md</c>. The number is the number.
///
/// Two invariants this step exists to hold:
///
/// <list type="number">
/// <item><b>Zero silent drops.</b> Every event that reached this verb leaves a row in the event table
/// carrying the gate that removed it or its sample membership, and every gate leaves a
/// <see cref="GateCountRow"/> where considered - removed = remaining. A removed event is a row with a
/// reason, never an absence (docs/LESSONS.md §3).</item>
/// <item><b>Nothing is decided here that was not decided before the data was read.</b> Every choice
/// the pre-registration leaves open is fixed in <see cref="Statistics"/>, <see cref="ClusteredBootstrap"/>
/// and <see cref="C1Verdict"/>, and printed in the memo's section 1.1.</item>
/// </list>
///
/// <see cref="TimingQa.Evaluate"/> and <see cref="AsOf.SharesOutstanding"/> belong to WP2 and are
/// injected, so this package's tests substitute fakes and the verb picks up the real rules the moment
/// they land — there is no second copy of either rule here.
/// </summary>
public sealed class ComputeStep(
    Func<EventTimingRow, ClosesRow, TimingQaResult>? priceQa = null,
    Func<IEnumerable<SharesFactRow>, DateOnly, SharesFactRow?>? sharesAsOf = null,
    Func<string>? describePriceQa = null) : IStudyStep
{
    /// <summary>The <see cref="GateCountRow.Step"/> this verb owns. Its rows are replaced on a re-run, never appended twice.</summary>
    public const string StepName = "compute";

    private readonly Func<EventTimingRow, ClosesRow, TimingQaResult> priceQa = priceQa ?? TimingQa.Evaluate;
    private readonly Func<IEnumerable<SharesFactRow>, DateOnly, SharesFactRow?> sharesAsOf = sharesAsOf ?? AsOf.SharesOutstanding;
    private readonly Func<string> describePriceQa = describePriceQa ?? TimingQa.Describe;

    public string Verb => "compute";
    public string Description => "Apply the gates, compute the registered statistics and write the memo.";

    public Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!ComputeOptions.TryParse(args, out var options, out var usage))
        {
            context.Output.WriteLine(usage);
            return Task.FromResult(2);
        }

        return Task.FromResult(Run(context, options, cancellationToken));
    }

    /// <summary>1-based position of a gate in the registered order, which is what a gate count row records.</summary>
    public static int GateOrder(string gate)
    {
        for (var i = 0; i < Gates.InOrder.Count; i++)
        {
            if (string.Equals(Gates.InOrder[i], gate, StringComparison.Ordinal)) return i + 1;
        }
        return 0;
    }

    private int Run(StudyContext context, ComputeOptions options, CancellationToken cancellationToken)
    {
        var paths = context.Paths;
        var required = new (string Name, string Path)[]
        {
            ("universe.csv", paths.Universe),
            ("events.csv", paths.Events),
            ("shares_facts.csv", paths.SharesFacts),
            ("event_timing.csv", paths.EventTiming),
            ("option_measures.csv", paths.OptionMeasures),
            ("closes.csv", paths.Closes)
        };

        var missing = required.Where(r => !File.Exists(r.Path)).ToList();
        if (missing.Count > 0)
        {
            foreach (var file in missing) context.Output.WriteLine($"compute: required input {file.Name} is missing at {file.Path}.");
            context.Output.WriteLine("compute: run the earlier verbs first. Nothing was written.");
            return 1;
        }

        var universe = CsvFile.Read<UniverseRow>(paths.Universe);
        var events = CsvFile.Read<EventRow>(paths.Events);
        var sharesFacts = CsvFile.Read<SharesFactRow>(paths.SharesFacts);
        var timings = CsvFile.Read<EventTimingRow>(paths.EventTiming);
        var measures = CsvFile.Read<OptionMeasuresRow>(paths.OptionMeasures);
        var closes = CsvFile.Read<ClosesRow>(paths.Closes);
        var priorGateCounts = File.Exists(paths.GateCounts) ? CsvFile.Read<GateCountRow>(paths.GateCounts) : [];

        // A duplicate key is a data defect upstream, and picking one of the two rows would decide the
        // statistics by accident. Refuse rather than choose (docs/LESSONS.md §8).
        if (FirstDuplicate(events.Select(e => e.EventId)) is { } dupEvent) return Refuse(context, $"events.csv has more than one row for event_id '{dupEvent}'.");
        if (FirstDuplicate(timings.Select(t => t.EventId)) is { } dupTiming) return Refuse(context, $"event_timing.csv has more than one row for event_id '{dupTiming}'.");
        if (FirstDuplicate(closes.Select(c => c.EventId)) is { } dupClose) return Refuse(context, $"closes.csv has more than one row for event_id '{dupClose}'.");
        if (FirstDuplicate(measures.Select(m => m.EventId)) is { } dupMeasure) return Refuse(context, $"option_measures.csv has more than one row for event_id '{dupMeasure}'.");

        var kept = events.Where(e => e.KeptAfterDedup).OrderBy(e => e.EventId, StringComparer.Ordinal).ToList();
        var timingById = timings.ToDictionary(t => t.EventId, StringComparer.Ordinal);
        var closesById = closes.ToDictionary(c => c.EventId, StringComparer.Ordinal);
        var measuresById = measures.ToDictionary(m => m.EventId, StringComparer.Ordinal);
        var factsByCik = sharesFacts.GroupBy(f => f.Cik).ToDictionary(g => g.Key, g => (IReadOnlyList<SharesFactRow>)[.. g]);

        var works = new List<EventWork>(kept.Count);
        foreach (var @event in kept)
        {
            var work = new EventWork(@event)
            {
                Timing = timingById.GetValueOrDefault(@event.EventId),
                Closes = closesById.GetValueOrDefault(@event.EventId),
                Measures = measuresById.GetValueOrDefault(@event.EventId)
            };
            works.Add(work);
        }

        // The set that ENTERS compute: kept by the events verb and classified, un-quarantined, by the
        // timing verb. Events the timing step already removed keep a row in the event table — their
        // removal is tallied by that step at gate 07, so counting them again here would double-count.
        var entering = new List<EventWork>(works.Count);
        var stoppedBeforeCompute = 0;
        foreach (var work in works)
        {
            if (work.Timing is null)
            {
                work.StopGate = Gates.TimingClassified;
                work.AddNote("no row in event_timing.csv");
                stoppedBeforeCompute++;
            }
            else if (work.Timing.Quarantined)
            {
                work.StopGate = Gates.TimingClassified;
                work.AddNote($"quarantined by the timing step: {work.Timing.QuarantineReason ?? "no reason given"}");
                stoppedBeforeCompute++;
            }
            else
            {
                entering.Add(work);
            }
        }

        var counts = new List<GateCountRow>();

        var afterCloses = ApplyGate(entering, Gates.ClosesPresent, counts, work =>
        {
            if (work.Closes is null) return "no row in closes.csv";
            if (!string.Equals(work.Closes.Status, "ok", StringComparison.Ordinal)) return $"closes status '{work.Closes.Status}'";
            if (work.Closes.ClosePreEntry is null) return "close_pre_entry missing";
            if (work.Closes.CloseEntry is null) return "close_entry missing";
            if (work.Closes.CloseExit is null) return "close_exit missing";
            return null;
        });

        var afterQa = ApplyGate(afterCloses, Gates.PriceQa, counts, work =>
        {
            var result = this.priceQa(work.Timing!, work.Closes!);
            work.Qa = result;
            work.PriceQa = result.Quarantined ? "quarantined" : "ok";
            return result.Quarantined ? $"quarantined: {result.Reason ?? "no reason given"}" : null;
        });

        var afterPrice = ApplyGate(afterQa, Gates.MinimumPrice, counts, work =>
            work.Closes!.CloseEntry!.Value >= C1Registration.MinimumEntryPrice
                ? null
                : $"entry close below {C1Registration.MinimumEntryPrice.ToString(CultureInfo.InvariantCulture)}");

        var afterChain = ApplyGate(afterPrice, Gates.ChainFound, counts, work =>
        {
            if (work.Measures is null) return "no row in option_measures.csv";
            if (!string.Equals(work.Measures.FetchStatus, "ok", StringComparison.Ordinal)) return $"fetch status '{work.Measures.FetchStatus}'";
            if (work.Measures.Expiration is null) return "no expiration spanning the event";
            return null;
        });

        var afterQuotable = ApplyGate(afterChain, Gates.Quotable, counts, work =>
        {
            var call = work.Measures!.CallBidEntry;
            var put = work.Measures!.PutBidEntry;
            if (call is null || put is null) return "an ATM bid is missing";
            if (call.Value <= 0m && put.Value <= 0m) return "both ATM bids are zero";
            if (call.Value <= 0m) return "the ATM call bid is zero";
            if (put.Value <= 0m) return "the ATM put bid is zero";
            return null;
        });

        var afterTradable = ApplyGate(afterQuotable, Gates.Tradable, counts, work =>
        {
            var spread = work.Measures!.CombinedSpreadEntry;
            var mid = work.Measures!.StraddleMidEntry;
            if (spread is null) return "combined ATM spread missing";
            if (mid is null) return "straddle mid missing";
            return spread.Value <= C1Registration.TradableSpreadFraction * mid.Value
                ? null
                : "combined ATM spread wider than the tradable tier";
        });

        foreach (var work in works) Measure(work);

        ApplyMarketCaps(works, factsByCik);

        foreach (var work in afterQuotable)
        {
            work.InSecondary = true;
            work.Membership = "secondary";
        }

        foreach (var work in afterTradable)
        {
            work.InPrimary = true;
            work.Membership = "primary";
        }

        cancellationToken.ThrowIfCancellationRequested();

        var byId = works.ToDictionary(w => w.Event.EventId, StringComparer.Ordinal);
        var spreadFraction = C1Registration.TradableSpreadFraction.ToString("0.##", CultureInfo.InvariantCulture);

        var primary = BuildSample(
            "primary",
            "Primary sample — tradable tier",
            $"Survivors of gate 13: combined ATM spread <= {spreadFraction} x straddle mid at the entry snapshot. This is the sample the verdict is measured on.",
            afterTradable,
            options,
            cancellationToken,
            (eventId, split, label) => StampQuintile(byId[eventId], "primary", split, label));

        var secondary = BuildSample(
            "secondary",
            "Secondary sample — all quotable",
            "Survivors of gate 12: ATM call and put bid both above zero at the entry snapshot. A superset of the primary sample, reported as the registration's all-quotable tier.",
            afterQuotable,
            options,
            cancellationToken,
            (eventId, split, label) => StampQuintile(byId[eventId], "secondary", split, label));

        var verdict = C1Verdict.Decide(primary.Median, primary.Bootstrap?.ProportionLess);
        var qaDescription = this.describePriceQa();

        var orphans = OrphanCounts(timings, closes, measures, works.Select(w => w.Event.EventId).ToHashSet(StringComparer.Ordinal));

        var gateCounts = priorGateCounts.Where(r => !string.Equals(r.Step, StepName, StringComparison.Ordinal)).Concat(counts).ToList();
        var report = BuildReport(
            verdict, primary, secondary, gateCounts, required, universe, entering, timings, orphans,
            options, qaDescription, stoppedBeforeCompute, kept.Count);

        CsvFile.Write(paths.EventTable, works.Select(w => w.ToRow()));
        CsvFile.Write(paths.GateCounts, gateCounts);
        File.WriteAllText(paths.Memo, MemoWriter.Render(report), new UTF8Encoding(false));

        foreach (var row in counts)
        {
            context.Log($"gate {row.Order} {row.Gate}: considered {row.Considered}, removed {row.Removed}, remaining {row.Remaining}");
        }

        context.Log($"event table: {paths.EventTable} ({works.Count} rows)");
        context.Log($"memo: {paths.Memo}");
        context.Log(verdict.Line.Replace("**", ""));
        return 0;
    }

    private static int Refuse(StudyContext context, string message)
    {
        context.Output.WriteLine($"compute: {message} Nothing was written.");
        return 1;
    }

    private static string? FirstDuplicate(IEnumerable<string> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!seen.Add(key)) return key;
        }
        return null;
    }

    /// <summary>
    /// Applies one gate: survivors out, removed events stamped with the gate and the reason, and one
    /// <see cref="GateCountRow"/> whose considered - removed = remaining by construction. The reason
    /// breakdown rides on the row's note so the exclusion table says why, not just how many.
    /// </summary>
    private static List<EventWork> ApplyGate(
        List<EventWork> entering,
        string gate,
        List<GateCountRow> counts,
        Func<EventWork, string?> failureReason,
        string? gateNote = null)
    {
        var survivors = new List<EventWork>(entering.Count);
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var work in entering)
        {
            var reason = failureReason(work);
            if (reason is null)
            {
                survivors.Add(work);
                continue;
            }

            work.StopGate = gate;
            work.AddNote(reason);
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        var removed = entering.Count - survivors.Count;
        counts.Add(new GateCountRow(StepName, GateOrder(gate), gate, entering.Count, removed, entering.Count - removed, Note(gateNote, reasons)));
        return survivors;
    }

    /// <summary>At most eight distinct reasons are named; the rest are counted together so a long tail cannot crowd out the total.</summary>
    private static string? Note(string? gateNote, Dictionary<string, int> reasons)
    {
        var parts = new List<string>();
        if (gateNote is { Length: > 0 }) parts.Add(gateNote);

        var ordered = reasons.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        foreach (var reason in ordered.Take(8)) parts.Add($"{reason.Key}={reason.Value}");
        if (ordered.Count > 8) parts.Add($"other reasons={ordered.Skip(8).Sum(r => r.Value)}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// The per-event measures, for every event that reached this verb whatever gate removed it — a
    /// realized move is worth recording even for an event with no chain, and the event table is the
    /// audit surface.
    /// </summary>
    private static void Measure(EventWork work)
    {
        if (work.Closes is { CloseEntry: { } entry, CloseExit: { } exit } && entry != 0m)
        {
            work.RealizedMove = Math.Abs((exit / entry) - 1m);
        }

        if (work.Measures is { } m)
        {
            if (m.SpotFeedEntry is { } feed)
            {
                work.SpotForIm = feed;
                work.SpotSource = "feed";
            }
            else if (m.SpotParityEntry is { } parity)
            {
                work.SpotForIm = parity;
                work.SpotSource = "parity";
            }

            if (m.StraddleMidEntry is { } mid && work.SpotForIm is { } spot && spot > 0m)
            {
                work.ImpliedMove = mid / spot;
            }

            if (m.StraddleMidEntry is { } entryMid && entryMid > 0m)
            {
                if (m.StraddleMidExit is { } exitMid) work.StraddleReturn = (exitMid - entryMid) / entryMid;
                if (m.CombinedSpreadEntry is { } spread) work.SpreadFraction = spread / entryMid;
            }

            if (m.SpotParityEntry is { } paritySpot && work.Closes?.CloseEntry is { } close && close != 0m)
            {
                work.ParityCloseDeviation = Math.Abs(paritySpot - close) / close;
            }
        }

        if (work.RealizedMove is { } rf && work.ImpliedMove is { } im && im > 0m)
        {
            work.Ratio = rf / im;
            work.RfLessThanIm = rf < im;
            work.RfEqualsIm = rf == im;
        }
    }

    /// <summary>
    /// Market capitalisation from the as-of shares fact and the entry close. An event with no fact in
    /// force at its entry date keeps a null market cap and lands in the split's unknown bucket — the
    /// split is a readout, not a gate, so the verdict is unaffected.
    /// </summary>
    private void ApplyMarketCaps(List<EventWork> works, Dictionary<long, IReadOnlyList<SharesFactRow>> factsByCik)
    {
        var candidates = works.Where(w => w.Timing?.EntryDate is not null).ToList();

        foreach (var work in works.Where(w => w.Timing?.EntryDate is null))
        {
            work.AddNote("market cap unknown: the timing step resolved no entry date to be as-of");
        }

        foreach (var work in candidates)
        {
            var fact = this.sharesAsOf(factsByCik.GetValueOrDefault(work.Event.Cik, []), work.Timing!.EntryDate!.Value);
            if (fact is null) continue;
            work.SharesOutstanding = fact.Value;
            work.SharesAsOfFiled = fact.Filed;
            if (work.Closes?.CloseEntry is { } close) work.MarketCap = fact.Value * close;
        }
    }

    private static SampleReport BuildSample(
        string key,
        string title,
        string definition,
        List<EventWork> members,
        ComputeOptions options,
        CancellationToken cancellationToken,
        Action<string, string, string> stampQuintile)
    {
        var sample = new List<SampleEvent>(members.Count);
        var unmeasurable = new List<string>();
        foreach (var work in members)
        {
            if (work.Ratio is { } ratio && work.RfLessThanIm is { } less && work.RfEqualsIm is { } equal)
            {
                work.RatioMeasurable = true;
                sample.Add(new SampleEvent(
                    work.Event.EventId,
                    work.ClusterKey,
                    ratio,
                    less,
                    equal,
                    work.MarketCap,
                    work.SpreadFraction,
                    work.Measures?.DteCalendarDays,
                    work.Timing?.TimingClass ?? "unknown",
                    work.StraddleReturn));
            }
            else
            {
                unmeasurable.Add($"`{work.Event.EventId}` ({work.Event.Symbol}): {Unmeasurable(work)}");
            }
        }

        var ratios = sample.Select(e => e.Ratio).ToList();
        var bootstrap = ClusteredBootstrap.Run(sample, options.Replications, C1Registration.ConfidenceLevel,
            ClusteredBootstrap.SeedFor(options.Seed, $"{key}:overall"));
        cancellationToken.ThrowIfCancellationRequested();

        var marketCap = Statistics.Quintiles(sample, "market_cap", "Market-cap quintile",
            "As-of shares outstanding (the latest fact filed on or before the entry date) x entry close, ranked within this sample. Events with no fact are in `unknown`.",
            e => e.MarketCap, MemoWriter.BigNumber);
        var atmSpread = Statistics.Quintiles(sample, "atm_spread", "ATM-spread quintile",
            "Combined ATM spread / straddle mid at the entry snapshot, ranked within this sample. Events with neither figure are in `unknown`.",
            e => e.SpreadFraction, MemoWriter.Ratio);

        // The quintile a sample ranked each event into goes back onto its event-table row, so the
        // memo's split tables can be recomputed from that file rather than taken on trust.
        foreach (var bucket in marketCap.Buckets)
        {
            foreach (var member in bucket.Events) stampQuintile(member.EventId, marketCap.Key, bucket.Label);
        }

        foreach (var bucket in atmSpread.Buckets)
        {
            foreach (var member in bucket.Events) stampQuintile(member.EventId, atmSpread.Key, bucket.Label);
        }

        var splits = new List<SplitReport>
        {
            Report(key, marketCap, options, cancellationToken),
            Report(key, atmSpread, options, cancellationToken),
            Report(key, Statistics.DteSplit(sample, C1Registration.ShortDteThresholdDays), options, cancellationToken),
            Report(key, Statistics.TimingSplit(sample), options, cancellationToken)
        };

        var clusters = sample.Select(e => e.ClusterKey).Distinct(StringComparer.Ordinal).Count();

        // Counted from the timing row rather than by sniffing the key's spelling: an earnings week that
        // happened to be written in the key's own shape would otherwise be miscounted as a singleton.
        var measurable = sample.Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
        var singletons = members.Count(w => measurable.Contains(w.Event.EventId) && string.IsNullOrWhiteSpace(w.Timing?.EarningsWeek));
        var spotSources = members
            .GroupBy(w => w.SpotSource, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Tally(g.Key, g.Count()))
            .ToList();

        return new SampleReport(
            key,
            title,
            definition,
            members.Count,
            sample.Count,
            unmeasurable,
            clusters,
            singletons,
            Statistics.Median(ratios),
            Statistics.ProportionLess(sample),
            sample.Count(e => e.RfLessThanIm),
            Statistics.TieCount(sample),
            bootstrap,
            Statistics.Logs(sample, C1Registration.TrimFraction),
            Statistics.Straddle(sample),
            spotSources,
            splits);
    }

    private static string Unmeasurable(EventWork work) =>
        work.RealizedMove is null
            ? "no realized move (the official closes are missing)"
            : work.ImpliedMove is null
                ? work.SpotForIm is null
                    ? "no implied move (neither a feed spot nor a parity spot at the entry snapshot)"
                    : "no implied move (no straddle mid at the entry snapshot, or a non-positive spot)"
                : "no ratio (the implied move is not positive)";

    private static SplitReport Report(string sampleKey, SplitDefinition split, ComputeOptions options, CancellationToken cancellationToken)
    {
        var buckets = new List<SplitBucketReport>(split.Buckets.Count);
        foreach (var bucket in split.Buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bootstrap = ClusteredBootstrap.Run(bucket.Events, options.Replications, C1Registration.ConfidenceLevel,
                ClusteredBootstrap.SeedFor(options.Seed, $"{sampleKey}:{split.Key}:{bucket.Label}"));
            buckets.Add(new SplitBucketReport(
                bucket.Label,
                bucket.Range,
                bucket.Events.Count,
                bucket.Events.Select(e => e.ClusterKey).Distinct(StringComparer.Ordinal).Count(),
                Statistics.Median([.. bucket.Events.Select(e => e.Ratio)]),
                bootstrap?.Median,
                Statistics.ProportionLess(bucket.Events),
                bootstrap?.ProportionLess));
        }

        return new SplitReport(split.Key, split.Title, split.Rule, buckets);
    }

    /// <summary>
    /// Records the quintile a sample ranked one event into. Quintiles are ranked WITHIN a sample, so
    /// an event in both samples can legitimately carry two different labels; both are written.
    /// </summary>
    private static void StampQuintile(EventWork work, string sample, string split, string label)
    {
        switch (sample, split)
        {
            case ("primary", "market_cap"): work.MarketCapQuintilePrimary = label; break;
            case ("secondary", "market_cap"): work.MarketCapQuintileSecondary = label; break;
            case ("primary", "atm_spread"): work.SpreadQuintilePrimary = label; break;
            case ("secondary", "atm_spread"): work.SpreadQuintileSecondary = label; break;
        }
    }

    private static C1Report BuildReport(
        C1Verdict verdict,
        SampleReport primary,
        SampleReport secondary,
        List<GateCountRow> gateCounts,
        (string Name, string Path)[] inputs,
        List<UniverseRow> universe,
        List<EventWork> entering,
        List<EventTimingRow> timings,
        List<Tally> orphans,
        ComputeOptions options,
        string priceQaDescription,
        int stoppedBeforeCompute,
        int keptAfterDedup)
    {
        var recognised = gateCounts.Where(r => GateOrder(r.Gate) > 0).OrderBy(r => GateOrder(r.Gate)).ToList();
        var unrecognised = gateCounts.Where(r => GateOrder(r.Gate) == 0).ToList();
        var withoutCounts = Gates.InOrder.Where(g => !gateCounts.Any(r => string.Equals(r.Gate, g, StringComparison.Ordinal))).ToList();

        var timingGateRows = gateCounts.Where(r => string.Equals(r.Gate, Gates.TimingClassified, StringComparison.Ordinal)).ToList();
        var priceQaQuarantined = gateCounts
            .Where(r => string.Equals(r.Step, StepName, StringComparison.Ordinal) && string.Equals(r.Gate, Gates.PriceQa, StringComparison.Ordinal))
            .Sum(r => r.Removed);

        // The last row wins: a re-run of the timing verb appends a fresh tally rather than editing one.
        var quarantine = timingGateRows.Count == 0
            ? new QuarantineRate(false, 0, 0, priceQaQuarantined, null,
                $"gate_counts.csv has no row for {Gates.TimingClassified}, so the denominator (events considered at gate 07) is not recorded. It is absent, not zero.")
            : Rate(timingGateRows[^1], priceQaQuarantined);

        var measured = new MeasuredContext(
            entering.Where(w => w.Measures is not null)
                .GroupBy(w => w.Measures!.SnapshotKind, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new Tally(g.Key, g.Count())).ToList(),
            entering.Where(w => w.Measures is not null)
                .GroupBy(w => w.Measures!.SnapshotTimeEt ?? "(none recorded)", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new Tally(g.Key, g.Count())).ToList(),
            Statistics.Quantiles([.. entering.Where(w => w.ParityCloseDeviation is not null).Select(w => w.ParityCloseDeviation!.Value)]),
            [.. timingGateRows.Where(r => !string.IsNullOrEmpty(r.Note)).Select(r => r.Note!)],
            timings.Where(t => t.Quarantined)
                .GroupBy(t => t.QuarantineReason ?? "(no reason given)", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new Tally(g.Key, g.Count())).ToList(),
            orphans,
            universe.Count,
            universe.Count(u => u.Eligible));

        var warnings = new List<string>();
        if (withoutCounts.Count > 0)
        {
            warnings.Add($"{withoutCounts.Count} registered gate(s) have no row in gate_counts.csv; the exclusion table shows them as absent rather than as zero.");
        }

        if (primary.UnmeasurableEvents.Count > 0 || secondary.UnmeasurableEvents.Count > 0)
        {
            warnings.Add($"{primary.UnmeasurableEvents.Count} primary and {secondary.UnmeasurableEvents.Count} secondary sample member(s) have no computable RF/IM; they are listed in section 4 and carried in the event table.");
        }

        return new C1Report(
            verdict,
            primary,
            secondary,
            recognised,
            withoutCounts,
            unrecognised,
            quarantine,
            [.. inputs.Select(i => new InputFile(i.Name, RowCount(i.Path), Sha256(i.Path)))],
            measured,
            keptAfterDedup,
            entering.Count,
            stoppedBeforeCompute,
            options.Replications,
            options.Seed,
            priceQaDescription,
            warnings);
    }

    private static QuarantineRate Rate(GateCountRow gate07, int priceQaQuarantined)
    {
        if (gate07.Considered <= 0)
        {
            return new QuarantineRate(false, gate07.Considered, gate07.Removed, priceQaQuarantined, null,
                "gate 07 considered no events, so the quarantine rate has no denominator.");
        }

        return new QuarantineRate(true, gate07.Considered, gate07.Removed, priceQaQuarantined,
            (decimal)(gate07.Removed + priceQaQuarantined) / gate07.Considered, null);
    }

    /// <summary>
    /// Rows in the downstream tables that name an event no longer kept after dedup. They contribute
    /// nothing to the statistics, and they are counted here rather than passed over in silence: an
    /// unexpected orphan count is how a broken join announces itself.
    /// </summary>
    private static List<Tally> OrphanCounts(
        List<EventTimingRow> timings,
        List<ClosesRow> closes,
        List<OptionMeasuresRow> measures,
        HashSet<string> keptIds)
    {
        var orphans = new List<Tally>();
        Add("event_timing.csv", timings.Count(t => !keptIds.Contains(t.EventId)));
        Add("closes.csv", closes.Count(c => !keptIds.Contains(c.EventId)));
        Add("option_measures.csv", measures.Count(m => !keptIds.Contains(m.EventId)));
        return orphans;

        void Add(string name, int count)
        {
            if (count > 0) orphans.Add(new Tally(name, count));
        }
    }

    private static int RowCount(string path)
    {
        var records = CsvFile.ParseRecords(File.ReadAllText(path));
        return Math.Max(0, records.Count - 1);
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>One event as it is carried through the gates. Mutable on purpose: the gates stamp it in order.</summary>
    private sealed class EventWork(EventRow @event)
    {
        public const string UnknownWeekPrefix = "unknown-week:";

        public EventRow Event { get; } = @event;
        public EventTimingRow? Timing { get; init; }
        public ClosesRow? Closes { get; init; }
        public OptionMeasuresRow? Measures { get; init; }

        public string StopGate { get; set; } = "";
        public string PriceQa { get; set; } = "not_reached";
        public TimingQaResult? Qa { get; set; }
        public string SpotSource { get; set; } = "none";
        public decimal? SpotForIm { get; set; }
        public decimal? RealizedMove { get; set; }
        public decimal? ImpliedMove { get; set; }
        public decimal? Ratio { get; set; }
        public bool? RfLessThanIm { get; set; }
        public bool? RfEqualsIm { get; set; }
        public decimal? StraddleReturn { get; set; }
        public decimal? SpreadFraction { get; set; }
        public decimal? ParityCloseDeviation { get; set; }
        public decimal? SharesOutstanding { get; set; }
        public DateOnly? SharesAsOfFiled { get; set; }
        public decimal? MarketCap { get; set; }
        public string Membership { get; set; } = "";
        public bool InPrimary { get; set; }
        public bool InSecondary { get; set; }
        public bool RatioMeasurable { get; set; }
        public string? MarketCapQuintilePrimary { get; set; }
        public string? MarketCapQuintileSecondary { get; set; }
        public string? SpreadQuintilePrimary { get; set; }
        public string? SpreadQuintileSecondary { get; set; }

        private readonly List<string> notes = [];

        public void AddNote(string note) => notes.Add(note);

        /// <summary>The bootstrap cluster. An event with no earnings week is its own singleton, counted and reported.</summary>
        public string ClusterKey =>
            string.IsNullOrWhiteSpace(Timing?.EarningsWeek) ? UnknownWeekPrefix + Event.EventId : Timing!.EarningsWeek!;

        public C1EventRow ToRow() => new(
            Event.EventId,
            Event.Symbol,
            Event.Cik,
            Event.FilingDate,
            Event.AcceptanceEt,
            Timing?.TimingClass ?? "",
            Timing?.PrintDate,
            Timing?.EntryDate,
            Timing?.ExitDate,
            Timing?.EarningsWeek,
            ClusterKey,
            Closes?.Status ?? "",
            Closes?.ClosePreEntry,
            Closes?.CloseEntry,
            Closes?.CloseExit,
            Closes?.MedianAbsReturn20,
            PriceQa,
            Qa?.Reason,
            Qa?.PreEntryMove,
            Qa?.EventMove,
            Measures?.Root,
            Measures?.Expiration,
            Measures?.DteCalendarDays,
            Measures?.SnapshotKind,
            Measures?.SnapshotTimeEt,
            Measures?.SpotFeedEntry,
            Measures?.SpotParityEntry,
            SpotSource,
            SpotForIm,
            ParityCloseDeviation,
            Measures?.AtmStrike,
            Measures?.CallBidEntry,
            Measures?.PutBidEntry,
            Measures?.StraddleMidEntry,
            Measures?.CombinedSpreadEntry,
            SpreadFraction,
            Measures?.StraddleMidExit,
            StraddleReturn,
            RealizedMove,
            ImpliedMove,
            Ratio,
            RfLessThanIm,
            SharesOutstanding,
            SharesAsOfFiled,
            MarketCap,
            MarketCapQuintilePrimary,
            MarketCapQuintileSecondary,
            SpreadQuintilePrimary,
            SpreadQuintileSecondary,
            DteBucket(),
            TimingBucket(),
            StopGate,
            Membership,
            InPrimary,
            InSecondary,
            RatioMeasurable,
            RealizedMove == 0m,
            notes.Count == 0 ? null : string.Join("; ", notes));

        private string DteBucket() => Measures?.DteCalendarDays is { } dte
            ? dte <= C1Registration.ShortDteThresholdDays ? $"DTE <= {C1Registration.ShortDteThresholdDays}" : $"DTE > {C1Registration.ShortDteThresholdDays}"
            : "unknown";

        private string TimingBucket() =>
            string.Equals(Timing?.TimingClass, "BMO", StringComparison.OrdinalIgnoreCase) ? "BMO"
            : string.Equals(Timing?.TimingClass, "AMC", StringComparison.OrdinalIgnoreCase) ? "AMC"
            : "other";
    }
}

/// <summary>The compute verb's command line. Both options default to the registered values.</summary>
public sealed record ComputeOptions(int Replications, int Seed)
{
    public static bool TryParse(IReadOnlyList<string> args, out ComputeOptions options, out string usage)
    {
        var replications = C1Registration.BootstrapReplications;
        var seed = C1Registration.BootstrapSeed;
        usage = "";

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--reps" when i + 1 < args.Count && int.TryParse(args[i + 1], out var reps) && reps > 0:
                    replications = reps;
                    i++;
                    break;
                case "--seed" when i + 1 < args.Count && int.TryParse(args[i + 1], out var parsedSeed):
                    seed = parsedSeed;
                    i++;
                    break;
                default:
                    options = new ComputeOptions(replications, seed);
                    usage = $"compute: cannot read the option '{args[i]}'.\n" +
                            "usage: compute [--reps N] [--seed N]\n" +
                            $"  --reps N   bootstrap replications (default {C1Registration.BootstrapReplications}, the registered value)\n" +
                            $"  --seed N   bootstrap seed (default {C1Registration.BootstrapSeed}, the registered value)";
                    return false;
            }
        }

        options = new ComputeOptions(replications, seed);
        return true;
    }
}
