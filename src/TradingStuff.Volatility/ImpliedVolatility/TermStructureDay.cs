using System;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>One constant-maturity point of the A4 term structure on one session date.</summary>
    public class TermStructurePoint
    {
        public int TargetDays { get; set; }

        /// <summary>Annualized implied variance at the target maturity; meaningful only when usable.</summary>
        public double Variance { get; set; }

        public double NearTermDays { get; set; }
        public double NextTermDays { get; set; }
        public int StrikesUsed { get; set; }

        public bool IsUsable { get; set; }

        /// <summary>Why the point could not be built, when it could not. Absence renders as absence.</summary>
        public string Note { get; set; }
    }

    /// <summary>
    /// The two constant-maturity points and the slope for one session date, per the frozen
    /// construction (docs/research/a4-slope-construction.md). A date that cannot be computed
    /// carries its reason rather than disappearing.
    /// </summary>
    public class TermStructureDay
    {
        public DateTime Date { get; set; }

        public TermStructurePoint NineDay { get; set; }
        public TermStructurePoint ThirtyDay { get; set; }

        public bool IsUsable
        {
            get
            {
                return NineDay != null && NineDay.IsUsable
                    && ThirtyDay != null && ThirtyDay.IsUsable
                    && NineDay.Variance > 0.0 && ThirtyDay.Variance > 0.0;
            }
        }

        /// <summary>
        /// The frozen primary slope definition: S = ln(sigma_9 / sigma_30)
        /// = 0.5 * ln(var_9 / var_30). Positive means the short-dated point sits above the
        /// 30-day point — an inverted term structure.
        /// </summary>
        public double? Slope
        {
            get
            {
                if (!IsUsable) return null;
                return 0.5 * Math.Log(NineDay.Variance / ThirtyDay.Variance);
            }
        }
    }
}
