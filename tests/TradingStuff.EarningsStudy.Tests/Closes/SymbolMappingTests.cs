using TradingStuff.EarningsStudy.Closes;

namespace TradingStuff.EarningsStudy.Tests.Closes;

public sealed class SymbolMappingTests
{
    [Theory]
    [InlineData("BRK.B", "BRK B")]
    [InlineData("BF.B", "BF B")]
    [InlineData("AAPL", "AAPL")]
    public void Dot_class_shares_become_space_class_shares(string cboeSymbol, string expectedIbkrSymbol)
    {
        Assert.Equal(expectedIbkrSymbol, SymbolMapping.ToIbkrSymbol(cboeSymbol));
    }

    [Theory]
    [InlineData("NYSE", "NYSE")]
    [InlineData("Nasdaq", "NASDAQ")]
    [InlineData("NYSE American", "AMEX")]
    // Universe rows are matched case-insensitively against Gates.PrimaryListing (C1Registration
    // uses OrdinalIgnoreCase), so the mapping must accept the same casing variance rather than
    // silently falling through to the "unrecognised" branch for a row the universe gate accepted.
    [InlineData("nasdaq", "NASDAQ")]
    public void Sec_exchange_maps_to_ibkr_primary_exchange(string secExchange, string expectedPrimaryExchange)
    {
        Assert.Equal(expectedPrimaryExchange, SymbolMapping.ToPrimaryExchange(secExchange));
    }

    [Fact]
    public void Null_exchange_maps_to_null_without_invoking_the_callback()
    {
        var invoked = false;
        var result = SymbolMapping.ToPrimaryExchange(null, _ => invoked = true);

        Assert.Null(result);
        Assert.False(invoked);
    }

    [Fact]
    public void Unrecognised_exchange_passes_through_and_is_reported()
    {
        string? reported = null;

        var result = SymbolMapping.ToPrimaryExchange("OTC", value => reported = value);

        Assert.Equal("OTC", result);
        Assert.Equal("OTC", reported);
    }
}
