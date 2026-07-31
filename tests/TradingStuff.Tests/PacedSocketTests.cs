using System.Diagnostics.Metrics;
using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;
using IbContract = IBApi.Contract;

namespace TradingStuff.Tests;

/// <summary>
/// Ordering guarantees for the one type allowed to write to the TWS socket.
/// </summary>
/// <remarks>
/// The property under test is that the live <c>EClientSocket</c> is resolved <em>after</em> every
/// pacing budget, never before. A budget wait can span minutes, a reconnect replaces the socket
/// outright, and a reference captured beforehand then addresses a dead one — for
/// <c>placeOrder</c> that means the gateway reporting a working order TWS never received, since the
/// order-map row and tracker claim are both written before transmission.
/// <para>
/// A real mid-await reconnect is not reproducible deterministically, so the ordering is pinned
/// instead by a connection that has never held a socket: with the message budget exhausted, a
/// method that resolves the client first fails with <see cref="IbkrConnectionException"/> and one
/// that resolves it last fails with <see cref="TimeoutException"/>. The two exceptions are the
/// witness.
/// </para>
/// </remarks>
public sealed class PacedSocketTests
{
    private static readonly TimeSpan RealWait = TimeSpan.FromSeconds(5);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A gateway wired exactly as production wires it, minus the socket: <see cref="IbkrConnection"/>
    /// is never started, so <c>RequireClient</c> throws while every other collaborator behaves
    /// normally.
    /// </summary>
    private static (PacedSocket Socket, IbkrConnection Connection, IbkrClientWrapper Wrapper, IbkrPacingGovernor Governor)
        Create(IbkrPacingOptions pacing)
    {
        var registry = new IbkrRequestRegistry();
        var tracker = new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance);
        var wrapper = new IbkrClientWrapper(registry, tracker, NullLogger<IbkrClientWrapper>.Instance);

        var governor = new IbkrPacingGovernor(
            Options.Create(new IbkrOptions { Pacing = pacing }),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T14:00:00Z")),
            new TestMeterFactory(),
            NullLogger<IbkrPacingGovernor>.Instance);

        var connection = new IbkrConnection(
            Options.Create(new IbkrOptions()),
            registry,
            wrapper,
            governor,
            NullLogger<IbkrConnection>.Instance);

        return (new PacedSocket(connection, governor, NullLogger<PacedSocket>.Instance), connection, wrapper, governor);
    }

    /// <summary>A budget nothing can draw from: zero tokens, and a refill slower than the timeout.</summary>
    private static IbkrPacingOptions ExhaustedMessageBudget => new()
    {
        MessagesPerSecond = 0.01,
        MessageBurst = 0,
        AcquireTimeoutSeconds = 1,
        LineCap = 10,
        ExecutionReservedLines = 0,
    };

    private static IbkrPacingOptions AmpleBudget => new()
    {
        MessagesPerSecond = 1000,
        MessageBurst = 1000,
        AcquireTimeoutSeconds = 5,
        LineCap = 10,
        ExecutionReservedLines = 0,
    };

    private static IbContract SampleContract => new()
    {
        Symbol = "SPY",
        SecType = "OPT",
        Exchange = "SMART",
        Currency = "USD",
    };

    public static TheoryData<string> BudgetedMethods =>
    [
        // Every method that sends. The two Cancel* methods and CancelAccountStreamsAsync are
        // excluded deliberately: they swallow everything by design, so they cannot report which
        // failure came first — they are covered by the release-and-never-throw tests below.
        "ReqContractDetailsAsync",
        "ReqSecDefOptParamsAsync",
        "ReqMktDataAsync",
        "ReqHistoricalDataAsync",
        "ReqHeadTimestampAsync",
        "PlaceOrderAsync",
        "CancelOrderAsync",
        "ReqAllOpenOrdersAsync",
        "ReqAccountSummaryAsync",
        "ReqPositionsMultiAsync",
        "ReqPnLAsync",
    ];

    [Theory]
    [MemberData(nameof(BudgetedMethods))]
    public async Task Every_sending_method_resolves_the_socket_after_its_budgets(string method)
    {
        var (socket, connection, _, _) = Create(ExhaustedMessageBudget);

        using (connection)
        {
            // TimeoutException means the budget was awaited first. IbkrConnectionException means the
            // socket was captured before the wait — the defect.
            await Assert.ThrowsAsync<TimeoutException>(() => Invoke(socket, method));
        }
    }

    [Fact]
    public async Task An_order_is_never_transmitted_when_the_socket_is_gone_at_the_wire()
    {
        var (socket, connection, wrapper, _) = Create(AmpleBudget);

        using (connection)
        {
            // Trading is permitted (a paper account was reported) but there is no socket, so the
            // failure must come from resolving the client — after the gate, before any write.
            wrapper.managedAccounts("DU1234567");

            var transmitted = false;

            await Assert.ThrowsAsync<IbkrConnectionException>(() => socket.PlaceOrderAsync(
                orderId: 42,
                SampleContract,
                new Order { Action = "BUY", OrderType = "LMT", TotalQuantity = 1, LmtPrice = 1.23 },
                () => transmitted = true,
                CancellationToken.None));

            Assert.False(
                transmitted,
                "onAboutToTransmit fired without a socket to write to. Order compensation keys off " +
                "this flag, so a false positive strands the internal order id on a phantom mapping.");
        }
    }

    [Fact]
    public async Task An_order_is_never_transmitted_when_the_trading_gate_refuses_at_the_wire()
    {
        var (socket, connection, _, _) = Create(AmpleBudget);

        using (connection)
        {
            // No managedAccounts, so the gate is shut — the state a reconnect onto an unexpected
            // account leaves behind while a placement is parked in the message budget.
            var transmitted = false;

            await Assert.ThrowsAsync<InvalidOperationException>(() => socket.PlaceOrderAsync(
                orderId: 43,
                SampleContract,
                new Order { Action = "BUY", OrderType = "LMT", TotalQuantity = 1, LmtPrice = 1.23 },
                () => transmitted = true,
                CancellationToken.None));

            Assert.False(transmitted);
        }
    }

    [Fact]
    public async Task A_market_data_line_goes_back_to_the_ledger_when_the_socket_is_gone()
    {
        var (socket, connection, _, governor) = Create(AmpleBudget);

        using (connection)
        {
            await Assert.ThrowsAsync<IbkrConnectionException>(() => socket.ReqMktDataAsync(
                tickerId: 7,
                SampleContract,
                genericTickList: string.Empty,
                snapshot: false,
                regulatorySnapshot: false,
                mktDataOptions: null,
                LineClass.Execution,
                CancellationToken.None));

            // The line was acquired before the socket was resolved; failing after that must not
            // strand it, or the ledger drifts up by one per failed request until quoting stops.
            var budget = governor.GetLineBudget();
            Assert.Equal(0, budget.ExecutionInUse);
            Assert.Equal(0, budget.ResearchInUse);
        }
    }

    [Fact]
    public async Task Cancelling_account_streams_never_throws_without_a_socket()
    {
        var (socket, connection, _, _) = Create(AmpleBudget);

        using (connection)
        {
            // A rebuild must proceed whether or not the desubscribe lands; a socket that has gone
            // already dropped the subscriptions.
            await socket.CancelAccountStreamsAsync(summaryId: 11, positionsId: 12, pnlId: 13)
                .WaitAsync(RealWait);

            await socket.CancelAccountStreamsAsync(summaryId: null, positionsId: null, pnlId: 13)
                .WaitAsync(RealWait);
        }
    }

    private static Task Invoke(PacedSocket socket, string method) => method switch
    {
        "ReqContractDetailsAsync" =>
            socket.ReqContractDetailsAsync(1, SampleContract, CancellationToken.None),

        "ReqSecDefOptParamsAsync" =>
            socket.ReqSecDefOptParamsAsync(1, "SPY", string.Empty, "STK", 756733, CancellationToken.None),

        "ReqMktDataAsync" =>
            socket.ReqMktDataAsync(1, SampleContract, string.Empty, false, false, null, LineClass.Execution, CancellationToken.None),

        "ReqHistoricalDataAsync" =>
            socket.ReqHistoricalDataAsync(
                1, SampleContract, string.Empty, "1 D", "1 min", "TRADES", 1, 2, false, null,
                "request", "contract", false, CancellationToken.None),

        "ReqHeadTimestampAsync" =>
            socket.ReqHeadTimestampAsync(1, SampleContract, "TRADES", 1, 2, "request", "contract", false, CancellationToken.None),

        "PlaceOrderAsync" =>
            socket.PlaceOrderAsync(
                1,
                SampleContract,
                new Order { Action = "BUY", OrderType = "LMT", TotalQuantity = 1, LmtPrice = 1.23 },
                () => throw new Xunit.Sdk.XunitException("The order reached the wire despite an exhausted budget."),
                CancellationToken.None),

        "CancelOrderAsync" => socket.CancelOrderAsync(1, new OrderCancel(), CancellationToken.None),
        "ReqAllOpenOrdersAsync" => socket.ReqAllOpenOrdersAsync(CancellationToken.None),
        "ReqAccountSummaryAsync" => socket.ReqAccountSummaryAsync(1, "All", "NetLiquidation", CancellationToken.None),
        "ReqPositionsMultiAsync" => socket.ReqPositionsMultiAsync(1, "DU1234567", string.Empty, CancellationToken.None),
        "ReqPnLAsync" => socket.ReqPnLAsync(1, "DU1234567", string.Empty, CancellationToken.None),

        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unmapped PacedSocket method."),
    };
}
