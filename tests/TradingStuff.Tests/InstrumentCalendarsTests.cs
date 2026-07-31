using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// The instrument -> calendar mapping, which is reference data every downstream expectation is
/// computed from.
/// </summary>
/// <remarks>
/// Worth pinning by hand rather than trusting: a wrong entry here does not fail, it produces a
/// confident report about the wrong sessions. Both directions have already happened — a single
/// shared Cboe pair was used as the coverage denominator for NYSE-listed SPY, and the 780-minute
/// Cboe index-OPTION session was used as the expectation for VIX INDEX bars, which flagged every
/// correct VIX overnight session as missing.
/// </remarks>
public sealed class InstrumentCalendarsTests
{
    [Fact]
    public void SPY_is_NYSE_calendared_and_shares_nothing_with_the_Cboe_index_pair()
    {
        var spy = InstrumentCalendars.For("SPY", "stock");

        Assert.Equal(["NYSE"], spy.Calendars);
        Assert.True(spy.Includes("NYSE", "RTH"));
        Assert.False(spy.Includes("CBOE_INDEX_RTH", "RTH"));
    }

    [Fact]
    public void VIX_index_is_not_mapped_to_the_index_option_overnight_session()
    {
        // CBOE_INDEX_GTH is 19:15-08:15 CT — the OPTION session, 780 minutes. VIX index values are
        // not published across it; the capability matrix records VIX 1-minute GTH bars beginning at
        // 02:15 CT. Expecting the option window over index data reported every complete, correct VIX
        // overnight session as succeeded_but_absent.
        var vix = InstrumentCalendars.For("VIX", "index");

        Assert.False(vix.Includes("CBOE_INDEX_GTH", "GTH"));
        Assert.True(vix.Includes("CBOE_INDEX_RTH", "RTH"));
    }

    [Fact]
    public void VIX_declares_its_overnight_window_unmodelled_rather_than_leaving_it_silent()
    {
        // The opposite failure to the one above, and the reason removing the wrong calendar is not
        // the whole fix: with no expectation at all, genuinely absent overnight VIX bars render as a
        // clean report. Naming the window as unmodelled is what keeps a consumer able to say so.
        var vix = InstrumentCalendars.For("VIX", "index");

        var unmodelled = Assert.Single(vix.Unmodelled);
        Assert.Contains("02:15", unmodelled.Description);
    }

    [Fact]
    public void The_same_symbol_under_a_different_kind_is_a_different_instrument()
    {
        // SPX the index and SPX the option class share a symbol and do not share an expectation.
        // Keying on symbol alone is what put an option-session expectation on index bars.
        Assert.NotEqual(
            InstrumentCalendars.For("VIX", "index").Expectations,
            InstrumentCalendars.For("SPX", "option_class").Expectations);
    }

    [Fact]
    public void ES_carries_both_labels_under_one_calendar_key_because_globex_nests_the_regular_session()
    {
        var es = InstrumentCalendars.For("ES", "future_family");

        Assert.Equal(["CME_ES"], es.Calendars);
        Assert.True(es.Includes("CME_ES", "GTH"));
        Assert.True(es.Includes("CME_ES", "RTH"));
    }

    [Theory]
    [InlineData("QQQ", "stock")]
    [InlineData("SPX", "future_family")]
    [InlineData("", "")]
    public void An_unmapped_instrument_yields_nothing_rather_than_a_default(string symbol, string kind)
    {
        // Callers must refuse to check on an empty mapping. A default calendar would be applied
        // silently and confidently to an instrument nobody has checked it against.
        var mapping = InstrumentCalendars.For(symbol, kind);

        Assert.Empty(mapping.Expectations);
        Assert.Empty(mapping.Calendars);
    }
}
