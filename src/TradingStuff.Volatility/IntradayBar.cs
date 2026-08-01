using System;

namespace TradingStuff.Volatility
{
    /// <summary>
    /// Numeric-friendly intraday bar used by the realized volatility estimators.
    /// Deliberately decoupled from persistence types so the estimator core never
    /// deals with decimal, nullable columns or storage concerns.
    /// </summary>
    /// <remarks>
    /// <see cref="Timestamp"/> is a UTC instant, per the platform's UTC-canonical doctrine.
    /// An exchange-local wall-clock time here would be silently wrong rather than loudly
    /// wrong: the session filter would still match something, just the wrong hours of the
    /// wrong day.
    /// </remarks>
    public struct IntradayBar
    {
        /// <summary>The bar's instant, in UTC.</summary>
        public DateTime Timestamp { get; }
        public double Open { get; }
        public double High { get; }
        public double Low { get; }
        public double Close { get; }
        public long Volume { get; }

        /// <param name="timestamp">
        /// The bar's instant, in UTC. <see cref="DateTimeKind.Unspecified"/> is accepted and
        /// read as UTC; <see cref="DateTimeKind.Local"/> is rejected, because a local
        /// timestamp is the one mistake that produces a plausible series from the wrong hours.
        /// </param>
        public IntradayBar(DateTime timestamp, double open, double high, double low, double close, long volume = 0L)
        {
            if (timestamp.Kind == DateTimeKind.Local)
                throw new ArgumentException(
                    "Bar timestamps are UTC instants. A local time here would select the wrong " +
                    "session without failing.", "timestamp");

            Timestamp = timestamp;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }

        /// <summary>
        /// Bars with a non-positive price cannot produce a log return and are dropped
        /// before any estimator sees them.
        /// </summary>
        public bool HasUsablePrices
        {
            get { return Open > 0.0 && High > 0.0 && Low > 0.0 && Close > 0.0; }
        }
    }

    /// <summary>
    /// Whether a bar's timestamp marks the start or the end of the interval it covers.
    /// Vendors differ: FMP and ThetaData stamp the interval start, IBKR historical bars
    /// also stamp the start. Getting this wrong shifts every sampled price by one bar,
    /// which quietly biases the overnight return and the first/last intraday return.
    /// </summary>
    public enum BarTimestampConvention
    {
        BarStart = 0,
        BarEnd = 1
    }
}
