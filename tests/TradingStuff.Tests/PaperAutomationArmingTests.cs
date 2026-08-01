using TradingStuff.ExecutionService;
using TradingStuff.MarketDataService;
using TradingStuff.ResearchService.Automation;

namespace TradingStuff.Tests;

/// <summary>
/// The gate between "automation is on" and "automation may place an order".
/// </summary>
/// <remarks>
/// Every test here is a REFUSAL test except two, and that ratio is the point. The single armed case
/// exists so the refusals cannot all pass by the function simply never arming — the failure mode
/// that made a gap-detection suite green with a fix that counted nothing (docs/LESSONS.md §2).
/// </remarks>
public sealed class PaperAutomationArmingTests
{
    private static readonly BrokerFacts HealthyBroker = new(
        Connected: true, TradingPermitted: true, TradingBlockedReason: null, ManagedAccounts: ["DUQ000001"]);

    private static ExecutionPlaneConfiguration CoherentPlane() =>
        new("ibkr", "ibkr", "ibkr-gateway", "ibkr-delayed");

    private static ArmingResult Evaluate(
        bool enabled = true,
        bool killed = false,
        ExecutionPlaneConfiguration? plane = null,
        string? planeError = null,
        BrokerFacts? broker = null,
        string? brokerError = null,
        int ordersThisSession = 0,
        int cap = 2) =>
        PaperAutomationArming.Evaluate(
            enabled, killed, killed ? "operator stopped it" : null,
            plane ?? (planeError is null ? CoherentPlane() : null), planeError,
            broker ?? (brokerError is null ? HealthyBroker : null), brokerError,
            ordersThisSession, cap);

    [Fact]
    public void Arms_when_every_condition_holds()
    {
        var result = Evaluate();

        Assert.True(result.Armed);
        Assert.Equal(ArmStates.Armed, result.State);

        // The positive control has to be non-vacuous too: an "armed" that said nothing about WHY
        // would let a refusal-shaped bug through as long as the flag came back true.
        Assert.Contains("ibkr", result.Reason);
        Assert.Contains("2 of 2 orders remaining", result.Reason);
    }

    [Fact]
    public void Refuses_when_disabled()
    {
        var result = Evaluate(enabled: false);

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.Disabled, result.State);
    }

    /// <summary>
    /// The docs/LESSONS.md §9 case, one setting at a time. Each of these is a stack that looks
    /// configured for real trading from every angle except the one being varied.
    /// </summary>
    [Theory]
    // Router on the simulator while everything else is real: orders never reach the broker.
    [InlineData("paper", "ibkr", "ibkr-gateway", "router")]
    // The exact 2026-08-01 incident: a real router transmitting against generated quotes.
    [InlineData("ibkr", "ibkr", "ibkr-deterministic-paper-feed", "quote provider")]
    // Real router and real quotes, fabricated buying power and a flat day: MAX_DAILY_LOSS cannot fire.
    [InlineData("ibkr", "development", "ibkr-gateway", "portfolio provider")]
    // A plausible-looking value that is not one of the recognised strings — the shape of the incident.
    [InlineData("ibkr-live", "ibkr", "ibkr-gateway", "router")]
    public void Refuses_when_the_execution_plane_does_not_agree(
        string router, string portfolioSource, string marketDataSource, string expectedFault)
    {
        var result = Evaluate(plane: new ExecutionPlaneConfiguration(router, portfolioSource, marketDataSource, "ibkr-delayed"));

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.IncoherentConfiguration, result.State);
        Assert.Contains(expectedFault, result.Reason);

        // The reason names all three measured values, not just the faulty one. An operator reading
        // "your quote provider is wrong" needs to know whether the other two are right.
        Assert.Contains($"router='{router}'", result.Reason);
        Assert.Contains($"portfolio='{portfolioSource}'", result.Reason);
        Assert.Contains($"marketData='{marketDataSource}'", result.Reason);
    }

    [Fact]
    public void Names_every_fault_when_more_than_one_setting_is_wrong()
    {
        var result = Evaluate(plane: new ExecutionPlaneConfiguration("paper", "development", "ibkr-deterministic-paper-feed", null));

        Assert.False(result.Armed);
        Assert.Contains("router", result.Reason);
        Assert.Contains("portfolio provider", result.Reason);
        Assert.Contains("quote provider", result.Reason);
    }

    [Fact]
    public void Refuses_when_the_execution_plane_cannot_be_read()
    {
        var result = Evaluate(planeError: "Connection refused");

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.PreflightFailed, result.State);
        Assert.Contains("Connection refused", result.Reason);
    }

    [Fact]
    public void Refuses_when_no_execution_plane_was_read_at_all()
    {
        // Neither a value nor an error. The one shape in which "we do not know" could quietly become
        // "it is fine" if the null check were missing.
        var result = PaperAutomationArming.Evaluate(
            true, false, null, null, null, HealthyBroker, null, 0, 2);

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.PreflightFailed, result.State);
    }

    [Theory]
    [InlineData(false, true, "not connected")]
    [InlineData(true, false, "trading gate is closed")]
    public void Refuses_when_the_broker_is_unusable(bool connected, bool tradingPermitted, string expected)
    {
        var result = Evaluate(broker: new BrokerFacts(connected, tradingPermitted, "an example reason", ["DUQ000001"]));

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.BrokerUnusable, result.State);
        Assert.Contains(expected, result.Reason);
    }

    [Fact]
    public void Refuses_when_the_gateway_status_cannot_be_read()
    {
        var result = Evaluate(brokerError: "504 Gateway Timeout");

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.BrokerUnusable, result.State);
        Assert.Contains("504", result.Reason);
    }

    [Fact]
    public void Refuses_when_no_account_is_reported()
    {
        var result = Evaluate(broker: HealthyBroker with { ManagedAccounts = [] });

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.BrokerUnusable, result.State);
        Assert.Contains("no managed accounts", result.Reason);
    }

    [Fact]
    public void Refuses_a_live_money_account()
    {
        var result = Evaluate(broker: HealthyBroker with { ManagedAccounts = ["U1234567"] });

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.BrokerUnusable, result.State);
        Assert.Contains("does not start with 'DU'", result.Reason);
    }

    [Fact]
    public void Refuses_when_any_managed_account_is_live_money_not_only_the_first()
    {
        // A session managing both is one where the gateway's configured account could be either, and
        // this component cannot see which. Checking only [0] would pass this.
        var result = Evaluate(broker: HealthyBroker with { ManagedAccounts = ["DUQ000001", "U7654321"] });

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.BrokerUnusable, result.State);
    }

    [Fact]
    public void Never_writes_a_whole_account_number_into_a_refusal()
    {
        var result = Evaluate(broker: HealthyBroker with { ManagedAccounts = ["U7654321"] });

        Assert.DoesNotContain("U7654321", result.Reason);
        Assert.Contains("***321", result.Reason);
    }

    [Fact]
    public void Refuses_when_the_cap_is_spent()
    {
        var result = Evaluate(ordersThisSession: 2, cap: 2);

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.CapReached, result.State);
        Assert.Contains("2 of 2", result.Reason);
    }

    [Fact]
    public void Refuses_when_the_cap_has_somehow_been_exceeded()
    {
        // Stops rather than wrapping. A `== cap` comparison would arm here, which is the one way a
        // cap can fail in the direction that matters.
        var result = Evaluate(ordersThisSession: 3, cap: 2);

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.CapReached, result.State);
    }

    [Fact]
    public void Arms_on_the_last_order_of_the_cap()
    {
        var result = Evaluate(ordersThisSession: 1, cap: 2);

        Assert.True(result.Armed);
        Assert.Contains("1 of 2 orders remaining", result.Reason);
    }

    [Fact]
    public void The_kill_switch_refuses_ahead_of_every_configuration_check()
    {
        // Deliberately with a broken plane as well: the operator asked it to stop, and a refusal
        // reason pointing at the router would send them to fix the wrong thing.
        var result = Evaluate(killed: true, plane: new ExecutionPlaneConfiguration("paper", "development", "x", null));

        Assert.False(result.Armed);
        Assert.Equal(ArmStates.Killed, result.State);
        Assert.Contains("operator stopped it", result.Reason);
    }

    [Fact]
    public void Being_disabled_refuses_ahead_of_the_kill_switch()
    {
        var result = Evaluate(enabled: false, killed: true);

        Assert.Equal(ArmStates.Disabled, result.State);
    }

    /// <summary>
    /// The constants are literals in ResearchService, so a rename on the other side of the mesh would
    /// silently disarm the coherence check — it would compare against a value nothing can report and
    /// refuse forever, which is safe but for a reason unrelated to the configuration it claims to
    /// check. That already happened once during this feature's construction: the accepted set was
    /// written as the two <c>MarketData:Source</c> configuration strings and compared against what
    /// MarketDataService actually reports, which is neither of them.
    /// </summary>
    [Fact]
    public void The_required_values_match_the_real_components()
    {
        Assert.Equal(OrderRouters.Ibkr, PaperAutomationArming.RequiredRouter);
        Assert.Equal(PortfolioSources.Ibkr, PaperAutomationArming.RequiredPortfolioSource);

        // The resolved provider's own name, which is what /market-data/ibkr/status reports as `mode`.
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost") };
        var provider = new IbkrOptionMarketDataProvider(
            http, Microsoft.Extensions.Logging.Abstractions.NullLogger<IbkrOptionMarketDataProvider>.Instance);

        Assert.Equal(provider.Source, PaperAutomationArming.RequiredMarketDataProvider);

        // And the value the check must REJECT is the deterministic generator's, not some third string.
        Assert.NotEqual(MarketDataSources.DeterministicPaperFeed, PaperAutomationArming.RequiredMarketDataProvider);
    }
}
