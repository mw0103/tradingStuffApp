namespace TradingStuff.ResearchService.Automation;

/// <summary>
/// <c>PaperAutomation:*</c>. Off unless <see cref="Enabled"/> is the exact string <c>true</c>, the
/// same shape as <c>Execution:Router</c>, <c>Portfolio:Source</c> and <c>MarketData:Source</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here can, on its own, cause an order.</b> Every value in this class is a narrowing:
/// enabling automation still requires the execution plane to be coherently configured (see
/// <see cref="PaperAutomationArming"/>), the broker to be connected on a <c>DU</c> account, a named
/// session, and a signal that asks for a position. Turning <see cref="Enabled"/> on is necessary and
/// nowhere near sufficient.
/// </para>
/// <para>
/// <b>Enabled is a string, not a bool, on purpose.</b> Binding to <c>bool</c> means an unrecognised
/// value ("yes", "1 ", "True!") throws at startup or — worse, depending on the binder — lands as the
/// default. Requiring the exact string and defaulting to off makes every unrecognised value degrade
/// in the safe direction, which is the pattern the three existing opt-ins already use and the one
/// operators here already know.
/// </para>
/// </remarks>
public sealed class PaperAutomationOptions
{
    /// <summary>Exactly <c>"true"</c> arms the loop. Anything else, including null, leaves it off.</summary>
    public string? Enabled { get; set; }

    /// <summary>True only for the exact opt-in value.</summary>
    public bool IsEnabled => string.Equals(Enabled, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Seconds between evaluations. Every evaluation writes a decision row.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Orders this process may submit in one trading date. A rail, not a tuning knob: when it is
    /// spent the loop refuses to arm and says so — it does not reset, wrap, or degrade to "one more".
    /// </summary>
    public int MaxOrdersPerSession { get; set; } = 2;

    /// <summary>
    /// The underlying to trade. SPY deliberately, NOT SPX/SPXW: every SPXW combo sent on 2026-07-31
    /// parked at <c>PreSubmitted</c> at TWS with no error while SPY combos on the identical code path
    /// filled (docs/STATE.md, "Open question"). An MVP loop that must demonstrably work trades the
    /// instrument that demonstrably works.
    /// </summary>
    public string Underlying { get; set; } = "SPY";

    /// <summary>SPY lists one series, so this matches the underlying. Present so the field is not implicit.</summary>
    public string TradingClass { get; set; } = "SPY";

    /// <summary>
    /// The <c>ISessionClock</c> calendar key automation acts inside. <c>NYSE</c> (09:30–16:00 ET),
    /// not <c>NYSE_EXTENDED</c>: SPY options have no book outside the regular session — pre-market
    /// bid/ask is 0/0 — so acting in the extended window can only produce refusals.
    /// </summary>
    public string Calendar { get; set; } = "NYSE";

    /// <summary>Days ahead to aim the expiration. TWS answers with the nearest LISTED expiration to it.</summary>
    public int TargetDaysToExpiration { get; set; } = 7;

    /// <summary>Chain half-width as a fraction of spot. Wide enough to contain both strikes at any SPY level.</summary>
    public decimal MoneynessHalfWidth { get; set; } = 0.02m;

    /// <summary>Dollar distance between the long and short strike. The spread's width, and its max value.</summary>
    public decimal SpreadWidthDollars { get; set; } = 1m;

    /// <summary>Added to the natural debit so the limit is marketable. Never an <c>OrderType.Market</c> combo.</summary>
    public decimal MarketableBufferDollars { get; set; } = 0.05m;

    /// <summary>
    /// The most this loop will ever pay for one spread. A defined-risk debit vertical's maximum loss
    /// IS the debit, so this is the loss cap expressed directly. Checked before submission — the risk
    /// service's own limits are a second, independent gate, not a substitute for this one.
    /// </summary>
    public decimal MaxDebitDollars { get; set; } = 0.75m;

    /// <summary>Contracts per leg. One. Raising it is not an MVP change.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Which structure the loop plans. <c>debit-vertical</c> (the original MVP shape) or
    /// <c>short-vol-credit-put</c> (the put credit spread the VRP hypothesis is actually about).
    /// An unknown value refuses at evaluation time - never silently falls back to either.
    /// </summary>
    public string Structure { get; set; } = Structures.DebitVertical;

    public static class Structures
    {
        public const string DebitVertical = "debit-vertical";
        public const string ShortVolCreditPut = "short-vol-credit-put";
    }

    /// <summary>
    /// Days ahead to aim the short-vol expiration. ~30 calendar days approximates the study's
    /// 21-trading-day horizon, so the paper structure tests the window the research was run at.
    /// </summary>
    public int ShortVolTargetDaysToExpiration { get; set; } = 30;

    /// <summary>
    /// Chain half-width for the short-vol window. Wider than the debit vertical's: the short put
    /// sits OTM by <see cref="ShortVolOtmOffsetFraction"/> and its wing a further width below.
    /// </summary>
    public decimal ShortVolMoneynessHalfWidth { get; set; } = 0.05m;

    /// <summary>How far below spot the short put sits, as a fraction. 2%: premium-rich but not at-the-money.</summary>
    public decimal ShortVolOtmOffsetFraction { get; set; } = 0.02m;

    /// <summary>
    /// The most this loop will risk on one credit spread, per share: strike width minus credit
    /// received. The short-vol counterpart of <see cref="MaxDebitDollars"/>, checked before
    /// submission; the risk service recomputes its own version independently.
    /// </summary>
    public decimal ShortVolMaxRiskDollars { get; set; } = 0.85m;
}
