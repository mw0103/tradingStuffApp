using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.Contracts;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Gateway;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Universe;

namespace TradingStuff.Tests;

/// <summary>
/// The whole bootstrap pass over the real seeded 54-node grid, with the gateway stubbed and Postgres
/// real. Excluded unless <c>TRADING_TEST_POSTGRES</c> holds a connection string.
/// </summary>
/// <remarks>
/// <see cref="NodeGridSelectionTests"/> pins the strike arithmetic; this pins what the grid ends up
/// as. Both matter, and only this one can answer the question that was never asked of the shipped
/// selector: after a pass, how many distinct contracts is the 54-node grid actually recording?
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class NodeGridSelectionPostgresTests
{
    private const decimal Spot = 7437.63m;

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<string> PrepareAsync(string server)
    {
        var database = $"trading_test_{Guid.NewGuid():N}";
        var connectionString = PostgresCollection.ConnectionString(server, database);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        await new MigrationRunner(configuration, NullLogger<MigrationRunner>.Instance)
            .ApplyOnceAsync(connectionString, CancellationToken.None);

        return connectionString;
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    /// <summary>
    /// A gateway that answers chain and resolve requests from a synthetic-but-measured SPX ladder.
    /// </summary>
    /// <param name="pinExpiration">
    /// When set, every bucket is answered with this one expiration — which is what TWS really does
    /// when two DTE buckets have no distinct listed expiration between them (on 2026-08-01 the SPX
    /// monthly series answered both the 60DTE and the 90DTE bucket with 2026-10-15).
    /// </param>
    /// <param name="spotCentred">False reproduces the gateway's degraded path: no reference price.</param>
    private sealed class StubGateway(DateOnly? pinExpiration = null, bool spotCentred = true) : HttpMessageHandler
    {
        public int ChainRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;

            if (uri.AbsolutePath.StartsWith("/ibkr/options/chains", StringComparison.Ordinal))
            {
                ChainRequests++;
                return Ok(Chain(uri));
            }

            if (uri.AbsolutePath == "/ibkr/contracts/resolve")
            {
                var body = await request.Content!.ReadFromJsonAsync<ResolveRequest>(Json, cancellationToken);

                return Ok(new ResolveResponse(
                    [.. (body?.Contracts ?? []).Select(c => new ResolvedContract(c, ConIdFor(c), null))]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private ChainWindow Chain(Uri uri)
        {
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

            var requested = DateOnly.Parse(query["expiration"], CultureInfo.InvariantCulture);
            var tradingClass = query["tradingClass"];
            var halfWidth = decimal.Parse(query["moneynessHalfWidth"], CultureInfo.InvariantCulture);

            if (!spotCentred)
            {
                // Exactly the shape the gateway now returns when its spot lookup times out: no
                // contracts, no reference price, and a reason.
                return new ChainWindow([], false, null, null, null, null, "No spot price is available for SPX.");
            }

            var expiration = pinExpiration ?? requested;

            var contracts = Ladder(expiration, tradingClass)
                .Where(c => c.Strike >= Spot * (1m - halfWidth) && c.Strike <= Spot * (1m + halfWidth))
                .ToArray();

            return new ChainWindow(
                contracts, true, Spot, expiration,
                contracts.Min(c => c.Strike), contracts.Max(c => c.Strike), null);
        }

        private static HttpResponseMessage Ok<T>(T body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            };

        private sealed record ResolveRequest(IReadOnlyList<OptionContract> Contracts);

        private sealed record ResolvedContract(OptionContract Contract, int? ConId, string? Error);

        private sealed record ResolveResponse(IReadOnlyList<ResolvedContract> Resolved);
    }

    /// <summary>SPX-shaped ladder: 5-point increments near the money, 25 outside. See NodeGridSelectionTests.</summary>
    private static IReadOnlyList<OptionContract> Ladder(DateOnly expiration, string tradingClass)
    {
        var contracts = new List<OptionContract>();

        for (var strike = 5000m; strike <= 9000m; strike += strike is >= 6000m and < 8000m ? 5m : 25m)
        {
            foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
            {
                contracts.Add(new OptionContract(
                    $"{tradingClass}{expiration:yyyyMMdd}{(right == OptionRight.Call ? 'C' : 'P')}{strike:0.##}",
                    "SPX", expiration, strike, right, TradingClass: tradingClass));
            }
        }

        return contracts;
    }

    /// <summary>Stable, collision-free conId for a listed contract — the broker's identity, modelled.</summary>
    private static int ConIdFor(OptionContract contract) =>
        Math.Abs(HashCode.Combine(
            contract.Expiration, contract.Strike, contract.Right, contract.TradingClass)) | 1;

    private static NodeSelector SelectorFor(string connectionString, StubGateway stub)
    {
        var configuration = ConfigurationFor(connectionString);

        HttpClient Client() => new(stub, disposeHandler: false) { BaseAddress = new Uri("http://gateway.test") };

        return new NodeSelector(
            configuration,
            new IbkrGatewayClient(Client(), NullLogger<IbkrGatewayClient>.Instance),
            new OptionChainClient(Client(), NullLogger<OptionChainClient>.Instance),
            NullLogger<NodeSelector>.Instance);
    }

    [Fact]
    public async Task The_registered_grid_binds_to_one_distinct_contract_per_role()
    {
        // The headline. Under the shipped selector this pass produced roughly 24 distinct conIds for
        // 54 roles and reported nothing wrong; the ~30 duplicated subscriptions were market-data
        // lines spent recording contracts already being recorded under another role.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var stub = new StubGateway();
        var selector = SelectorFor(connectionString, stub);

        var assigned = await selector.BootstrapAssignmentsAsync(CancellationToken.None);
        var report = await selector.GetGridReportAsync(CancellationToken.None);

        Assert.Equal(54, report.Registered);
        Assert.Equal(54, assigned);
        Assert.Equal(54, report.Assigned);
        Assert.Equal(0, report.Unassigned);
        Assert.Equal(54, report.DistinctConIds);
        Assert.All(report.Nodes, node => Assert.False(node.DuplicateConId));

        // Six buckets, one chain request each — not one per node.
        Assert.Equal(6, stub.ChainRequests);

        // And every node is bound to a contract selected for ITS target, which is the check that
        // did not exist anywhere in the system before.
        Assert.All(report.Nodes, node =>
        {
            Assert.NotNull(node.StrikeDeviation);
            Assert.InRange(Math.Abs(node.StrikeDeviation!.Value), 0m, 0.015m);
            Assert.Equal(Spot, node.ReferencePrice);
            Assert.Equal(Spot * (1 + node.MoneynessTarget), node.TargetStrike);
        });
    }

    [Fact]
    public async Task A_pass_is_idempotent_and_records_selection_provenance()
    {
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var selector = SelectorFor(connectionString, new StubGateway());

        await selector.BootstrapAssignmentsAsync(CancellationToken.None);
        var second = await selector.BootstrapAssignmentsAsync(CancellationToken.None);

        Assert.Equal(0, second);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM research.node_assignments " +
            "WHERE assigned_to IS NULL AND selector_version = 2 " +
            "  AND strike IS NOT NULL AND target_strike IS NOT NULL AND reference_price IS NOT NULL " +
            "  AND expiration IS NOT NULL",
            connection);

        Assert.Equal(54L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_chain_that_is_not_spot_centred_assigns_nothing_and_says_why()
    {
        // Defect 2. The gateway used to answer a failed spot lookup with the ENTIRE listed strike
        // set and no marker; NodeSelector took the median of that as a spot proxy and rebound all 54
        // nodes to deep-OTM contracts that ticked normally and reported full coverage. There is no
        // spot proxy to invent now, and no assignment without one.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var selector = SelectorFor(connectionString, new StubGateway(spotCentred: false));

        var assigned = await selector.BootstrapAssignmentsAsync(CancellationToken.None);
        var report = await selector.GetGridReportAsync(CancellationToken.None);

        Assert.Equal(0, assigned);
        Assert.Equal(54, report.Unassigned);
        Assert.Equal(0, report.DistinctConIds);
        Assert.All(report.Nodes, node =>
        {
            Assert.Equal(NodeUnassignedReasons.ChainUnavailable, node.Unassigned);
            Assert.Contains("No spot price", node.UnassignedDetail);
        });
    }

    [Fact]
    public async Task A_previous_assignment_survives_a_pass_that_cannot_read_spot()
    {
        // The other half of refusing: a bucket that cannot be selected this pass must not tear down
        // the tenure it already has. Recording continues on yesterday's contracts rather than
        // stopping, and the refusal is reported alongside the still-current assignment.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await SelectorFor(connectionString, new StubGateway()).BootstrapAssignmentsAsync(CancellationToken.None);

        var degraded = SelectorFor(connectionString, new StubGateway(spotCentred: false));
        var assigned = await degraded.BootstrapAssignmentsAsync(CancellationToken.None);
        var report = await degraded.GetGridReportAsync(CancellationToken.None);

        Assert.Equal(0, assigned);
        Assert.Equal(54, report.Assigned);
        Assert.Equal(54, report.DistinctConIds);
    }

    [Fact]
    public async Task Two_buckets_on_one_expiration_cannot_bind_one_contract_to_two_roles()
    {
        // Real, not hypothetical: on 2026-08-01 the SPX monthly series had no listed expiration
        // inside the 76-105 DTE bucket (2026-10-15 was 75 DTE, 2026-11-19 was 110), so both the
        // 60DTE and the 90DTE bucket resolved to 2026-10-15 and would have selected the same nine
        // contracts for eighteen roles.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);
        var pinned = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var selector = SelectorFor(connectionString, new StubGateway(pinExpiration: pinned));

        await selector.BootstrapAssignmentsAsync(CancellationToken.None);
        var report = await selector.GetGridReportAsync(CancellationToken.None);

        // The invariant: one conId, one role. Assigned count and distinct conId count are equal, so
        // no contract is doing double duty.
        Assert.Equal(report.Assigned, report.DistinctConIds);

        // Four SPXW buckets share nine contracts and two SPX buckets share nine more, so eighteen
        // roles bind and the other thirty-six are refused by name. The shipped selector wrote all 54.
        Assert.Equal(18, report.Assigned);
        Assert.Equal(36, report.Unassigned);
        Assert.All(report.Nodes, node => Assert.False(node.DuplicateConId));

        Assert.All(
            report.Nodes.Where(n => n.ConId is null),
            node => Assert.Equal(NodeUnassignedReasons.DuplicateConId, node.Unassigned));
    }

    [Fact]
    public async Task The_grid_report_flags_a_conId_already_shared_by_two_roles()
    {
        // Assignments written by selector_version 1 are still in the table and this fix does not
        // rewrite history, so the report has to be able to show an inherited collapse rather than
        // only prevent a new one.
        if (ServerConnectionString is not { } server)
        {
            return;
        }

        var connectionString = await PrepareAsync(server);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            await using var insert = new NpgsqlCommand(
                "INSERT INTO research.node_assignments (node_id, con_id, assigned_from, reason, selector_version) " +
                "VALUES (3, 900001, now(), 'bootstrap', 1), (5, 900001, now(), 'bootstrap', 1)",
                connection);
            await insert.ExecuteNonQueryAsync();
        }

        var report = await SelectorFor(connectionString, new StubGateway())
            .GetGridReportAsync(CancellationToken.None);

        Assert.Equal(2, report.Assigned);
        Assert.Equal(1, report.DistinctConIds);
        Assert.Equal(2, report.Nodes.Count(n => n.DuplicateConId));

        // Rows from the old selector carry no provenance, and that absence is reported as absence
        // rather than backfilled with a plausible number.
        Assert.All(report.Nodes.Where(n => n.DuplicateConId), node => Assert.Null(node.TargetStrike));
    }
}
