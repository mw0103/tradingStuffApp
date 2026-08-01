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
        public string BaseAddress { get; set; }

        /// <summary>
        /// API version prefix. v2 is the stable, fully documented surface and is the
        /// default. v3 merges the hist and bulk_hist endpoints into one history endpoint
        /// and uses a wildcard for bulk requests; see <see cref="ThetaDataEndpoints"/>.
        /// </summary>
        public string ApiVersion { get; set; }

        public TimeSpan Timeout { get; set; }

        /// <summary>
        /// Divisor converting ThetaData's integer strike representation to dollars.
        /// Strikes are quoted in tenths of a cent, so the default is 1000. Wrong here and
        /// every strike is off by three orders of magnitude, which
        /// <see cref="ThetaDataChainLoader"/> checks for explicitly.
        /// </summary>
        public double StrikeDivisor { get; set; }

        /// <summary>
        /// Time of day sampled for the daily snapshot, in milliseconds since midnight ET.
        /// Defaults to 15:45, late enough to be representative but before the closing
        /// auction widens quotes.
        /// </summary>
        public int SnapshotMillisecondsOfDay { get; set; }

        public ThetaDataOptions()
        {
            BaseAddress = "http://127.0.0.1:25510";
            ApiVersion = "v2";
            Timeout = TimeSpan.FromMinutes(10);
            StrikeDivisor = 1000.0;
            SnapshotMillisecondsOfDay = (15 * 3600 + 45 * 60) * 1000;
        }
    }

    /// <summary>
    /// Endpoint paths, isolated so an API version change is a single edit.
    /// </summary>
    public static class ThetaDataEndpoints
    {
        public static string Expirations(string version)
        {
            // v3: /v3/option/list/expirations
            return version == "v3" ? "/v3/option/list/expirations" : "/v2/list/expirations";
        }

        public static string Strikes(string version)
        {
            // v3: /v3/option/list/strikes
            return version == "v3" ? "/v3/option/list/strikes" : "/v2/list/strikes";
        }

        /// <summary>
        /// Quotes for every strike of one expiration. v2 has a dedicated bulk endpoint;
        /// v3 folds it into the single history endpoint with a wildcard strike.
        /// </summary>
        public static string BulkOptionQuotes(string version)
        {
            return version == "v3" ? "/v3/option/history/quote" : "/v2/bulk_hist/option/quote";
        }

        public static string IndexPrice(string version)
        {
            return version == "v3" ? "/v3/index/history/price" : "/v2/hist/index/price";
        }

        public static string StockOhlc(string version)
        {
            return version == "v3" ? "/v3/stock/history/ohlc" : "/v2/hist/stock/ohlc";
        }
    }

    /// <summary>
    /// Thin HTTP client over the local Theta Terminal.
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
            query.Add("use_csv=true");

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

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(string.Format(
                    "{0} returned {1}: {2}", url, (int)response.StatusCode, Truncate(body, 500)));

            // The Terminal reports several conditions with a 200 and an error body.
            if (body.IndexOf("No data for the specified", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ThetaDataNoDataException(string.Format("No data available for {0}.", url));

            return CsvTable.Parse(body);
        }

        public Task<CsvTable> ListExpirationsAsync(string root)
        {
            return GetAsync(ThetaDataEndpoints.Expirations(_options.ApiVersion),
                new Dictionary<string, string> { { "root", root } });
        }

        /// <summary>
        /// Quotes for every strike of one expiration, sampled once per day at the
        /// configured snapshot time across a date range.
        /// </summary>
        public Task<CsvTable> GetDailyChainQuotesAsync(
            string root, DateTime expiration, DateTime startDate, DateTime endDate)
        {
            var snapshot = _options.SnapshotMillisecondsOfDay.ToString(CultureInfo.InvariantCulture);

            var parameters = new Dictionary<string, string>
            {
                { "root", root },
                { "start_date", Yyyymmdd(startDate) },
                { "end_date", Yyyymmdd(endDate) },
                // One-minute bars restricted to a single minute yields exactly one row per
                // contract per day, instead of a full day of ticks that would be discarded.
                { "ivl", "60000" },
                { "start_time", snapshot },
                { "end_time", snapshot }
            };

            if (_options.ApiVersion == "v3")
            {
                parameters["expiration"] = Yyyymmdd(expiration);
                parameters["strike"] = "*";
                parameters["right"] = "*";
            }
            else
            {
                parameters["exp"] = Yyyymmdd(expiration);
            }

            return GetAsync(ThetaDataEndpoints.BulkOptionQuotes(_options.ApiVersion), parameters);
        }

        public Task<CsvTable> GetIndexPriceAsync(string root, DateTime startDate, DateTime endDate, int intervalMs)
        {
            return GetAsync(ThetaDataEndpoints.IndexPrice(_options.ApiVersion),
                new Dictionary<string, string>
                {
                    { "root", root },
                    { "start_date", Yyyymmdd(startDate) },
                    { "end_date", Yyyymmdd(endDate) },
                    { "ivl", intervalMs.ToString(CultureInfo.InvariantCulture) }
                });
        }

        public Task<CsvTable> GetStockOhlcAsync(string root, DateTime startDate, DateTime endDate, int intervalMs)
        {
            return GetAsync(ThetaDataEndpoints.StockOhlc(_options.ApiVersion),
                new Dictionary<string, string>
                {
                    { "root", root },
                    { "start_date", Yyyymmdd(startDate) },
                    { "end_date", Yyyymmdd(endDate) },
                    { "ivl", intervalMs.ToString(CultureInfo.InvariantCulture) }
                });
        }

        public static string Yyyymmdd(DateTime date)
        {
            return date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
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
}
