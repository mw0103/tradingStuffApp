using System;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Estimator policy for a session: how much of the open to discard, and when a session
    /// has too little usable data to be trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately says nothing about when a session opens or closes, which days are
    /// holidays, or which are half days. Those are calendar questions and
    /// <c>ISessionClock</c> is the platform's only answer to them; a second, weaker answer
    /// living here is exactly the drift that doctrine exists to prevent. What remains is
    /// genuinely the estimator's own: thresholds about data quality, not about time.
    /// </para>
    /// <para>
    /// This replaced a <c>SessionProfile</c> that hardcoded 09:30-16:00 wall clock and
    /// inferred half days from a tolerance in minutes. That inference was wrong twice over -
    /// it could not see a holiday at all, and it read a genuine early close as identical to a
    /// feed that simply stopped reporting.
    /// </para>
    /// </remarks>
    public class SessionQualityPolicy
    {
        /// <summary>
        /// Minutes skipped after the session opens. The opening auction print and the first
        /// minute or two of continuous trading carry enormous, non-representative variance;
        /// including them inflates realized variance on every single day.
        /// </summary>
        public int SkipMinutesAfterOpen { get; set; }

        /// <summary>
        /// Minimum number of sampled returns before a day is considered complete. Days below
        /// this are still emitted but carry <see cref="RealizedVolatilityDay.IsComplete"/> =
        /// false, so downstream code makes an explicit decision instead of silently training
        /// on a gap.
        /// </summary>
        public int MinimumReturnsPerDay { get; set; }

        /// <summary>
        /// Maximum share of sampled grid points that may reuse a previous bar before the
        /// session is treated as unreliable. A mid-session data hole produces zero returns
        /// rather than missing ones, so it biases realized variance downward without reducing
        /// the return count - the count alone cannot catch it.
        /// </summary>
        public double MaximumStaleSampleFraction { get; set; }

        public SessionQualityPolicy()
        {
            SkipMinutesAfterOpen = 1;
            MinimumReturnsPerDay = 20;
            MaximumStaleSampleFraction = 0.20;
        }

        /// <summary>US cash equities and ETFs. Correct for SPY.</summary>
        public static SessionQualityPolicy UsEquity()
        {
            return new SessionQualityPolicy();
        }

        /// <summary>
        /// The S&amp;P 500 index itself. The printed index open is stitched together from
        /// staggered constituent opening prints and is not a tradeable simultaneous price, so
        /// more of the open is discarded than for a single security.
        /// </summary>
        public static SessionQualityPolicy SpxIndex()
        {
            return new SessionQualityPolicy { SkipMinutesAfterOpen = 5 };
        }

        public void Validate()
        {
            if (SkipMinutesAfterOpen < 0)
                throw new InvalidOperationException("SkipMinutesAfterOpen cannot be negative.");
            if (MinimumReturnsPerDay < 0)
                throw new InvalidOperationException("MinimumReturnsPerDay cannot be negative.");
            if (MaximumStaleSampleFraction < 0.0 || MaximumStaleSampleFraction > 1.0)
                throw new InvalidOperationException("MaximumStaleSampleFraction must be a share between 0 and 1.");
        }
    }
}
