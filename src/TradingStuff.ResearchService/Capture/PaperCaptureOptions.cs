namespace TradingStuff.ResearchService.Capture;

/// <summary>
/// <c>PaperCapture:*</c> — the post-close raw capture pass (docs/plans/paper-test/plan-c-capture.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here can cause an order, and that is why it defaults ON</b> where
/// <c>PaperAutomation:Enabled</c> defaults off. That opt-in guards a surface that TRADES, so its
/// safe direction is "do nothing". This one guards a surface that only READS, and its safe direction
/// is the opposite: the fills, margin and positions of a session that was not captured cannot be
/// reconstructed afterwards, while a capture nobody wanted costs two HTTP reads a day. Missing
/// capture is forever; a spurious row is not.
/// </para>
/// <para>
/// It still degrades to a no-op without a <c>trading</c> connection string, and every read goes to
/// the gateway's read-only account surface — there is no path from this component to
/// <c>placeOrder</c>, to <c>PaperAutomationService</c>, or to any signal or gate.
/// </para>
/// <para>
/// <see cref="Enabled"/> is a string for the reason the three existing opt-ins are: binding to
/// <c>bool</c> makes an unrecognised value either throw at startup or land as the default depending
/// on the binder, and here the exact string is what turns it OFF.
/// </para>
/// </remarks>
public sealed class PaperCaptureOptions
{
    /// <summary>Exactly <c>"false"</c> switches the pass off. Anything else, including null, leaves it on.</summary>
    public string? Enabled { get; set; }

    /// <summary>True unless the exact opt-out value is present.</summary>
    public bool IsEnabled => !string.Equals(Enabled, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>ISessionClock</c> calendar whose closes trigger a capture. <c>NYSE</c>, matching
    /// <c>PaperAutomation:Calendar</c> — the account being captured is the one that calendar's
    /// sessions traded, and capturing on a different calendar's close would date the record wrong.
    /// </summary>
    public string Calendar { get; set; } = "NYSE";

    /// <summary>Only sessions with this label trigger a capture. RTH: there is no separate overnight paper close.</summary>
    public string SessionLabel { get; set; } = "RTH";

    /// <summary>Seconds between passes. A pass with nothing due does no work and no I/O.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// How long after the session close to snapshot.
    /// </summary>
    /// <remarks>
    /// Not zero: TWS settles the day's executions and recomputes margin for some minutes after the
    /// bell, and a snapshot taken at the close records a state that is about to change. Fifteen
    /// minutes is comfortably past that and still the same evening.
    /// </remarks>
    public int CloseDelayMinutes { get; set; } = 15;

    /// <summary>
    /// How many closed SESSIONS back a pass will still try to capture.
    /// </summary>
    /// <remarks>
    /// Sessions, not calendar days, so a long weekend or a holiday cannot silently shrink the
    /// window — three calendar days back from a Tuesday reaches Saturday and loses Friday's close.
    /// This is the recovery window, and the reason an evening with the gateway down is not a
    /// permanent hole: the next pass that finds the gateway up captures every uncaptured session
    /// inside it. Reaching much further back would only manufacture refusals, since TWS serves
    /// executions for the current and immediately preceding days and not indefinitely.
    /// </remarks>
    public int LookbackSessions { get; set; } = 3;

    /// <summary>
    /// The account to capture. Null reads the account the gateway is configured to trade, which is
    /// the intended setting — naming one here only matters for a TWS session managing several.
    /// </summary>
    public string? AccountId { get; set; }
}
