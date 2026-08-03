using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchService.Capture;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Sessions;

namespace TradingStuff.Tests;

/// <summary>
/// The raw capture tables against a real schema (migration 024): round trip, idempotency, refusal
/// visibility, and the append-only guarantee.
/// </summary>
/// <remarks>
/// The correctness statement here is a negative one — "a re-run adds nothing, and nothing that was
/// captured can later be rewritten" — so each test names what would be observable if it were false.
/// The absent-row check is <c>HasCaptureAsync</c> measured on
/// <c>research.paper_account_snapshots</c>: a refusal row must NOT satisfy it, or an evening the
/// gateway was down would be permanently mistaken for an evening that was captured.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class PaperCapturePostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static readonly DateOnly TradingDate = new(2026, 8, 6);
    private static readonly DateTimeOffset SnapshotAt = new(2026, 8, 6, 20, 15, 0, TimeSpan.Zero);

    // ---- round trip ------------------------------------------------------------------------------

    [Fact]
    public async Task A_capture_round_trips_with_its_margin_fields_and_its_fills()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        var outcome = await store.SaveAsync(Capture(fills: [Fill("exec-1"), Fill("exec-2")]), CancellationToken.None);

        Assert.True(outcome.Stored);
        Assert.Equal(2, outcome.FillsWritten);

        var snapshots = await store.ListSnapshotsAsync(10, CancellationToken.None);
        var snapshot = Assert.Single(snapshots);

        Assert.Equal(TradingDate, snapshot.TradingDate);
        Assert.Equal("DU1234567", snapshot.AccountId);
        Assert.Equal(1_004_321.55m, snapshot.NetLiquidation);
        Assert.Equal(1250.00m, snapshot.MaintenanceMargin);
        Assert.Equal(1400.00m, snapshot.InitMargin);
        Assert.Equal(998_000.10m, snapshot.ExcessLiquidity);
        Assert.Equal(2, snapshot.FillCount);
        Assert.Equal(1, snapshot.PositionCount);
        Assert.Null(snapshot.RefusalKind);

        // The positions document is the provenance the typed columns cannot carry.
        using var positions = JsonDocument.Parse(snapshot.PositionsJson!);
        Assert.Equal(776_512_301, positions.RootElement[0].GetProperty("conId").GetInt32());

        var fills = await store.ListFillsAsync(TradingDate, 100, CancellationToken.None);
        Assert.Equal(2, fills.Count);
        Assert.Equal(725m, fills[0].Strike);
        Assert.Equal("SLD", fills[0].Side);
        Assert.Equal(1.37m, fills[0].Price);
        Assert.Equal("20260806-19:45:12", fills[0].ExecutedAtRaw);
    }

    [Fact]
    public async Task A_fill_with_no_commission_reported_stores_null_rather_than_zero()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        await store.SaveAsync(
            Capture(fills: [Fill("exec-1") with { Commission = null, CommissionCurrency = null }]),
            CancellationToken.None);

        var fill = Assert.Single(await store.ListFillsAsync(TradingDate, 100, CancellationToken.None));

        // A zero would be a fabricated cost basis, and the protocol's item 9 is computed from these.
        Assert.Null(fill.Commission);
        Assert.Null(fill.CommissionCurrency);
    }

    [Fact]
    public async Task An_unparseable_execution_time_stores_the_raw_string_with_no_instant()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        await store.SaveAsync(
            Capture(fills: [Fill("exec-1") with { ExecutedAt = null, ExecutedAtRaw = "20260806 something" }]),
            CancellationToken.None);

        var fill = Assert.Single(await store.ListFillsAsync(TradingDate, 100, CancellationToken.None));

        Assert.Null(fill.ExecutedAt);
        Assert.Equal("20260806 something", fill.ExecutedAtRaw);
    }

    // ---- idempotency -----------------------------------------------------------------------------

    [Fact]
    public async Task The_same_day_captured_twice_leaves_one_snapshot_and_no_duplicate_fills()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        var first = await store.SaveAsync(Capture(fills: [Fill("exec-1")]), CancellationToken.None);
        var second = await store.SaveAsync(Capture(fills: [Fill("exec-1")]), CancellationToken.None);

        Assert.True(first.Stored);
        Assert.Equal(1, first.FillsWritten);

        // Not an error: re-running is the intended recovery path. The schema is what makes it safe,
        // so two processes racing cannot both write even though both saw "nothing captured".
        Assert.False(second.Stored);
        Assert.Equal(0, second.FillsWritten);

        Assert.Single(await store.ListSnapshotsAsync(10, CancellationToken.None));
        Assert.Single(await store.ListFillsAsync(TradingDate, 100, CancellationToken.None));
    }

    [Fact]
    public async Task A_later_pass_adds_only_the_executions_the_first_one_did_not_see()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        await store.SaveAsync(Capture(fills: [Fill("exec-1")]), CancellationToken.None);
        var second = await store.SaveAsync(Capture(fills: [Fill("exec-1"), Fill("exec-2")]), CancellationToken.None);

        Assert.Equal(1, second.FillsWritten);
        Assert.Equal(2, (await store.ListFillsAsync(TradingDate, 100, CancellationToken.None)).Count);
    }

    // ---- refusals --------------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_is_visible_and_does_not_count_as_a_capture()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        Assert.True(await store.RecordRefusalAsync(
            TradingDate, SnapshotAt, GatewayRefusalKinds.GatewayUnreachable,
            "Could not read account summary from the IBKR gateway.",
            CaptureSources.GatewayAccount, CancellationToken.None));

        // THE absent-row check. If a refusal satisfied this, an evening the gateway was down would
        // be permanently indistinguishable from an evening that was captured — the exact
        // absence-reads-as-health failure the table exists to prevent.
        Assert.False(await store.HasCaptureAsync(TradingDate, CancellationToken.None));

        var refusal = Assert.Single(await store.ListSnapshotsAsync(10, CancellationToken.None));
        Assert.Equal(GatewayRefusalKinds.GatewayUnreachable, refusal.RefusalKind);
        Assert.Null(refusal.AccountId);
        Assert.Null(refusal.PositionCount);
    }

    [Fact]
    public async Task The_same_refusal_reason_is_one_fact_however_many_passes_observed_it()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        await store.RecordRefusalAsync(
            TradingDate, SnapshotAt, GatewayRefusalKinds.GatewayUnreachable, "down",
            CaptureSources.GatewayAccount, CancellationToken.None);

        var repeat = await store.RecordRefusalAsync(
            TradingDate, SnapshotAt.AddMinutes(5), GatewayRefusalKinds.GatewayUnreachable, "still down",
            CaptureSources.GatewayAccount, CancellationToken.None);

        // Otherwise a night of five-minute retries writes ~150 rows saying the same thing.
        Assert.False(repeat);
        Assert.Single(await store.ListSnapshotsAsync(10, CancellationToken.None));

        // A DIFFERENT reason is a different fact — the gateway coming back up with TWS still down is
        // a distinct state an operator has to act on differently.
        Assert.True(await store.RecordRefusalAsync(
            TradingDate, SnapshotAt.AddMinutes(10), GatewayRefusalKinds.BrokerNotConnected, "no socket",
            CaptureSources.GatewayAccount, CancellationToken.None));

        Assert.Equal(2, (await store.ListSnapshotsAsync(10, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_capture_can_still_land_on_a_date_that_already_refused()
    {
        if (ServerConnectionString is not { } server) return;

        var store = new PaperCaptureStore(ConfigurationFor(await PrepareAsync(server)));

        await store.RecordRefusalAsync(
            TradingDate, SnapshotAt, GatewayRefusalKinds.GatewayUnreachable, "down",
            CaptureSources.GatewayAccount, CancellationToken.None);

        var outcome = await store.SaveAsync(Capture(fills: [Fill("exec-1")]), CancellationToken.None);

        // The recovery path: the refusal stays on the record as the history of the evening, and the
        // capture is not blocked by it.
        Assert.True(outcome.Stored);
        Assert.True(await store.HasCaptureAsync(TradingDate, CancellationToken.None));
        Assert.Equal(2, (await store.ListSnapshotsAsync(10, CancellationToken.None)).Count);
    }

    // ---- the schema's own guarantees --------------------------------------------------------------

    [Fact]
    public async Task Both_capture_tables_reject_an_update_and_a_delete()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = new PaperCaptureStore(ConfigurationFor(connectionString));

        await store.SaveAsync(Capture(fills: [Fill("exec-1")]), CancellationToken.None);

        foreach (var statement in new[]
                 {
                     "UPDATE research.paper_fills SET price = 99",
                     "DELETE FROM research.paper_fills",
                     "UPDATE research.paper_account_snapshots SET net_liquidation = 0",
                     "DELETE FROM research.paper_account_snapshots",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, statement));

            // A capture that can be edited after the outcome is known is not a measurement.
            Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
            Assert.Contains("append-only", ex.MessageText);
        }
    }

    [Fact]
    public async Task A_row_cannot_be_half_a_capture_and_half_a_refusal()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);

        // Without the CHECK a partially-written pass leaves a row that reads as a successful
        // snapshot of an account holding nothing, and the pass is never retried.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            "INSERT INTO research.paper_account_snapshots " +
            "(trading_date, snapshot_at, account_id, capture_source) " +
            "VALUES (DATE '2026-08-06', now(), 'DU1234567', 'test')"));

        Assert.Contains("paper_account_snapshots_capture_or_refusal", ex.Message);
    }

    // ---- the whole pass ---------------------------------------------------------------------------

    [Fact]
    public async Task A_pass_after_the_close_captures_the_session_and_a_second_pass_adds_nothing()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = new PaperCaptureStore(ConfigurationFor(connectionString));
        var service = ServiceOver(store, new StubGatewayHandler());

        // 2026-08-06 is a Thursday; NYSE closes 20:00 UTC. 20:20 is past the 15-minute settle delay.
        var now = new DateTimeOffset(2026, 8, 6, 20, 20, 0, TimeSpan.Zero);

        await service.RunPassAsync(now, CancellationToken.None);
        await service.RunPassAsync(now.AddMinutes(5), CancellationToken.None);

        var snapshot = Assert.Single(await store.ListSnapshotsAsync(10, CancellationToken.None));
        Assert.Equal(TradingDate, snapshot.TradingDate);
        Assert.Equal(1250.00m, snapshot.MaintenanceMargin);

        var fill = Assert.Single(await store.ListFillsAsync(TradingDate, 100, CancellationToken.None));
        Assert.Equal("stub-exec-1", fill.ExecId);
        Assert.Equal(TradingDate, fill.TradingDate);
    }

    [Fact]
    public async Task A_pass_that_cannot_reach_the_gateway_records_a_refusal_and_retries_later()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var store = new PaperCaptureStore(ConfigurationFor(connectionString));

        var handler = new StubGatewayHandler { Status = HttpStatusCode.ServiceUnavailable };
        var service = ServiceOver(store, handler);

        var now = new DateTimeOffset(2026, 8, 6, 20, 20, 0, TimeSpan.Zero);

        await service.RunPassAsync(now, CancellationToken.None);

        var refusal = Assert.Single(await store.ListSnapshotsAsync(10, CancellationToken.None));
        Assert.Equal(GatewayRefusalKinds.BrokerNotConnected, refusal.RefusalKind);
        Assert.Empty(await store.ListFillsAsync(TradingDate, 100, CancellationToken.None));

        // The gateway comes back: the refusal is not a terminal state, and nothing had to be
        // cleared for the capture to land.
        handler.Status = HttpStatusCode.OK;
        await service.RunPassAsync(now.AddMinutes(5), CancellationToken.None);

        Assert.True(await store.HasCaptureAsync(TradingDate, CancellationToken.None));
        Assert.Single(await store.ListFillsAsync(TradingDate, 100, CancellationToken.None));
    }

    // ---- fixtures ---------------------------------------------------------------------------------

    private static PaperCaptureService ServiceOver(PaperCaptureStore store, StubGatewayHandler handler) =>
        new(new IbkrGatewayClient(
                new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100") },
                NullLogger<IbkrGatewayClient>.Instance),
            new SessionClock(),
            store,
            Options.Create(new PaperCaptureOptions { LookbackSessions = 1 }),
            TimeProvider.System,
            NullLogger<PaperCaptureService>.Instance);

    /// <summary>
    /// Answers the three <c>/ibkr/account/*</c> reads with fixed bodies, or a chosen failure status.
    /// </summary>
    private sealed class StubGatewayHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Content = new StringContent("{\"detail\":\"Not connected to TWS.\"}", Encoding.UTF8, "application/json"),
                });
            }

            var path = request.RequestUri!.AbsolutePath;

            var body = path switch
            {
                "/ibkr/account/summary" =>
                    """
                    {"account":"DU1234567","capturedAt":"2026-08-06T20:20:00+00:00","tags":[
                      {"tag":"NetLiquidation","value":"1004321.55","currency":"USD"},
                      {"tag":"MaintMarginReq","value":"1250.00","currency":"USD"},
                      {"tag":"InitMarginReq","value":"1400.00","currency":"USD"},
                      {"tag":"ExcessLiquidity","value":"998000.10","currency":"USD"}]}
                    """,

                "/ibkr/account/positions" =>
                    """
                    {"account":"DU1234567","capturedAt":"2026-08-06T20:20:00+00:00","positions":[
                      {"conId":776512301,"symbol":"SPY","secType":"OPT","expiration":"2026-09-04",
                       "strike":725.0,"right":"P","tradingClass":"SPY","currency":"USD","multiplier":"100",
                       "localSymbol":"SPY 260904P00725000","quantity":-1,"averageCost":137.0}]}
                    """,

                "/ibkr/account/executions" =>
                    """
                    {"account":"DU1234567","capturedAt":"2026-08-06T20:20:00+00:00",
                     "sinceUtc":"2026-08-06T13:30:00+00:00","commissionsMissing":0,"executions":[
                      {"execId":"stub-exec-1","permId":1234567890,"orderId":42,"clientId":7,
                       "account":"DU1234567","conId":776512301,"symbol":"SPY","secType":"OPT",
                       "expiration":"2026-09-04","strike":725.0,"right":"P","tradingClass":"SPY",
                       "multiplier":100,"exchange":"CBOE","side":"SLD","quantity":1,"price":1.37,
                       "executedAtRaw":"20260806-19:45:12","executedAt":"2026-08-06T19:45:12+00:00",
                       "commission":0.799346,"commissionCurrency":"USD","realizedPnL":null}]}
                    """,

                _ => "{}",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static PaperAccountCapture Capture(IReadOnlyList<PaperFill> fills) =>
        new(TradingDate,
            SnapshotAt,
            "DU1234567",
            NetLiquidation: 1_004_321.55m,
            MaintenanceMargin: 1250.00m,
            InitMargin: 1400.00m,
            ExcessLiquidity: 998_000.10m,
            AvailableFunds: 997_000.00m,
            BuyingPower: 3_988_000.40m,
            GrossPositionValue: 137.00m,
            Currency: "USD",
            SummaryJson: """[{"tag":"NetLiquidation","value":"1004321.55","currency":"USD"}]""",
            PositionsJson: """[{"conId":776512301,"symbol":"SPY","secType":"OPT","quantity":-1}]""",
            PositionCount: 1,
            Fills: fills,
            CaptureSource: CaptureSources.GatewayAccount);

    private static PaperFill Fill(string execId) =>
        new(TradingDate,
            "DU1234567",
            execId,
            PermId: 1_234_567_890L,
            IbkrOrderId: 42,
            ClientId: 7,
            ConId: 776_512_301,
            Symbol: "SPY",
            SecType: "OPT",
            Expiration: new DateOnly(2026, 9, 4),
            Strike: 725m,
            OptionRight: "P",
            TradingClass: "SPY",
            Multiplier: 100,
            Side: "SLD",
            Quantity: 1m,
            Price: 1.37m,
            ExecutedAtRaw: "20260806-19:45:12",
            ExecutedAt: new DateTimeOffset(2026, 8, 6, 19, 45, 12, TimeSpan.Zero),
            Exchange: "CBOE",
            Commission: 0.799346m,
            CommissionCurrency: "USD",
            RealizedPnL: null,
            CaptureSource: CaptureSources.GatewayExecutions);

    // ---- plumbing ---------------------------------------------------------------------------------

    private static async Task<string> PrepareAsync(string server)
    {
        var connectionString = PostgresCollection.FreshDatabase(server);
        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);
        return connectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();
}
