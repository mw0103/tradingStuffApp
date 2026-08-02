using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// Sorts scored days into spread quintiles and reports what each bucket actually paid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Breakpoints come from the training window. This is not a detail.</b> Quintiles cut at
/// evaluation-sample percentiles look almost identical and are a quiet leak: the boundary moves to
/// wherever the test data happens to sit, so every bucket is guaranteed to be a fifth of the test
/// sample and the "conditioning rule" could not have been stated in advance. A rule you could
/// actually have followed has fixed edges, and the bucket counts in the evaluation sample then come
/// out uneven — which is the visible signature that the edges were frozen, and is reported rather
/// than smoothed away.
/// </para>
/// <para>
/// <b>Monotonicity is the headline, not a threshold.</b> A single well-chosen cut point is easy to
/// produce by accident on ~120 effective observations; an ordering that holds across five buckets is
/// much harder to fake. So the verdict this file produces is the SHAPE of the relationship, and
/// non-monotonicity is reported plainly rather than being narrated as "monotone apart from Q3".
/// </para>
/// </remarks>
public static class VrpConditioningQuintiles
{
    public const int BucketCount = 5;

    /// <summary>The four interior cut points of a quintile split.</summary>
    private static readonly double[] CutQuantiles = [0.2, 0.4, 0.6, 0.8];

    /// <summary>
    /// Quintile breakpoints of a TRAINING-window spread sample, using the same type-7 quantile
    /// definition the bootstrap intervals use (<see cref="StationaryBlockBootstrap.Quantile"/>) so
    /// no two places in this study disagree about where a percentile is.
    /// </summary>
    public static double[] Breakpoints(IReadOnlyList<double> trainingSpreads)
    {
        ArgumentNullException.ThrowIfNull(trainingSpreads);
        if (trainingSpreads.Count == 0)
            throw new ArgumentException("Cannot derive quintile breakpoints from an empty training sample.", nameof(trainingSpreads));

        var sorted = trainingSpreads.ToArray();
        Array.Sort(sorted);

        return [.. CutQuantiles.Select(q => StationaryBlockBootstrap.Quantile(sorted, q))];
    }

    /// <summary>Bucket 1..5 for a spread, against frozen breakpoints. 1 is the narrowest spread.</summary>
    public static int BucketOf(double spread, double[] breakpoints)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);

        var bucket = 1;
        foreach (var edge in breakpoints)
        {
            if (spread <= edge) break;
            bucket++;
        }

        return bucket;
    }

    /// <summary>
    /// Aggregates one arm's buckets over the pooled, date-ordered evaluation days, with stationary
    /// block-bootstrap percentile intervals for each bucket mean and for the Q5 − Q1 difference.
    /// </summary>
    /// <remarks>
    /// The bootstrap resamples the WHOLE date-ordered series in blocks and recomputes the bucket
    /// means inside each resample, rather than bootstrapping each bucket's own subsequence. Bucket
    /// membership is scattered through time, so a bucket's rows are not a contiguous series and
    /// blocking them directly would destroy exactly the dependence the block length exists to
    /// preserve. Resampling the series and re-aggregating keeps the 21-day overlap structure intact.
    /// </remarks>
    public static VrpConditioningArmConditioning Aggregate(
        IReadOnlyList<VrpConditioningDailyResult> orderedDays,
        string arm,
        double[] breakpoints,
        double meanBlockLength = VrpConditioningHorizon.BootstrapMeanBlockLength,
        int resamples = StationaryBlockBootstrap.RegisteredResamples,
        ulong seed = VrpConditioningHorizon.BootstrapSeed)
    {
        ArgumentNullException.ThrowIfNull(orderedDays);

        var n = orderedDays.Count;
        var bucketOf = new int[n];
        for (var i = 0; i < n; i++) bucketOf[i] = orderedDays[i].Bucket[arm];

        var observed = BucketMeans(orderedDays, bucketOf, arm);

        // --- bootstrap ---
        var pnlDraws = NewDrawBuffers(resamples);
        var premiumDraws = NewDrawBuffers(resamples);
        var realizedDraws = NewDrawBuffers(resamples);
        var q5MinusQ1Pnl = new List<double>(resamples);

        var monotonePnl = 0;
        var monotonePremium = 0;
        var usableResamples = 0;

        var counts = new int[BucketCount + 1];
        var sumPnl = new double[BucketCount + 1];
        var sumPremium = new double[BucketCount + 1];
        var sumRealized = new double[BucketCount + 1];

        StationaryBlockBootstrap.ForEachResample(n, resamples, meanBlockLength, seed, indices =>
        {
            Array.Clear(counts);
            Array.Clear(sumPnl);
            Array.Clear(sumPremium);
            Array.Clear(sumRealized);

            for (var t = 0; t < n; t++)
            {
                var row = orderedDays[indices[t]];
                var b = bucketOf[indices[t]];
                counts[b]++;
                sumPnl[b] += row.PnlPerVegaNotional;
                sumPremium[b] += row.PremiumCollected;
                sumRealized[b] += row.RealizedVariance;
            }

            var complete = true;
            for (var b = 1; b <= BucketCount; b++) if (counts[b] == 0) complete = false;
            if (!complete) return;

            usableResamples++;

            var pnl = new double[BucketCount + 1];
            var premium = new double[BucketCount + 1];
            for (var b = 1; b <= BucketCount; b++)
            {
                pnl[b] = sumPnl[b] / counts[b];
                premium[b] = sumPremium[b] / counts[b];

                pnlDraws[b].Add(pnl[b]);
                premiumDraws[b].Add(premium[b]);
                realizedDraws[b].Add(sumRealized[b] / counts[b]);
            }

            q5MinusQ1Pnl.Add(pnl[BucketCount] - pnl[1]);

            if (IsMonotone([.. pnl[1..]])) monotonePnl++;
            if (IsMonotone([.. premium[1..]])) monotonePremium++;
        });

        var buckets = new List<VrpConditioningBucket>(BucketCount);
        for (var b = 1; b <= BucketCount; b++)
        {
            buckets.Add(new VrpConditioningBucket(
                b,
                BucketLabel(b, breakpoints),
                observed.Counts[b],
                observed.MeanSpread[b],
                observed.MeanRealizedVariance[b],
                observed.MeanRealizedAnnualizedVolPct[b],
                observed.MeanImpliedVariance[b],
                observed.MeanPremium[b],
                Interval(premiumDraws[b]),
                observed.MeanPnl[b],
                Interval(pnlDraws[b]),
                Interval(realizedDraws[b])));
        }

        var observedPnlByBucket = new double[BucketCount];
        var observedPremiumByBucket = new double[BucketCount];
        var observedRealizedByBucket = new double[BucketCount];
        for (var b = 1; b <= BucketCount; b++)
        {
            observedPnlByBucket[b - 1] = observed.MeanPnl[b];
            observedPremiumByBucket[b - 1] = observed.MeanPremium[b];
            observedRealizedByBucket[b - 1] = observed.MeanRealizedVariance[b];
        }

        return new VrpConditioningArmConditioning(
            arm,
            breakpoints,
            buckets,
            Verdict(observedPnlByBucket),
            Verdict(observedPremiumByBucket),
            Verdict(observedRealizedByBucket),
            observed.MeanPnl[BucketCount] - observed.MeanPnl[1],
            Interval(q5MinusQ1Pnl),
            usableResamples == 0 ? 0.0 : (double)monotonePnl / usableResamples,
            usableResamples == 0 ? 0.0 : (double)monotonePremium / usableResamples,
            usableResamples);
    }

    private static List<double>[] NewDrawBuffers(int resamples)
    {
        var buffers = new List<double>[BucketCount + 1];
        for (var b = 1; b <= BucketCount; b++) buffers[b] = new List<double>(resamples);
        return buffers;
    }

    private sealed record ObservedBuckets(
        int[] Counts,
        double[] MeanSpread,
        double[] MeanRealizedVariance,
        double[] MeanRealizedAnnualizedVolPct,
        double[] MeanImpliedVariance,
        double[] MeanPremium,
        double[] MeanPnl);

    private static ObservedBuckets BucketMeans(
        IReadOnlyList<VrpConditioningDailyResult> days, int[] bucketOf, string arm)
    {
        var counts = new int[BucketCount + 1];
        var spread = new double[BucketCount + 1];
        var realized = new double[BucketCount + 1];
        var realizedVol = new double[BucketCount + 1];
        var implied = new double[BucketCount + 1];
        var premium = new double[BucketCount + 1];
        var pnl = new double[BucketCount + 1];

        for (var i = 0; i < days.Count; i++)
        {
            var b = bucketOf[i];
            counts[b]++;
            spread[b] += days[i].Spread[arm];
            realized[b] += days[i].RealizedVariance;
            realizedVol[b] += days[i].RealizedAnnualizedVolPct;
            implied[b] += days[i].ImpliedVariance;
            premium[b] += days[i].PremiumCollected;
            pnl[b] += days[i].PnlPerVegaNotional;
        }

        for (var b = 1; b <= BucketCount; b++)
        {
            if (counts[b] == 0) continue;
            spread[b] /= counts[b];
            realized[b] /= counts[b];
            realizedVol[b] /= counts[b];
            implied[b] /= counts[b];
            premium[b] /= counts[b];
            pnl[b] /= counts[b];
        }

        return new ObservedBuckets(counts, spread, realized, realizedVol, implied, premium, pnl);
    }

    /// <summary>
    /// Two-sided percentile interval from a bootstrap draw sample. Deliberately NOT paired with a
    /// p-value anywhere in this study: the registration allows this companion "bootstrap CIs only,
    /// no significance claims".
    /// </summary>
    internal static VrpConditioningInterval Interval(List<double> draws, double alpha = 0.10)
    {
        if (draws.Count == 0) return new VrpConditioningInterval(double.NaN, double.NaN, alpha, 0);

        var sorted = draws.ToArray();
        Array.Sort(sorted);

        return new VrpConditioningInterval(
            StationaryBlockBootstrap.Quantile(sorted, alpha / 2.0),
            StationaryBlockBootstrap.Quantile(sorted, 1.0 - alpha / 2.0),
            alpha,
            draws.Count);
    }

    internal static bool IsMonotone(double[] values)
    {
        var increasing = true;
        var decreasing = true;

        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1]) increasing = false;
            if (values[i] > values[i - 1]) decreasing = false;
        }

        return increasing || decreasing;
    }

    /// <summary>
    /// The shape of the bucket sequence, plus how many adjacent pairs run against the dominant
    /// direction. "Non-monotone with one violation" and "non-monotone with three" are different
    /// findings and the verdict string keeps them apart.
    /// </summary>
    internal static VrpConditioningMonotonicity Verdict(double[] byBucket)
    {
        var up = 0;
        var down = 0;
        for (var i = 1; i < byBucket.Length; i++)
        {
            if (byBucket[i] > byBucket[i - 1]) up++;
            else if (byBucket[i] < byBucket[i - 1]) down++;
        }

        var direction = up >= down ? "increasing" : "decreasing";
        var violations = up >= down ? down : up;

        var shape = violations == 0
            ? $"monotone-{direction}"
            : $"non-monotone (dominant direction {direction}, {violations} of {byBucket.Length - 1} adjacent pairs reversed)";

        return new VrpConditioningMonotonicity(shape, violations == 0, direction, violations, byBucket.Length - 1);
    }

    private static string BucketLabel(int bucket, double[] breakpoints) => bucket switch
    {
        1 => $"Q1 (spread <= {breakpoints[0]:0.######})",
        5 => $"Q5 (spread > {breakpoints[3]:0.######})",
        _ => $"Q{bucket} ({breakpoints[bucket - 2]:0.######} < spread <= {breakpoints[bucket - 1]:0.######})",
    };
}
