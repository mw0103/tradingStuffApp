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

        Assert.Equal(["NYSE", "NYSE_EXTENDED"], spy.Calendars);
        Assert.True(spy.Includes("NYSE", "RTH"));
        Assert.True(spy.Includes("NYSE_EXTENDED", "GTH"));
        Assert.False(spy.Includes("CBOE_INDEX_RTH", "RTH"));
    }

    [Fact]
    public void SPY_declares_no_unmodelled_window_over_any_window_the_calendar_covers()
    {
        // SPY's real 04:00-20:00 ET day was declared unmodelled while nothing described it — 570
        // minutes a day whose absence would not have been reported. NYSE_EXTENDED describes it now
        // (measured 2026-08-01 against the venue's published schedule and against 960 useRth=false
        // 1-minute bars a day), and the second calendar key NESTS the first rather than sitting beside
        // it, so a full day expects 960 minutes and not 960 + 390. The shapes are pinned with literal
        // instants in ExchangeSessionScheduleTests.
        var spy = InstrumentCalendars.For("SPY", "stock");
        var window = spy.Unmodelled.Single();

        Assert.Null(window.Intersect(
            new DateTimeOffset(2010, 1, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Every_unmodelled_window_a_shipped_instrument_declares_is_bounded()
    {
        // The property that matters, stated over the whole mapping rather than per instrument. An
        // UNBOUNDED admission makes its series report as never-reconciled on every run forever, and
        // both that ever existed (VIX's overnight leg, SPY's whole extended day) were retired by
        // measuring the venue rather than by deciding the window did not matter. What is left is
        // allowed to exist but must name where it stops, or the report goes permanently red again and
        // stops being read — which costs more than the caveat is worth.
        string[][] shipped =
        [
            ["SPX", "option_class"], ["SPX", "index"], ["VIX", "index"],
            ["SPY", "stock"], ["ES", "future_family"],
        ];

        Assert.All(shipped, entry =>
        {
            var mapping = InstrumentCalendars.For(entry[0], entry[1]);

            Assert.NotEmpty(mapping.Expectations);
            Assert.All(mapping.Unmodelled, window =>
            {
                Assert.NotEmpty(window.Description);
                Assert.True(
                    window.From is not null || window.To is not null,
                    $"{entry[0]} ({entry[1]}) declares an unbounded unmodelled window, which makes its " +
                    "series permanently unreconciled.");
            });
        });

        // VIX and SPX have none at all, which is the stronger statement and the one their own
        // calendars earned.
        Assert.Empty(InstrumentCalendars.For("VIX", "index").Unmodelled);
        Assert.Empty(InstrumentCalendars.For("SPX", "index").Unmodelled);
    }

    [Fact]
    public void VIX_index_is_mapped_to_its_own_sessions_not_to_the_index_option_ones()
    {
        // CBOE_INDEX_GTH is 19:15-08:25 CT — the OPTION session. VIX index values are not published
        // across it: the venue reports the index on two legs of its own, 02:15-08:15 and 08:30-16:00
        // CT. Expecting the option window over index data reported every complete, correct VIX
        // overnight session as succeeded_but_absent, and expecting the option RTH close understated
        // the day leg by 45 minutes. Both now have their own calendars; the shapes are pinned with
        // literal instants in ExchangeSessionScheduleTests.
        var vix = InstrumentCalendars.For("VIX", "index");

        Assert.False(vix.Includes("CBOE_INDEX_GTH", "GTH"));
        Assert.False(vix.Includes("CBOE_INDEX_RTH", "RTH"));
        Assert.True(vix.Includes("CBOE_VIX_RTH", "RTH"));
        Assert.True(vix.Includes("CBOE_VIX_GTH", "GTH"));
    }

    [Fact]
    public void VIX_no_longer_has_an_unmodelled_window_to_declare()
    {
        // This used to assert the opposite, and that was right on the evidence then: one day's bars
        // are not a published schedule, so the overnight window was named as unaudited rather than
        // approximated. It is now established from the venue's own schedule (TWS contractDetails plus
        // reqHistoricalData SCHEDULE, 2026-08-01), and leaving it unmodelled had become its own
        // failure — the VIX series reported as never-reconciled on every run, which is a permanently
        // red gate for a non-problem.
        Assert.Empty(InstrumentCalendars.For("VIX", "index").Unmodelled);
    }

    [Fact]
    public void SPX_the_index_is_measured_against_the_cash_session_not_the_option_session()
    {
        // The index level stops when the cash market does (08:30-15:00 CT, 390 minutes); the options
        // trade fifteen minutes longer. Mapping the index to the option calendar made every SPX RTH
        // session report succeeded_but_absent, 405 expected against 390 landed. There is also no
        // overnight leg at all — the entry claiming one cited a capability matrix that says the
        // reverse.
        var spx = InstrumentCalendars.For("SPX", "index");

        Assert.Equal(["CBOE_SPX_RTH"], spx.Calendars);
        Assert.False(spx.Includes("CBOE_INDEX_RTH", "RTH"));
        Assert.False(spx.Includes("CBOE_INDEX_GTH", "GTH"));

        // The option CLASS keeps both Cboe sessions; that mapping was never wrong.
        var options = InstrumentCalendars.For("SPX", "option_class");
        Assert.True(options.Includes("CBOE_INDEX_RTH", "RTH"));
        Assert.True(options.Includes("CBOE_INDEX_GTH", "GTH"));
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
