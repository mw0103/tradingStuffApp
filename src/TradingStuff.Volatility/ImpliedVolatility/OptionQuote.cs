using System;
using System.Collections.Generic;

namespace TradingStuff.Volatility.ImpliedVolatility
{
    public enum OptionRight
    {
        Call = 0,
        Put = 1
    }

    /// <summary>A single option contract's best bid and offer at a point in time.</summary>
    public struct OptionQuote
    {
        public double Strike { get; }
        public OptionRight Right { get; }
        public double Bid { get; }
        public double Ask { get; }

        public OptionQuote(double strike, OptionRight right, double bid, double ask)
        {
            Strike = strike;
            Right = right;
            Bid = bid;
            Ask = ask;
        }

        /// <summary>
        /// Midpoint of the quoted spread, which is what the model-free calculation
        /// integrates. Using the trade price instead would introduce bid-ask bounce into
        /// a measure that is supposed to be a clean expectation.
        /// </summary>
        public double Mid
        {
            get { return (Bid + Ask) / 2.0; }
        }

        /// <summary>
        /// A zero bid means there is no real market, only an offer to sell. The CBOE
        /// methodology excludes these outright rather than treating the midpoint as a
        /// price, because half the offer on a worthless option is not an expectation of
        /// anything.
        /// </summary>
        public bool HasTwoSidedMarket
        {
            get { return Bid > 0.0 && Ask > 0.0 && Ask >= Bid; }
        }
    }

    /// <summary>
    /// All quotes for one root and expiration at one observation time.
    /// </summary>
    public class OptionChainSlice
    {
        public string Root { get; set; }

        /// <summary>Observation timestamp, in exchange local time.</summary>
        public DateTime ObservedAt { get; set; }

        /// <summary>
        /// Settlement moment for this expiration. SPX monthly options settle against the
        /// opening print on expiration morning while SPXW weeklies settle at the close,
        /// a difference of most of a trading day - material at short maturities where it
        /// is a large share of the remaining life.
        /// </summary>
        public DateTime SettlesAt { get; set; }

        public List<OptionQuote> Quotes { get; private set; }

        public OptionChainSlice()
        {
            Quotes = new List<OptionQuote>();
        }

        /// <summary>Time to settlement in years, on an actual/365 basis measured to the minute.</summary>
        public double TimeToExpiryYears
        {
            get
            {
                var minutes = (SettlesAt - ObservedAt).TotalMinutes;
                return minutes / (365.0 * 1440.0);
            }
        }
    }
}
