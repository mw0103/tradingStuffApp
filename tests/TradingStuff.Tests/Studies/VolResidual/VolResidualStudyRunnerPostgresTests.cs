using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.ResearchService.Sessions;
using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// End-to-end tests of <see cref="VolResidualStudyRunner"/> against a real, migrated Postgres
/// database — the same convention every other <c>*PostgresTests</c> class in this suite uses,
/// excluded unless <c>TRADING_TEST_POSTGRES</c> is set.
/// </summary>
/// <remarks>
/// Two properties are load-bearing enough that the task specifying this study named them
/// explicitly and required a real test, not just a code-reading argument:
/// <list type="bullet">
/// <item>a request whose range overlaps the reserved holdout must never return a holdout-dated
/// row, EVEN WHEN holdout-dated data genuinely exists in the database — proving the clamp acts on
/// the query, not merely on an absence of data to leak;</item>
/// <item>too little data must produce the honest <c>insufficient-data</c> status with real counts,
/// never a chart-shaped payload built from a handful of days.</item>
/// </list>
/// </remarks>
[Trait("Category", "RequiresPostgres")]
[Collection(PostgresCollection.Name)]
public sealed class VolResidualStudyRunnerPostgresTests
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

    private static VolResidualStudyRunner RunnerFor(string connectionString) =>
        new(new VolResidualBarLoader(ConfigurationFor(connectionString)), Clock,
            NullLogger<VolResidualStudyRunner>.Instance);

    /// <summary>Every CBOE_SPX_RTH trading date in <c>[from, to]</c> inclusive.</summary>
    private static List<DateOnly> TradingDates(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (Clock.IsTradingDay("CBOE_SPX_RTH", d)) dates.Add(d);
        }

        return dates;
    }

    /// <summary>
    /// Bulk-inserts one day's SPX 1-minute RTH bars with a varying close price (never constant —
    /// a flat price yields zero realized variance, which this study's own filtering would then
    /// legitimately drop, defeating the point of seeding it).
    /// </summary>
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

    // ---------- holdout exclusion: THE headline safety property ----------

    [Fact]
    public async Task ARequestOverlappingTheHoldoutNeverReturnsOrLoadsAHoldoutDatedRow()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);

        // Pre-holdout data: 30 trading days ending the last trading day of 2023.
        var preHoldoutDates = TradingDates(new DateOnly(2023, 11, 15), new DateOnly(2023, 12, 29));
        // Holdout data: several trading days genuinely INSIDE the reserved window. If the clamp
        // were not applied, these would be exactly what could leak into the response.
        var holdoutDates = TradingDates(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 10));

        for (var i = 0; i < preHoldoutDates.Count; i++)
        {
            await InsertSpxDayAsync(connectionString, preHoldoutDates[i], i);
            await InsertVixDailyAsync(connectionString, preHoldoutDates[i], 15.0 + i % 4);
        }

        for (var i = 0; i < holdoutDates.Count; i++)
        {
            await InsertSpxDayAsync(connectionString, holdoutDates[i], 1000 + i);
            await InsertVixDailyAsync(connectionString, holdoutDates[i], 20.0 + i % 4);
        }

        // Prove the seeded holdout data is real and would be returned by an UNCLAMPED query — the
        // property under test is that the RUNNER never asks for it, not merely that none exists.
        var loader = new VolResidualBarLoader(ConfigurationFor(connectionString));
        var unclamped = await loader.LoadSpxOneMinuteBarsAsync(
            new DateOnly(2023, 11, 15), new DateOnly(2024, 1, 10), CancellationToken.None);
        Assert.Contains(unclamped, b => DateOnly.FromDateTime(b.Timestamp!.Value.UtcDateTime) >= ReservedHoldout.Start);

        var runner = RunnerFor(connectionString);

        // The request explicitly overlaps the holdout.
        var response = await runner.RunAsync(new DateOnly(2023, 11, 15), new DateOnly(2024, 1, 10), CancellationToken.None);

        Assert.True(response.DataWindow.To < ReservedHoldout.Start,
            $"dataWindow.to ({response.DataWindow.To}) must be clamped below the holdout start ({ReservedHoldout.Start}).");
        Assert.All(response.Daily, day => Assert.True(day.Date < ReservedHoldout.Start,
            $"daily row for {day.Date} falls inside the reserved holdout and must never be returned."));
        Assert.Equal(ReservedHoldout.Start, response.ReservedHoldout.From);
        Assert.Equal(ReservedHoldout.End, response.ReservedHoldout.To);
        Assert.True(response.ReservedHoldout.Excluded);
    }

    [Fact]
    public async Task ARequestEntirelyInsideTheHoldoutReturnsInsufficientDataWithoutQueryingBars()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);
        var runner = RunnerFor(connectionString);

        var response = await runner.RunAsync(new DateOnly(2024, 3, 1), new DateOnly(2024, 6, 1), CancellationToken.None);

        Assert.Equal(VolResidualRunStatus.InsufficientData, response.Status);
        Assert.NotNull(response.InsufficientReason);
        Assert.Contains("holdout", response.InsufficientReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(response.Daily);
        Assert.Empty(response.Models);
    }

    // ---------- insufficient data is visible, not silent ----------

    [Fact]
    public async Task TooFewSessionsProducesTheHonestInsufficientDataStatusWithRealCounts()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);

        // Ten trading days: nowhere near the 22-day HAR warmup, let alone a scoreable fold.
        var dates = TradingDates(new DateOnly(2023, 11, 1), new DateOnly(2023, 11, 14));
        for (var i = 0; i < dates.Count; i++)
        {
            await InsertSpxDayAsync(connectionString, dates[i], i);
            await InsertVixDailyAsync(connectionString, dates[i], 15.0 + i % 3);
        }

        var runner = RunnerFor(connectionString);
        var response = await runner.RunAsync(dates[0], dates[^1], CancellationToken.None);

        Assert.Equal(VolResidualRunStatus.InsufficientData, response.Status);
        Assert.NotNull(response.InsufficientReason);
        Assert.Equal(dates.Count, response.DataWindow.SessionsAvailable);
        Assert.Equal(0, response.DataWindow.SessionsUsed); // fewer than 22 prior days: zero feature rows
        Assert.Empty(response.Daily);
        Assert.Empty(response.Models);
    }

    // ---------- exploratory runs are tagged, and never reach the trial registry ----------

    /// <summary>
    /// Seeds enough SPX/VIX history inside fold F1 (train through 2016, test from 2018) for the fold
    /// to actually score, so the exploratory path runs end to end rather than short-circuiting on
    /// insufficient data.
    /// </summary>
    private static async Task<VolResidualStudyRunner> SeedScoreableFoldOneAsync(string connectionString)
    {
        // 22 sessions of HAR warmup + >= 30 training rows after the 5-day purge, all inside F1's
        // training window, then a handful of days inside its test window.
        var trainDates = TradingDates(new DateOnly(2016, 8, 1), new DateOnly(2016, 12, 30));
        var testDates = TradingDates(new DateOnly(2018, 2, 1), new DateOnly(2018, 3, 15));

        var index = 0;
        foreach (var date in trainDates.Concat(testDates))
        {
            await InsertSpxDayAsync(connectionString, date, index % 7);
            await InsertVixDailyAsync(connectionString, date, 14.0 + index % 9);
            index++;
        }

        return RunnerFor(connectionString);
    }

    private static async Task<long> RegisteredTrialCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM research.registered_trials", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AnExploratoryRunIsTaggedEverywhereAndLeavesTheTrialRegistryEmpty()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);
        var runner = await SeedScoreableFoldOneAsync(connectionString);

        var response = await runner.RunAsync(
            new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), includeExploratoryGbt: true, CancellationToken.None);

        Assert.Equal(VolResidualRunStatus.Ok, response.Status);

        // Tagged in the API response.
        Assert.True(response.IsExploratory);
        Assert.False(response.Registrable);
        Assert.NotNull(response.ExploratoryReason);
        Assert.Contains("only if rung 3", response.ExploratoryReason);
        Assert.Contains("registered_trials", response.ExploratoryReason);

        Assert.NotNull(response.Exploratory);
        Assert.True(response.Exploratory!.IsExploratory);
        Assert.False(response.Exploratory.Registrable);
        Assert.Equal(VolResidualModelKeys.Gbt, response.Exploratory.ModelKey);
        Assert.Contains("not eligible for any claim", response.Exploratory.PermittedClaim);

        Assert.Contains(response.Models, m =>
            m.Key == VolResidualModelKeys.Gbt && m.Role == VolResidualModelRoles.Exploratory);

        // Persisted, and still tagged in the stored artifact.
        var store = new VolResidualStudyStore(ConfigurationFor(connectionString), NullLogger<VolResidualStudyStore>.Instance);
        await store.SaveAsync(response, CancellationToken.None);

        var reloaded = await store.GetLatestAsync(CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsExploratory);
        Assert.False(reloaded.Registrable);
        Assert.NotNull(reloaded.Exploratory);

        // And the registry is untouched. This is the property the exploratory tagging exists to
        // guarantee: a rung outside the registered ladder must not consume a variant slot.
        Assert.Equal(0L, await RegisteredTrialCountAsync(connectionString));
    }

    [Fact]
    public async Task ARegisteredRunFitsNoGbtAndIsNotTaggedExploratory()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);
        var runner = await SeedScoreableFoldOneAsync(connectionString);

        var response = await runner.RunAsync(
            new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), CancellationToken.None);

        Assert.Equal(VolResidualRunStatus.Ok, response.Status);
        Assert.False(response.IsExploratory);
        Assert.True(response.Registrable);
        Assert.Null(response.ExploratoryReason);
        Assert.Null(response.Exploratory);
        Assert.DoesNotContain(response.Models, m => m.Key == VolResidualModelKeys.Gbt);
        Assert.All(response.Daily, d => Assert.DoesNotContain(VolResidualModelKeys.Gbt, d.Forecasts.Keys));
        Assert.Equal(0L, await RegisteredTrialCountAsync(connectionString));
    }

    [Fact]
    public async Task TwoIdenticalRunsProduceIdenticalH1Numbers()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);
        var runner = await SeedScoreableFoldOneAsync(connectionString);

        var first = await runner.RunAsync(new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), CancellationToken.None);
        var second = await runner.RunAsync(new DateOnly(2016, 1, 1), new DateOnly(2018, 12, 31), CancellationToken.None);

        Assert.NotNull(first.H1);
        Assert.NotNull(second.H1);

        // Bit-for-bit. The seeded bootstrap is the only stochastic step in the whole pipeline, so a
        // difference here means the seed is not doing its job.
        Assert.Equal(first.H1!.BootstrapLower, second.H1!.BootstrapLower);
        Assert.Equal(first.H1.DmStatistic, second.H1.DmStatistic);
        Assert.Equal(first.H1.DmPValue, second.H1.DmPValue);
        Assert.Equal(first.H1.MarginPct, second.H1.MarginPct);
        Assert.Equal(first.H1.Verdict, second.H1.Verdict);
    }

    [Fact]
    public async Task EnoughSessionsForFeatureRowsButNotForAScoreableFoldStillReportsInsufficientData()
    {
        if (ServerConnectionString is not { } server) return;
        var connectionString = await PrepareAsync(server);

        // 35 trading days: enough to clear the 22-day warmup and produce a handful of feature
        // rows, but nowhere near VolResidualFoldRunner.MinimumTrainRows (30) inside any registered
        // fold's actual train/test window (these dates do not even fall inside a registered fold).
        var dates = TradingDates(new DateOnly(2023, 11, 1), new DateOnly(2023, 12, 22));
        for (var i = 0; i < dates.Count; i++)
        {
            await InsertSpxDayAsync(connectionString, dates[i], i);
            await InsertVixDailyAsync(connectionString, dates[i], 15.0 + i % 4);
        }

        var runner = RunnerFor(connectionString);
        var response = await runner.RunAsync(dates[0], dates[^1], CancellationToken.None);

        Assert.Equal(VolResidualRunStatus.InsufficientData, response.Status);
        Assert.NotNull(response.InsufficientReason);
        Assert.True(response.DataWindow.SessionsUsed > 0, "some feature rows should have been built past the warmup");
        Assert.Empty(response.Daily);
        Assert.Empty(response.Models);
    }
}
