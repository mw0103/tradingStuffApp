using TradingStuff.Contracts;
using TradingStuff.ExecutionService;
using TradingStuff.IbkrGateway;
using IbContract = IBApi.Contract;

namespace TradingStuff.Tests;

/// <summary>
/// Account and position mapping for the portfolio read that feeds the risk engine. All of it is
/// pure logic; anything requiring a socket belongs in the integration suite, never here.
/// </summary>
public sealed class IbkrAccountTests
{
    private const string PaperAccount = "DU1234567";

    // ---- account selection --------------------------------------------------------------------

    [Fact]
    public void A_sole_managed_account_is_used_when_nothing_is_requested_or_configured()
    {
        Assert.Equal(PaperAccount, IbkrAccountClient.SelectAccount([PaperAccount], null, null));
    }

    [Fact]
    public void The_configured_account_wins_over_the_first_managed_one()
    {
        var managed = new[] { "DU1111111", PaperAccount };

        Assert.Equal(PaperAccount, IbkrAccountClient.SelectAccount(managed, null, PaperAccount));
    }

    [Fact]
    public void Several_managed_accounts_without_configuration_is_an_error()
    {
        // Guessing here would evaluate risk against a portfolio the order never touches.
        Assert.Throws<IbkrRequestException>(() =>
            IbkrAccountClient.SelectAccount(["DU1111111", "DU2222222"], null, null));
    }

    [Fact]
    public void A_requested_account_must_be_one_TWS_manages()
    {
        Assert.Throws<IbkrRequestException>(() =>
            IbkrAccountClient.SelectAccount([PaperAccount], "DU9999999", null));
    }

    [Fact]
    public void No_managed_accounts_is_a_connection_problem_not_a_request_problem()
    {
        // managedAccounts has not arrived yet, which means the session is not usable rather than the
        // request being wrong — the distinction drives a 503 rather than a 400.
        Assert.Throws<IbkrConnectionException>(() => IbkrAccountClient.SelectAccount([], null, null));
    }

    // ---- account summary ----------------------------------------------------------------------

    [Fact]
    public void Buying_power_falls_back_through_the_tag_list()
    {
        // Not every account type reports BuyingPower; AvailableFunds is the next best answer.
        var rows = new[]
        {
            new AccountSummaryValue(PaperAccount, "NetLiquidation", "125000.00", "USD"),
            new AccountSummaryValue(PaperAccount, "AvailableFunds", "42500.75", "USD"),
        };

        var value = IbkrAccountClient.ReadDecimal(rows, PaperAccount, "BuyingPower", "AvailableFunds");

        Assert.Equal(42_500.75m, value);
    }

    [Fact]
    public void Account_summary_rows_for_other_accounts_are_ignored()
    {
        // reqAccountSummary is issued for the "All" group, so a multi-account login reports every one.
        var rows = new[]
        {
            new AccountSummaryValue("DU9999999", "BuyingPower", "999999.00", "USD"),
            new AccountSummaryValue(PaperAccount, "BuyingPower", "25000.00", "USD"),
        };

        Assert.Equal(25_000m, IbkrAccountClient.ReadDecimal(rows, PaperAccount, "BuyingPower"));
    }

    [Fact]
    public void Usd_rows_win_when_a_tag_arrives_in_several_currencies()
    {
        var rows = new[]
        {
            new AccountSummaryValue(PaperAccount, "BuyingPower", "18000.00", "EUR"),
            new AccountSummaryValue(PaperAccount, "BuyingPower", "25000.00", "USD"),
        };

        Assert.Equal(25_000m, IbkrAccountClient.ReadDecimal(rows, PaperAccount, "BuyingPower"));
    }

    [Fact]
    public void A_missing_tag_reads_as_null_rather_than_zero()
    {
        // Zero buying power would silently reject every order; absent must be distinguishable.
        var rows = new[] { new AccountSummaryValue(PaperAccount, "NetLiquidation", "125000.00", "USD") };

        Assert.Null(IbkrAccountClient.ReadDecimal(rows, PaperAccount, "BuyingPower"));
    }

    // ---- position contract mapping ------------------------------------------------------------

    [Fact]
    public void An_option_position_maps_to_a_contract()
    {
        var contract = IbkrAccountClient.TryToOptionContract(new IbContract
        {
            Symbol = "SPX",
            SecType = "OPT",
            LastTradeDateOrContractMonth = "20260731",
            Strike = 7435d,
            Right = "C",
            Multiplier = "100",
            Currency = "USD",
            TradingClass = "SPXW",
        });

        Assert.NotNull(contract);
        Assert.Equal("SPX", contract.Underlying);
        Assert.Equal(new DateOnly(2026, 7, 31), contract.Expiration);
        Assert.Equal(7435m, contract.Strike);
        Assert.Equal(OptionRight.Call, contract.Right);
        Assert.Equal(100, contract.Multiplier);
        Assert.Equal("SPXW", contract.TradingClass);
    }

    [Fact]
    public void A_position_without_an_exchange_is_quoted_on_smart()
    {
        // Position rows carry the listing, not a route, and frequently report no exchange at all.
        var contract = IbkrAccountClient.TryToOptionContract(new IbContract
        {
            Symbol = "SPY",
            SecType = "OPT",
            LastTradeDateOrContractMonth = "20260821",
            Strike = 450d,
            Right = "P",
            Multiplier = "100",
            Currency = "USD",
        });

        Assert.NotNull(contract);
        Assert.Equal("SMART", contract.Exchange);
        Assert.Equal(OptionRight.Put, contract.Right);
        Assert.Null(contract.TradingClass);
    }

    [Fact]
    public void Non_option_positions_are_not_mapped()
    {
        // PositionSnapshot carries an OptionContract, so equities have no representation. They are
        // counted and reported rather than being dropped without trace.
        Assert.Null(IbkrAccountClient.TryToOptionContract(new IbContract
        {
            Symbol = "SPY",
            SecType = "STK",
            Currency = "USD",
        }));
    }

    [Theory]
    [InlineData("202608")]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void A_position_with_an_unusable_expiration_is_not_mapped(string expiration)
    {
        Assert.Null(IbkrAccountClient.TryToOptionContract(new IbContract
        {
            Symbol = "SPY",
            SecType = "OPT",
            LastTradeDateOrContractMonth = expiration,
            Strike = 450d,
            Right = "C",
            Multiplier = "100",
        }));
    }

    // ---- position snapshots ---------------------------------------------------------------------

    [Fact]
    public void Average_price_divides_ibkr_average_cost_by_the_multiplier()
    {
        // IBKR reports avgCost per contract, so it already includes the multiplier. Every other price
        // in the system is per share; 380 here is a 3.80 option.
        var snapshot = IbkrAccountClient.ToPositionSnapshot(SpxwCall(), quantity: 1m, averageCost: 380d, greeks: null);

        Assert.Equal(3.80m, snapshot.AveragePrice);
    }

    [Fact]
    public void Position_greeks_scale_by_quantity_and_multiplier()
    {
        // Same scaling PortfolioRiskEvaluator applies to the incoming order, so the two are summable.
        var snapshot = IbkrAccountClient.ToPositionSnapshot(
            SpxwCall(),
            quantity: 2m,
            averageCost: 380d,
            greeks: new OptionGreeks(0.662m, 0.004m, -0.85m, 0.35m));

        Assert.Equal(2, snapshot.Quantity);
        Assert.Equal(132.4m, snapshot.GreeksExposure.Delta);
        Assert.Equal(0.8m, snapshot.GreeksExposure.Gamma);
        Assert.Equal(-170m, snapshot.GreeksExposure.Theta);
        Assert.Equal(70m, snapshot.GreeksExposure.Vega);
    }

    [Fact]
    public void A_short_position_flips_the_sign_of_every_greek()
    {
        var snapshot = IbkrAccountClient.ToPositionSnapshot(
            SpxwCall(),
            quantity: -1m,
            averageCost: 380d,
            greeks: new OptionGreeks(0.662m, 0.004m, -0.85m, 0.35m));

        Assert.Equal(-1, snapshot.Quantity);
        Assert.Equal(-66.2m, snapshot.GreeksExposure.Delta);
        Assert.Equal(85m, snapshot.GreeksExposure.Theta);
    }

    [Fact]
    public void A_position_without_greeks_reports_zero_exposure()
    {
        var snapshot = IbkrAccountClient.ToPositionSnapshot(SpxwCall(), quantity: 1m, averageCost: 380d, greeks: null);

        Assert.Equal(GreeksVector.Zero, snapshot.GreeksExposure);
    }

    [Fact]
    public void An_unset_average_cost_sentinel_does_not_poison_the_snapshot()
    {
        // TWS sends double.MaxValue for a value it has not computed; casting it to decimal overflows.
        var snapshot = IbkrAccountClient.ToPositionSnapshot(
            SpxwCall(),
            quantity: 1m,
            averageCost: double.MaxValue,
            greeks: null);

        Assert.Equal(0m, snapshot.AveragePrice);
    }

    // ---- provider selection -------------------------------------------------------------------

    [Theory]
    [InlineData("ibkr")]
    [InlineData("IBKR")]
    public void Portfolio_source_switches_to_ibkr_only_on_the_exact_value(string source)
    {
        Assert.True(PortfolioSources.UsesIbkr(source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("development")]
    [InlineData("ibkr-live")]
    [InlineData("ibrk")]
    public void An_unrecognised_portfolio_source_stays_on_development(string? source)
    {
        // A typo must degrade to fixed development figures, never to a half-configured broker read.
        Assert.False(PortfolioSources.UsesIbkr(source));
    }

    private static OptionContract SpxwCall() => new(
        "SPXW20260731C7435",
        "SPX",
        new DateOnly(2026, 7, 31),
        7435m,
        OptionRight.Call,
        TradingClass: "SPXW");
}
