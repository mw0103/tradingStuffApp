using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TradingStuff.Volatility.ImpliedVolatility;

namespace TradingStuff.Volatility.ThetaData
{
    /// <summary>
    /// Resolves the moment an expiration settles.
    ///
    /// This is not a formality at short maturities. Standard monthly SPX options are
    /// AM-settled against the Special Opening Quotation on expiration morning, while the
    /// SPXW weeklies settle against the close. That is most of a trading day of
    /// difference, and at a twenty-three day near term it moves the time-to-expiry - and
    /// therefore the annualization - by enough to see.
    /// </summary>
    public class ExpirationSettlement
    {
        public TimeSpan MorningSettlementTime { get; set; }
        public TimeSpan AfternoonSettlementTime { get; set; }

        /// <summary>Roots whose options settle against the opening print.</summary>
        public HashSet<string> MorningSettledRoots { get; private set; }

        public ExpirationSettlement()
        {
            MorningSettlementTime = new TimeSpan(9, 30, 0);
            AfternoonSettlementTime = new TimeSpan(16, 0, 0);
            MorningSettledRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SPX" };
        }

        public DateTime SettlementFor(string root, DateTime expirationDate)
        {
            var time = MorningSettledRoots.Contains(root)
                ? MorningSettlementTime
                : AfternoonSettlementTime;

            return expirationDate.Date.Add(time);
        }
    }

    /// <summary>
    /// Converts a ThetaData bulk quote response into option chain slices.
    /// </summary>
    public class ThetaDataChainLoader
    {
        private readonly ThetaDataOptions _options;
        private readonly ExpirationSettlement _settlement;

        public ThetaDataChainLoader(ThetaDataOptions options = null, ExpirationSettlement settlement = null)
        {
            _options = options ?? new ThetaDataOptions();
            _settlement = settlement ?? new ExpirationSettlement();
        }

        /// <summary>
        /// Parses a bulk quote response into one slice per observation date.
        /// </summary>
        /// <param name="expectedUnderlyingLevel">
        /// Approximate level of the underlying. When supplied, the loader verifies that
        /// the strikes bracket it - the cheapest possible guard against
        /// <see cref="ThetaDataOptions.StrikeDivisor"/> being wrong, which would otherwise
        /// produce a chain that parses cleanly and is off by three orders of magnitude.
        /// </param>
        public List<OptionChainSlice> Parse(
            CsvTable table,
            string root,
            DateTime expiration,
            double? expectedUnderlyingLevel = null)
        {
            if (table == null) throw new ArgumentNullException("table");

            // v3 carries a single ISO timestamp; the older feed split it into a date and a
            // milliseconds-of-day column. Both spellings are resolved so a response from
            // either shape parses, and neither is read positionally.
            var timestampColumn = table.HasColumn("timestamp") ? table.RequireColumn("timestamp") : -1;
            var dateColumn = timestampColumn >= 0 ? -1 : table.RequireColumn("date");
            var strikeColumn = table.RequireColumn("strike");
            var rightColumn = table.RequireColumn("right");
            var bidColumn = table.RequireColumn("bid");
            var askColumn = table.RequireColumn("ask");
            var timeColumn = table.HasColumn("ms_of_day") ? table.RequireColumn("ms_of_day") : -1;

            var settlesAt = _settlement.SettlementFor(root, expiration);
            var byDate = new Dictionary<DateTime, OptionChainSlice>();

            foreach (var row in table.Rows)
            {
                var observed = timestampColumn >= 0
                    ? ParseTimestamp(CsvTable.GetString(row, timestampColumn))
                    : ParseDate(CsvTable.GetString(row, dateColumn));
                var date = observed.Date;
                var strike = CsvTable.GetDouble(row, strikeColumn) / _options.StrikeDivisor;
                var right = ParseRight(CsvTable.GetString(row, rightColumn));
                var bid = CsvTable.GetDouble(row, bidColumn);
                var ask = CsvTable.GetDouble(row, askColumn);

                if (strike <= 0.0) continue;

                OptionChainSlice slice;
                if (!byDate.TryGetValue(date, out slice))
                {
                    // A v3 timestamp already carries the time of day; the older shape needs
                    // it from ms_of_day, falling back to the configured snapshot time.
                    var observedAt = timestampColumn >= 0
                        ? observed
                        : timeColumn >= 0
                            ? date.AddMilliseconds(CsvTable.GetDouble(row, timeColumn))
                            : date.Add(_options.SnapshotTimeOfDay);

                    slice = new OptionChainSlice
                    {
                        Root = root,
                        ObservedAt = observedAt,
                        SettlesAt = settlesAt
                    };
                    byDate[date] = slice;
                }

                slice.Quotes.Add(new OptionQuote(strike, right, bid, ask));
            }

            var slices = byDate.Values.OrderBy(s => s.ObservedAt).ToList();

            if (expectedUnderlyingLevel.HasValue)
            {
                VerifyStrikeScale(slices, expectedUnderlyingLevel.Value, root, expiration);
            }

            return slices;
        }

        private void VerifyStrikeScale(
            IReadOnlyList<OptionChainSlice> slices, double expectedLevel, string root, DateTime expiration)
        {
            foreach (var slice in slices)
            {
                if (slice.Quotes.Count == 0) continue;

                var lowest = slice.Quotes.Min(q => q.Strike);
                var highest = slice.Quotes.Max(q => q.Strike);

                if (expectedLevel >= lowest * 0.5 && expectedLevel <= highest * 2.0) continue;

                throw new InvalidOperationException(string.Format(
                    "Strikes for {0} expiring {1:yyyy-MM-dd} span {2:F2} to {3:F2}, which does not bracket an " +
                    "underlying near {4:F2}. StrikeDivisor is currently {5}; ThetaData quotes strikes in tenths " +
                    "of a cent, so this is almost certainly a units mismatch.",
                    root, expiration, lowest, highest, expectedLevel, _options.StrikeDivisor));
            }
        }

        /// <summary>Parses an ISO timestamp, keeping its time of day.</summary>
        private static DateTime ParseTimestamp(string value)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException(string.Format("Could not parse '{0}' as a timestamp.", value));
        }

        private static DateTime ParseDate(string value)
        {
            DateTime parsed;
            if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
            {
                return parsed;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed.Date;
            }

            throw new InvalidOperationException(string.Format("Could not parse '{0}' as a date.", value));
        }

        private static OptionRight ParseRight(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException("The right column was empty.");

            var first = char.ToUpperInvariant(value[0]);
            if (first == 'C') return OptionRight.Call;
            if (first == 'P') return OptionRight.Put;

            throw new InvalidOperationException(string.Format("Unrecognized option right '{0}'.", value));
        }

        /// <summary>
        /// Picks the expirations needed to bracket a target maturity on a given date.
        /// </summary>
        public static List<DateTime> SelectBracketingExpirations(
            IEnumerable<DateTime> availableExpirations,
            DateTime asOf,
            int targetDays = 30,
            double minimumNearTermDays = 23.0,
            double maximumNextTermDays = 37.0)
        {
            if (availableExpirations == null) throw new ArgumentNullException("availableExpirations");

            var ordered = availableExpirations.Select(e => e.Date).Distinct().OrderBy(e => e).ToList();

            var near = ordered
                .Where(e => (e - asOf.Date).TotalDays >= minimumNearTermDays
                            && (e - asOf.Date).TotalDays <= targetDays)
                .OrderByDescending(e => e)
                .FirstOrDefault();

            var next = ordered
                .Where(e => (e - asOf.Date).TotalDays > targetDays
                            && (e - asOf.Date).TotalDays <= maximumNextTermDays)
                .OrderBy(e => e)
                .FirstOrDefault();

            var selected = new List<DateTime>();
            if (near != default(DateTime)) selected.Add(near);
            if (next != default(DateTime)) selected.Add(next);
            return selected;
        }
    }
}
