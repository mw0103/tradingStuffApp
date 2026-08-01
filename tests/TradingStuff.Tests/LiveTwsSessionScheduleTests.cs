using System.Globalization;
using System.Net.Sockets;
using IBApi;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// Re-derives the shipped session calendar from the venue and fails if it disagrees. Excluded from
/// the default run — set <c>TRADING_TEST_TWS=host:port</c> (e.g. <c>127.0.0.1:7497</c>) and run with
/// <c>--filter "Category=RequiresTws"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>exchange-calendars.json</c> is a ground-truth manufacturer: coverage
/// denominators, gap detection and feature cutoffs are validated against it, so a wrong entry is not
/// caught by anything downstream — it is believed. The only real check is against a source outside
/// this repository, and the only such source available to this platform is the broker's own published
/// trading schedule. Every other test in the suite asserts literals a human transcribed from that
/// schedule once; this one asks the schedule again, and so it keeps working when the venue changes
/// its hours rather than only when somebody remembers to re-probe.
/// </para>
/// <para>
/// <b>What it compares.</b> <c>reqHistoricalData(whatToShow: "SCHEDULE")</c> returns one
/// (start, end, refDate) triple per session as exchange-local wall-clock strings. Generated sessions
/// are converted back to that venue's local wall clock and the two SETS of (start, end) pairs are
/// compared — deliberately not the refDate, because IBKR rolls a shortened holiday session's refDate
/// onto the NEXT trading date (it did for 2026-07-03 and did not for 2025-01-20; the convention
/// changed during 2025) whereas this platform files a session under the date it ends on. Boundaries
/// are the thing both sides genuinely agree on.
/// </para>
/// <para>
/// <b>Both directions.</b> A missing session and an invented one are different failures and the
/// second is not caught by checking the first, so the comparison is symmetric. And the row count is
/// asserted to be non-trivial before anything else, because a comparison over an empty schedule
/// passes perfectly.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
public sealed class LiveTwsSessionScheduleTests
{
    private const int SpxIndexConId = 416904;   // verified live: SPX index, CBOE
    private const int VixIndexConId = 13455763; // verified live: VIX index, CBOE
    private const int SpyConId = 756733;        // verified live: SPY, STK, primary ARCA

    /// <summary>
    /// IBKR's SPY schedule reaches 1998 and is a weekday FILL before this date — it returns sessions
    /// on Christmas Day 1998 and July 4 2000-2006, reports every half day from 1999 to 2005 as a full
    /// 20:00 close, and emits zero-length 16:00-16:00 rows on 2007-11-23, 2007-12-24 and 2009-11-27.
    /// <c>NYSE_EXTENDED</c> deliberately asserts nothing before it, so comparing against those rows
    /// would fail on IBKR's fill rather than on this calendar. Matches the calendar's own
    /// <c>effectiveFrom</c>, and a test below pins that they agree.
    /// </summary>
    private static readonly DateOnly NyseExtendedTrustedFrom = new(2010, 1, 4);

    /// <summary>
    /// IBKR's schedule only distinguishes CME's shortened holiday sessions from this date. Earlier
    /// rows report every holiday it does not close outright as a full 17:00-16:00 session, which is
    /// demonstrably wrong (Thanksgiving 2021), so comparing against them would fail on IBKR's fill
    /// rather than on this calendar.
    /// </summary>
    private static readonly DateOnly CmeScheduleTrustedFrom = new(2022, 11, 24);

    private static (string Host, int Port)? TwsEndpoint
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("TRADING_TEST_TWS");

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(':', 2);

            return parts.Length == 2 && int.TryParse(parts[1], out var port) ? (parts[0], port) : ("127.0.0.1", 7497);
        }
    }

    private static bool CanReachTws((string Host, int Port) endpoint)
    {
        try
        {
            using var probe = new TcpClient();

            return probe.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(3)) && probe.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    [Fact]
    public void The_spx_index_calendar_matches_the_schedule_the_venue_publishes()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new ScheduleClient(endpoint.Host, endpoint.Port, clientId: 281);
        client.Connect();

        AssertMatches(
            client.Schedule(Index(SpxIndexConId), "1 M"),
            ["CBOE_SPX_RTH"],
            DateOnly.MinValue,
            minimumRows: 15);
    }

    [Fact]
    public void The_vix_index_calendar_matches_both_legs_the_venue_publishes()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new ScheduleClient(endpoint.Host, endpoint.Port, clientId: 282);
        client.Connect();

        // Two legs a day, so a month is ~40 rows. If the overnight leg ever stopped being published
        // this fails on the count as well as on the comparison.
        AssertMatches(
            client.Schedule(Index(VixIndexConId), "1 M"),
            ["CBOE_VIX_RTH", "CBOE_VIX_GTH"],
            DateOnly.MinValue,
            minimumRows: 30);
    }

    [Fact]
    public void The_cme_calendar_matches_the_venue_schedule_including_its_shortened_holiday_sessions()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new ScheduleClient(endpoint.Host, endpoint.Port, clientId: 283);
        client.Connect();

        // A year, so the window necessarily contains several US holidays — the whole point. The
        // front month is resolved rather than hardcoded so this does not rot at every quarterly roll.
        var schedule = client.Schedule(client.FrontMonthEs(), "1 Y");

        // useRTH=0 SCHEDULE returns only the Globex day rows, never the nested regular session, so
        // only the GTH label is comparable here.
        AssertMatches(schedule, ["CME_ES"], CmeScheduleTrustedFrom, minimumRows: 200, labels: ["GTH"]);

        // And specifically: at least one row must be a shortened holiday session, or this test would
        // pass just as well against a calendar that still deletes them.
        var shortened = schedule.Count(row => row.Close != new TimeOnly(16, 0));
        Assert.True(shortened > 0, "the sampled year contained no shortened CME session at all");
    }

    [Fact]
    public void The_nyse_extended_calendar_matches_the_schedule_the_venue_publishes()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        using var client = new ScheduleClient(endpoint.Host, endpoint.Port, clientId: 284);
        client.Connect();

        // A year, so the window necessarily contains at least two of the 34 measured half days. One
        // session a day, so ~250 rows.
        var schedule = client.Schedule(Stock(SpyConId), "1 Y");

        AssertMatches(schedule, ["NYSE_EXTENDED"], NyseExtendedTrustedFrom, minimumRows: 200);

        // And specifically: at least one row must be a SHORTENED session, or this comparison would
        // pass just as happily against a calendar that models no early close at all — which is exactly
        // the defect a calendar copied from `tradingHours` alone would have. The same guard the CME
        // test carries, for the same reason.
        var shortened = schedule.Count(row => row.Close != new TimeOnly(20, 0));
        Assert.True(shortened > 0, "the sampled year contained no shortened SPY extended session at all");

        // The venue's regular session is a strict subset of what this schedule reports: useRTH=0
        // SCHEDULE returns trading hours, and every row must therefore be wider than the 09:30-16:00
        // NYSE calendar. If IBKR ever started answering this request with liquid hours instead, the
        // set comparison above would still pass on a calendar that had been "corrected" to match, so
        // the relationship is asserted rather than assumed.
        Assert.All(schedule, row => Assert.Equal(new TimeOnly(4, 0), TimeOnly.FromDateTime(row.OpenLocal)));
    }

    [Fact]
    public void The_extended_calendars_trusted_window_matches_what_it_claims_to_assert()
    {
        // Cheap, and it is the seam where a live comparison quietly stops being a comparison: if the
        // calendar's effectiveFrom were moved earlier than the date IBKR's feed becomes real, the
        // comparison above would keep passing (its window is a recent year) while the calendar
        // asserted sessions for years nothing has ever checked.
        Assert.Equal(
            NyseExtendedTrustedFrom,
            ExchangeCalendarSet.Embedded.Calendar("NYSE_EXTENDED").EffectiveFrom);
    }

    // ------------------------------------------------------------------------------------ comparison

    private static void AssertMatches(
        IReadOnlyList<ScheduleRow> schedule,
        string[] calendars,
        DateOnly trustedFrom,
        int minimumRows,
        string[]? labels = null)
    {
        // A comparison over an empty schedule is a perfect match, so establish there is something to
        // compare before comparing it.
        Assert.True(
            schedule.Count >= minimumRows,
            $"TWS returned {schedule.Count} schedule rows, fewer than the {minimumRows} this comparison needs " +
            "to be meaningful.");

        var published = schedule
            .Where(row => row.TradingDateFloor >= trustedFrom)
            .Select(row => (row.OpenLocal, row.CloseLocal))
            .ToHashSet();

        var from = schedule.Min(row => row.TradingDateFloor);
        var to = schedule.Max(row => row.TradingDateFloor);

        if (from < trustedFrom)
        {
            from = trustedFrom;
        }

        var clock = new SessionClock();
        var generated = new HashSet<(DateTime Open, DateTime Close)>();

        foreach (var calendar in calendars)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(
                ExchangeCalendarSet.Embedded.Calendar(calendar).TimeZone);

            foreach (var session in clock.SessionsBetween(calendar, from, to))
            {
                if (labels is not null && !labels.Contains(session.Label))
                {
                    continue;
                }

                // Converting here rather than in the platform is deliberate: the independent half of
                // this oracle is the venue's wall-clock hours, and comparing in the venue's own units
                // is what makes a disagreement legible ("we say 15:15, the exchange says 15:00").
                generated.Add((
                    TimeZoneInfo.ConvertTimeFromUtc(session.OpenUtc.UtcDateTime, zone),
                    TimeZoneInfo.ConvertTimeFromUtc(session.CloseUtc.UtcDateTime, zone)));
            }
        }

        // The schedule's first and last rows are clipped by the request window rather than by a
        // trading date, so a generated session at either edge can legitimately be outside it.
        var edges = (First: schedule.Min(row => row.OpenLocal), Last: schedule.Max(row => row.CloseLocal));
        generated.RemoveWhere(session => session.Open < edges.First || session.Close > edges.Last);

        var missing = published.Except(generated).Order().ToArray();
        var invented = generated.Except(published).Order().ToArray();

        Assert.True(
            missing.Length == 0,
            $"[{string.Join(", ", calendars)}] the venue publishes sessions this calendar does not " +
            $"generate: {Describe(missing)}");
        Assert.True(
            invented.Length == 0,
            $"[{string.Join(", ", calendars)}] this calendar generates sessions the venue does not " +
            $"publish: {Describe(invented)}");
    }

    private static string Describe(IEnumerable<(DateTime Open, DateTime Close)> sessions) =>
        string.Join(
            "; ",
            sessions.Take(8).Select(session => $"{session.Open:yyyy-MM-dd HH:mm} -> {session.Close:yyyy-MM-dd HH:mm}"));

    private static Contract Index(int conId) =>
        new() { ConId = conId, SecType = "IND", Exchange = "CBOE", Currency = "USD" };

    private static Contract Stock(int conId) =>
        new() { ConId = conId, SecType = "STK", Exchange = "SMART", PrimaryExch = "ARCA", Currency = "USD" };

    /// <summary>One published session, in the contract's own exchange-local wall clock.</summary>
    private sealed record ScheduleRow(DateTime OpenLocal, DateTime CloseLocal)
    {
        public TimeOnly Close => TimeOnly.FromDateTime(CloseLocal);

        /// <summary>The local calendar date the session ENDS on — which is the trading date this platform files it under.</summary>
        public DateOnly TradingDateFloor => DateOnly.FromDateTime(CloseLocal);
    }

    private sealed class ScheduleClient(string host, int port, int clientId) : DefaultEWrapper, IDisposable
    {
        private const string ScheduleStamp = "yyyyMMdd-HH:mm:ss";

        private readonly EReaderMonitorSignal _signal = new();
        private readonly ManualResetEventSlim _connected = new(false);
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource<bool>> _pending = [];
        private readonly Dictionary<int, List<ScheduleRow>> _schedules = [];
        private readonly Dictionary<int, List<ContractDetails>> _details = [];
        private readonly Dictionary<int, string> _errors = [];

        private EClientSocket _socket = null!;
        private int _nextRequestId = 8100;

        public void Connect()
        {
            _socket = new EClientSocket(this, _signal);
            _socket.eConnect(host, port, clientId, extraAuth: false);

            var reader = new EReader(_socket, _signal);
            reader.Start();

            new Thread(() =>
            {
                while (_socket.IsConnected())
                {
                    _signal.waitForSignal();
                    reader.processMsgs();
                }
            })
            { IsBackground = true }.Start();

            Assert.True(_connected.Wait(TimeSpan.FromSeconds(20)), "TWS never sent nextValidId");
        }

        /// <summary>The nearest ES quarterly that has not expired, so the test survives every roll.</summary>
        public Contract FrontMonthEs()
        {
            var (id, completion) = Begin();
            _socket.reqContractDetails(
                id, new Contract { SecType = "FUT", Exchange = "CME", Symbol = "ES", Currency = "USD" });
            Await(id, completion, TimeSpan.FromSeconds(60));

            List<ContractDetails> found;
            lock (_gate)
            {
                found = _details[id];
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var front = found
                .Select(detail => detail.Contract)
                .Where(contract => DateOnly.TryParseExact(
                                       contract.LastTradeDateOrContractMonth, "yyyyMMdd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var expiry)
                                   && expiry > today)
                .OrderBy(contract => contract.LastTradeDateOrContractMonth, StringComparer.Ordinal)
                .FirstOrDefault();

            Assert.NotNull(front);

            return front!;
        }

        public IReadOnlyList<ScheduleRow> Schedule(Contract contract, string duration)
        {
            var (id, completion) = Begin();

            // useRTH=0: the full published trading schedule, including the overnight legs. The bar
            // size and formatDate are ignored for a SCHEDULE request but must still be well-formed.
            _socket.reqHistoricalData(id, contract, "", duration, "1 day", "SCHEDULE", 0, 1, false, null);
            Await(id, completion, TimeSpan.FromSeconds(120));

            lock (_gate)
            {
                Assert.False(_errors.TryGetValue(id, out var error), $"TWS refused the schedule request: {error}");

                return _schedules[id];
            }
        }

        private (int Id, Task<bool> Completion) Begin()
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                _pending[id] = completion;
                _schedules[id] = [];
                _details[id] = [];
            }

            return (id, completion.Task);
        }

        private static void Await(int id, Task<bool> completion, TimeSpan timeout) =>
            Assert.True(completion.Wait(timeout), $"TWS request {id} never completed");

        private void Complete(int id)
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(id, out var completion))
                {
                    completion.TrySetResult(true);
                }
            }
        }

        public override void nextValidId(int orderId) => _connected.Set();

        public override void contractDetails(int reqId, ContractDetails details)
        {
            lock (_gate)
            {
                if (_details.TryGetValue(reqId, out var list))
                {
                    list.Add(details);
                }
            }
        }

        public override void contractDetailsEnd(int reqId) => Complete(reqId);

        public override void historicalSchedule(
            int reqId, string start, string end, string timeZone, HistoricalSession[] sessions)
        {
            lock (_gate)
            {
                if (_schedules.TryGetValue(reqId, out var list))
                {
                    list.AddRange(sessions.Select(session => new ScheduleRow(
                        DateTime.ParseExact(session.StartDateTime, ScheduleStamp, CultureInfo.InvariantCulture),
                        DateTime.ParseExact(session.EndDateTime, ScheduleStamp, CultureInfo.InvariantCulture))));
                }
            }

            Complete(reqId);
        }

        public override void historicalDataEnd(int reqId, string start, string end) => Complete(reqId);

        public override void error(int id, long errorTime, int code, string message, string advancedOrderRejectJson)
        {
            // 2104/2106/2158 and friends are connection-status notices, not failures.
            if (code is 2100 or 2104 or 2106 or 2107 or 2119 or 2158 || id <= 0)
            {
                return;
            }

            lock (_gate)
            {
                _errors[id] = $"{code}: {message}";
            }

            Complete(id);
        }

        public void Dispose()
        {
            try
            {
                if (_socket.IsConnected())
                {
                    _socket.eDisconnect();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or SocketException)
            {
                // Shutting down a socket TWS has already dropped is not a test failure.
            }
        }
    }
}
