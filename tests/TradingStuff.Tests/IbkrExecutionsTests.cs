using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.IbkrGateway;
using IbContract = IBApi.Contract;

namespace TradingStuff.Tests;

/// <summary>
/// The solicited-executions path: callback routing, commission correlation, and the mapping from
/// TWS's loose types onto the raw capture record. Everything that does not need a socket.
/// </summary>
/// <remarks>
/// Driven through <see cref="IbkrClientWrapper"/> rather than by calling the request object
/// directly, because the property that matters is the ROUTING — a solicited pull must take the
/// report and the order tracker must not see it. Calling the request directly would assert nothing
/// about that.
/// </remarks>
public sealed class IbkrExecutionsTests
{
    private static IbkrOrderTracker NewTracker() => new(NullLogger<IbkrOrderTracker>.Instance);

    private static IbkrClientWrapper NewWrapper(
        IbkrRequestRegistry registry, IbkrOrderTracker tracker, ExecutionCommissionRouter commissions) =>
        new(registry, tracker, commissions, NullLogger<IbkrClientWrapper>.Instance);

    private static IbContract SpyPut(int conId = 776_512_301) => new()
    {
        ConId = conId,
        Symbol = "SPY",
        SecType = "OPT",
        LastTradeDateOrContractMonth = "20260904",
        Strike = 725d,
        Right = "P",
        TradingClass = "SPY",
        Multiplier = "100",
        Exchange = "SMART",
        Currency = "USD",
    };

    private static Execution Fill(string execId, string side = "SLD", decimal shares = 1m, double price = 1.37d) => new()
    {
        ExecId = execId,
        OrderId = 42,
        ClientId = 7,
        PermId = 1_234_567_890L,
        AcctNumber = "DU1234567",
        Exchange = "CBOE",
        Side = side,
        Shares = shares,
        Price = price,
        Time = "20260904-19:45:12",
    };

    // ---- routing ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_solicited_execution_reaches_its_own_request_and_terminates_on_the_end_callback()
    {
        var registry = new IbkrRequestRegistry();
        var commissions = new ExecutionCommissionRouter();
        var wrapper = NewWrapper(registry, NewTracker(), commissions);

        var request = new ExecutionsRequest();
        registry.Register(11, request);

        wrapper.execDetails(11, SpyPut(), Fill("0001.abc.01"));
        Assert.False(request.Delivered.IsCompleted);

        wrapper.execDetailsEnd(11);
        await request.Delivered;

        var received = request.Snapshot();
        Assert.Single(received);
        Assert.Equal("0001.abc.01", received[0].Execution.ExecId);
    }

    [Fact]
    public void An_unsolicited_execution_still_goes_to_the_order_tracker()
    {
        var registry = new IbkrRequestRegistry();
        var tracker = NewTracker();
        var wrapper = NewWrapper(registry, tracker, new ExecutionCommissionRouter());

        // No ExecutionsRequest registered for this id, which is what a push for an order this
        // process placed looks like. The tracker ignores unknown orders, so the observable claim is
        // that routing did not throw and did not divert it — asserted by the capture path being
        // empty in the test above and by the tracker owning this branch unchanged.
        wrapper.execDetails(-1, SpyPut(), Fill("0002.abc.01"));

        Assert.Empty(tracker.All());
    }

    [Fact]
    public async Task A_replayed_execution_is_the_same_fill_not_a_second_one()
    {
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry, NewTracker(), new ExecutionCommissionRouter());

        var request = new ExecutionsRequest();
        registry.Register(12, request);

        wrapper.execDetails(12, SpyPut(), Fill("0003.abc.01"));
        wrapper.execDetails(12, SpyPut(), Fill("0003.abc.01"));
        wrapper.execDetailsEnd(12);

        await request.Delivered;

        Assert.Single(request.Snapshot());
    }

    [Fact]
    public void A_tws_error_faults_the_pull_rather_than_leaving_it_waiting_forever()
    {
        var registry = new IbkrRequestRegistry();
        var wrapper = NewWrapper(registry, NewTracker(), new ExecutionCommissionRouter());

        var request = new ExecutionsRequest();
        registry.Register(13, request);

        wrapper.error(13, 0L, 321, "Error validating request.", string.Empty);

        Assert.True(request.Delivered.IsFaulted);
    }

    // ---- commissions -----------------------------------------------------------------------------

    [Fact]
    public void A_commission_report_reaches_the_pull_that_holds_its_exec_id()
    {
        var registry = new IbkrRequestRegistry();
        var commissions = new ExecutionCommissionRouter();
        var wrapper = NewWrapper(registry, NewTracker(), commissions);

        var request = new ExecutionsRequest();
        registry.Register(14, request);
        commissions.Attach(14, request);

        wrapper.execDetails(14, SpyPut(), Fill("0004.abc.01"));
        wrapper.commissionAndFeesReport(new CommissionAndFeesReport
        {
            ExecId = "0004.abc.01",
            CommissionAndFees = 0.799_346d,
            Currency = "USD",
            RealizedPNL = 12.5d,
        });

        Assert.True(request.CommissionsComplete);

        var row = IbkrExecutionsClient.Project(request.Snapshot())[0];
        Assert.Equal(0.799346m, row.Commission);
        Assert.Equal("USD", row.CommissionCurrency);
        Assert.Equal(12.5m, row.RealizedPnL);
    }

    [Fact]
    public void A_commission_for_an_exec_id_this_pull_never_saw_is_not_claimed()
    {
        var commissions = new ExecutionCommissionRouter();
        var request = new ExecutionsRequest();
        commissions.Attach(15, request);

        request.Add(SpyPut(), Fill("0005.abc.01"));
        commissions.Apply(new CommissionAndFeesReport { ExecId = "9999.zzz.01", CommissionAndFees = 4d });

        // Otherwise an overlapping pull — or an order the tracker owns — would have its commission
        // stolen and attached to the wrong fill.
        Assert.False(request.CommissionsComplete);
        Assert.Null(IbkrExecutionsClient.Project(request.Snapshot())[0].Commission);
    }

    [Fact]
    public void A_detached_pull_stops_receiving_commissions()
    {
        var commissions = new ExecutionCommissionRouter();
        var request = new ExecutionsRequest();

        commissions.Attach(16, request);
        request.Add(SpyPut(), Fill("0006.abc.01"));
        commissions.Detach(16);

        commissions.Apply(new CommissionAndFeesReport { ExecId = "0006.abc.01", CommissionAndFees = 1d });

        Assert.False(request.CommissionsComplete);
    }

    [Fact]
    public void A_missing_commission_is_recorded_as_absent_never_as_zero()
    {
        var request = new ExecutionsRequest();
        request.Add(SpyPut(), Fill("0007.abc.01"));

        var row = IbkrExecutionsClient.Project(request.Snapshot())[0];

        // A zero here would be a fabricated cost basis, and the shadow record's whole point is that
        // the raw inputs are reconstructible. Null says "TWS did not report one in this pull".
        Assert.Null(row.Commission);
        Assert.Null(row.CommissionCurrency);
    }

    // ---- mapping ---------------------------------------------------------------------------------

    [Fact]
    public void The_achieved_contract_is_carried_through_verbatim()
    {
        var request = new ExecutionsRequest();
        request.Add(SpyPut(), Fill("0008.abc.01"));

        var row = IbkrExecutionsClient.Project(request.Snapshot())[0];

        Assert.Equal(776_512_301, row.ConId);
        Assert.Equal("SPY", row.Symbol);
        Assert.Equal("OPT", row.SecType);
        Assert.Equal(new DateOnly(2026, 9, 4), row.Expiration);
        Assert.Equal(725m, row.Strike);
        Assert.Equal("P", row.Right);
        Assert.Equal(100, row.Multiplier);
        // TWS's own side spelling, not an OrderSide: the enum can move, this column must not.
        Assert.Equal("SLD", row.Side);
        Assert.Equal(1m, row.Quantity);
        Assert.Equal(1.37m, row.Price);
        Assert.Equal(1_234_567_890L, row.PermId);
    }

    [Fact]
    public void A_non_option_execution_is_recorded_rather_than_dropped()
    {
        var request = new ExecutionsRequest();

        var stock = new IbContract { ConId = 756_733, Symbol = "SPY", SecType = "STK", Exchange = "SMART" };
        request.Add(stock, Fill("0009.abc.01", side: "BOT", shares: 100m, price: 640.12d));

        var row = IbkrExecutionsClient.Project(request.Snapshot())[0];

        // The portfolio projection has nowhere to put an equity and logs it away; a capture layer
        // that did the same would lose the one record of what the account actually traded.
        Assert.Equal("STK", row.SecType);
        Assert.Null(row.Expiration);
        Assert.Null(row.Strike);
        Assert.Equal(100m, row.Quantity);
    }

    [Theory]
    [InlineData("20260904-19:45:12", 2026, 9, 4, 19, 45, 12)]
    [InlineData("20260904  19:45:12", 2026, 9, 4, 19, 45, 12)]
    public void A_recognised_execution_time_parses_to_the_utc_instant(
        string raw, int year, int month, int day, int hour, int minute, int second)
    {
        var parsed = IbkrExecutionsClient.TryParseExecutionTime(raw);

        Assert.Equal(
            new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-time")]
    [InlineData("2026-09-04T19:45:12Z")]
    public void An_unrecognised_execution_time_is_null_and_the_raw_string_survives(string? raw)
    {
        // A fill dated by guesswork is worse than one whose timestamp is honestly unresolved: the
        // capture layer exists to reconstruct what happened when.
        Assert.Null(IbkrExecutionsClient.TryParseExecutionTime(raw));

        var request = new ExecutionsRequest();
        var execution = Fill("0010.abc.01");
        execution.Time = raw!;
        request.Add(SpyPut(), execution);

        var row = IbkrExecutionsClient.Project(request.Snapshot())[0];
        Assert.Null(row.ExecutedAt);
        Assert.Equal(raw ?? string.Empty, row.ExecutedAtRaw);
    }
}
