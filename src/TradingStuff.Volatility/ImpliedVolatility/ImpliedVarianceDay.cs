using System;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>
    /// One day's constant-maturity model-free implied variance for a symbol.
    /// </summary>
    public class ImpliedVarianceDay
    {
        public string Symbol { get; set; }
        public DateTime Date { get; set; }

        /// <summary>Annualized model-free implied variance at the target maturity.</summary>
        public double ImpliedVariance { get; set; }

        public double ImpliedVolatility
        {
            get { return Math.Sqrt(Math.Max(ImpliedVariance, 0.0)); }
        }

        public int TargetDays { get; set; }
        public double NearTermDays { get; set; }
        public double NextTermDays { get; set; }
        public int StrikesUsed { get; set; }

        /// <summary>
        /// Widest median strike spacing among the expirations used. A coarse grid
        /// inflates the discretized integral, so a series whose spacing varies carries a
        /// bias that moves around with it rather than a constant one that would wash out.
        /// </summary>
        public double WidestStrikeSpacing { get; set; }

        public bool IsExtrapolated { get; set; }

        /// <summary>False when the day could not be computed; <see cref="Note"/> says why.</summary>
        public bool IsUsable { get; set; }

        /// <summary>Reason the day is unusable, or any caveat attached to a usable one.</summary>
        public string Note { get; set; }
    }
}
