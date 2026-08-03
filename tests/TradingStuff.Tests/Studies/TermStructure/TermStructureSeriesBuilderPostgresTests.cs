using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.TermStructure;

namespace TradingStuff.Tests.Studies.TermStructure;

/// <summary>
/// End-to-end over a real schema: seeded 15:30 ET chain quotes for one 2013 session date come
/// out as a usable term-structure row per the frozen construction, and the unresolved-vs-absent
/// distinction (frozen doc § 8) is enforced from the ingestion checkpoint table's state.
/// </summary>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class TermStructureSeriesBuilderPostgresTests
{
    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    // A regular Tuesday before the 2013-03-10 DST change: 15:30 ET (EST) = 20:30 UTC.
    private static readonly DateOnly TradingDate = new(2013, 3, 5);
    private static readonly DateTimeOffset SnapshotUtc = new(2013, 3, 5, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_seeded_session_date_builds_a_usable_row_and_the_frontier_parks_later_dates_as_unresolved()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var configuration = ConfigurationFor(connectionString);

        await SeedRatesAsync(connectionString);
        await SeedJobAndRequestsAsync(connectionString);
        await SeedChainAsync(connectionString);

        var store = new TermStructureStore(configuration);
        var builder = new TermStructureSeriesBuilder(
            store, new SessionClock(), configuration,
            NullLogger<TermStructureSeriesBuilder>.Instance);

        var report = await builder.BuildAsync(TradingDate, TradingDate.AddDays(1), CancellationToken.None);

        Assert.Equal(2, report.Sessions);
        Assert.Equal(1, report.Usable);
        Assert.Equal(1, report.Unresolved);

        var rows = await store.ListAsync(TradingDate, TradingDate.AddDays(1), CancellationToken.None);
        Assert.Equal(2, rows.Count);

        var built = rows[0];
        Assert.Equal("usable", built.Status);
        Assert.Equal(SnapshotUtc, built.SnapshotUtc);
        Assert.NotNull(built.Slope);
        // The seeded 9d legs carry a higher vol than the 30d legs: the slope must read inverted.
        Assert.True(built.Slope!.Value > 0.0, $"slope {built.Slope} should be positive (inverted)");
        Assert.Equal(7.0, built.Near9dDays!.Value, 1);
        Assert.Equal(10.0, built.Far9dDays!.Value, 1);

        // 2013-03-06 has no quotes seeded, and a pending expiration (2013-04-12) sits ahead of
        // it — the missing legs COULD be ingestion lag, so it parks as unresolved, never absent.
        var parked = rows[1];
        Assert.Equal("unresolved", parked.Status);
        Assert.Contains("Awaiting chain ingestion", parked.Note);
    }

    [Fact]
    public async Task An_empty_rate_table_refuses_to_build_rather_than_falling_back_flat()
    {
        if (ServerConnectionString is not { } server) return;

        var connectionString = await PrepareAsync(server);
        var configuration = ConfigurationFor(connectionString);

        var builder = new TermStructureSeriesBuilder(
            new TermStructureStore(configuration), new SessionClock(),
            configuration, NullLogger<TermStructureSeriesBuilder>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.BuildAsync(TradingDate, TradingDate, CancellationToken.None));

        Assert.Contains("risk_free_rates", ex.Message);
    }

    // ---- seeding --------------------------------------------------------------------------------

    private static async Task SeedRatesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO research.risk_free_rates (rate_date, discount_rate_pct, source) " +
            "VALUES ('2013-03-01', 0.09, 'test-seed DTB4WK')",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The checkpoint table drives the § 8 distinction: expirations through 2013-04-05 are
    /// resolved; 2013-04-12 onward is still pending. 2013-03-05's far legs (2013-03-15 and
    /// 2013-04-05) sit inside the resolved region; 2013-03-06's 30d bracket would need
    /// 2013-04-12, which is not resolved yet.
    /// </summary>
    private static async Task SeedJobAndRequestsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var job = new NpgsqlCommand(
            """
            INSERT INTO research.option_chain_jobs
              (name, underlying, trading_class, target_from, target_to, interval, priority, status)
            VALUES ('test-spxw', 'SPX', 'SPXW', '2013-01-01', '2013-12-31', '1m', 0, 'running')
            RETURNING job_id
            """,
            connection);
        var jobId = (long)(await job.ExecuteScalarAsync())!;

        await using var requests = new NpgsqlCommand(
            """
            INSERT INTO research.option_chain_requests (job_id, expiration, state) VALUES
              ($1, '2013-03-12', 'succeeded'), ($1, '2013-03-15', 'succeeded'),
              ($1, '2013-04-05', 'succeeded'), ($1, '2013-04-12', 'pending')
            """,
            connection);
        requests.Parameters.Add(new() { Value = jobId });
        await requests.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Four SPXW expirations at the 15:30 ET snapshot of 2013-03-05: 7d and 10d legs at 25 vol
    /// bracketing the 9-day point, 31d and 38d legs at 20 vol bracketing the 30-day point.
    /// Strikes 1350..1750 step 25 around a 1540 spot with parity-consistent synthetic prices.
    /// </summary>
    private static async Task SeedChainAsync(string connectionString)
    {
        const double spot = 1540.0;

        var expirations = new (DateOnly Expiration, double Vol)[]
        {
            (new DateOnly(2013, 3, 12), 0.25), (new DateOnly(2013, 3, 15), 0.25),
            (new DateOnly(2013, 4, 5), 0.20), (new DateOnly(2013, 4, 12), 0.20),
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var (expiration, vol) in expirations)
        {
            var yearsToExpiry = (expiration.DayNumber - TradingDate.DayNumber) / 365.0;

            for (var strike = 1350.0; strike <= 1750.0; strike += 25.0)
            {
                foreach (var right in new[] { 'C', 'P' })
                {
                    var mid = SyntheticPrice(spot, strike, yearsToExpiry, vol, right == 'C');

                    await using var insert = new NpgsqlCommand(
                        """
                        INSERT INTO research.option_chain_quotes
                          (underlying, trading_class, expiration, strike, option_right, observed_at,
                           trading_date, bid, ask, vendor, vendor_symbol, vendor_endpoint, interval)
                        VALUES ('SPX', 'SPXW', $1, $2, $3, $4, $5, $6, $7, 'test', 'SPXW', 'test', '1m')
                        """,
                        connection);

                    insert.Parameters.Add(new() { Value = expiration });
                    insert.Parameters.Add(new() { Value = (decimal)strike });
                    insert.Parameters.Add(new() { Value = right.ToString() });
                    insert.Parameters.Add(new() { Value = SnapshotUtc });
                    insert.Parameters.Add(new() { Value = TradingDate });
                    insert.Parameters.Add(new() { Value = (decimal)Math.Max(0.0, mid - 0.05) });
                    insert.Parameters.Add(new() { Value = (decimal)(mid + 0.05) });

                    await insert.ExecuteNonQueryAsync();
                }
            }
        }
    }

    private static double SyntheticPrice(
        double spot, double strike, double timeToExpiry, double volatility, bool isCall)
    {
        var sqrtT = Math.Sqrt(Math.Max(timeToExpiry, 1e-9));
        var d1 = (Math.Log(spot / strike) + 0.5 * volatility * volatility * timeToExpiry)
                 / (volatility * sqrtT);
        var d2 = d1 - volatility * sqrtT;

        return isCall
            ? spot * NormalCdf(d1) - strike * NormalCdf(d2)
            : strike * NormalCdf(-d2) - spot * NormalCdf(-d1);
    }

    private static double NormalCdf(double x)
    {
        var t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2.0));
        var erf = 1.0 - t * (0.254829592 + t * (-0.284496736 + t * (1.421413741
                  + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-x * x / 2.0);
        return x >= 0 ? 0.5 * (1.0 + erf) : 0.5 * (1.0 - erf);
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private static async Task<string> PrepareAsync(string server)
    {
        var connectionString = PostgresCollection.FreshDatabase(server);
        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);
        return connectionString;
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trading"] = connectionString,
            })
            .Build();
}
