using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Capture;
using TradingStuff.ResearchService.Gateway;

namespace TradingStuff.Tests;

/// <summary>
/// What a capture pass makes of the three raw gateway reads, and how it classifies a read it could
/// not make.
/// </summary>
/// <remarks>
/// The projection is where a raw capture can quietly stop being raw: a defaulted zero for a tag TWS
/// never sent, an execution attributed to the wrong session, a refusal that reads as an empty
/// account. Each of those is a fact nobody could recover later, so each has a test.
/// </remarks>
public sealed class PaperCaptureProjectionTests
{
    private static readonly TradingSession Session = new(
        1, "NYSE", new DateOnly(2026, 8, 6),
        new DateTimeOffset(2026, 8, 6, 13, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 6, 20, 0, 0, TimeSpan.Zero),
        "RTH", IsHalfDay: false);

    private static readonly DateTimeOffset SnapshotAt = new(2026, 8, 6, 20, 15, 0, TimeSpan.Zero);

    private static AccountSummaryRead Summary(params (string Tag, string Value, string Currency)[] tags) =>
        new("DU1234567", SnapshotAt, [.. tags.Select(t => new AccountSummaryTagRead(t.Tag, t.Value, t.Currency))]);

    private static AccountPositionsRead NoPositions() => new("DU1234567", SnapshotAt, []);

    private static AccountExecutionsRead NoExecutions() =>
        new("DU1234567", SnapshotAt, Session.OpenUtc, [], 0);

    private static AccountExecutionRead Execution(string execId, DateTimeOffset? at) =>
        new(execId, 1L, 1, 0, "DU1234567", 776_512_301, "SPY", "OPT",
            new DateOnly(2026, 9, 4), 725m, "P", "SPY", 100, "CBOE",
            "SLD", 1m, 1.37m, at?.ToString("yyyyMMdd-HH:mm:ss") ?? "garbage", at, null, null, null);

    // ---- summary projection ----------------------------------------------------------------------

    [Fact]
    public void The_margin_and_equity_tags_land_in_their_typed_columns()
    {
        var capture = PaperCaptureService.BuildCapture(
            Session, SnapshotAt,
            Summary(
                ("NetLiquidation", "1004321.55", "USD"),
                ("MaintMarginReq", "1250.00", "USD"),
                ("InitMarginReq", "1400.00", "USD"),
                ("ExcessLiquidity", "998000.10", "USD"),
                ("AvailableFunds", "997000.00", "USD"),
                ("BuyingPower", "3988000.40", "USD"),
                ("GrossPositionValue", "0.00", "USD")),
            NoPositions(), NoExecutions());

        Assert.Equal(1_004_321.55m, capture.NetLiquidation);
        Assert.Equal(1250.00m, capture.MaintenanceMargin);
        Assert.Equal(1400.00m, capture.InitMargin);
        Assert.Equal(998_000.10m, capture.ExcessLiquidity);
        Assert.Equal(3_988_000.40m, capture.BuyingPower);
        Assert.Equal("USD", capture.Currency);
    }

    [Fact]
    public void A_tag_tws_did_not_send_is_null_never_zero()
    {
        var capture = PaperCaptureService.BuildCapture(
            Session, SnapshotAt, Summary(("NetLiquidation", "1000.00", "USD")),
            NoPositions(), NoExecutions());

        // A zero maintenance margin reads as an unmargined position, which is a claim about the
        // account rather than an absence of information about it.
        Assert.Null(capture.MaintenanceMargin);
        Assert.Null(capture.InitMargin);
        Assert.Null(capture.ExcessLiquidity);
    }

    [Fact]
    public void An_unparseable_tag_value_is_absent_rather_than_guessed()
    {
        var capture = PaperCaptureService.BuildCapture(
            Session, SnapshotAt, Summary(("MaintMarginReq", "n/a", "USD")),
            NoPositions(), NoExecutions());

        Assert.Null(capture.MaintenanceMargin);
    }

    [Fact]
    public void Usd_wins_when_the_account_reports_a_tag_in_several_currencies()
    {
        var capture = PaperCaptureService.BuildCapture(
            Session, SnapshotAt,
            Summary(("NetLiquidation", "900000.00", "EUR"), ("NetLiquidation", "1000000.00", "USD")),
            NoPositions(), NoExecutions());

        Assert.Equal(1_000_000.00m, capture.NetLiquidation);
        Assert.Equal("USD", capture.Currency);
    }

    [Fact]
    public void Every_tag_survives_verbatim_in_the_summary_document()
    {
        var capture = PaperCaptureService.BuildCapture(
            Session, SnapshotAt,
            Summary(("NetLiquidation", "1000.00", "USD"), ("SomeTagNobodyHasAColumnFor", "17", "USD")),
            NoPositions(), NoExecutions());

        // The typed columns are a convenience projection; the jsonb is the provenance, and a tag
        // nobody anticipated has to be on the record the day it turns out to matter.
        using var document = JsonDocument.Parse(capture.SummaryJson);
        Assert.Contains(
            document.RootElement.EnumerateArray(),
            element => element.GetProperty("tag").GetString() == "SomeTagNobodyHasAColumnFor");
    }

    // ---- execution attribution -------------------------------------------------------------------

    [Fact]
    public void Executions_outside_the_session_window_belong_to_a_different_trading_date()
    {
        // TWS's filter takes a lower bound only, so recovering an older session also returns every
        // later fill. Attributing those to the session being recovered would date them wrong, in a
        // table whose entire purpose is reconstructing what happened when.
        var executions = new AccountExecutionsRead(
            "DU1234567", SnapshotAt, Session.OpenUtc,
            [
                Execution("in-session", Session.OpenUtc.AddHours(2)),
                Execution("next-day", Session.CloseUtc.AddDays(1)),
                Execution("before-open", Session.OpenUtc.AddHours(-2)),
            ],
            0);

        var kept = PaperCaptureService.SessionExecutions(Session, executions).Select(e => e.ExecId).ToArray();

        Assert.Equal(["in-session"], kept);
    }

    [Fact]
    public void An_execution_with_no_parseable_time_is_kept_rather_than_dropped()
    {
        var executions = new AccountExecutionsRead(
            "DU1234567", SnapshotAt, Session.OpenUtc, [Execution("unparsed", null)], 0);

        // It has no timestamp to bound, so it cannot be excluded without losing it — and a fill that
        // is not captured is gone. The verbatim TWS string travels with the row for re-attribution.
        var kept = PaperCaptureService.SessionExecutions(Session, executions).ToArray();

        Assert.Single(kept);
        Assert.Equal("garbage", kept[0].ExecutedAtRaw);
    }

    [Fact]
    public void The_fills_on_a_capture_carry_the_sessions_trading_date_not_the_utc_date()
    {
        // The close is 20:00 UTC and the pass runs at 20:15 UTC on the same UTC day here, but the
        // claim under test is that the date comes from the SESSION rather than from either clock.
        var executions = new AccountExecutionsRead(
            "DU1234567", SnapshotAt, Session.OpenUtc, [Execution("one", Session.CloseUtc.AddMinutes(-5))], 0);

        var capture = PaperCaptureService.BuildCapture(
            Session, SnapshotAt, Summary(("NetLiquidation", "1000", "USD")), NoPositions(), executions);

        Assert.Equal(Session.TradingDate, capture.Fills[0].TradingDate);
        Assert.Equal(Session.TradingDate, capture.TradingDate);
    }

    // ---- refusal classification ------------------------------------------------------------------

    [Fact]
    public async Task A_gateway_that_is_not_listening_is_a_named_unreachable_refusal()
    {
        // A real refused connection rather than a hand-built exception, for the reason
        // BackfillGatewayClientTests gives: the classification rests on the runtime populating
        // HttpRequestException.HttpRequestError, and a fixture that sets it asserts its own premise.
        using var closed = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)closed.LocalEndPoint!).Port;
        closed.Close();

        var client = new IbkrGatewayClient(
            new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) },
            NullLogger<IbkrGatewayClient>.Instance);

        var ex = await Assert.ThrowsAsync<GatewayReadException>(
            () => client.GetAccountSummaryAsync(null, CancellationToken.None));

        Assert.Equal(GatewayRefusalKinds.GatewayUnreachable, ex.RefusalKind);
    }

    [Fact]
    public async Task A_gateway_reporting_a_dead_tws_socket_is_a_named_broker_refusal()
    {
        var client = new IbkrGatewayClient(
            new HttpClient(new StatusHandler(HttpStatusCode.ServiceUnavailable))
            {
                BaseAddress = new Uri("http://localhost:5100"),
            },
            NullLogger<IbkrGatewayClient>.Instance);

        var ex = await Assert.ThrowsAsync<GatewayReadException>(
            () => client.GetExecutionsAsync(null, DateTimeOffset.UtcNow, CancellationToken.None));

        // Distinct from unreachable on purpose: the gateway is up and the broker is not, which is a
        // different thing for an operator to go and fix.
        Assert.Equal(GatewayRefusalKinds.BrokerNotConnected, ex.RefusalKind);
    }

    [Fact]
    public async Task An_empty_body_is_a_refusal_rather_than_an_empty_account()
    {
        var client = new IbkrGatewayClient(
            new HttpClient(new StatusHandler(HttpStatusCode.OK, "null"))
            {
                BaseAddress = new Uri("http://localhost:5100"),
            },
            NullLogger<IbkrGatewayClient>.Instance);

        // The failure this rules out: a 200 with nothing in it becoming a snapshot of an account
        // holding nothing, which would satisfy the idempotency check and never be retried.
        var ex = await Assert.ThrowsAsync<GatewayReadException>(
            () => client.GetPositionsAsync(null, CancellationToken.None));

        Assert.Equal(GatewayRefusalKinds.GatewayRefused, ex.RefusalKind);
    }

    private sealed class StatusHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
