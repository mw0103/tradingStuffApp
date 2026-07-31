using TradingStuff.IbkrGateway;
using TradingStuff.IbkrGateway.History;

namespace TradingStuff.Tests;

/// <summary>
/// How a rejected <c>reqHeadTimeStamp</c> is classified. Separate from the bars path because the
/// same TWS error code means something different there: no data for one historical SLICE says
/// nothing about the contract, while no data for its head timestamp answers the only question a
/// head timestamp asks.
/// </summary>
/// <remarks>
/// The failure this pins is not a wrong answer but an unbounded one. Classified transient, a
/// genuine no-data head timestamp makes the backfill coordinator defer planning, re-probe next pass,
/// and spend one paced historical request per pass forever — the job never plans a slice, and
/// nothing in the logs says "this contract has no history", only that the probe failed again.
/// </remarks>
public sealed class IbkrHeadTimestampFailureTests
{
    [Theory]
    [InlineData("HMDS query returned no data: SPX   260821C07500000@SMART")]
    [InlineData("Historical Market Data Service error message:No historical market data for SPX/IND@CBOE MidPoint 60")]
    [InlineData("Historical Market Data Service error message:No historical market data for SPX/IND@CBOE MidPoint 0")]
    [InlineData("Historical Market Data Service error message:No historical market data for SPX/IND@CBOE Bid 0")]
    public void A_genuine_no_data_head_timestamp_is_permanent(string message)
    {
        // The last three are the real shape of "this contract and whatToShow have no history at
        // all", and none of them contains a literal "no data". The last two were captured from a
        // live reqHeadTimeStamp against paper TWS on 2026-07-31 (SPX/IND@CBOE: TRADES answered
        // 2004-03-04, MIDPOINT and BID_ASK were both refused with 162) — note the trailing number
        // differs from the reqHistoricalData variant recorded in
        // docs/research/ibkr-data-capability-matrix.md, which is exactly why the matcher keys off
        // the phrasing and not the whole string.
        var reclassified = IbkrHistoricalClient.AsMissingHeadTimestamp(
            new IbkrRequestException(IbkrErrorCodes.NoHistoricalData, message));

        Assert.NotNull(reclassified);
        Assert.True(reclassified.IsPermanent);
        Assert.Equal(IbkrErrorCodes.NoHistoricalData, reclassified.ErrorCode);
        Assert.Equal(message, reclassified.TwsMessage);
    }

    [Theory]
    [InlineData("Historical Market Data Service query message:pacing violation")]
    [InlineData("Historical Market Data Service error message:Too many requests in the last 10 minutes.")]
    [InlineData("some unrecognised future TWS phrasing")]
    [InlineData("")]
    public void A_pacing_or_ambiguous_162_stays_retryable(string message)
    {
        // Error 162 is overloaded, and the head timestamp path must fail safe the same way the bars
        // path does: a needless retry costs one paced request, while wrongly retiring a contract that
        // DOES have history plans every job on it against the wrong floor.
        Assert.Null(IbkrHistoricalClient.AsMissingHeadTimestamp(
            new IbkrRequestException(IbkrErrorCodes.NoHistoricalData, message)));
    }

    [Fact]
    public void A_rejection_that_is_not_162_is_left_alone()
    {
        // Only 162 is overloaded. Everything else keeps whatever the code-based classification says —
        // error 200 is already permanent, and a connection-level failure must stay transient.
        Assert.Null(IbkrHistoricalClient.AsMissingHeadTimestamp(
            new IbkrRequestException(IbkrErrorCodes.NoSecurityDefinition, "No security definition has been found")));
    }

    [Fact]
    public void Permanence_is_still_decided_by_the_error_code_unless_a_call_site_overrides_it()
    {
        // The override exists for one overloaded code and must not leak into the default. A bare 162
        // — the one the BARS path raises, where a different date range can still have data — stays
        // transient.
        Assert.False(new IbkrRequestException(IbkrErrorCodes.NoHistoricalData, "HMDS query returned no data").IsPermanent);
        Assert.True(new IbkrRequestException(IbkrErrorCodes.NoSecurityDefinition, "No security definition").IsPermanent);
    }
}
