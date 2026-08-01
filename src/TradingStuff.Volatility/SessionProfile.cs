using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Describes the regular trading session for a symbol. Realized volatility is only
    /// meaningful over a consistently defined session: pre/post market prints and the
    /// opening auction have wildly different microstructure to the continuous session
    /// and will dominate the estimator if they are left in.
    /// </summary>
    public class SessionProfile
    {
        /// <summary>Start of the regular session, in the exchange's local time.</summary>
        public TimeSpan RegularOpen { get; set; }

        /// <summary>End of the regular session, in the exchange's local time.</summary>
        public TimeSpan RegularClose { get; set; }

        /// <summary>
        /// Minutes skipped after the open. The opening auction print and the first
        /// minute or two of continuous trading carry enormous, non-representative
        /// variance; including them inflates realized variance on every single day.
        /// </summary>
        public int SkipMinutesAfterOpen { get; set; }

        /// <summary>
        /// A session whose last observed bar falls more than this many minutes before
        /// <see cref="RegularClose"/> is treated as a short session (US markets close at
        /// 13:00 on roughly three days a year). Short sessions are flagged rather than
        /// rescaled, because intraday volatility is U-shaped and a flat time rescale
        /// would be wrong.
        /// </summary>
        public int ShortSessionToleranceMinutes { get; set; }

        /// <summary>
        /// Minimum number of sampled returns required before a day is considered
        /// complete. Days below this threshold are still emitted but carry
        /// <see cref="RealizedVolatilityDay.IsComplete"/> = false so that downstream
        /// code makes an explicit decision instead of silently training on a gap.
        /// </summary>
        public int MinimumReturnsPerDay { get; set; }

        /// <summary>
        /// Maximum share of sampled grid points that may reuse a previous bar before the
        /// session is treated as unreliable. A mid-session data hole produces zero
        /// returns rather than missing ones, so it biases realized variance downward
        /// without reducing the return count - the count alone cannot catch it.
        /// </summary>
        public double MaximumStaleSampleFraction { get; set; }

        /// <summary>Dates explicitly known to be short sessions, if a calendar is available.</summary>
        public HashSet<DateTime> KnownShortSessions { get; private set; }

        public SessionProfile()
        {
            RegularOpen = new TimeSpan(9, 30, 0);
            RegularClose = new TimeSpan(16, 0, 0);
            SkipMinutesAfterOpen = 1;
            ShortSessionToleranceMinutes = 60;
            MinimumReturnsPerDay = 20;
            MaximumStaleSampleFraction = 0.20;
            KnownShortSessions = new HashSet<DateTime>();
        }

        /// <summary>US cash equities / ETFs: 09:30-16:00 ET. Correct for SPY.</summary>
        public static SessionProfile UsEquity()
        {
            return new SessionProfile();
        }

        /// <summary>
        /// The S&amp;P 500 index itself. The index level is disseminated across the same
        /// core hours, but the printed "open" is stitched together from staggered
        /// constituent opening prints and is not a tradeable simultaneous price, so we
        /// skip further into the session than we do for SPY.
        /// </summary>
        public static SessionProfile SpxIndex()
        {
            return new SessionProfile
            {
                RegularOpen = new TimeSpan(9, 30, 0),
                RegularClose = new TimeSpan(16, 0, 0),
                SkipMinutesAfterOpen = 5,
                ShortSessionToleranceMinutes = 60,
                MinimumReturnsPerDay = 20
            };
        }

        public TimeSpan EffectiveOpen
        {
            get { return RegularOpen.Add(TimeSpan.FromMinutes(SkipMinutesAfterOpen)); }
        }

        public bool IsInRegularSession(DateTime timestamp)
        {
            var t = timestamp.TimeOfDay;
            return t >= EffectiveOpen && t <= RegularClose;
        }
    }
}
