using Microsoft.Extensions.Options;
using TradingStuff.ResearchContracts;

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

    /// <summary>The instrument's symbol has no entry in <c>GapCalendars</c>; extend it rather than guess a calendar.</summary>
    public const string NoCalendarMapping = "no-calendar-mapping";

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

/// <summary>One job's gap-detection outcome.</summary>
/// <param name="JobStatus"><c>research.backfill_jobs.status</c> — lets a caller tell "complete but has real gaps" apart from "still running, gaps are expected".</param>
/// <param name="CheckStatus">See <see cref="GapCheckStatus"/>.</param>
/// <param name="From">The window actually analyzed, after clamping. Null when <see cref="CheckStatus"/> is not <see cref="GapCheckStatus.Checked"/> (or <see cref="GapCheckStatus.WindowRejected"/> partially populated).</param>
/// <param name="HeadTimestampUtc">
/// The cached <c>reqHeadTimeStamp</c> result this job's lower bound was clamped to, if one has been
/// probed. Reported so a caller can see WHY a job's own <c>target_from</c> is not where checking
/// actually started — the pre-head range is not a gap, it is history that does not exist.
/// </param>
/// <param name="Truncated">True when more merged ranges existed than <see cref="GapOptions.MaxRangesPerJob"/> allows; <see cref="Gaps"/> is a prefix, not the whole finding.</param>
public sealed record JobGapReport(
    long JobId,
    string JobName,
    string JobStatus,
    string CheckStatus,
    string? CheckDetail,
    DateTimeOffset? From,
    DateTimeOffset? To,
    DateTimeOffset? HeadTimestampUtc,
    bool Truncated,
    IReadOnlyList<GapRange> Gaps);

/// <summary>What <c>GET /research/backfill/gaps</c> answers with.</summary>
public sealed record GapReport(IReadOnlyList<JobGapReport> Jobs);

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

        var reports = new List<JobGapReport>(selected.Length);

        foreach (var job in selected)
        {
            try
            {
                reports.Add(await BuildJobReportAsync(job, from, to, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One job's analysis failing must not blank the whole response, and must not silently
                // drop this job either — the same per-job isolation BackfillCoordinator.PlanAsync
                // applies, applied here so a bug in one job's calendar mapping cannot hide every other
                // job's real gaps behind an unhandled exception.
                logger.LogError(ex, "Gap analysis for job {Job} failed.", job.Name);
                reports.Add(new JobGapReport(
                    job.JobId, job.Name, job.Status, GapCheckStatus.Error, ex.Message, null, null, null, false, []));
            }
        }

        return new GapReport(reports);
    }

    private async Task<JobGapReport> BuildJobReportAsync(
        BackfillJobStatus job, DateTimeOffset? requestedFrom, DateTimeOffset? requestedTo,
        CancellationToken cancellationToken)
    {
        if (job.ConId is not { } conId)
        {
            return NotChecked(job, GapCheckStatus.MultiContractJobUnsupported,
                "This job has a NULL con_id — it walks multiple contracts over time (e.g. the ES roll) " +
                "— and gap detection does not yet support per-contract analysis for it. Inspect " +
                "research.backfill_requests for this job_id directly.");
        }

        var instrument = await store.GetInstrumentAsync(job.InstrumentId, cancellationToken);

        if (instrument is null)
        {
            return NotChecked(job, GapCheckStatus.UnknownInstrument,
                $"Instrument {job.InstrumentId} is not in research.instruments.");
        }

        if (!GapCalendars.TryGetCalendars(instrument.Symbol, out var calendars))
        {
            return NotChecked(job, GapCheckStatus.NoCalendarMapping,
                $"No calendar mapping for symbol '{instrument.Symbol}'; extend GapCalendars.");
        }

        var shape = GapArithmetic.ClassifyBarSize(job.BarSize);

        if (shape.Kind == BarSizeKind.Unsupported)
        {
            return NotChecked(job, GapCheckStatus.UnsupportedBarSize,
                $"Bar size '{job.BarSize}' names no per-minute or per-trading-date expectation this detector computes.");
        }

        // Historical jobs only: PlanJobAsync never resolves a head timestamp for a top-up job (its
        // planner returns before reaching that code), so this is null for one by construction — which
        // is fine, because the kind-based window below never consults it for a top-up job anyway.
        DateTimeOffset? headUtc = job.Kind == BackfillJobKinds.Historical
            ? (await store.GetCachedHeadTimestampAsync($"head_timestamp:{job.Name}", cancellationToken))?.Head
            : null;

        var (lowerBound, upperBoundNominal) = job.Kind == BackfillJobKinds.TopUp
            ? (DateTimeOffset.UtcNow.AddDays(-_options.TopUpDefaultLookbackDays), DateTimeOffset.UtcNow)
            : (headUtc is { } head && head > job.TargetFrom ? head : job.TargetFrom,
               job.TargetTo < DateTimeOffset.UtcNow ? job.TargetTo : DateTimeOffset.UtcNow);

        var from = requestedFrom is { } rf && rf > lowerBound ? rf : lowerBound;
        var to = requestedTo is { } rt && rt < upperBoundNominal ? rt : upperBoundNominal;

        if (to <= from)
        {
            return NotChecked(job, GapCheckStatus.WindowRejected,
                "The effective window — after clamping to the job's own range, its head timestamp, and " +
                "the requested window — is empty.", from, to, headUtc);
        }

        if ((to - from).TotalDays > _options.MaxWindowDays)
        {
            return NotChecked(job, GapCheckStatus.WindowRejected,
                $"The effective window spans more than {_options.MaxWindowDays} days.", from, to, headUtc);
        }

        var fromDate = DateOnly.FromDateTime(from.UtcDateTime).AddDays(-2);
        var toDate = DateOnly.FromDateTime(to.UtcDateTime).AddDays(2);

        var rawSessions = calendars
            .SelectMany(calendar => clock.SessionsBetween(calendar, fromDate, toDate))
            .ToArray();

        var requestRows = await store.GetRequestWindowsAsync(job.JobId, cancellationToken);
        var windows = GapArithmetic.ComputeRequestWindows(requestRows);
        var sweep = new GapArithmetic.RequestWindowSweep(windows);

        IReadOnlyList<GapRange> gaps;
        bool truncated;

        if (shape.Kind == BarSizeKind.Daily)
        {
            var tradingDates = GapArithmetic.TradingDatesInRange(rawSessions, from, to);
            var landed = await store.GetLandedTradingDatesAsync(
                conId, job.WhatToShow, job.BarSize, job.UseRth, from, to, cancellationToken);

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

        return new JobGapReport(
            job.JobId, job.Name, job.Status, GapCheckStatus.Checked, null, from, to, headUtc, truncated, gaps);
    }

    private static JobGapReport NotChecked(
        BackfillJobStatus job, string status, string detail,
        DateTimeOffset? from = null, DateTimeOffset? to = null, DateTimeOffset? headUtc = null) =>
        new(job.JobId, job.Name, job.Status, status, detail, from, to, headUtc, false, []);
}
