using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>Knobs for gap detection, bound from the <c>Gaps</c> configuration section.</summary>
public sealed class GapOptions
{
    /// <summary>
    /// Longest window a single job's gap analysis will cover. Generous relative to
    /// <see cref="Recording.CoverageOptions.MaxWindowDays"/> (92): this scans already-landed rows in
    /// <c>research.bars</c> and <c>research.backfill_requests</c> — one row per bar or per slice, at
    /// most tens of thousands per job — not the raw per-tick event tables coverage measures (~7M
    /// rows/day), so a multi-decade audit of one job's whole history is cheap enough to allow outright
    /// rather than something an operator has to page through.
    /// </summary>
    public int MaxWindowDays { get; set; } = 20_000; // ~55 years — comfortably spans 1990-2035.

    /// <summary>
    /// How far back a TOP-UP job's default window reaches when the caller does not specify one.
    /// </summary>
    /// <remarks>
    /// A top-up job's own <c>target_from</c> is not a meaningful lower bound for gap checking: it never
    /// walks backward past its most recent bucket, by design (<see cref="BackfillPlanner.PlanTopUp"/>),
    /// so treating its declared range the way a historical job's is treated would report years of
    /// <see cref="GapBasis.NotRequested"/> that are entirely expected and none of this job's business
    /// to fill — that range belongs to its sibling historical job instead.
    /// </remarks>
    public int TopUpDefaultLookbackDays { get; set; } = 2;

    /// <summary>Caps merged ranges reported per job, so a job with pathologically scattered shortfalls cannot blow up the response.</summary>
    public int MaxRangesPerJob { get; set; } = 500;

    /// <summary>
    /// How far back from <c>now</c> the audited window stops, so an in-progress session is not
    /// judged against an expectation it has not had time to meet.
    /// </summary>
    /// <remarks>
    /// A top-up job's data necessarily lags the clock — the planner anchors on a 15-minute bucket and
    /// the request then has to be claimed, paced, and landed — so measuring the session that is
    /// running right now against its full elapsed minutes reported
    /// <see cref="GapBasis.SucceededButAbsent"/>, the alarm state that is supposed to mean "the
    /// checkpoint lied", on every poll during market hours. An hour comfortably exceeds one top-up
    /// bucket plus its queue time.
    /// <para>
    /// The excluded tail is NOT dropped: it is reported as an unaudited range on the job, so
    /// "not checked yet" cannot pass for "checked and clean". That distinction is the entire point of
    /// the grace period rather than simply widening the shortfall tolerance.
    /// </para>
    /// </remarks>
    public int InProgressGraceMinutes { get; set; } = 60;
}

/// <summary>
/// Why a job's gap analysis could, or could not, run. Mirrors <c>CoverageBasisStatus</c>'s
/// refuse-rather-than-guess discipline: an unchecked job is reported WITH a reason, never omitted.
/// </summary>
public static class GapCheckStatus
{
    /// <summary>The analysis ran; <see cref="JobGapReport.Gaps"/> is the true finding (possibly empty, meaning fully covered).</summary>
    public const string Checked = "checked";

    /// <summary>
    /// The job's own <c>con_id</c> is NULL — it walks multiple contracts over time (e.g. the ES roll;
    /// see <see cref="BackfillJob.ConId"/>) — and there is no single conId to check
    /// <c>research.bars</c> against. Deliberately not silently skipped; see <see cref="GapDetector"/>.
    /// </summary>
    public const string MultiContractJobUnsupported = "multi-contract-job-unsupported";

    /// <summary>The job names an <c>instrument_id</c> that is not (or no longer) in <c>research.instruments</c>.</summary>
    public const string UnknownInstrument = "unknown-instrument";

    /// <summary>
    /// The instrument has no entry in <c>InstrumentCalendars</c>; extend that mapping rather than
    /// guess a calendar. Guessing does not fail loudly — it produces a confident report about the
    /// wrong sessions, which is how a 780-minute index-OPTION expectation came to be applied to VIX
    /// index bars and flagged every correct session as missing.
    /// </summary>
    public const string NoCalendarMapping = "no-calendar-mapping";

    /// <summary>
    /// The window is real but the session calendar produced NO expectation unit inside it, so this
    /// run verified nothing.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Checked"/> deliberately. An empty <see cref="JobGapReport.Gaps"/> list
    /// was emitted identically for "every session in this window was verified complete" and "there
    /// was nothing to verify" — a weekend-only top-up window, a calendar whose
    /// <c>effectiveFrom</c> postdates the window, an instrument mapped to a calendar that generates
    /// nothing here. The first is the strongest statement this report can make and the second is the
    /// weakest, and they must not render the same.
    /// </remarks>
    public const string NoExpectationUnits = "no-expectation-units";

    /// <summary>The job's bar size names no per-minute or per-trading-date expectation this detector knows how to compute.</summary>
    public const string UnsupportedBarSize = "unsupported-bar-size";

    /// <summary>
    /// The requested window, after clamping to the job's own range and head timestamp, is empty or
    /// exceeds <see cref="GapOptions.MaxWindowDays"/>.
    /// </summary>
    public const string WindowRejected = "window-rejected";

    /// <summary>The analysis itself threw; the job is still reported (with the exception message) rather than vanishing from the response.</summary>
    public const string Error = "error";
}

/// <summary>Known values for <see cref="UnauditedRange.Reason"/>.</summary>
public static class GapAuditReasons
{
    /// <summary>The caller's own <c>from</c>/<c>to</c> narrowed the window below what the job covers.</summary>
    public const string CallerNarrowedWindow = "caller-narrowed-window";

    /// <summary>Inside <see cref="GapOptions.InProgressGraceMinutes"/> of now: too recent to judge.</summary>
    public const string InProgress = "in-progress";

    /// <summary>This job could not be analyzed at all; see its <see cref="JobGapReport.CheckStatus"/>.</summary>
    public const string JobNotChecked = "job-not-checked";

    /// <summary>
    /// The instrument trades here but this platform has no session definition for the window, so no
    /// expectation exists. See <c>InstrumentCalendars</c>'s unmodelled windows — VIX's overnight
    /// session is the current instance.
    /// </summary>
    public const string NoSessionDefinition = "no-session-definition";

    /// <summary>Reported only at the series level: no job in the series audited this span.</summary>
    public const string NoJobAuditedIt = "no-job-audited-it";
}

/// <summary>
/// A span some job CLAIMS to cover that this run did not actually audit, and why.
/// </summary>
/// <remarks>
/// The counterweight to an empty gap list. Every other field in this report describes what was
/// checked; without this one, a window that was silently never examined is indistinguishable from a
/// window that was examined and found clean — which is the failure mode this whole package exists
/// to prevent, turned on the report itself.
/// </remarks>
public sealed record UnauditedRange(DateTimeOffset From, DateTimeOffset To, string Reason);

/// <summary>One job's gap-detection outcome.</summary>
/// <param name="JobStatus"><c>research.backfill_jobs.status</c> — lets a caller tell "complete but has real gaps" apart from "still running, gaps are expected".</param>
/// <param name="CheckStatus">See <see cref="GapCheckStatus"/>.</param>
/// <param name="From">The window actually analyzed, after clamping. Null when <see cref="CheckStatus"/> is not <see cref="GapCheckStatus.Checked"/> (or <see cref="GapCheckStatus.WindowRejected"/> partially populated).</param>
/// <param name="NominalFrom">
/// The start of the span this job claims to cover, before the caller's window or any grace period
/// narrowed it. Reported next to <paramref name="From"/> so the difference between "what this job is
/// responsible for" and "what this run looked at" is visible rather than implied.
/// </param>
/// <param name="NominalTo">The end of the claimed span. See <paramref name="NominalFrom"/>.</param>
/// <param name="HeadTimestampUtc">
/// The cached <c>reqHeadTimeStamp</c> result this job's lower bound was clamped to, if one has been
/// probed. Reported so a caller can see WHY a job's own <c>target_from</c> is not where checking
/// actually started — the pre-head range is not a gap, it is history that does not exist.
/// </param>
/// <param name="UnitsChecked">
/// How many expectation units (session windows, or trading dates for a daily job) this run actually
/// evaluated. Zero with an empty <paramref name="Gaps"/> list means nothing was verified, not that
/// everything passed — which is why <see cref="GapCheckStatus.NoExpectationUnits"/> exists.
/// </param>
/// <param name="Truncated">True when more merged ranges existed than <see cref="GapOptions.MaxRangesPerJob"/> allows; <see cref="Gaps"/> is a prefix, not the whole finding.</param>
/// <param name="Unaudited">Spans of the claimed window this run did not examine. See <see cref="UnauditedRange"/>.</param>
public sealed record JobGapReport(
    long JobId,
    string JobName,
    string JobStatus,
    string CheckStatus,
    string? CheckDetail,
    DateTimeOffset? From,
    DateTimeOffset? To,
    DateTimeOffset? NominalFrom,
    DateTimeOffset? NominalTo,
    DateTimeOffset? HeadTimestampUtc,
    int UnitsChecked,
    bool Truncated,
    IReadOnlyList<GapRange> Gaps,
    IReadOnlyList<UnauditedRange> Unaudited);

/// <summary>
/// Whether every instant SOME job claims to cover was actually audited by some job, for one
/// (contract, whatToShow, barSize, useRth) series.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only check in the report computed across jobs rather than within one, and it is the
/// only one that can see a seam.</b> A series is normally covered by a pair — a historical job
/// walking backward and a top-up job holding the recent tail — and each was measured against its own
/// window and reported clean. Nothing measured the space between them, so when the historical
/// ceiling stopped advancing (it was pinned to a <c>target_to</c> frozen at the job's creation day)
/// the un-audited band grew by one day per day of operation while both jobs kept reporting
/// <c>checked</c> with zero gaps. Three entirely missing SPX sessions inside that band produced a
/// clean bill of health from both, and no query existed that could surface them.
/// </para>
/// <para>
/// <b>Computed over the whole series, never over the caller's selection.</b> A <c>jobId</c> filter
/// narrows what <see cref="GapReport.Jobs"/> displays and nothing else: reconciling over the filtered
/// subset let the narrowest possible question return the healthiest possible answer, because an
/// unselected sibling's claim never entered the subtraction and so had nothing left to leave over.
/// <paramref name="JobNames"/> therefore names every member of the series and may name a job that is
/// not in <see cref="GapReport.Jobs"/> — that is the point, not an inconsistency.
/// </para>
/// </remarks>
/// <param name="JobNames">Every job in the series, whether or not it is displayed in <see cref="GapReport.Jobs"/>.</param>
/// <param name="Reconciled">True when <paramref name="Unaudited"/> is empty: every claimed instant was examined.</param>
public sealed record SeriesReconciliation(
    int? ConId,
    string WhatToShow,
    string BarSize,
    bool UseRth,
    IReadOnlyList<string> JobNames,
    bool Reconciled,
    IReadOnlyList<UnauditedRange> Unaudited);

/// <summary>What <c>GET /research/backfill/gaps</c> answers with.</summary>
/// <param name="Jobs">
/// One entry per job the caller asked about — narrowed by <c>jobId</c> when one was given.
/// </param>
/// <param name="Series">
/// The cross-job reconciliation, one entry per data series. A caller asking "is this backfill
/// actually complete" must read this as well as <paramref name="Jobs"/>: a per-job report can only
/// ever be silent about a range no job looked at. <b>Not narrowed by <c>jobId</c></b> — see
/// <see cref="SeriesReconciliation"/> — so it can name jobs absent from <paramref name="Jobs"/>.
/// </param>
public sealed record GapReport(IReadOnlyList<JobGapReport> Jobs, IReadOnlyList<SeriesReconciliation> Series);

/// <summary>
/// Compares each backfill job's OWN declared range — expected sessions × expected bars, from the
/// session calendar — against what actually landed in <c>research.bars</c>, and reports the mismatch
/// as labelled <see cref="GapRange"/>s. The roadmap's Phase 2 item (g): "gap detection ... empty-or-
/// explained is an acceptance criterion."
/// </summary>
/// <remarks>
/// <para>
/// <b>The negative claim, and where it is measured.</b> This detector's whole reason to exist is the
/// claim "nothing is silently missing". That claim is measured against <c>research.bars</c> (for
/// intraday bar sizes, via bar COUNTS per session window; for daily bar sizes, via trading-DATE
/// presence), joined the safe direction: the EXPECTED set — every session-unit the session calendar
/// says should exist for a job's window — is built first, from
/// <see cref="TradingStuff.ResearchContracts.ISessionClock"/>, and reality is then LEFT-joined onto it
/// (<see cref="BackfillStore.GetLandedBarCountsAsync"/>'s own remarks name the exact SQL mechanism).
/// A unit nothing landed for gets a zero rather than being absent from the result set, so it is
/// reported with a <see cref="GapBasis"/>, never silently dropped. The same discipline applies one
/// level up: a job with NO request rows, NO resolved conId, or an instrument this detector cannot map
/// to a calendar is never left out of <see cref="GapReport.Jobs"/> — it appears with a
/// <see cref="GapCheckStatus"/> other than <see cref="GapCheckStatus.Checked"/> explaining why, the
/// same way <c>BackfillStore.GetStatusAsync</c> reports a zero-row job at 0% rather than omitting it.
/// A totally unplanned job (zero request rows at all) therefore reports
/// <see cref="GapBasis.NotRequested"/> across its ENTIRE window — a query that started from
/// <c>research.backfill_requests</c> instead could not have produced anything for such a job, because
/// there would be nothing to group.
/// </para>
/// <para>
/// <b>Sessions come from <see cref="TradingStuff.ResearchContracts.ISessionClock"/> directly, never
/// from <c>research.sessions</c>.</b> <see cref="Recording.CoverageMonitor"/> reads the persisted table
/// and cross-checks it against the generator because ITS numerator query needs session boundaries as
/// SQL join parameters against a live, high-volume tick stream, and treats the persisted row as the
/// audited value. This detector has no analogous reason to prefer the table — its own queries already
/// take freshly generated session boundaries as parameters — so it goes straight to the one documented
/// session authority rather than adding a second consumer of the persisted table with its own
/// reconciliation logic. <c>SessionClock</c>'s own remarks are explicit that this is the intended
/// use: the persisted table exists to catch up to the generator, not the other way around.
/// </para>
/// </remarks>
public sealed class GapDetector(
    BackfillStore store,
    ISessionClock clock,
    IOptions<GapOptions> gapOptions,
    IOptions<BackfillOptions> backfillOptions,
    ILogger<GapDetector> logger)
{
    private readonly GapOptions _options = gapOptions.Value;
    private readonly BackfillOptions _backfillOptions = backfillOptions.Value;

    public async Task<GapReport> GetReportAsync(
        long? jobId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var jobs = await store.GetStatusAsync(_backfillOptions.MaxAttempts, cancellationToken);
        var selected = jobId is { } id
            ? jobs.Where(j => j.JobId == id).ToArray()
            : jobs.ToArray();

        // A jobId filter narrows what is DISPLAYED; it must not narrow what is CHECKED.
        // SeriesReconciliation is the only cross-job statement this report makes, and computing it
        // from the filtered subset made it a series-wide claim derived from one member of the series:
        // an unselected sibling's claim never entered `claimed`, so subtracting audited from claimed
        // had nothing to subtract and a series with a known missing session reported Reconciled=true.
        // Every job sharing a selected job's series key is therefore analyzed as well — the set is
        // bounded and small (a historical job and its top-up, in practice) — and the selection is
        // applied to the returned Jobs list at the end instead.
        BackfillJobStatus[] analyzed;

        if (jobId is null)
        {
            analyzed = selected;
        }
        else
        {
            var seriesKeys = selected.Select(SeriesKey).ToHashSet();
            analyzed = [.. jobs.Where(j => seriesKeys.Contains(SeriesKey(j)))];
        }

        var reports = new List<JobGapReport>(analyzed.Length);
        var now = DateTimeOffset.UtcNow;

        foreach (var job in analyzed)
        {
            var nominal = NominalWindow(job, headUtc: null, now);

            try
            {
                reports.Add(await BuildJobReportAsync(job, from, to, now, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One job's analysis failing must not blank the whole response, and must not silently
                // drop this job either — the same per-job isolation BackfillCoordinator.PlanAsync
                // applies, applied here so a bug in one job's calendar mapping cannot hide every other
                // job's real gaps behind an unhandled exception. The job's claimed window is still
                // reported as UNAUDITED, because "the analysis threw" and "the analysis found nothing"
                // rendered identically before: both produced an empty gap list.
                logger.LogError(ex, "Gap analysis for job {Job} failed.", job.Name);
                reports.Add(new JobGapReport(
                    job.JobId, job.Name, job.Status, GapCheckStatus.Error, ex.Message,
                    null, null, nominal.From, nominal.To, null, 0, false, [],
                    [new UnauditedRange(nominal.From, nominal.To, GapAuditReasons.JobNotChecked)]));
            }
        }

        var series = Reconcile(analyzed, reports, now, now.AddMinutes(-_options.InProgressGraceMinutes));
        var selectedIds = selected.Select(j => j.JobId).ToHashSet();

        return new GapReport([.. reports.Where(r => selectedIds.Contains(r.JobId))], series);
    }

    /// <summary>
    /// What makes two jobs jointly responsible for the same rows of <c>research.bars</c>, and
    /// therefore for the same span of history.
    /// </summary>
    /// <remarks>
    /// The one definition of a series, used both to widen a filtered query to the whole series and to
    /// group the reconciliation. Two copies of it would drift, and a widening that disagreed with the
    /// grouping would silently reintroduce a partially-reconciled series under a different name.
    /// </remarks>
    private static (int? ConId, string WhatToShow, string BarSize, bool UseRth) SeriesKey(BackfillJobStatus job) =>
        (job.ConId, job.WhatToShow, job.BarSize, job.UseRth);

    /// <summary>
    /// The span a job CLAIMS to cover, independent of what any particular run audits.
    /// </summary>
    /// <remarks>
    /// A historical job's ceiling is <c>now</c>, deliberately NOT its <c>target_to</c>.
    /// <c>target_to</c> is a PLANNING anchor — frozen at the job's creation day so that lowering
    /// <c>target_from</c> stays a pure extension of the slice grid (see
    /// <c>BackfillStore.EnsureJobAsync</c>) — and it was never a statement about what the job is
    /// responsible for. The coordinator plans forward from it to the current UTC midnight
    /// (<c>BackfillPlanner.PlanForward</c>), so treating the frozen anchor as an audit ceiling left
    /// the audited window falling one day further behind for every day the platform ran, with the
    /// top-up job's two-day lookback nowhere near it.
    /// </remarks>
    private GapArithmetic.Span NominalWindow(BackfillJobStatus job, DateTimeOffset? headUtc, DateTimeOffset now) =>
        job.Kind == BackfillJobKinds.TopUp
            ? new GapArithmetic.Span(now.AddDays(-_options.TopUpDefaultLookbackDays), now)
            : new GapArithmetic.Span(headUtc is { } head && head > job.TargetFrom ? head : job.TargetFrom, now);

    /// <summary>
    /// Per data series, whether the union of the windows actually audited covers the union of the
    /// windows the jobs claim.
    /// </summary>
    /// <param name="auditCeiling">
    /// The in-progress boundary. Claims are clamped to it so <see cref="SeriesReconciliation.Reconciled"/>
    /// answers "was every instant old enough to judge audited by somebody" rather than being
    /// permanently false because the last hour is deliberately not judged. The excluded tail is not
    /// hidden by the clamp — every job reports it as an <see cref="GapAuditReasons.InProgress"/>
    /// unaudited range of its own.
    /// </param>
    /// <remarks>
    /// <b>Every span this produces is derived from the job reports' own words, and only ever in the
    /// direction that reports LESS coverage.</b> Both halves of that matter, and both were breaches:
    /// a job whose report is missing from <paramref name="reports"/> still contributes its claim (and
    /// no coverage), and a job's contribution to the audited set is the span it evaluated MINUS the
    /// spans it declared unaudited itself. So a series cannot say "reconciled" over anything one of
    /// its own jobs said it did not look at, and cannot be talked into a healthier answer by being
    /// handed a smaller set of reports.
    /// </remarks>
    private IReadOnlyList<SeriesReconciliation> Reconcile(
        IReadOnlyList<BackfillJobStatus> jobs, IReadOnlyList<JobGapReport> reports,
        DateTimeOffset now, DateTimeOffset auditCeiling)
    {
        var byId = reports.ToDictionary(r => r.JobId);

        return
        [
            .. jobs
                // Grouped on the data key, not on the job: two jobs are jointly responsible for a
                // range only if they write the same (contract, whatToShow, barSize, useRth) rows of
                // research.bars. Reconciling across instruments would let SPY's audited window
                // "cover" an SPX hole, which is worse than not reconciling at all.
                .GroupBy(SeriesKey)
                .Select(group =>
                {
                    var claimed = new List<GapArithmetic.Span>();
                    var audited = new List<GapArithmetic.Span>();

                    foreach (var job in group)
                    {
                        var report = byId.GetValueOrDefault(job.JobId);

                        // A member with no report in hand claims its window and audits nothing.
                        // Unreachable today — the loop above reports every analyzed job, including
                        // one whose analysis threw — but the previous `continue` is written out
                        // rather than left implicit because dropping a member is the one move that
                        // can only ever make a series look HEALTHIER: the claim it withholds is
                        // exactly the claim the subtraction would have had left over. That is how
                        // the jobId filter turned a series with a known missing session into
                        // Reconciled=true, one level up, and a future caller handing this a subset
                        // of reports must not be able to do it again. The head clamp is deliberately
                        // not applied to the fallback: an unclamped lower bound is a WIDER claim,
                        // and wider is the safe direction here.
                        claimed.Add(report is { NominalFrom: { } nf, NominalTo: { } nt }
                            ? new GapArithmetic.Span(nf, nt < auditCeiling ? nt : auditCeiling)
                            : Clamp(NominalWindow(job, headUtc: null, now)));

                        // Only a run that actually evaluated expectation units contributes coverage.
                        // A window-rejected or zero-unit report examined nothing inside its bounds,
                        // and counting it would be the report laundering its own silence.
                        //
                        // `checked` is not a statement about every instant in [From, To) either — it
                        // only means at least one expectation unit was evaluated somewhere inside it
                        // — so the coverage a job contributes is its window MINUS the ranges it
                        // reported as unaudited on itself. Without that subtraction a VIX job could
                        // report its ENTIRE window unmodelled-and-unaudited and simultaneously
                        // reconcile its series to zero remainder, and both halves of that response
                        // shipped in the same JSON object.
                        if (report is { CheckStatus: GapCheckStatus.Checked, From: { } f, To: { } t })
                        {
                            audited.AddRange(GapArithmetic.Subtract(
                                [new GapArithmetic.Span(f, t)],
                                report.Unaudited.Select(u => new GapArithmetic.Span(u.From, u.To))));
                        }
                    }

                    var unaudited = GapArithmetic.Subtract(claimed, audited);

                    return new SeriesReconciliation(
                        group.Key.ConId,
                        group.Key.WhatToShow,
                        group.Key.BarSize,
                        group.Key.UseRth,
                        [.. group.Select(j => j.Name).Order(StringComparer.Ordinal)],
                        unaudited.Count == 0,
                        [.. unaudited.Select(s => new UnauditedRange(s.From, s.To, GapAuditReasons.NoJobAuditedIt))]);
                })
        ];

        GapArithmetic.Span Clamp(GapArithmetic.Span span) =>
            span with { To = span.To < auditCeiling ? span.To : auditCeiling };
    }

    private async Task<JobGapReport> BuildJobReportAsync(
        BackfillJobStatus job, DateTimeOffset? requestedFrom, DateTimeOffset? requestedTo, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Historical jobs only: PlanJobAsync never resolves a head timestamp for a top-up job (its
        // planner returns before reaching that code), so this is null for one by construction — which
        // is fine, because the kind-based window below never consults it for a top-up job anyway.
        // Read BEFORE the refusal branches so a not-checked job still reports the window it claims.
        DateTimeOffset? headUtc = job.Kind == BackfillJobKinds.Historical
            ? (await store.GetCachedHeadTimestampAsync($"head_timestamp:{job.Name}", cancellationToken))?.Head
            : null;

        var nominal = NominalWindow(job, headUtc, now);

        JobGapReport NotChecked(string status, string detail, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
            new(job.JobId, job.Name, job.Status, status, detail, from, to, nominal.From, nominal.To, headUtc, 0, false, [],
                [new UnauditedRange(nominal.From, nominal.To, GapAuditReasons.JobNotChecked)]);

        if (job.ConId is not { } conId)
        {
            return NotChecked(GapCheckStatus.MultiContractJobUnsupported,
                "This job has a NULL con_id — it walks multiple contracts over time (e.g. the ES roll) " +
                "— and gap detection does not yet support per-contract analysis for it. Inspect " +
                "research.backfill_requests for this job_id directly.");
        }

        var instrument = await store.GetInstrumentAsync(job.InstrumentId, cancellationToken);

        if (instrument is null)
        {
            return NotChecked(GapCheckStatus.UnknownInstrument,
                $"Instrument {job.InstrumentId} is not in research.instruments.");
        }

        var mapping = InstrumentCalendars.For(instrument.Symbol, instrument.Kind);

        if (mapping.Expectations.Count == 0)
        {
            return NotChecked(GapCheckStatus.NoCalendarMapping,
                $"No calendar mapping for {instrument.Symbol} ({instrument.Kind}); extend InstrumentCalendars. " +
                "Guessing a calendar does not fail loudly — it reports confidently about the wrong sessions.");
        }

        var shape = GapArithmetic.ClassifyBarSize(job.BarSize);

        if (shape.Kind == BarSizeKind.Unsupported)
        {
            return NotChecked(GapCheckStatus.UnsupportedBarSize,
                $"Bar size '{job.BarSize}' names no per-minute or per-trading-date expectation this detector computes.");
        }

        // The audit ceiling stops short of now: an in-progress session has not had time to land the
        // minutes it is accruing, and judging it against them reported the alarm basis on every poll
        // during market hours. The excluded tail is reported below rather than dropped.
        var auditCeiling = now.AddMinutes(-_options.InProgressGraceMinutes);

        if (auditCeiling > nominal.To)
        {
            auditCeiling = nominal.To;
        }

        var from = requestedFrom is { } rf && rf > nominal.From ? rf : nominal.From;
        var to = requestedTo is { } rt && rt < auditCeiling ? rt : auditCeiling;

        var unaudited = new List<UnauditedRange>();

        if (from > nominal.From)
        {
            unaudited.Add(new UnauditedRange(nominal.From, from, GapAuditReasons.CallerNarrowedWindow));
        }

        if (to < auditCeiling)
        {
            unaudited.Add(new UnauditedRange(to, auditCeiling, GapAuditReasons.CallerNarrowedWindow));
        }

        if (auditCeiling < nominal.To)
        {
            unaudited.Add(new UnauditedRange(auditCeiling, nominal.To, GapAuditReasons.InProgress));
        }

        // A window this platform knows the instrument trades in but has no session definition for is
        // named explicitly rather than quietly producing no expectation unit. VIX's overnight bars
        // are the current case: they exist, they are not audited, and before this their absence
        // rendered as a clean report.
        unaudited.AddRange(mapping.Unmodelled.Select(
            window => new UnauditedRange(from, to, $"{GapAuditReasons.NoSessionDefinition}: {window.Description}")));

        if (to <= from)
        {
            return NotChecked(GapCheckStatus.WindowRejected,
                "The effective window — after clamping to the job's own range, its head timestamp, the " +
                $"requested window, and the {_options.InProgressGraceMinutes}-minute in-progress grace — is empty.",
                from, to);
        }

        if ((to - from).TotalDays > _options.MaxWindowDays)
        {
            return NotChecked(GapCheckStatus.WindowRejected,
                $"The effective window spans more than {_options.MaxWindowDays} days.", from, to);
        }

        var fromDate = DateOnly.FromDateTime(from.UtcDateTime).AddDays(-2);
        var toDate = DateOnly.FromDateTime(to.UtcDateTime).AddDays(2);

        // Filtered through the mapping rather than merely fetched by calendar key: a calendar can
        // carry labels this instrument's data does not fill (CME_ES nests RTH inside GTH), and the
        // mapping is the authority on which of them count.
        var rawSessions = mapping.Calendars
            .SelectMany(calendar => clock.SessionsBetween(calendar, fromDate, toDate))
            .Where(session => mapping.Includes(session.Calendar, session.Label))
            .ToArray();

        var requestRows = await store.GetRequestWindowsAsync(job.JobId, cancellationToken);
        var windows = GapArithmetic.ComputeRequestWindows(requestRows);
        var sweep = new GapArithmetic.RequestWindowSweep(windows);

        IReadOnlyList<GapRange> gaps;
        bool truncated;
        int unitsChecked;

        if (shape.Kind == BarSizeKind.Daily)
        {
            var tradingDates = GapArithmetic.TradingDatesInRange(rawSessions, from, to);
            var landed = await store.GetLandedTradingDatesAsync(
                conId, job.WhatToShow, job.BarSize, job.UseRth, from, to, cancellationToken);

            unitsChecked = tradingDates.Count;

            var sequence = tradingDates.Select(date =>
            {
                var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var end = start.AddDays(1);
                var basis = landed.Contains(date)
                    ? null
                    : GapArithmetic.DetermineBasis(sweep.FindOverlapping(start, end), _backfillOptions.MaxAttempts);

                return (start, end, basis);
            });

            (gaps, truncated) = GapArithmetic.BuildRanges(sequence, _options.MaxRangesPerJob);
        }
        else
        {
            var units = GapArithmetic.BuildSessionUnits(rawSessions, job.UseRth, from, to);
            var intervalMinutes = shape.IntervalMinutes!.Value;

            unitsChecked = units.Count;

            var counts = await store.GetLandedBarCountsAsync(
                conId, job.WhatToShow, job.BarSize, job.UseRth,
                [.. units.Select(u => u.MeasuredFromUtc)], [.. units.Select(u => u.MeasuredToUtc)],
                cancellationToken);

            var sequence = units.Select((unit, i) =>
            {
                var expectedMinutes = (int)((unit.MeasuredToUtc - unit.MeasuredFromUtc).Ticks / TimeSpan.TicksPerMinute);
                // Exact for the "1 min" bar size every current job uses (expectedMinutes IS the bar
                // count). For a wider bar (5 min, 1 hour — none seeded today) this is an approximation:
                // TWS's own intra-session bucket alignment for N>1 is not runtime-verified, so this
                // floors rather than guesses upward, which can only ever UNDER-flag a shortfall for
                // that unverified case, never invent one.
                var expectedBars = Math.Max(1, expectedMinutes / intervalMinutes);

                var basis = counts[i] >= expectedBars
                    ? null
                    : GapArithmetic.DetermineBasis(
                        sweep.FindOverlapping(unit.MeasuredFromUtc, unit.MeasuredToUtc), _backfillOptions.MaxAttempts);

                return (unit.MeasuredFromUtc, unit.MeasuredToUtc, basis);
            });

            (gaps, truncated) = GapArithmetic.BuildRanges(sequence, _options.MaxRangesPerJob);
        }

        if (unitsChecked == 0)
        {
            // Zero units and zero gaps is the weakest statement this report can make, and it used to
            // render exactly like the strongest one.
            return new JobGapReport(
                job.JobId, job.Name, job.Status, GapCheckStatus.NoExpectationUnits,
                "The session calendar produced no expectation unit inside this window, so nothing was " +
                "verified. An empty gap list here means 'nothing was checked', not 'everything passed'.",
                from, to, nominal.From, nominal.To, headUtc, 0, false, [],
                [new UnauditedRange(from, to, GapAuditReasons.NoSessionDefinition), .. unaudited]);
        }

        return new JobGapReport(
            job.JobId, job.Name, job.Status, GapCheckStatus.Checked, null, from, to,
            nominal.From, nominal.To, headUtc, unitsChecked, truncated, gaps, unaudited);
    }
}
