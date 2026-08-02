using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.Volatility;

namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// One decision date's features, its implied leg, and its 21-trading-day forward label.
/// </summary>
/// <remarks>
/// <para>
/// <b>The temporal contract, in one place.</b> The decision is made at the CLOSE of
/// <see cref="Date"/>. Everything from <see cref="LogRv"/> through <see cref="SpxDrawdown22"/> is
/// computed from sessions that closed at or before <see cref="Date"/>; <see cref="VixLevel"/> is
/// that day's VIX close, the last print before the label window opens. The label
/// (<see cref="LabelCumulativeVariance"/>) sums session realized variance over the 21 trading days
/// <see cref="LabelFrom"/> .. <see cref="LabelTo"/>, which begin the session AFTER
/// <see cref="Date"/>. No field on this record reads a bar timestamped inside the label window.
/// </para>
/// <para>
/// <b>Why the HAR terms are indexed differently from the parent study's.</b>
/// <see cref="VolResidualRawRow"/> labels day <c>t</c> itself, so its features stop at <c>t-1</c>.
/// Here the label starts at <c>t+1</c>, so day <c>t</c>'s own realized variance is legitimately in
/// the information set and is used. Reusing the parent row verbatim would have thrown that day away
/// and left the implied and realized legs a day apart for no reason.
/// </para>
/// </remarks>
/// <param name="LabelSessions">
/// How many completed sessions the label actually summed. Always
/// <see cref="VrpConditioningHorizon.LabelTradingDays"/> by construction — carried anyway so the
/// count is a value a test can assert on rather than an invariant a reader has to trust.
/// </param>
public sealed record VrpConditioningRawRow(
    DateOnly Date,
    DateOnly LabelFrom,
    DateOnly LabelTo,
    int LabelSessions,
    double LabelCumulativeVariance,
    double VixLevel,
    double ImpliedVariance,
    double LogImpliedVariance,
    double LogRv,
    double MeanLogRv5,
    double MeanLogRv22,
    double[] DayOfWeekDummies,
    double DaysToMonthlyOpex,
    double Vix5DayChange,
    double Spx1DayLogReturn,
    double SpxDrawdown22);

/// <summary>
/// Builds the companion study's dataset: one row per decision date that has both a full feature
/// history behind it and a full 21-trading-day label ahead of it.
/// </summary>
public static class VrpConditioningFeatureBuilder
{
    /// <summary>
    /// One row per trading day with 22 prior complete SPX sessions, an aligned VIX close, and 21
    /// complete SPX sessions AFTER it. A day missing any of those is skipped — never fabricated,
    /// never padded with a shorter label — and the caller reports the resulting count as coverage
    /// (docs/LESSONS.md #3, "absence renders as health").
    /// </summary>
    /// <param name="spxDays">The session realized-variance series. Incomplete sessions are dropped first.</param>
    /// <param name="vixDailyClose">VIX daily closes keyed by trading date.</param>
    public static List<VrpConditioningRawRow> BuildRawRows(
        IReadOnlyList<RealizedVolatilityDay> spxDays,
        IReadOnlyDictionary<DateOnly, double> vixDailyClose)
    {
        ArgumentNullException.ThrowIfNull(spxDays);
        ArgumentNullException.ThrowIfNull(vixDailyClose);

        var ordered = spxDays
            .Where(d => d.IsComplete && d.TotalVariance > 0.0 && d.SessionClose > 0.0)
            .OrderBy(d => d.Date)
            .ToList();

        var rows = new List<VrpConditioningRawRow>();

        const int weekly = VrpConditioningHorizon.WeeklyWindow;
        const int monthly = VrpConditioningHorizon.MonthlyWindow;
        const int horizon = VrpConditioningHorizon.LabelTradingDays;

        // t indexes the DECISION date. The lower bound leaves `monthly - 1` sessions behind t so the
        // 22-session window t-21..t is complete (t itself is the 22nd). The upper bound leaves
        // `horizon` sessions ahead of t so the label window t+1..t+horizon is complete: the last
        // usable t is Count-1-horizon, hence the strict `t < Count - horizon`.
        for (var t = monthly - 1; t < ordered.Count - horizon; t++)
        {
            var day = ordered[t];
            var decisionDate = DateOnly.FromDateTime(day.Date);

            if (!vixDailyClose.TryGetValue(decisionDate, out var vix) || vix <= 0.0) continue;

            var dateMinus5 = DateOnly.FromDateTime(ordered[t - weekly].Date);
            if (!vixDailyClose.TryGetValue(dateMinus5, out var vixMinus5) || vixMinus5 <= 0.0) continue;

            // ---- LABEL: sessions t+1 .. t+horizon inclusive, exactly `horizon` of them ----
            var labelVariance = 0.0;
            var labelSessions = 0;
            for (var k = t + 1; k <= t + horizon; k++)
            {
                labelVariance += ordered[k].TotalVariance;
                labelSessions++;
            }

            if (labelSessions != horizon || labelVariance <= 0.0) continue;

            var labelFrom = DateOnly.FromDateTime(ordered[t + 1].Date);
            var labelTo = DateOnly.FromDateTime(ordered[t + horizon].Date);

            // ---- FEATURES: sessions <= t only ----
            // Windows are inclusive of t: t-4..t is five sessions, t-21..t is twenty-two.
            var window5 = ordered.Skip(t - weekly + 1).Take(weekly).ToList();
            var window22 = ordered.Skip(t - monthly + 1).Take(monthly).ToList();

            var logRv = Math.Log(day.TotalVariance);
            var meanLogRv5 = window5.Average(d => Math.Log(d.TotalVariance));
            var meanLogRv22 = window22.Average(d => Math.Log(d.TotalVariance));

            var spx1DayLogReturn = Math.Log(day.SessionClose / ordered[t - 1].SessionClose);

            // Drawdown from the running 22-session high, at or below zero by construction. The
            // registration names "recent SPX drawdown" as one of the three conditioning state
            // variables for this companion study.
            var peak = window22.Max(d => d.SessionClose);
            var drawdown = Math.Log(day.SessionClose / peak);

            var impliedVariance = VrpConditioningHorizon.ImpliedVarianceOverLabelHorizon(vix);

            rows.Add(new VrpConditioningRawRow(
                decisionDate,
                labelFrom,
                labelTo,
                labelSessions,
                labelVariance,
                vix,
                impliedVariance,
                Math.Log(impliedVariance),
                logRv,
                meanLogRv5,
                meanLogRv22,
                DayOfWeekDummies(decisionDate.DayOfWeek),
                VolResidualFeatureBuilder.DaysToNextThirdFriday(decisionDate),
                vix - vixMinus5,
                spx1DayLogReturn,
                drawdown));
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
        _ => [0, 0, 0, 0],
    };
}
