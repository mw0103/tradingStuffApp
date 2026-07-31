using System.Diagnostics.Metrics;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingStuff.Contracts;
using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.Pacing;
using TradingStuff.IbkrGateway.Persistence;
using IbContract = IBApi.Contract;

namespace TradingStuff.Tests;

/// <summary>
/// What happens to the tracker claim and the order-map row when a placement fails before it reaches
/// TWS.
/// </summary>
/// <remarks>
/// Both are written BEFORE transmission, on purpose: they are what stops a caller retry putting a
/// second live order on the book for one internal id. The cost of that ordering is that a placement
/// which never reaches the wire leaves them behind, and every later retry of the internal order then
/// returns a phantom "working" order that does not exist at the broker — permanently, since nothing
/// else clears them.
/// <para>
/// So the compensation must fire for every pre-transmission failure, and the set is wider than it
/// looks: the pacing budget throws <see cref="TimeoutException"/>, resolving the socket throws
/// <see cref="IbkrConnectionException"/>, cancellation throws
/// <see cref="OperationCanceledException"/>, and the trading-gate re-check at the wire throws
/// <see cref="InvalidOperationException"/>. A catch filter enumerating types is exactly the shape
/// that misses one.
/// </para>
/// </remarks>
public sealed class IbkrOrderCompensationTests
{
    private const string PaperAccount = "DU1234567";

    public static TheoryData<string> PreTransmissionFailures =>
    [
        nameof(InvalidOperationException),
        nameof(IbkrConnectionException),
        nameof(TimeoutException),
        nameof(OperationCanceledException),
        nameof(IbkrPacingRejectedException),
    ];

    [Theory]
    [MemberData(nameof(PreTransmissionFailures))]
    public async Task A_placement_that_never_reached_TWS_releases_its_claim(string failure)
    {
        var harness = new Harness();
        harness.Socket.PlaceFailure = Failure(failure);

        var internalOrderId = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Orders.PlaceAsync(internalOrderId, Vertical(), CancellationToken.None));

        Assert.False(harness.Socket.Transmitted);

        // The claim is gone, so a retry of this internal order can legitimately place. Left behind,
        // it would make every retry return a tracked order TWS has never heard of.
        Assert.Null(harness.Tracker.FindByInternalOrderId(internalOrderId));
        Assert.Empty(harness.Orders.All());
    }

    [Fact]
    public async Task A_failure_after_the_write_begins_keeps_the_claim()
    {
        // Deliberately conservative, and the reason compensation keys off the transmit callback
        // rather than the exception type. Once the socket write has been entered the order may be
        // live at TWS; refusing a retry is recoverable by reconciliation, while placing a second
        // live order for one internal id is not.
        var harness = new Harness();
        harness.Socket.PlaceFailure = new IOException("the socket died mid-write");
        harness.Socket.FailAfterTransmitting = true;

        var internalOrderId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(() =>
            harness.Orders.PlaceAsync(internalOrderId, Vertical(), CancellationToken.None));

        Assert.True(harness.Socket.Transmitted);
        Assert.NotNull(harness.Tracker.FindByInternalOrderId(internalOrderId));
    }

    private static Exception Failure(string name) => name switch
    {
        nameof(InvalidOperationException) => new InvalidOperationException(
            "Order placement is blocked: the reconnect landed on an account the gate refuses."),
        nameof(IbkrConnectionException) => new IbkrConnectionException("Not connected to TWS."),
        nameof(TimeoutException) => new TimeoutException("Socket message budget not available."),
        nameof(OperationCanceledException) => new OperationCanceledException(),
        nameof(IbkrPacingRejectedException) => new IbkrPacingRejectedException(TimeSpan.FromSeconds(30)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unmapped failure."),
    };

    private static SubmitOrderRequest Vertical() => new(
        PaperAccount,
        StrategyKind.Vertical,
        OrderType.Limit,
        TimeInForce.Day,
        [
            new OrderLegRequest(Contract(100m), OrderSide.Buy, 1, PositionEffect.Open),
            new OrderLegRequest(Contract(105m), OrderSide.Sell, 1, PositionEffect.Open),
        ],
        LimitPrice: 1.20m);

    private static OptionContract Contract(decimal strike) => new(
        $"XYZ20260821C{strike}",
        "XYZ",
        new DateOnly(2026, 8, 21),
        strike,
        OptionRight.Call);

    /// <summary>Every real collaborator except the socket, which is faked at the PacedSocket seam.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            var options = Options.Create(new IbkrOptions { AccountId = PaperAccount });
            var registry = new IbkrRequestRegistry();

            Tracker = new IbkrOrderTracker(NullLogger<IbkrOrderTracker>.Instance);

            var wrapper = new IbkrClientWrapper(registry, Tracker, NullLogger<IbkrClientWrapper>.Instance);

            var governor = new IbkrPacingGovernor(
                options, TimeProvider.System, new TestMeterFactory(), NullLogger<IbkrPacingGovernor>.Instance);

            var connection = new IbkrConnection(
                options, registry, wrapper, governor, NullLogger<IbkrConnection>.Instance);

            // Opens the trading gate without a socket: managedAccounts is what sets it, and a
            // DU-prefixed account is what makes it permit anything.
            wrapper.managedAccounts(PaperAccount);

            Socket = new FakePacedSocket(connection, governor);

            // No 'trading' connection string, so the order map reports Unavailable and placement
            // continues without it — the documented RequireOrderPersistence=false behaviour.
            var store = new OrderIdStore(
                new ConfigurationBuilder().Build(), NullLogger<OrderIdStore>.Instance);

            Orders = new IbkrOrderClient(
                connection,
                Socket,
                new IbkrMarketDataClient(connection, Socket, options, NullLogger<IbkrMarketDataClient>.Instance),
                Tracker,
                store,
                options,
                NullLogger<IbkrOrderClient>.Instance);
        }

        public FakePacedSocket Socket { get; }

        public IbkrOrderTracker Tracker { get; }

        public IbkrOrderClient Orders { get; }
    }

    /// <summary>
    /// Answers contract resolution in-process and hands the placement outcome to the test.
    /// </summary>
    private sealed class FakePacedSocket(IbkrConnection connection, IbkrPacingGovernor governor)
        : PacedSocket(connection, governor, NullLogger<PacedSocket>.Instance)
    {
        private int _nextConId = 1000;

        public Exception? PlaceFailure { get; set; }

        /// <summary>Raise <see cref="PlaceFailure"/> from inside the write rather than before it.</summary>
        public bool FailAfterTransmitting { get; set; }

        public bool Transmitted { get; private set; }

        public override Task ReqContractDetailsAsync(
            int requestId, IbContract contract, CancellationToken cancellationToken)
        {
            var request = connection.Registry.Get<ListRequest<ContractDetails>>(requestId);

            request?.Add(new ContractDetails
            {
                Contract = new IbContract { ConId = Interlocked.Increment(ref _nextConId) },
                MinTick = 0.01,
            });

            request?.Complete();

            return Task.CompletedTask;
        }

        public override Task PlaceOrderAsync(
            int orderId,
            IbContract contract,
            Order order,
            Action onAboutToTransmit,
            CancellationToken cancellationToken)
        {
            if (PlaceFailure is { } failure && !FailAfterTransmitting)
            {
                return Task.FromException(failure);
            }

            onAboutToTransmit();
            Transmitted = true;

            return PlaceFailure is { } afterWrite ? Task.FromException(afterWrite) : Task.CompletedTask;
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }
}
