using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;

namespace TradingStuff.Volatility.ThetaData
{
    public class ThetaDataOptions
    {
        /// <summary>
        /// Base address of the local Theta Terminal. The Terminal holds the credentials
        /// and proxies to ThetaData, so requests from here carry no authentication of
        /// their own - if calls fail with a connection error, the Terminal is not running.
        /// </summary>
        /// <remarks>
        /// Port 25503 is the v3 REST port. The v2 Terminal served 25510; a Terminal on that
        /// port is too old for this client.
        /// </remarks>
        public string BaseAddress { get; set; }

        public TimeSpan Timeout { get; set; }

        /// <summary>
        /// Divisor converting the feed's strike representation to dollars. API v3 quotes
        /// strikes in dollars already, so the default is 1. Wrong here and every strike is
        /// off by orders of magnitude, which <see cref="ThetaDataChainLoader"/> checks for
        /// explicitly - the v2 feed quoted tenths of a cent and needed 1000.
        /// </summary>
        public double StrikeDivisor { get; set; }

        /// <summary>
        /// Time of day sampled for the daily snapshot, in exchange-local terms as the feed
        /// reports it. Defaults to 15:45, late enough to be representative but before the
        /// closing auction widens quotes.
        /// </summary>
        public TimeSpan SnapshotTimeOfDay { get; set; }

        public ThetaDataOptions()
        {
            BaseAddress = "http://127.0.0.1:25503";
            Timeout = TimeSpan.FromMinutes(10);
            StrikeDivisor = 1.0;
            SnapshotTimeOfDay = new TimeSpan(15, 45, 0);
        }
    }

    /// <summary>
    /// Endpoint paths, isolated so a version change is a single edit.
    /// </summary>
    /// <remarks>
    /// API v3 only. v2 is not deprecated but removed - a v2 Terminal answers every v2 route
    /// with 410 and a message naming the renamed parameters - so there is nothing to branch
    /// between and the version parameter these methods used to take has gone.
    /// </remarks>
    public static class ThetaDataEndpoints
    {
        public const string Expirations = "/v3/option/list/expirations";
        public const string Strikes = "/v3/option/list/strikes";

        /// <summary>
        /// Option quote history. Serves both the single-contract and the whole-expiration
        /// case; see <see cref="ThetaDataClient.GetDailyChainQuotesAsync"/> for how the
        /// bulk form is requested.
        /// </summary>
        public const string OptionQuotes = "/v3/option/history/quote";

        public const string OptionEndOfDay = "/v3/option/history/eod";
        public const string IndexPrice = "/v3/index/history/price";
        public const string StockOhlc = "/v3/stock/history/ohlc";
    }

    /// <summary>
    /// Thin HTTP client over the local Theta Terminal, API v3.
    ///
    /// Every request asks for CSV: it is a fraction of the size of the JSON form for
    /// bulk option data, which matters when a single expiration-day pull runs to
    /// hundreds of thousands of rows.
    /// </summary>
    public class ThetaDataClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ThetaDataOptions _options;

        public ThetaDataOptions Options
        {
            get { return _options; }
        }

        public ThetaDataClient(ThetaDataOptions options = null, HttpClient httpClient = null)
        {
            _options = options ?? new ThetaDataOptions();
            _http = httpClient ?? new HttpClient();
            _http.BaseAddress = new Uri(_options.BaseAddress);
            _http.Timeout = _options.Timeout;
        }

        public async Task<CsvTable> GetAsync(string path, IDictionary<string, string> parameters)
        {
            if (path == null) throw new ArgumentNullException("path");

            var query = new List<string>();
            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    query.Add(string.Format("{0}={1}",
                        Uri.EscapeDataString(parameter.Key), Uri.EscapeDataString(parameter.Value)));
                }
            }
            query.Add("format=csv");

            var url = path + "?" + string.Join("&", query);

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(string.Format(
                    "Could not reach the Theta Terminal at {0}. It must be running locally for any " +
                    "request to succeed. Underlying error: {1}", _options.BaseAddress, ex.Message), ex);
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // The Terminal answers a route from an older API version with 410 and a message
            // naming the renamed parameters. Worth calling out separately: it means the
            // client is too old for the Terminal, not that the request was malformed.
            if ((int)response.StatusCode == 410)
                throw new ThetaDataVersionException(string.Format(
                    "{0} was rejected as an outdated API version. This client speaks v3. Terminal said: {1}",
                    url, Truncate(body, 300)));

            // A subscription that does not cover the asset class is a account-level fact
            // rather than a fault in the request, and is worth distinguishing so a caller
            // can skip the endpoint rather than retry it.
            if ((int)response.StatusCode == 403)
                throw new ThetaDataSubscriptionException(string.Format(
                    "{0} requires a subscription this account does not have. Terminal said: {1}",
                    url, Truncate(body, 300)));

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(string.Format(
                    "{0} returned {1}: {2}", url, (int)response.StatusCode, Truncate(body, 500)));

            // The Terminal reports several conditions with a 200 and an error body.
            if (body.IndexOf("No data for the specified", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ThetaDataNoDataException(string.Format("No data available for {0}.", url));

            return CsvTable.Parse(body);
        }

        public Task<CsvTable> ListExpirationsAsync(string symbol)
        {
            return GetAsync(ThetaDataEndpoints.Expirations,
                new Dictionary<string, string> { { "symbol", symbol } });
        }

        public Task<CsvTable> ListStrikesAsync(string symbol, DateTime expiration)
        {
            return GetAsync(ThetaDataEndpoints.Strikes,
                new Dictionary<string, string>
                {
                    { "symbol", symbol },
                    { "expiration", IsoDate(expiration) }
                });
        }

        /// <summary>
        /// Quotes for every strike and right of one expiration, sampled once per day at the
        /// configured snapshot time across a date range.
        /// </summary>
        /// <remarks>
        /// The bulk form is requested by OMITTING strike and right, not by passing a
        /// wildcard - v3 answers <c>strike=*</c> with a 400. Bounding the interval to a
        /// single minute at the snapshot time yields exactly one row per contract per day
        /// instead of a full session of ticks that would be discarded.
        /// </remarks>
        public Task<CsvTable> GetDailyChainQuotesAsync(
            string symbol, DateTime expiration, DateTime startDate, DateTime endDate)
        {
            var snapshot = TimeOfDay(_options.SnapshotTimeOfDay);

            return GetAsync(ThetaDataEndpoints.OptionQuotes,
                new Dictionary<string, string>
                {
                    { "symbol", symbol },
                    { "expiration", IsoDate(expiration) },
                    { "start_date", IsoDate(startDate) },
                    { "end_date", IsoDate(endDate) },
                    { "interval", "1m" },
                    { "start_time", snapshot },
                    { "end_time", snapshot }
                });
        }

        /// <summary>Quotes for a single contract over a date range.</summary>
        public Task<CsvTable> GetContractQuotesAsync(
            string symbol, DateTime expiration, double strike, OptionRightCode right,
            DateTime startDate, DateTime endDate, TimeSpan interval)
        {
            return GetAsync(ThetaDataEndpoints.OptionQuotes,
                new Dictionary<string, string>
                {
                    { "symbol", symbol },
                    { "expiration", IsoDate(expiration) },
                    { "strike", strike.ToString("0.####", CultureInfo.InvariantCulture) },
                    { "right", right == OptionRightCode.Call ? "C" : "P" },
                    { "start_date", IsoDate(startDate) },
                    { "end_date", IsoDate(endDate) },
                    { "interval", FormatInterval(interval) }
                });
        }

        /// <summary>
        /// Index level history.
        /// </summary>
        /// <remarks>
        /// Requires a value subscription. Verified against a live Terminal only to the point
        /// of the subscription check, which a FREE account fails with a 403 - so the request
        /// shape here is unconfirmed beyond being accepted as well-formed.
        /// </remarks>
        public Task<CsvTable> GetIndexPriceAsync(
            string symbol, DateTime startDate, DateTime endDate, TimeSpan interval)
        {
            return GetAsync(ThetaDataEndpoints.IndexPrice, HistoryParameters(symbol, startDate, endDate, interval));
        }

        /// <summary>
        /// Stock OHLC history.
        /// </summary>
        /// <remarks>Requires a value subscription; see <see cref="GetIndexPriceAsync"/>.</remarks>
        public Task<CsvTable> GetStockOhlcAsync(
            string symbol, DateTime startDate, DateTime endDate, TimeSpan interval)
        {
            return GetAsync(ThetaDataEndpoints.StockOhlc, HistoryParameters(symbol, startDate, endDate, interval));
        }

        private static Dictionary<string, string> HistoryParameters(
            string symbol, DateTime startDate, DateTime endDate, TimeSpan interval)
        {
            return new Dictionary<string, string>
            {
                { "symbol", symbol },
                { "start_date", IsoDate(startDate) },
                { "end_date", IsoDate(endDate) },
                { "interval", FormatInterval(interval) }
            };
        }

        /// <summary>Dates are ISO in v3; the v2 feed used a compact yyyyMMdd form.</summary>
        public static string IsoDate(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static string TimeOfDay(TimeSpan timeOfDay)
        {
            if (timeOfDay < TimeSpan.Zero || timeOfDay >= TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException("timeOfDay", "A time of day must fall within one day.");

            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                timeOfDay.Hours, timeOfDay.Minutes, timeOfDay.Seconds);
        }

        /// <summary>
        /// Formats a sampling interval the way v3 expects: a whole number of seconds,
        /// minutes or hours.
        /// </summary>
        /// <remarks>
        /// The largest whole unit is used, because that is the form the documentation shows
        /// and the only one confirmed against a live Terminal across the range. Days are
        /// deliberately not produced - v3 rejects <c>1d</c> - and neither is zero, which it
        /// also rejects.
        /// </remarks>
        public static string FormatInterval(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("interval", "A sampling interval must be positive.");
            if (interval.Milliseconds != 0)
                throw new ArgumentOutOfRangeException("interval", "Sub-second intervals are not supported.");

            var totalSeconds = (long)interval.TotalSeconds;

            if (totalSeconds % 3600 == 0)
                return (totalSeconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
            if (totalSeconds % 60 == 0)
                return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture) + "m";

            return totalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= length ? value : value.Substring(0, length) + "...";
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    /// <summary>Which side of the chain a single-contract request wants.</summary>
    public enum OptionRightCode
    {
        Call = 0,
        Put = 1
    }

    /// <summary>
    /// Raised when the Terminal reports no data for a request. Distinguished from a
    /// genuine failure because an expiration with no quotes on a given date is normal and
    /// should be skipped, not retried.
    /// </summary>
    public class ThetaDataNoDataException : Exception
    {
        public ThetaDataNoDataException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Raised when the Terminal rejects the request as belonging to a removed API version.
    /// Separate from a malformed request: nothing about the arguments will fix it.
    /// </summary>
    public class ThetaDataVersionException : Exception
    {
        public ThetaDataVersionException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Raised when the account's subscription does not cover the asset class. A property of
    /// the account rather than the request, so a caller should skip the endpoint rather than
    /// retry or adjust it.
    /// </summary>
    public class ThetaDataSubscriptionException : Exception
    {
        public ThetaDataSubscriptionException(string message) : base(message)
        {
        }
    }
}
