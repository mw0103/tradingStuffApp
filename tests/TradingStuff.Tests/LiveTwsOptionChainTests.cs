using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.ResearchService.Universe;
using Xunit.Abstractions;

namespace TradingStuff.Tests;

/// <summary>
/// The chain window, measured against paper TWS rather than assumed. Needs
/// <c>TRADING_TEST_TWS=host:port</c>; run with <c>--filter "Category=RequiresTws"</c>.
/// </summary>
/// <remarks>
/// This suite exists because the node-grid collapse was a disagreement about what a number meant —
/// the gateway read the chain window as a half-COUNT of strikes and the selector reasoned about it
/// as a moneyness range — and no mocked test can settle that, because the answer depends entirely on
/// what strike increments TWS actually lists. Read-only throughout: contract details and one index
/// quote. Nothing here goes near an order.
/// <para>
/// Measured 2026-08-01, SPX at 7437.63: the SPXW strike union spans 1400–12400 across 728 strikes,
/// but a single expiration lists far fewer (238 for 2026-08-06, 502 for 2026-08-31, 70 for
/// 2026-09-14) and a union strike absent from an expiration is rejected with error 200. A
/// <c>strikeHalfCount: 20</c> window spanned 7340–7540, ±1.3%, while the seeded node targets reach
/// ±15%.
/// </para>
/// </remarks>
[Trait("Category", "RequiresTws")]
public sealed class LiveTwsOptionChainTests(LiveTwsOptionChainTests.TwsFixture tws, ITestOutputHelper output)
    : IClassFixture<LiveTwsOptionChainTests.TwsFixture>
{
    /// <summary>
    /// One socket for the whole suite.
    /// </summary>
    /// <remarks>
    /// Not merely tidy: connecting a second client immediately after the first disconnects is
    /// unreliable against a real TWS — observed here as a 30-second connect timeout on the second
    /// test of a run that passes in isolation. One connection also matches how the gateway actually
    /// behaves, since it is the sole socket owner by design.
    /// </remarks>
    public sealed class TwsFixture : IAsyncLifetime
    {
        private const int TestClientId = 275; // distinct from the gateway's 11 and the other live suites' 271/273

        private IbkrConnection? _connection;

        public IbkrMarketDataClient? Client { get; private set; }

        public bool Available => _connection?.IsConnected == true;

        public async Task InitializeAsync()
        {
            if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
            {
                return;
            }

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

            Client = new IbkrMarketDataClient(
                _connection, socket, options, NullLogger<IbkrMarketDataClient>.Instance);

            await _connection.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => _connection.IsConnected, TimeSpan.FromSeconds(30));
        }

        public async Task DisposeAsync()
        {
            if (_connection is not null)
            {
                await _connection.StopAsync(CancellationToken.None);
                _connection.Dispose();
            }
        }
    }

    /// <summary>The nine roles migration 003 seeds per DTE bucket.</summary>
    private static readonly (string Role, OptionRight Right, decimal Moneyness)[] SeededGrid =
    [
        ("ATM-C", OptionRight.Call, 0.000m),
        ("ATM-P", OptionRight.Put, 0.000m),
        ("40D-C", OptionRight.Call, 0.025m),
        ("40D-P", OptionRight.Put, -0.025m),
        ("25D-C", OptionRight.Call, 0.060m),
        ("25D-P", OptionRight.Put, -0.060m),
        ("10D-C", OptionRight.Call, 0.110m),
        ("10D-P", OptionRight.Put, -0.110m),
        ("5D-P", OptionRight.Put, -0.150m),
    ];

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
    public async Task A_moneyness_window_reaches_every_seeded_node_target_and_a_strike_count_window_does_not()
    {
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        Assert.True(tws.Available, "TWS did not accept the API connection.");
        var client = tws.Client!;

        {
            // The 30DTE bucket: migration 003's DTE window is 21-37, midpoint 29.
            var target = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(29);

            var wide = await client.GetOptionChainAsync(
                "SPX", target, strikeHalfCount: null, CancellationToken.None,
                tradingClass: "SPXW", moneynessHalfWidth: 0.20m);

            Assert.True(wide.SpotCentred, $"No spot-centred window came back: {wide.Unavailable}");
            Assert.NotNull(wide.ReferencePrice);

            var spot = wide.ReferencePrice!.Value;
            var strikes = wide.Contracts.Select(c => c.Strike).Distinct().OrderBy(s => s).ToArray();

            output.WriteLine(
                $"moneynessHalfWidth 0.20: expiration {wide.Expiration}, spot {spot}, " +
                $"{wide.Contracts.Count} contracts over {strikes.Length} strikes {wide.WindowLow}-{wide.WindowHigh} " +
                $"(±{(wide.WindowHigh!.Value - wide.WindowLow!.Value) / 2m / spot:P2})");
            output.WriteLine("  increments: " + string.Join(", ",
                strikes.Zip(strikes.Skip(1), (a, b) => b - a).GroupBy(d => d)
                    .OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}")));

            // One expiration, one series. A chain built from the strike/expiration cross-product
            // could not promise this.
            Assert.All(wide.Contracts, c => Assert.Equal(wide.Expiration, c.Expiration));
            Assert.All(wide.Contracts, c => Assert.Equal("SPXW", c.TradingClass));

            // The whole point: every seeded role finds its own contract.
            var picks = SeededGrid
                .Select(node => (node.Role, Pick: NodeSelector.PickStrike(spot, node.Moneyness, node.Right, wide.Contracts)))
                .ToArray();

            foreach (var (role, pick) in picks)
            {
                output.WriteLine(pick.Contract is null
                    ? $"  {role}: REFUSED {pick.Refusal} — {pick.RefusalDetail}"
                    : $"  {role}: target {pick.TargetStrike:F2} -> strike {pick.Contract.Strike} (deviation {pick.Deviation:P3})");
            }

            Assert.All(picks, p => Assert.Null(p.Pick.Refusal));
            Assert.Equal(9, picks.Select(p => p.Pick.Contract!.Key()).Distinct().Count());

            // The same request the shipped selector made. Whether it reaches the targets is a fact
            // about TWS's strike increments, which is exactly why this assertion lives here and not
            // in a unit test.
            var narrow = await client.GetOptionChainAsync(
                "SPX", target, strikeHalfCount: 20, CancellationToken.None, tradingClass: "SPXW");

            Assert.True(narrow.SpotCentred);
            Assert.Equal(41, narrow.Contracts.Select(c => c.Strike).Distinct().Count());

            output.WriteLine(
                $"strikeHalfCount 20: 41 strikes {narrow.WindowLow}-{narrow.WindowHigh} " +
                $"(±{(narrow.WindowHigh!.Value - narrow.WindowLow!.Value) / 2m / narrow.ReferencePrice!.Value:P2})");

            var narrowPicks = SeededGrid
                .Select(node => NodeSelector.PickStrike(
                    narrow.ReferencePrice!.Value, node.Moneyness, node.Right, narrow.Contracts))
                .ToArray();

            // Refused, not clamped. Before the fix these seven bound to the window's two edge
            // strikes and nine roles became four contracts.
            var refused = narrowPicks.Count(p => p.Contract is null);
            output.WriteLine($"strikeHalfCount 20: {refused} of 9 seeded roles refused");

            Assert.True(refused >= 7, $"Expected the deep targets to be unreachable in a 41-strike window; {refused} were.");
            Assert.All(
                narrowPicks.Where(p => p.Contract is not null),
                p => Assert.InRange(Math.Abs(p.Deviation), 0m, 0.015m));
        }
    }

    [Fact]
    public async Task The_window_holds_only_strikes_TWS_lists_for_that_expiration()
    {
        // The union trap, stated as a broker fact. reqSecDefOptParams returns the strike set for the
        // whole trading class, not for an expiration, so windowing IT produces contracts that do not
        // exist — and it produces them precisely for the far-from-the-money nodes, because that is
        // where the increment coarsens. Measured 2026-08-01: SPXW 2026-08-06 P 6620 is in the union
        // and rejected with error 200; 6625 is listed.
        if (TwsEndpoint is not { } endpoint || !CanReachTws(endpoint))
        {
            return;
        }

        Assert.True(tws.Available, "TWS did not accept the API connection.");
        var client = tws.Client!;

        {
            var window = await client.GetOptionChainAsync(
                "SPX", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), strikeHalfCount: null,
                CancellationToken.None, tradingClass: "SPXW", moneynessHalfWidth: 0.20m);

            Assert.True(window.SpotCentred, $"No spot-centred window came back: {window.Unavailable}");

            var calls = window.Contracts
                .Where(c => c.Right == OptionRight.Call)
                .Select(c => c.Strike)
                .Distinct()
                .OrderBy(s => s)
                .ToArray();

            // Find a coarse stretch and aim at a strike inside it — the shape a union-derived window
            // would have handed to a deep-OTM node.
            var gap = calls.Zip(calls.Skip(1), (a, b) => (Low: a, Size: b - a))
                .Where(g => g.Size >= 10m)
                .OrderByDescending(g => g.Size)
                .FirstOrDefault();

            if (gap.Size == 0m)
            {
                output.WriteLine("No coarse stretch in this window; nothing to probe.");
                return;
            }

            var phantom = gap.Low + 5m;
            output.WriteLine(
                $"{window.Expiration}: widest gap {gap.Low}->{gap.Low + gap.Size} ({gap.Size} points); probing {phantom}");

            Assert.DoesNotContain(phantom, calls);

            var probe = new OptionContract(
                $"SPXW{window.Expiration:yyyyMMdd}C{phantom:0.##}", "SPX", window.Expiration!.Value,
                phantom, OptionRight.Call, TradingClass: "SPXW");

            var rejected = await Assert.ThrowsAsync<IbkrRequestException>(
                () => client.ResolveOptionConIdAsync(probe, CancellationToken.None));

            output.WriteLine($"TWS rejected {phantom}: {rejected.ErrorCode} {rejected.Message}");
            Assert.Equal(IbkrErrorCodes.NoSecurityDefinition, rejected.ErrorCode);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }
}
