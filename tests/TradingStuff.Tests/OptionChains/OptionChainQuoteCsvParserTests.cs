using TradingStuff.Volatility.ThetaData;
using TradingStuff.ResearchService.OptionChains;

namespace TradingStuff.Tests.OptionChains;

public sealed class OptionChainQuoteCsvParserTests
{
    private const string Header =
        "symbol,expiration,strike,right,timestamp,bid_size,bid_exchange,bid,bid_condition,ask_size,ask_exchange,ask,ask_condition";

    // A byte-for-byte sample of the live Terminal's response shape, captured 2026-08-02 against
    // SPXW expiring 2012-06-08 (see the ingestion report for the full probe).
    private static CsvTable SampleTable() => CsvTable.Parse(
        Header + "\n" +
        "\"SPXW\",\"2012-06-08\",1050.000,\"CALL\",2012-06-01T15:45:00.000,275,5,226.30,50,191,5,229.20,50\n" +
        "\"SPXW\",\"2012-06-08\",1050.000,\"PUT\",2012-06-01T15:45:00.000,718,5,0.05,50,1639,5,0.10,50\n");

    [Fact]
    public void Parses_full_NBBO_both_sides()
    {
        var rows = OptionChainQuoteCsvParser.Parse(SampleTable(), "SPX", "SPXW", new DateOnly(2012, 6, 8));

        Assert.Equal(2, rows.Count);

        var call = Assert.Single(rows, r => r.Right == 'C');
        Assert.Equal(1050m, call.Strike);
        Assert.Equal(226.30m, call.Bid);
        Assert.Equal(229.20m, call.Ask);
        Assert.Equal(275m, call.BidSize);
        Assert.Equal(191m, call.AskSize);
        Assert.Equal((short)5, call.BidExchange);
        Assert.Equal((short)5, call.AskExchange);
        Assert.Equal("SPX", call.Underlying);
        Assert.Equal("SPXW", call.TradingClass);
        Assert.Equal(new DateOnly(2012, 6, 8), call.Expiration);

        var put = Assert.Single(rows, r => r.Right == 'P');
        Assert.Equal(0.05m, put.Bid);
    }

    [Fact]
    public void Trading_date_matches_the_local_timestamps_date_component()
    {
        var rows = OptionChainQuoteCsvParser.Parse(SampleTable(), "SPX", "SPXW", new DateOnly(2012, 6, 8));

        Assert.All(rows, r => Assert.Equal(new DateOnly(2012, 6, 1), r.TradingDate));
    }

    [Fact]
    public void Observed_at_is_a_UTC_instant_ahead_of_the_naive_local_timestamp()
    {
        // 2012-06-01T15:45:00 America/New_York is EDT (UTC-4), so the UTC instant must be 19:45.
        var rows = OptionChainQuoteCsvParser.Parse(SampleTable(), "SPX", "SPXW", new DateOnly(2012, 6, 8));

        Assert.All(rows, r =>
        {
            Assert.Equal(TimeSpan.Zero, r.ObservedAt.Offset);
            Assert.Equal(new DateTime(2012, 6, 1, 19, 45, 0), r.ObservedAt.UtcDateTime);
        });
    }

    [Fact]
    public void A_zero_or_negative_strike_row_is_dropped()
    {
        var table = CsvTable.Parse(
            Header + "\n" +
            "\"SPXW\",\"2012-06-08\",0.000,\"CALL\",2012-06-01T15:45:00.000,0,0,0,0,0,0,0,0\n" +
            "\"SPXW\",\"2012-06-08\",1050.000,\"CALL\",2012-06-01T15:45:00.000,275,5,226.30,50,191,5,229.20,50\n");

        var rows = OptionChainQuoteCsvParser.Parse(table, "SPX", "SPXW", new DateOnly(2012, 6, 8));

        Assert.Single(rows);
        Assert.Equal(1050m, rows[0].Strike);
    }

    [Fact]
    public void A_negative_bid_or_ask_sentinel_is_stored_as_null_not_as_a_negative_price()
    {
        var table = CsvTable.Parse(
            Header + "\n" +
            "\"SPXW\",\"2012-06-08\",1050.000,\"CALL\",2012-06-01T15:45:00.000,0,0,-1,0,0,0,-1,0\n");

        var rows = OptionChainQuoteCsvParser.Parse(table, "SPX", "SPXW", new DateOnly(2012, 6, 8));

        Assert.Null(rows[0].Bid);
        Assert.Null(rows[0].Ask);
    }
}

public sealed class ThetaSymbolMapTests
{
    [Theory]
    [InlineData("SPX", "SPXW", "SPXW")]
    [InlineData("SPX", "SPX", "SPX")]
    [InlineData("VIX", "VIX", "VIX")]
    [InlineData("SPX", "spxw", "SPXW")]
    public void Maps_the_trading_class_to_the_vendor_symbol_case_insensitively(
        string underlying, string tradingClass, string expected)
    {
        Assert.Equal(expected, ThetaSymbolMap.VendorSymbolFor(underlying, tradingClass));
    }

    [Fact]
    public void Refuses_a_missing_trading_class()
    {
        Assert.Throws<ArgumentException>(() => ThetaSymbolMap.VendorSymbolFor("SPX", ""));
    }
}

public sealed class OptionChainIntervalValidationTests
{
    [Fact]
    public void No_interval_supplied_defaults_to_one_minute()
    {
        var result = OptionChainIntervalValidation.Validate(null, confirmTick: null);

        Assert.True(result.IsValid);
        Assert.Equal("1m", result.ResolvedInterval);
    }

    [Fact]
    public void Explicit_1m_is_accepted_without_confirmation()
    {
        var result = OptionChainIntervalValidation.Validate("1m", confirmTick: null);

        Assert.True(result.IsValid);
        Assert.Equal("1m", result.ResolvedInterval);
    }

    [Fact]
    public void Tick_without_confirmation_is_rejected()
    {
        var result = OptionChainIntervalValidation.Validate("tick", confirmTick: null);

        Assert.False(result.IsValid);
        Assert.Contains("confirmTick", result.ErrorDetail);
    }

    [Fact]
    public void Tick_with_confirmation_false_is_still_rejected()
    {
        var result = OptionChainIntervalValidation.Validate("tick", confirmTick: false);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Tick_with_explicit_confirmation_is_accepted()
    {
        var result = OptionChainIntervalValidation.Validate("tick", confirmTick: true);

        Assert.True(result.IsValid);
        Assert.Equal("tick", result.ResolvedInterval);
    }

    [Fact]
    public void An_unrecognized_interval_is_rejected()
    {
        var result = OptionChainIntervalValidation.Validate("5m", confirmTick: null);

        Assert.False(result.IsValid);
    }
}
