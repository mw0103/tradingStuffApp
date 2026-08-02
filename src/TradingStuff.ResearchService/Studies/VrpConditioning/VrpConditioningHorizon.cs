namespace TradingStuff.ResearchService.Studies.VrpConditioning;

/// <summary>
/// Every constant that defines the companion study's horizon, and the one conversion that puts an
/// implied and a realized quantity on the same basis. Frozen here rather than spread across the
/// runner so a reader can check the arithmetic in one place — an off-by-one in
/// <see cref="LabelTradingDays"/> silently changes every number this study produces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this study exists.</b> The parent study
/// (<c>docs/research/volatility-forecast-residual-study.md</c>) forecasts ONE 6.5-hour session. A
/// 30-60 DTE short-volatility decision cannot be informed by a one-session forecast, so the
/// registration's "Companion study: VrpConditioningStudy" section defines the multi-week version:
/// <c>VIX^2(t) - RV(t, t+21d)</c>, daily grain, sharing this pipeline entirely.
/// </para>
/// <para>
/// <b>The decision timestamp is the CLOSE of day t.</b> The label spans <c>t+1 .. t+21</c>, so the
/// window opens after the decision is made. Every feature is computed from sessions that have
/// already closed at or before <c>t</c>, and the VIX leg is the close of day <c>t</c> — the last
/// VIX print before the window opens, and never a print from inside it. This is the reading of the
/// registration's "use the prior close, never same-day" that keeps "VIX^2 at t" literally true:
/// "same-day" means a day inside the label window, not day <c>t</c> itself.
/// </para>
/// </remarks>
public static class VrpConditioningHorizon
{
    /// <summary>
    /// The label horizon, in TRADING days: the label sums session realized variance over the 21
    /// trading days <c>t+1 .. t+21</c> inclusive. Not calendar days, and not 21 rows of whatever the
    /// series happens to contain — the builder indexes the completed-session series directly, so a
    /// holiday shifts the calendar span and never the session count.
    /// </summary>
    public const int LabelTradingDays = 21;

    /// <summary>Trading days per year, for de-annualizing VIX and re-annualizing realized variance.</summary>
    public const double TradingDaysPerYear = 252.0;

    /// <summary>
    /// The HAR monthly window, in trading days. Also the warm-up: a row needs this many sessions of
    /// history behind it before any feature is defined.
    /// </summary>
    public const int MonthlyWindow = 22;

    /// <summary>The HAR weekly window, in trading days.</summary>
    public const int WeeklyWindow = 5;

    /// <summary>
    /// Newey-West truncation lag for the OVERLAPPING daily series. The registration fixes lag 5 for
    /// the 1-day label and lag 9 for the 5-day label — that is <c>4 + h</c>, and the requirement
    /// that the lag reach at least <c>h - 1</c> (the mechanical overlap) is satisfied with room to
    /// spare. Extending the same rule to <c>h = 21</c> gives 25, which is what this study uses. It
    /// is stated rather than derived at the call site because "the lag must be at least the horizon
    /// minus one" is the property that matters and 25 &gt;= 20 is the check a reader should be able
    /// to make without reading code.
    /// </summary>
    public const int OverlappingHacLag = 4 + LabelTradingDays;

    /// <summary>
    /// Newey-West truncation lag for the NON-OVERLAPPING (stride-thinned) series. After thinning by
    /// <see cref="LabelTradingDays"/> each observation is one whole, disjoint window, so the horizon
    /// measured in observations of the thinned series is 1 — and the registration's rule for
    /// <c>h = 1</c> is lag 5. The mechanical overlap is gone; what is left is genuine volatility
    /// persistence across adjacent quarters, which a lag of 5 thinned observations (over a year of
    /// calendar time) is generous about.
    /// </summary>
    public const int NonOverlappingHacLag = 5;

    /// <summary>
    /// Mean block length for the stationary block bootstrap, in trading days. The registered daily
    /// study uses 20 — approximately one horizon for a 1-day label, in the sense that it comfortably
    /// exceeds the dependence the label itself induces (none) plus the dependence volatility carries
    /// anyway. Here the label MECHANICALLY induces MA(20) dependence: two decisions 20 days apart
    /// share 1 of 21 label days. A block shorter than 21 would routinely cut through a single
    /// label's own overlap structure and understate the variance of the mean, so the block is set to
    /// three horizons (63 trading days, one quarter). Stated, not tuned: it was chosen before any
    /// result was computed and is not varied.
    /// </summary>
    public const double BootstrapMeanBlockLength = 3.0 * LabelTradingDays;

    /// <summary>
    /// Rows purged from the tail of each fold's training block. A training row dated <c>s</c> carries
    /// a label reaching to <c>s + 21</c>, so any purge below 21 leaves training labels overlapping
    /// the block that follows. The registration purges 5 for the 1-day label and 10 for the 5-day
    /// label — twice the horizon — so twice 21 is the consistent extension. (The registered folds
    /// already leave a full validation YEAR between train and test, so this is a second safety
    /// margin, not the only one.)
    /// </summary>
    public const int PurgeRows = 2 * LabelTradingDays;

    /// <summary>
    /// Fixed so an identical rerun produces an identical interval, for the same reason the parent
    /// study's seed is a constant rather than a parameter: a seed a caller can pass is a seed a
    /// caller can search over.
    /// </summary>
    public const ulong BootstrapSeed = 0x565250434F4E4431UL; // "VRPCOND1"

    /// <summary>
    /// Converts a VIX index level into implied variance on the study's 21-TRADING-DAY basis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conversion, stated.</b> <c>VIX/100</c> is an ANNUALIZED volatility, so
    /// <c>(VIX/100)^2</c> is annualized variance. The label is a 21-trading-day CUMULATIVE variance.
    /// The conversion is therefore a pure de-annualization,
    /// <c>impliedVar_21 = (VIX/100)^2 * 21/252</c>, and nothing else.
    /// </para>
    /// <para>
    /// <b>Why no maturity interpolation.</b> VIX is a 30-CALENDAR-day construct. Thirty calendar
    /// days contain, on average, <c>30 * 252/365 = 20.7</c> trading days — within a third of a day of
    /// the 21-trading-day label. The two horizons are already the same length to within rounding, so
    /// interpolating onto an exact 21-day maturity would need a second index (VIX9D / VIX3M) and a
    /// term-structure assumption, and would move the answer by well under a percent. Not doing it is
    /// a deliberate, stated simplification, not an oversight; the residual maturity mismatch is
    /// absorbed by <c>CalibratedVixFit</c>'s intercept and slope in the calibrated arm, exactly as
    /// the registration says B1's parameters absorb "30-day-vs-1-day maturity mismatch,
    /// calendar-vs-session time, overnight and weekend variance, and annualization scaling all at
    /// once".
    /// </para>
    /// <para>
    /// <b>What this conversion does NOT fix, and it matters.</b> VIX prices CALENDAR time — it
    /// includes overnight and weekend variance. The label is SESSION realized variance with the
    /// overnight gap deliberately excluded
    /// (<see cref="TradingStuff.Volatility.VolatilityPresets.SpxStudyTarget"/>). So
    /// <c>implied - realized</c> computed here is structurally WIDER than a true variance risk
    /// premium by the whole overnight/weekend component. That bias is a roughly constant level
    /// shift, which is harmless for the question this study asks — whether SORTING on the spread
    /// sorts outcomes — and fatal to any claim about the LEVEL of the premium. It is reported as a
    /// limitation on the API response for that reason, not merely commented here.
    /// </para>
    /// </remarks>
    public static double ImpliedVarianceOverLabelHorizon(double vixIndexLevel)
    {
        var annualizedVariance = vixIndexLevel / 100.0 * (vixIndexLevel / 100.0);
        return annualizedVariance * (LabelTradingDays / TradingDaysPerYear);
    }

    /// <summary>
    /// Annualizes a cumulative 21-trading-day variance back onto the scale VIX is quoted in, so a
    /// realized outcome and a strike can be compared as volatilities.
    /// </summary>
    public static double AnnualizedVolatilityFromLabel(double cumulativeVarianceOverLabelHorizon) =>
        Math.Sqrt(cumulativeVarianceOverLabelHorizon * (TradingDaysPerYear / LabelTradingDays));

    /// <summary>
    /// The variance-swap-style payoff to a SHORT variance position, per unit VEGA notional, in
    /// annualized volatility POINTS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A variance swap pays the long <c>N_var * (sigma^2 - K^2)</c>, where <c>K</c> is the strike in
    /// annualized volatility and <c>sigma</c> the annualized realized volatility. Market convention
    /// quotes size in vega notional, with <c>N_var = N_vega / (2K)</c>, so the short's payoff per
    /// unit vega notional is <c>(K^2 - sigma^2) / (2K)</c>. Multiplying by 100 expresses it in
    /// volatility points, the unit these numbers are actually discussed in.
    /// </para>
    /// <para>
    /// <b>This is a payoff formula, not a P&amp;L model.</b> There is no bid-ask, no execution, no
    /// slippage, no delta hedging, no replication error, no margin, no financing, no discounting and
    /// no capacity in it. A real short-variance position is not a variance swap and does not earn
    /// this. The number exists solely so that "does conditioning on the spread sort outcomes?" can
    /// be answered in an economically ordered unit rather than in raw variance. The API response
    /// carries that limitation as a field precisely because a P&amp;L-shaped number with no costs in
    /// it will otherwise be read as tradeable profit.
    /// </para>
    /// </remarks>
    /// <param name="strikeAnnualizedVol">The strike, as a decimal annualized volatility (VIX/100).</param>
    /// <param name="realizedAnnualizedVol">Realized annualized volatility over the label window, as a decimal.</param>
    public static double ShortVarianceSwapPayoffPerVegaNotional(
        double strikeAnnualizedVol, double realizedAnnualizedVol)
    {
        if (strikeAnnualizedVol <= 0.0) return 0.0;

        var payoff = (strikeAnnualizedVol * strikeAnnualizedVol - realizedAnnualizedVol * realizedAnnualizedVol)
                     / (2.0 * strikeAnnualizedVol);

        return 100.0 * payoff;
    }
}
