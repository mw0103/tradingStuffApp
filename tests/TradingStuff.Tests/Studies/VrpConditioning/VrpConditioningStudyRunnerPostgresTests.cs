using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.VolResidual;
using TradingStuff.ResearchService.Studies.VrpConditioning;

namespace TradingStuff.Tests.Studies.VrpConditioning;

/// <summary>
/// End-to-end tests of <see cref="VrpConditioningStudyRunner"/> against a real, migrated Postgres.
/// </summary>
/// <remarks>
/// The holdout property is stronger here than in the parent study and needs its own assertion. The
/// parent only has to keep holdout-dated rows out of the output; this study also has a forward LABEL
/// window, so a decision date safely before the holdout could still reach into it. The clamp handles
/// that by construction — bars are never fetched past the clamped bound, so a decision date near the
/// end has no 21 following sessions and is dropped — but "by construction" is a claim, and the test
/// below measures it on <c>dataWindow.lastLabelTo</c>.
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class VrpConditioningStudyRunnerPostgresTests
{
    private const short SpxInstrumentId = 1;
    private const short VixInstrumentId = 4;
    private const int SpxConId = 416904;
    private const int VixConId = 990000;

    private static readonly SessionClock Clock = new();

    private static string? ServerConnectionString => Environment.GetEnvironmentVariable("TRADING_TEST_POSTGRES");

    private static async Task<string> PrepareAsync(string server)
    {
        var connectionString = PostgresCollection.FreshDatabase(server);
        var runner = new MigrationRunner(ConfigurationFor(connectionString), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyOnceAsync(connectionString, CancellationToken.None);
        return connectionString;
    }

    private static IConfiguration ConfigurationFor(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

    private static VrpConditioningStudyRunner RunnerFor(string connectionString) =>
        new(new VolResidualBarLoader(ConfigurationFor(connectionString)), Clock,
            NullLogger<VrpConditioningStudyRunner>.Instance);

    private static List<DateOnly> TradingDates(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (Clock.IsTradingDay("CBOE_SPX_RTH", d)) dates.Add(d);
        }

        return dates;
    }

    private static async Task InsertSpxDayAsync(string connectionString, DateOnly date, int dayIndex)
    {
        var session = Clock.SessionsBetween("CBOE_SPX_RTH", date, date).Single(s => s.Label == "RTH");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.bars (con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, open, high, low, close, source) " +
            "SELECT $1, $2, '1 min', 'TRADES', true, gs, " +
            "  100 + $3 + (extract(minute from gs)::int % 5), " +
            "  100 + $3 + (extract(minute from gs)::int % 5), " +
            "  100 + $3 + (extract(minute from gs)::int % 5), " +
            "  100 + $3 + (extract(minute from gs)::int % 5), 'backfill' " +
            "FROM generate_series($4::timestamptz, $5::timestamptz - interval '1 minute', interval '1 minute') AS gs",
            connection)
        {
            Parameters =
            {
                new() { Value = SpxConId },
                new() { Value = SpxInstrumentId },
                new() { Value = (double)dayIndex },
                new() { Value = session.OpenUtc },
                new() { Value = session.CloseUtc },
            },
        };

        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertVixDailyAsync(string connectionString, DateOnly date, double close)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO research.bars (con_id, instrument_id, bar_size, what_to_show, use_rth, ts_utc, trading_date, open, high, low, close, source) " +
            "VALUES ($1, $2, '1 day', 'TRADES', true, $3, $4, $5, $5, $5, $5, 'backfill')",
            connection)
        {
            Parameters =
            {
                new() { Value = VixConId },
                new() { Value = VixInstrumentId },
                new() { Value = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) },
                new() { Value = date },
                new() { Value = (decimal)close },
            },
        };

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAsync(string connectionString, IEnumerable<DateOnly> dates, int offset = 0)
    {
        var index = offset;
        foreach (var date in dates)
        {
            await InsertSpxDayAsync(connectionString, date, index % 7);
            await InsertVixDailyAsync(connectionString, date, 14.0 + index % 9);
            index++;
        }
    }

    private static async Task<long> RegisteredTrialCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM research.registered_trials", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // ---------- holdout exclusion: the headline safety property, plus the forward-label version ----------

    [Fact]
    public async Task NoDecisionDateAndNoLABELDAYEverReachesTheReservedHoldout()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);

        // Pre-holdout history right up against the boundary, so the LAST usable decision date's
        // 21-session label would land squarely inside the holdout if the clamp did not stop it.
        var preHoldout = TradingDates(new DateOnly(2023, 9, 1), new DateOnly(2023, 12, 29));
        var insideHoldout = TradingDates(new DateOnly(2024, 1, 2), new DateOnly(2024, 3, 29));

        await SeedAsync(connectionString, preHoldout);
        await SeedAsync(connectionString, insideHoldout, offset: 1000);

        // Prove the holdout data is real and an UNCLAMPED query would return it. The property is
        // that the runner never asks for it, not that there is nothing there to leak.
        var loader = new VolResidualBarLoader(ConfigurationFor(connectionString));
        var unclamped = await loader.LoadSpxOneMinuteBarsAsync(
            new DateOnly(2023, 9, 1), new DateOnly(2024, 3, 29), CancellationToken.None);
        Assert.Contains(unclamped, b => DateOnly.FromDateTime(b.Timestamp!.Value.UtcDateTime) >= ReservedHoldout.Start);

        var response = await RunnerFor(connectionString).RunAsync(
            new DateOnly(2023, 9, 1), new DateOnly(2024, 3, 29), CancellationToken.None);

        Assert.True(response.DataWindow.To < ReservedHoldout.Start,
            $"dataWindow.to ({response.DataWindow.To}) must be clamped below the holdout start ({ReservedHoldout.Start}).");

        // Decision dates were built here (there is plenty of history), so this is a real assertion
        // and not one that passes because nothing was produced.
        Assert.True(response.DataWindow.DecisionDates > 0, "no decision date was built, so this test proves nothing.");
        Assert.NotNull(response.DataWindow.LastLabelTo);

        Assert.True(response.DataWindow.LastLabelTo!.Value < ReservedHoldout.Start,
            $"the last label window closes on {response.DataWindow.LastLabelTo}, inside the reserved " +
            $"holdout ({ReservedHoldout.Start}..{ReservedHoldout.End}). A forward label reached data " +
            "that must never be touched.");

        Assert.All(response.Daily, day =>
        {
            Assert.True(day.Date < ReservedHoldout.Start);
            Assert.True(day.LabelTo < ReservedHoldout.Start);
        });

        Assert.Equal(ReservedHoldout.Start, response.ReservedHoldout.From);
        Assert.Equal(ReservedHoldout.End, response.ReservedHoldout.To);
        Assert.True(response.ReservedHoldout.Excluded);
    }

    [Fact]
    public async Task ARequestEntirelyInsideTheHoldoutReturnsInsufficientDataAndStillCarriesTheLimitations()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);

        var response = await RunnerFor(connectionString).RunAsync(
            new DateOnly(2024, 3, 1), new DateOnly(2024, 6, 1), CancellationToken.None);

        Assert.Equal(VrpConditioningRunStatus.InsufficientData, response.Status);
        Assert.Contains("holdout", response.InsufficientReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(response.Daily);
        Assert.Empty(response.Conditioning);
        Assert.Empty(response.Arms);

        // The limitations ride on EVERY response, including this one. A consumer that renders the
        // insufficient-data path must not lose them.
        Assert.Contains("NO SIGNIFICANCE CLAIMS", response.Limitations.Inference);
        Assert.False(response.Registrable);
    }

    [Fact]
    public async Task TooFewSessionsProducesTheHonestInsufficientDataStatusWithRealCounts()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);

        var dates = TradingDates(new DateOnly(2023, 11, 1), new DateOnly(2023, 11, 14));
        await SeedAsync(connectionString, dates);

        var response = await RunnerFor(connectionString).RunAsync(dates[0], dates[^1], CancellationToken.None);

        Assert.Equal(VrpConditioningRunStatus.InsufficientData, response.Status);
        Assert.NotNull(response.InsufficientReason);
        Assert.Equal(dates.Count, response.DataWindow.SessionsAvailable);
        Assert.Equal(0, response.DataWindow.DecisionDates);
        Assert.Null(response.DataWindow.LastLabelTo);
        Assert.Empty(response.Daily);
    }

    // ---------- a scoreable run: shape, reproducibility, and the registry it must not touch ----------

    private static async Task SeedScoreableFoldOneAsync(string connectionString)
    {
        // Inside F1: train through 2016, test from 2018. Enough 2016 sessions to clear the 22-day
        // warm-up, the 21-day forward label, the 42-row purge and the 60-row training floor.
        await SeedAsync(connectionString, TradingDates(new DateOnly(2016, 4, 1), new DateOnly(2016, 12, 30)));
        await SeedAsync(connectionString, TradingDates(new DateOnly(2018, 1, 2), new DateOnly(2018, 5, 31)), offset: 500);
    }

    [Fact]
    public async Task AScoreableRunProducesFourArmsFiveBucketsAndLeavesTheTrialRegistryEmpty()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);
        await SeedScoreableFoldOneAsync(connectionString);

        var response = await RunnerFor(connectionString).RunAsync(
            new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), CancellationToken.None);

        Assert.Equal(VrpConditioningRunStatus.Ok, response.Status);
        Assert.Equal(4, response.Arms.Count);
        Assert.Equal(VrpConditioningArms.Gate, response.GateArmKey);

        Assert.Equal(4, response.Conditioning.Count);
        Assert.All(response.Conditioning, arm => Assert.Equal(5, arm.Buckets.Count));

        // Every scored day carries a bucket for every arm.
        Assert.NotEmpty(response.Daily);
        Assert.All(response.Daily, d => Assert.Equal(4, d.Bucket.Count));

        // The design block echoes the frozen constants, so a reader never has to trust prose.
        Assert.Equal(21, response.Design.LabelTradingDays);
        Assert.Equal(25, response.Design.OverlappingHacLag);
        Assert.Equal(21, response.Design.NonOverlappingStride);

        // Non-registrable, and the registry is untouched. This companion produces conditioning
        // knowledge, never a claim, so it may not consume a registered-variant slot.
        Assert.False(response.Registrable);
        Assert.Equal(0L, await RegisteredTrialCountAsync(connectionString));

        // Persisted and reloadable with the limitations intact.
        var store = new VrpConditioningStudyStore(
            ConfigurationFor(connectionString), NullLogger<VrpConditioningStudyStore>.Instance);
        await store.SaveAsync(response, CancellationToken.None);

        var reloaded = await store.GetLatestAsync(CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.Registrable);
        Assert.Contains("NO SIGNIFICANCE CLAIMS", reloaded.Limitations.Inference);
        Assert.Equal(response.Conditioning.Count, reloaded.Conditioning.Count);
        Assert.Equal(0L, await RegisteredTrialCountAsync(connectionString));
    }

    [Fact]
    public async Task TwoIdenticalRunsProduceIdenticalBootstrapNumbers()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);
        await SeedScoreableFoldOneAsync(connectionString);

        var runner = RunnerFor(connectionString);
        var first = await runner.RunAsync(new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), CancellationToken.None);
        var second = await runner.RunAsync(new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), CancellationToken.None);

        Assert.Equal(VrpConditioningRunStatus.Ok, first.Status);

        for (var i = 0; i < first.Conditioning.Count; i++)
        {
            Assert.Equal(first.Conditioning[i].Q5MinusQ1Pnl, second.Conditioning[i].Q5MinusQ1Pnl);
            Assert.Equal(first.Conditioning[i].Q5MinusQ1PnlInterval.Lower, second.Conditioning[i].Q5MinusQ1PnlInterval.Lower);
            Assert.Equal(first.Conditioning[i].Q5MinusQ1PnlInterval.Upper, second.Conditioning[i].Q5MinusQ1PnlInterval.Upper);
            Assert.Equal(first.Conditioning[i].BootstrapMonotoneFractionPnl, second.Conditioning[i].BootstrapMonotoneFractionPnl);
        }
    }
}
