using TradingStuff.ResearchService.Studies.VrpConditioning;
using TradingStuff.Tests.Volatility;
using TradingStuff.Volatility;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// Deterministic SPX/VIX inputs for the companion study's unit tests, built against the platform's
/// real session calendar so holidays and half days are inherited rather than assumed.
/// </summary>
internal static class VrpConditioningFixture
{
    public const string Calendar = SessionBars.CboeIndex;

    public static List<DateOnly> TradingDates(int count, DateOnly from) =>
        SessionBars.TradingDates(count, from, Calendar);

    /// <summary>
    /// Session realized-variance rows for <paramref name="dates"/>, with a baseline that drifts so
    /// no two sessions carry the same variance — a constant series would let an off-by-one in the
    /// label window pass unnoticed.
    /// </summary>
    public static List<RealizedVolatilityDay> SpxDays(IReadOnlyList<DateOnly> dates) =>
        VolatilityPresets.BuildSpxStudyTarget(
            SessionBars.Clock,
            dates.SelectMany((d, i) => SessionBars.Wiggly(
                d, baseline: 100.0 + i * 0.1, amplitude: 0.15 + (i % 11) * 0.02, calendar: Calendar)));

    /// <summary>A never-zero, never-constant VIX series; a constant VIX degenerates the z-scores.</summary>
    public static Dictionary<DateOnly, double> Vix(IReadOnlyList<DateOnly> dates)
    {
        var dict = new Dictionary<DateOnly, double>();
        for (var i = 0; i < dates.Count; i++)
        {
            dict[dates[i]] = 15.0 + Math.Sin(i * 0.3) * 4.0 + Math.Cos(i * 0.11) * 2.0 + i * 0.01;
        }

        return dict;
    }

    /// <summary>The completed sessions the builder itself will index, in the same order.</summary>
    public static List<RealizedVolatilityDay> Usable(IEnumerable<RealizedVolatilityDay> days) =>
        [.. days.Where(d => d.IsComplete && d.TotalVariance > 0.0 && d.SessionClose > 0.0).OrderBy(d => d.Date)];

    public static List<VrpConditioningRawRow> Rows(IReadOnlyList<DateOnly> dates) =>
        VrpConditioningFeatureBuilder.BuildRawRows(SpxDays(dates), Vix(dates));
}
