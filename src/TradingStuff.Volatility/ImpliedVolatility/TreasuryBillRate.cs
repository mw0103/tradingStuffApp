using System;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    /// <summary>
    /// Converts a T-bill discount rate as published (FRED DTB4WK: percent, bank-discount
    /// basis, actual/360) into the continuously compounded actual/365 rate the model-free
    /// variance calculation discounts with (frozen construction § 7).
    /// </summary>
    public static class TreasuryBillRate
    {
        /// <summary>
        /// A 4-week bill quoted at discount rate d pays par in 28 days for a price of
        /// 1 - d * 28/360; the continuously compounded annualized equivalent is
        /// -ln(price) * 365/28.
        /// </summary>
        public static double ContinuousFromDiscount(double discountRatePercent, int tenorDays = 28)
        {
            if (tenorDays <= 0) throw new ArgumentOutOfRangeException("tenorDays");

            var price = 1.0 - (discountRatePercent / 100.0) * tenorDays / 360.0;

            if (price <= 0.0)
                throw new ArgumentOutOfRangeException("discountRatePercent",
                    "The discount rate implies a non-positive bill price.");

            return -Math.Log(price) * 365.0 / tenorDays;
        }
    }
}
