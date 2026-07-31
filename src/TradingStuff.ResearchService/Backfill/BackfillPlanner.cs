using System.Globalization;
using TradingStuff.ResearchContracts;

namespace TradingStuff.ResearchService.Backfill;

/// <summary>The unit a <see cref="SliceCadence"/> steps in. Calendar units, not fixed spans.</summary>
public enum SliceCadenceUnit
{
    /// <summary>UTC midnights.</summary>
    Day,

    /// <summary>Mondays at 00:00 UTC.</summary>
    Week,

    /// <summary>1 January at 00:00 UTC.</summary>
    Year,
}

/// <summary>
/// How far back one TWS request reaches, and — just as importantly — the grid its boundaries land on.
/// </summary>
/// <remarks>
/// The grid is the whole point. A planner that walks back from <c>now</c>, or from any instant that
/// moves between runs, produces different <c>end_time_utc</c> values on every pass; every one of
/// them is a fresh row under <c>research.backfill_requests</c>'s idempotency key, so "an identical
/// rerun adds zero rows" quietly stops holding. The obvious acceptance test does not catch it
/// either: <c>research.bars</c>'s primary key absorbs the duplicate bars, so bar counts stay
/// correct while the request table doubles. Anchoring every boundary to a calendar instant derived
/// only from fixed job columns is what makes the key work.
/// </remarks>
public sealed record SliceCadence(SliceCadenceUnit Unit, int Count)
{
    /// <summary>The TWS duration string this cadence sends, e.g. <c>"1 D"</c>.</summary>
    public string Duration => Unit switch
    {
        SliceCadenceUnit.Day => $"{Count} D",
        SliceCadenceUnit.Week => $"{Count} W",
        _ => $"{Count} Y",
    };

    /// <summary>Roughly how much wall-clock time one slice covers. Used for neighbour proximity, never for boundaries.</summary>
    public TimeSpan ApproximateSpan => Unit switch
    {
        SliceCadenceUnit.Day => TimeSpan.FromDays(Count),
        SliceCadenceUnit.Week => TimeSpan.FromDays(7 * Count),
        _ => TimeSpan.FromDays(365 * Count),
    };

    /// <summary>The grid boundary at or before <paramref name="instant"/>.</summary>
    public DateTimeOffset FloorBoundary(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();

        return Unit switch
        {
            SliceCadenceUnit.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            SliceCadenceUnit.Week => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero)
                .AddDays(-(((int)utc.DayOfWeek + 6) % 7)), // Monday-anchored: Sunday is 0, so shift by 6.
            _ => new DateTimeOffset(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }

    /// <summary>The next boundary back from <paramref name="boundary"/>, staying on the same grid.</summary>
    public DateTimeOffset Previous(DateTimeOffset boundary) => Unit switch
    {
        SliceCadenceUnit.Day => boundary.AddDays(-Count),
        SliceCadenceUnit.Week => boundary.AddDays(-7 * Count),
        _ => boundary.AddYears(-Count),
    };
}

/// <summary>
/// Turns a backfill job into the concrete <c>reqHistoricalData</c> slices that cover it.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is a pure function of its arguments. Nothing reads the clock, the database, or
/// configuration — the current instant only ever enters as an explicit parameter, and then only for
/// top-ups, where "the current bucket" is the whole point. That is not stylistic: the planner's one
/// hard requirement is that the same job row produces byte-identical slices on every run forever,
/// because <c>research.backfill_requests</c>'s uniqueness key is the ONLY thing making a rerun free.
/// </para>
/// <para>
/// Historical slices walk BACKWARD from the job's fixed <c>target_to</c>. Anchoring at the far end
/// rather than the near one has a second payoff beyond determinism: lowering a job's
/// <c>target_from</c> (the roadmap's "SPX from 2010, then probe toward the 2004 head") extends the
/// existing sequence rather than shifting it, so deepening a job adds exactly the newly-reachable
/// older slices and re-derives every existing one identically.
/// </para>
/// </remarks>
public static class BackfillPlanner
{
    /// <summary>The bucket a top-up request's end instant is floored to. Also the top-up run cadence.</summary>
    public static readonly TimeSpan TopUpBucket = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Default TWS duration for a top-up slice: an hour of tail, re-requested every 15 minutes.
    /// The 4x overlap is deliberate — a coordinator that misses up to three consecutive runs (a
    /// restart, a pacing storm) still closes its own gap on the next successful one, and the
    /// overlapping bars cost nothing because <c>research.bars</c>'s primary key absorbs them.
    /// </summary>
    public const string DefaultTopUpDuration = "3600 S";

    /// <summary>
    /// Refuses to plan a job that would produce more slices than this. A runaway target range is a
    /// misconfiguration, and silently enqueuing a million paced requests is a worse outcome than
    /// refusing and saying so.
    /// </summary>
    public const int MaxSlicesPerJob = 25_000;

    /// <summary>
    /// The TWS duration a job's slices use: its own <c>slice_duration</c> if set, else derived from
    /// its bar size. Returns null when an explicit override cannot be parsed — the caller must
    /// refuse to plan rather than silently substituting the derived value, because a job planned at
    /// a duration nobody asked for lands request rows that will never match the operator's intent.
    /// </summary>
    public static SliceCadence? CadenceFor(BackfillJob job) =>
        job.SliceDuration is { Length: > 0 } explicitDuration
            ? TryParseCadence(explicitDuration)
            : CadenceForBarSize(job.BarSize);

    /// <summary>
    /// Slice cadence per TWS bar size: one day per request for minute bars, one week for hourly, one
    /// year for daily and coarser.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. IBKR documents "long durations allowed" for 1-minute and coarser
    /// bars but publishes no per-instrument maximum, and
    /// docs/research/ibkr-data-capability-matrix.md defers the actual maxima to a probe. A day per
    /// 1-minute slice is a duration every probe in this repo has already exercised, and it is the
    /// only choice that puts slice boundaries on UTC midnights — which for Cboe products is a real
    /// session boundary, since the SPX/SPXW overnight session opens at 19:15 CT (after UTC
    /// midnight), so no session is ever split across two slices. Raising throughput later is a
    /// per-job <c>slice_duration</c> change, not a code change.
    /// </remarks>
    public static SliceCadence? CadenceForBarSize(string barSize)
    {
        var normalized = barSize.Trim().ToLowerInvariant();

        if (normalized.Contains("sec"))
        {
            // Sub-minute bars are not a backfill target in this platform (TWS serves them for six
            // months at most). Refuse rather than invent a cadence for them.
            return null;
        }

        if (normalized.Contains("min"))
        {
            return new SliceCadence(SliceCadenceUnit.Day, 1);
        }

        if (normalized.Contains("hour"))
        {
            return new SliceCadence(SliceCadenceUnit.Week, 1);
        }

        return normalized.Contains("day") || normalized.Contains("week") || normalized.Contains("month")
            ? new SliceCadence(SliceCadenceUnit.Year, 1)
            : null;
    }

    /// <summary>Parses a TWS duration string into a cadence, or null if it names no supported grid.</summary>
    public static SliceCadence? TryParseCadence(string duration)
    {
        var parts = duration.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count <= 0)
        {
            return null;
        }

        return parts[1].ToUpperInvariant() switch
        {
            "D" => new SliceCadence(SliceCadenceUnit.Day, count),
            "W" => new SliceCadence(SliceCadenceUnit.Week, count),
            "Y" => new SliceCadence(SliceCadenceUnit.Year, count),
            _ => null, // "S" and "M" name no boundary grid this planner can walk deterministically.
        };
    }

    /// <summary>
    /// Roughly how much time a duration string covers, for proximity comparisons only. Falls back to
    /// a day for shapes with no grid (e.g. the top-up's <c>"3600 S"</c>).
    /// </summary>
    public static TimeSpan ApproximateSpanOf(string duration)
    {
        var parts = duration.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count <= 0)
        {
            return TimeSpan.FromDays(1);
        }

        return parts[1].ToUpperInvariant() switch
        {
            "S" => TimeSpan.FromSeconds(count),
            "D" => TimeSpan.FromDays(count),
            "W" => TimeSpan.FromDays(7 * count),
            "M" => TimeSpan.FromDays(30 * count),
            "Y" => TimeSpan.FromDays(365 * count),
            _ => TimeSpan.FromDays(1),
        };
    }

    /// <summary>
    /// Every slice covering <paramref name="job"/>'s target range for <paramref name="conId"/>,
    /// newest first.
    /// </summary>
    /// <param name="headTimestampUtc">
    /// The instrument's real data floor from <c>reqHeadTimeStamp</c>, or null when it is not known.
    /// Clamping the range to it is what makes "VIX intraday, probe to the floor" a plain job rather
    /// than a special mode: set <c>target_from</c> optimistically deep and the head decides where
    /// planning actually starts. Passed in rather than fetched here so the planner stays pure and so
    /// determinism is a property of the (job, head) pair — a head that later moves earlier simply
    /// extends the sequence; it never rewrites the slices already planned.
    /// </param>
    public static IReadOnlyList<BackfillSlice> PlanHistorical(
        BackfillJob job, int conId, DateTimeOffset? headTimestampUtc, SliceCadence cadence)
    {
        var targetTo = job.TargetTo.ToUniversalTime();
        var effectiveFrom = job.TargetFrom.ToUniversalTime();

        if (headTimestampUtc is { } head && head.ToUniversalTime() > effectiveFrom)
        {
            effectiveFrom = head.ToUniversalTime();
        }

        if (effectiveFrom >= targetTo)
        {
            return [];
        }

        var slices = new List<BackfillSlice>();
        var boundary = cadence.FloorBoundary(targetTo);

        // A target_to that is not itself on the grid gets one leading slice ending exactly at it.
        // The alternative — rounding UP to the next boundary — would put the newest slice's end in
        // the future, where TWS refuses it outright and the job stalls one boundary short of done.
        // This slice over-reaches backward past `boundary`; the overlap is free (research.bars
        // deduplicates) and keeps the covered range contiguous.
        if (targetTo > boundary)
        {
            slices.Add(SliceAt(job, conId, targetTo, cadence.Duration));
        }

        for (var end = boundary; end > effectiveFrom; end = cadence.Previous(end))
        {
            if (slices.Count >= MaxSlicesPerJob)
            {
                // Truncating keeps the newest slices, which is the half worth having if a job is
                // this badly misconfigured. The caller logs the cutoff.
                break;
            }

            slices.Add(SliceAt(job, conId, end, cadence.Duration));
        }

        return slices;
    }

    /// <summary>
    /// The one slice a top-up run issues: the tail ending at the 15-minute bucket
    /// <paramref name="nowUtc"/> falls in.
    /// </summary>
    /// <remarks>
    /// This is the resolution of the top-up idempotency contradiction migration 004 shipped. That
    /// design gave every top-up request a constant NULL <c>end_time_utc</c>, which under
    /// <c>UNIQUE NULLS NOT DISTINCT</c> makes them collide with each other by construction; with
    /// <c>succeeded</c> meaning "never re-request", the second and every later run became a silent
    /// no-op that logged success while the recent tail stopped advancing.
    /// <para>
    /// Flooring to a concrete bucket instead satisfies both halves at once. Two runs inside one
    /// bucket produce byte-identical slices and therefore zero new rows — the idempotency guarantee
    /// the key exists for. The next bucket produces a genuinely different row, so the tail advances.
    /// No checkpoint row is ever mutated back out of a terminal state, no run-scoped discriminator
    /// column is needed, and the recorded request stays exactly reproducible: an operator can
    /// re-issue the identical TWS call from the row months later, which a NULL "whenever this ran"
    /// anchor could never support.
    /// </para>
    /// </remarks>
    public static BackfillSlice PlanTopUp(BackfillJob job, int conId, DateTimeOffset nowUtc) =>
        SliceAt(
            job,
            conId,
            FloorToBucket(nowUtc, TopUpBucket),
            job.SliceDuration is { Length: > 0 } duration ? duration : DefaultTopUpDuration);

    /// <summary>Floors <paramref name="instant"/> down to a whole multiple of <paramref name="bucket"/> from the epoch.</summary>
    public static DateTimeOffset FloorToBucket(DateTimeOffset instant, TimeSpan bucket)
    {
        var utc = instant.ToUniversalTime();
        var ticks = utc.UtcTicks - (utc.UtcTicks % bucket.Ticks);

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static BackfillSlice SliceAt(BackfillJob job, int conId, DateTimeOffset endUtc, string duration) =>
        new(job.JobId, conId, endUtc.ToUniversalTime(), duration, job.WhatToShow, job.BarSize, job.UseRth);
}
