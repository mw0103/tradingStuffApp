using TradingStuff.Volatility;
using TradingStuff.Volatility.Forecasting;

namespace TradingStuff.ResearchService.Studies.VolResidual;

/// <summary>
/// One trading day's worth of registered features for the volatility-forecast-residual study, plus
/// its label. Every field here is computed from strictly prior sessions relative to
/// <see cref="Date"/> — see <see cref="VolResidualFeatureBuilder.BuildRawRows"/> for the index
/// arithmetic that guarantees it — except <see cref="DayOfWeekDummies"/> and
/// <see cref="DaysToMonthlyOpex"/>, which describe <see cref="Date"/> itself but are calendar facts
/// known arbitrarily far in advance and so carry no look-ahead of their own.
/// </summary>
/// <remarks>
/// Tier-0's HAR triplet is deliberately re-derived here rather than reused from
/// <see cref="TradingStuff.Volatility.Baselines.HarDatasetBuilder"/>: the registration defines it as
/// the MEAN of log RV over each window (<c>mean_i log(RV_i)</c>), whereas
/// <c>HarDatasetBuilder</c> computes the log of the MEAN RV level
/// (<c>log(mean_i RV_i)</c>) — a genuinely different quantity by a Jensen gap, not a
/// simplification. Using the general-purpose builder here would silently substitute an
/// unregistered feature definition.
/// </remarks>
public sealed record VolResidualRawRow(
    DateOnly Date,
    double ActualVariance,
    double LogRvDMinus1,
    double MeanLogRv5,
    double MeanLogRv22,
    double[] DayOfWeekDummies,
    double DaysToMonthlyOpex,
    double LogPriorVix2,
    double Vix5DayChange,
    double Spx1DayLogReturn,
    // Realized quarticity of session d-1, in the estimator's session-variance-squared units.
    // Carried for HARQ-style measurement-error attenuation (candidate A1): sqrt(RQ) proxies the
    // sampling error of that day's RV, so a model can trust a noisily-measured daily lag less.
    // Zero when the source day predates quarticity being persisted - consumers treat zero as
    // "unknown", which collapses the attenuation term to plain HAR-X behaviour for that row.
    double RqDMinus1 = 0.0,
    // Signed semivariances of session d-1 (candidate A2, SHAR): the sum of squared negative and
    // positive intraday returns, which partition TotalVariance exactly. Patton-Sheppard find the
    // downside half carries most of the predictive content.
    double DownsideVarianceDMinus1 = 0.0,
    double UpsideVarianceDMinus1 = 0.0,
    // Bipower variation and the jump component of session d-1 (candidate A3, HAR-CJ). BV estimates
    // the continuous part of quadratic variation; the jump residual mean-reverts faster, so one
    // coefficient across both blurs each.
    double BipowerDMinus1 = 0.0,
    double JumpDMinus1 = 0.0);

/// <summary>One VIX daily close, keyed by the SPX-calendar trading date it was observed on.</summary>
public sealed record VixDailyClose(DateOnly Date, double Close);

public static class VolResidualFeatureBuilder
{
    private const int WeeklyWindow = 5;
    private const int MonthlyWindow = 22;

    /// <summary>
    /// Builds one row per trading day with enough history (22 prior complete SPX sessions, plus VIX
    /// coverage) to compute every Tier-0/Tier-1 feature and label. A day is silently skipped — never
    /// fabricated — if any input it needs is incomplete, missing, or non-positive; the caller sees
    /// the resulting row count and reports it as coverage, per LESSONS.md #3 ("absence renders as
    /// health").
    /// </summary>
    public static List<VolResidualRawRow> BuildRawRows(
        IReadOnlyList<RealizedVolatilityDay> spxDays,
        IReadOnlyDictionary<DateOnly, double> vixDailyClose)
    {
        ArgumentNullException.ThrowIfNull(spxDays);
        ArgumentNullException.ThrowIfNull(vixDailyClose);

        var ordered = spxDays
            .Where(d => d.IsComplete && d.TotalVariance > 0.0 && d.SessionClose > 0.0)
            .OrderBy(d => d.Date)
            .ToList();

        var rows = new List<VolResidualRawRow>();

        for (var t = MonthlyWindow; t < ordered.Count; t++)
        {
            var day = ordered[t];
            var dateD = DateOnly.FromDateTime(day.Date);

            var rvDMinus1 = ordered[t - 1].TotalVariance;
            var logRvDMinus1 = Math.Log(rvDMinus1);

            var window5 = ordered.Skip(t - WeeklyWindow).Take(WeeklyWindow).ToList();
            var window22 = ordered.Skip(t - MonthlyWindow).Take(MonthlyWindow).ToList();
            var meanLogRv5 = window5.Average(d => Math.Log(d.TotalVariance));
            var meanLogRv22 = window22.Average(d => Math.Log(d.TotalVariance));

            var dateDMinus1 = DateOnly.FromDateTime(ordered[t - 1].Date);
            var dateDMinus6 = DateOnly.FromDateTime(ordered[t - 1 - WeeklyWindow].Date);

            if (!vixDailyClose.TryGetValue(dateDMinus1, out var vixDMinus1) || vixDMinus1 <= 0.0) continue;
            if (!vixDailyClose.TryGetValue(dateDMinus6, out var vixDMinus6) || vixDMinus6 <= 0.0) continue;

            var q = vixDMinus1 / 100.0 * (vixDMinus1 / 100.0);
            var logPriorVix2 = Math.Log(q);
            var vix5DayChange = vixDMinus1 - vixDMinus6;

            var spxCloseDMinus1 = ordered[t - 1].SessionClose;
            var spxCloseDMinus2 = ordered[t - 2].SessionClose;
            var spx1DayLogReturn = Math.Log(spxCloseDMinus1 / spxCloseDMinus2);

            rows.Add(new VolResidualRawRow(
                dateD,
                day.TotalVariance,
                logRvDMinus1,
                meanLogRv5,
                meanLogRv22,
                DayOfWeekDummies(dateD.DayOfWeek),
                DaysToNextThirdFriday(dateD),
                logPriorVix2,
                vix5DayChange,
                spx1DayLogReturn,
                ordered[t - 1].RealizedQuarticity,
                ordered[t - 1].DownsideVariance,
                ordered[t - 1].UpsideVariance,
                ordered[t - 1].BipowerVariation,
                ordered[t - 1].JumpVariation));
        }

        return rows;
    }

    /// <summary>Tue/Wed/Thu/Fri dummies; Monday is the reference level absorbed into the intercept.</summary>
    private static double[] DayOfWeekDummies(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Tuesday => [1, 0, 0, 0],
        DayOfWeek.Wednesday => [0, 1, 0, 0],
        DayOfWeek.Thursday => [0, 0, 1, 0],
        DayOfWeek.Friday => [0, 0, 0, 1],
        _ => [0, 0, 0, 0], // Monday (reference) or, defensively, a weekend that should not appear
    };

    /// <summary>
    /// Calendar days from <paramref name="date"/> to the next monthly-options expiration (the third
    /// Friday of a month), inclusive of <paramref name="date"/> itself if it IS that Friday. A
    /// calendar-day count rather than a trading-day count: simple, deterministic, and the
    /// registration does not specify which — see the study runner's report for this call-out.
    /// </summary>
    internal static int DaysToNextThirdFriday(DateOnly date)
    {
        var candidate = ThirdFridayOfMonth(date.Year, date.Month);
        if (candidate < date) candidate = ThirdFridayOfMonth(date.Year, date.Month, monthsAhead: 1);
        return candidate.DayNumber - date.DayNumber;
    }

    private static DateOnly ThirdFridayOfMonth(int year, int month, int monthsAhead = 0)
    {
        var first = new DateOnly(year, month, 1).AddMonths(monthsAhead);
        var offsetToFirstFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offsetToFirstFriday + 14);
    }
}

/// <summary>One fold's train/test rows for this study's dataset, after the registered purge.</summary>
public sealed record VolResidualFoldSplit(WalkForwardFold Fold, List<VolResidualRawRow> Train, List<VolResidualRawRow> Test);

/// <summary>
/// Splits this study's dataset into the registered walk-forward folds
/// (<see cref="WalkForwardFold.Registered"/>), purging the tail of training the same way
/// <see cref="TradingStuff.Volatility.Forecasting.WalkForwardSplitter"/> does for the general HAR
/// pipeline.
/// </summary>
/// <remarks>
/// The registered folds carry a full validation-year GAP between train and test (e.g. F1 trains
/// through 2016, tests from 2018 — 2017 sits unused in between), so unlike a design with train
/// immediately abutting test, the purge here is a small extra safety margin on top of that gap, not
/// the only thing preventing a leaked boundary. No embargo step is applied for the same reason
/// <see cref="TradingStuff.Volatility.Forecasting.WalkForwardEvaluation.Score"/> never reads a
/// fold's validation block: nothing downstream of this splitter looks at it, so trimming it is a
/// no-op given how it is discarded regardless. Only the two blocks something actually consumes —
/// train and test — are produced here.
/// </remarks>
public static class VolResidualSplitter
{
    public static List<VolResidualFoldSplit> Split(
        IReadOnlyList<VolResidualRawRow> rows, IReadOnlyList<WalkForwardFold> folds, int purgeDays = 5) =>
        Split(rows, r => r.Date, folds, purgeDays)
            .Select(s => new VolResidualFoldSplit(s.Fold, s.Train, s.Test))
            .ToList();

    /// <summary>
    /// The same walk-forward cut, over any row type that can name its own date. Added so the
    /// companion VRP-conditioning study reuses this purge rather than growing a second, subtly
    /// different copy of it — the failure mode the parent study's own remarks describe.
    /// </summary>
    /// <param name="purgeDays">
    /// Rows dropped from the tail of TRAINING. It is the caller's job to pass a purge at least as
    /// large as its label horizon: a row dated <c>s</c> whose label reaches <c>s + h</c> leaks into
    /// whatever block follows unless at least <c>h</c> rows are removed.
    /// </param>
    public static List<(WalkForwardFold Fold, List<T> Train, List<T> Test)> Split<T>(
        IReadOnlyList<T> rows, Func<T, DateOnly> dateOf, IReadOnlyList<WalkForwardFold> folds, int purgeDays)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(dateOf);
        ArgumentNullException.ThrowIfNull(folds);

        var ordered = rows.OrderBy(dateOf).ToList();
        var result = new List<(WalkForwardFold, List<T>, List<T>)>();

        foreach (var fold in folds)
        {
            var trainStart = DateOnly.FromDateTime(fold.TrainStart);
            var trainEnd = DateOnly.FromDateTime(fold.TrainEnd);
            var testStart = DateOnly.FromDateTime(fold.TestStart);
            var testEnd = DateOnly.FromDateTime(fold.TestEnd);

            var train = ordered.Where(r => dateOf(r) >= trainStart && dateOf(r) <= trainEnd).ToList();
            var test = ordered.Where(r => dateOf(r) >= testStart && dateOf(r) <= testEnd).ToList();

            var purge = Math.Min(purgeDays, train.Count);
            if (purge > 0) train.RemoveRange(train.Count - purge, purge);

            result.Add((fold, train, test));
        }

        return result;
    }
}
