using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.History;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Volatility;
using TradingStuff.Volatility;
using Xunit.Abstractions;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// The realized-volatility layer against real underlying bars, pulled from paper TWS. Needs
/// <c>TRADING_TEST_TWS=host:port</c>; run with <c>--filter "Category=RequiresTws"</c>.
/// </summary>
/// <remarks>
/// This suite exists to close the last "never run against real data" caveat on the estimator.
/// Everything before it was verified against synthetic paths with known volatility, which proves
/// the arithmetic but says nothing about what a real feed looks like — duplicate prints, halts,
/// the opening auction, the timestamp convention, and whether the session filter admits the hours
/// it should.
/// <para>
/// SPY rather than SPX because the capability matrix records SPY 1-min history back to
/// 1993-01-29 against SPX's 2004-03-04, and because SPY carries tradable prices where the index
/// is a computed level. The realized series is built on the NYSE calendar accordingly.
/// </para>
/// <para>
/// Read-only throughout: historical bars and a head timestamp. Nothing here goes near an order.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
public sealed class LiveIbkrUnderlyingBarsTests(LiveIbkrUnderlyingBarsTests.HistoryFixture tws, ITestOutputHelper output)
    : IClassFixture<LiveIbkrUnderlyingBarsTests.HistoryFixture>
{
    private static readonly HistoricalContractSpec Spy = new("SPY", "STK", Exchange: "SMART", PrimaryExchange: "ARCA");

    public sealed class HistoryFixture : IAsyncLifetime
    {
        // Distinct from the gateway's 11 and the other live suites' 271/273/275.
        private const int TestClientId = 277;

        private IbkrConnection? _connection;

        public IbkrHistoricalClient? Client { get; private set; }

        public bool Available => _connection?.IsConnected == true;

        public async Task InitializeAsync()
        {
            if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint)) return;

            // A paper port, always. 7496/4001 are live money and nothing here may reach them.
            Assert.True(endpoint.Port is 7497 or 4002, $"refusing to run against non-paper port {endpoint.Port}");

            var options = Options.Create(new IbkrOptions
            {
                Host = endpoint.Host,
                Port = endpoint.Port,
                ClientId = TestClientId,
            });

            var registry = new IbkrRequestRegistry();
            var governor = new IbkrPacingGovernor(
                options, TimeProvider.System, new TestMeterFactory(), NullLogger<IbkrPacingGovernor>.Instance);
            var wrapper = new IbkrClientWrapper(
                registry, new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance), new ExecutionCommissionRouter(),
                NullLogger<IbkrClientWrapper>.Instance);

            _connection = new IbkrConnection(
                options, registry, wrapper, governor, NullLogger<IbkrConnection>.Instance);
            var socket = new PacedSocket(_connection, governor, NullLogger<PacedSocket>.Instance);

            Client = new IbkrHistoricalClient(
                _connection, socket, options, NullLogger<IbkrHistoricalClient>.Instance);

            await _connection.StartAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!_connection.IsConnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }

        public async Task DisposeAsync()
        {
            if (_connection is not null)
            {
                await _connection.StopAsync(CancellationToken.None);
                _connection.Dispose();
            }
        }

        private sealed class TestMeterFactory : IMeterFactory
        {
            public Meter Create(MeterOptions options) => new(options);

            public void Dispose()
            {
            }
        }

        private static (string Host, int Port)? TwsEndpoint
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("TRADING_TEST_TWS");
                if (string.IsNullOrWhiteSpace(raw)) return null;

                var parts = raw.Split(':', 2);
                return parts.Length == 2 && int.TryParse(parts[1], out var port) ? (parts[0], port) : ("127.0.0.1", 7497);
            }
        }

        private static bool CanReachTws((string Host, int Port) endpoint)
        {
            try
            {
                using var probe = new TcpClient();
                return probe.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                return false;
            }
        }
    }

    private bool Skip()
    {
        if (tws.Available) return false;
        output.WriteLine("No paper TWS reachable (set TRADING_TEST_TWS); skipping.");
        return true;
    }

    /// <summary>A recent weekday, far enough back that the session has certainly closed.</summary>
    private static DateTimeOffset RecentSessionEnd()
    {
        var end = DateTimeOffset.UtcNow.Date.AddDays(-7);
        while (end.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) end = end.AddDays(-1);

        // 21:00 UTC is the 16:00 ET close in winter, 17:00 ET in summer; either way the
        // session is over and TWS returns the completed day.
        return new DateTimeOffset(end, TimeSpan.Zero).AddHours(23);
    }

    [Fact]
    public async Task SpyOneMinuteHistoryReachesBackDecades()
    {
        if (Skip()) return;

        var head = await tws.Client!.GetHeadTimestampAsync(
            new HeadTimestampQuery(Spy, "TRADES", UseRth: true), CancellationToken.None);

        output.WriteLine($"SPY head timestamp: {head.HeadTimestamp:yyyy-MM-dd}");

        // The capability matrix records 1993-01-29. Asserted loosely: the point is that the
        // depth is decades, not that a vendor never re-anchors its history.
        Assert.True(head.HeadTimestamp.Year <= 2000,
            $"expected deep SPY history, head was {head.HeadTimestamp:yyyy-MM-dd}");
    }

    [Fact]
    public async Task ASingleSpySessionOfOneMinuteBarsArrives()
    {
        if (Skip()) return;

        var response = await tws.Client!.GetHistoricalBarsAsync(
            new HistoricalBarsRequest(Spy, RecentSessionEnd(), "1 D", "1 min", "TRADES", UseRth: true),
            CancellationToken.None);

        Assert.True(response.HasData, "TWS reported no data for a recent regular session");

        // A full RTH session is 390 one-minute bars; allow for a half day or a late start.
        Assert.InRange(response.Bars.Count, 200, 400);
        Assert.All(response.Bars, b => Assert.NotNull(b.Timestamp));
        Assert.All(response.Bars, b => Assert.True(b.Close > 0m));

        output.WriteLine($"{response.Bars.Count} bars, " +
            $"{response.Bars[0].Timestamp:yyyy-MM-dd HH:mm}Z..{response.Bars[^1].Timestamp:HH:mm}Z, " +
            $"close {response.Bars[^1].Close}");
    }

    [Fact]
    public async Task RealizedVolatilityIsComputedFromRealBars()
    {
        if (Skip()) return;

        var response = await tws.Client!.GetHistoricalBarsAsync(
            new HistoricalBarsRequest(Spy, RecentSessionEnd(), "1 D", "1 min", "TRADES", UseRth: true),
            CancellationToken.None);

        Assert.True(response.HasData);

        // The adapter is the decimal-to-double boundary and the only conversion point.
        var bars = HistoricalBarAdapter.ToIntradayBars(response.Bars.Select(b => new HistoricalBarDto(
            b.Timestamp, b.TradingDate, b.Open, b.High, b.Low, b.Close, b.Volume, b.Count, b.Wap))).ToList();

        var options = new RealizedVolatilityOptions { OvernightPolicy = OvernightPolicy.Exclude };
        var days = new RealizedVolatilitySeriesBuilder(
            SessionBars.Clock, VolatilityPresets.SpyCalendar, SessionQualityPolicy.UsEquity(), options)
            .Build("SPY", bars);

        Assert.Single(days);
        var day = days[0];

        output.WriteLine(
            $"{day.Date:yyyy-MM-dd}  annualized={day.AnnualizedVolatility:P2}  returns={day.ReturnCount}  " +
            $"stale={day.StaleSamples}  complete={day.IsComplete}  short={day.IsShortSession}  " +
            $"open={day.SessionOpen} close={day.SessionClose}");

        // Deliberately wide. This asserts the pipeline produces something in the right
        // universe from a real feed, not a particular market outcome: SPY realized
        // volatility has spent time at 5% and at 80%.
        Assert.InRange(day.AnnualizedVolatility, 0.02, 1.50);
        Assert.True(day.ReturnCount > 50, $"only {day.ReturnCount} sampled returns");
        Assert.True(day.SessionOpen > 0.0 && day.SessionClose > 0.0);

        // The session filter must have admitted the calendar's hours: a wall-clock filter
        // against UTC bars would have kept pre-market and produced far fewer usable returns.
        Assert.True(day.IsComplete, "the session was not complete — check the session window");
    }

    [Fact]
    public async Task SeveralSessionsFormASeriesWithOvernightReturns()
    {
        if (Skip()) return;

        var response = await tws.Client!.GetHistoricalBarsAsync(
            new HistoricalBarsRequest(Spy, RecentSessionEnd(), "5 D", "1 min", "TRADES", UseRth: true),
            CancellationToken.None);

        Assert.True(response.HasData);

        var bars = HistoricalBarAdapter.ToIntradayBars(response.Bars.Select(b => new HistoricalBarDto(
            b.Timestamp, b.TradingDate, b.Open, b.High, b.Low, b.Close, b.Volume, b.Count, b.Wap))).ToList();

        var days = VolatilityPresets.BuildSpy(SessionBars.Clock, bars);

        Assert.True(days.Count >= 3, $"expected several sessions, got {days.Count}");

        // Every session after the first has a prior close to measure the overnight move from.
        Assert.False(days[0].HasOvernightReturn);
        Assert.All(days.Skip(1), d => Assert.True(d.HasOvernightReturn));

        // Trading dates are strictly increasing and distinct: a calendar-date bucketing bug
        // would merge or duplicate them.
        Assert.Equal(days.Select(d => d.Date).Distinct().Count(), days.Count);
        Assert.True(days.Zip(days.Skip(1)).All(p => p.First.Date < p.Second.Date));

        foreach (var d in days)
        {
            output.WriteLine($"{d.Date:yyyy-MM-dd}  vol={d.AnnualizedVolatility:P2}  returns={d.ReturnCount}  " +
                $"overnight={d.OvernightReturn:F5}  complete={d.IsComplete}");
        }
    }

    [Fact]
    public async Task TheSeriesDiagnosticSummarisesRealData()
    {
        if (Skip()) return;

        var response = await tws.Client!.GetHistoricalBarsAsync(
            new HistoricalBarsRequest(Spy, RecentSessionEnd(), "10 D", "1 min", "TRADES", UseRth: true),
            CancellationToken.None);

        Assert.True(response.HasData);

        var bars = HistoricalBarAdapter.ToIntradayBars(response.Bars.Select(b => new HistoricalBarDto(
            b.Timestamp, b.TradingDate, b.Open, b.High, b.Low, b.Close, b.Volume, b.Count, b.Wap))).ToList();

        var days = VolatilityPresets.BuildSpy(SessionBars.Clock, bars);
        var diagnostics = SeriesDiagnostics.Summarize(days);

        output.WriteLine(diagnostics.ToString());

        // The diagnostic exists to surface faults before training time is spent on them, so
        // the assertion is that a clean feed produces a clean report.
        Assert.Equal(days.Count, diagnostics.TotalSessions);
        Assert.Empty(diagnostics.Outliers);
        Assert.Equal(0, diagnostics.ZeroVarianceSessions);
        Assert.True(diagnostics.LargestGapDays <= 5, $"unexpected gap of {diagnostics.LargestGapDays} days");
    }
}
