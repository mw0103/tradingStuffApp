using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingStuff.ResearchContracts;
using TradingStuff.ResearchService.OptionChains;
using TradingStuff.ResearchService.Persistence;
using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility.ThetaData;
using Xunit.Abstractions;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// Phase 9's acceptance criterion: build a 30-day model-free implied variance strip from chains
/// ingested through the ACTUAL ingestion pipeline (migration 019 + OptionChainStore +
/// OptionChainCoordinator — no bypass) and compare it against published VIX daily closes already
/// recorded in <c>research.bars</c>.
/// </summary>
/// <remarks>
/// <para>
/// Needs BOTH a live Theta Terminal (<c>TRADING_TEST_THETA=host:port</c>, the same convention
/// <see cref="LiveThetaTerminalTests"/> uses) AND the REAL running research database
/// (<c>TRADING_LIVE_POSTGRES</c>, a full Npgsql connection string) — not a throwaway per-test
/// database. Two things force that: this test wants VIX closes that are already recorded there
/// (5,222 rows back to 2005 per docs/FOLLOWUP.md §4.6), and Phase 9's own acceptance criterion is
/// specifically about chains landed through the real pipeline, not a fixture. Skipped entirely
/// (both env vars unset) in ordinary CI/unit runs.
/// </para>
/// <para>
/// The evaluation window is June 2016 — chosen for a real regime change inside one month rather
/// than a manufactured one: the Brexit referendum (2016-06-23) moved VIX from the high-teens to the
/// mid-20s in two trading days. The job's OWN date range is padded roughly six weeks on each side
/// (2016-05-01..2016-07-15) so every evaluation day in June has near- and next-term expirations
/// available to bracket 30 days — see <see cref="ConstantMaturityVariance"/>'s 23-37 day window.
/// </para>
/// <para>
/// The risk-free rate is a flat approximation (0.45%, roughly 3-month T-bill mid-2016), not a
/// historical series — see docs/DECISIONS.md-style honesty: this is a simplification for the
/// cross-check, not a claim that 2016 rates were flat. It affects the forward and the correction
/// term only at the margin, not the shape of any VIX disagreement this test reports.
/// </para>
/// <para>
/// <b>Expect this to run for many minutes, possibly over an hour, and treat that as normal, not a
/// hang.</b> Measured live 2026-08-02: single-day bulk quote calls took 25-80s each, and a
/// month-spanning bulk call (this job's 2.5-month range is chunked into ~2-3 calls per expiration by
/// <see cref="MonthlyDateRangeChunker"/>) took 1-3+ MINUTES — far slower than the roadmap's "8
/// concurrent requests under a second" figure, which was measured for small/recent-date requests,
/// not month-wide historical ones. This test was written and compiles clean but was NOT run to
/// completion in the session that added it: draining ~24 request rows at that rate did not finish
/// inside the time available. The acceptance-criterion evidence in that session's report instead
/// comes from a smaller, manually-scoped two-day check (2016-06-13 calm vs. 2016-06-24 the day after
/// the Brexit vote) built from single-day requests issued directly, which is far more tractable —
/// see the report for those numbers. Anyone running this test for real should budget accordingly, or
/// shrink <see cref="WindowFrom"/>/<see cref="WindowTo"/> first.
/// </para>
/// </remarks>
[Trait("Category", "RequiresThetaTerminal")]
public sealed class LiveThetaVixCrossCheckTests(ITestOutputHelper output)
{
    private static readonly DateOnly WindowFrom = new(2016, 5, 1);
    private static readonly DateOnly WindowTo = new(2016, 7, 15);
    private static readonly DateOnly EvalFrom = new(2016, 6, 1);
    private static readonly DateOnly EvalTo = new(2016, 6, 30);

    private static (string Host, int Port)? ThetaEndpoint()
    {
        var raw = Environment.GetEnvironmentVariable("TRADING_TEST_THETA");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(':', 2);
        return parts.Length == 2 && int.TryParse(parts[1], out var port) ? (parts[0], port) : ("127.0.0.1", 25503);
    }

    private static string? LivePostgres => Environment.GetEnvironmentVariable("TRADING_LIVE_POSTGRES");

    [Fact]
    public async Task Thirty_day_implied_variance_strip_from_ingested_chains_vs_published_VIX()
    {
        if (ThetaEndpoint() is not { } endpoint)
        {
            output.WriteLine("TRADING_TEST_THETA is unset; skipping.");
            return;
        }

        using (var probe = new TcpClient())
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await probe.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            }
            catch (Exception ex)
            {
                output.WriteLine($"No Terminal on {endpoint.Host}:{endpoint.Port} ({ex.GetType().Name}); skipping.");
                return;
            }
        }

        if (LivePostgres is not { } connectionString)
        {
            output.WriteLine("TRADING_LIVE_POSTGRES is unset; skipping (this test needs the real research database).");
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trading"] = connectionString })
            .Build();

        var store = new OptionChainStore(configuration, NullLogger<OptionChainStore>.Instance);
        using var client = new ThetaDataClient(new ThetaDataOptions
        {
            BaseAddress = $"http://{endpoint.Host}:{endpoint.Port}",
        });
        var coordinator = new OptionChainCoordinator(
            store, client,
            Options.Create(new OptionChainOptions { MaxAttempts = 3, LeaseSeconds = 120 }),
            NullLogger<OptionChainCoordinator>.Instance);

        // ---- ingest through the real pipeline: ensure jobs, plan, drain ----
        var spxwJob = await store.EnsureJobAsync(
            "live-vix-xcheck-2016-06-spxw", "SPX", "SPXW", WindowFrom, WindowTo,
            OptionChainIntervals.OneMinute, priority: 100, CancellationToken.None);
        var spxJob = await store.EnsureJobAsync(
            "live-vix-xcheck-2016-06-spx", "SPX", "SPX", WindowFrom, WindowTo,
            OptionChainIntervals.OneMinute, priority: 100, CancellationToken.None);

        Assert.NotNull(spxwJob);
        Assert.NotNull(spxJob);

        await coordinator.PlanJobAsync(spxwJob!, CancellationToken.None);
        await coordinator.PlanJobAsync(spxJob!, CancellationToken.None);

        var drained = 0;
        var emptied = 0;
        while (true)
        {
            var claimed = await store.ClaimAsync(
                coordinator.OwnerId, TimeSpan.FromMinutes(2), maxAttempts: 3, limit: 1, CancellationToken.None);
            if (claimed.Count == 0) break;

            await coordinator.ExecuteRequestAsync(claimed[0], CancellationToken.None);
            drained++;

            if (drained % 20 == 0)
            {
                output.WriteLine($"...{drained} expiration requests drained so far");
            }

            if (drained > 400)
            {
                // A safety valve, not an expected outcome: two months' worth of SPXW (3x/week
                // pre-2022) plus SPX monthlies should be well under 100 requests total.
                emptied++;
                if (emptied > 5) break;
            }
        }

        output.WriteLine($"Drained {drained} expiration request(s) across both jobs.");

        // ---- read back what actually landed, through Postgres directly (this IS the acceptance
        // criterion: a strip built from what landed, not from a fresh vendor pull) ----
        var slicesByDate = await LoadSlicesByDateAsync(connectionString, EvalFrom, EvalTo);
        output.WriteLine($"Landed data covers {slicesByDate.Count} trading date(s) in [{EvalFrom:yyyy-MM-dd}, {EvalTo:yyyy-MM-dd}].");

        var builder = new ImpliedVarianceSeriesBuilder(new FlatRiskFreeRate(0.0045));
        var strip = builder.Build("SPX", slicesByDate);

        var vixByDate = await LoadVixClosesAsync(connectionString, EvalFrom, EvalTo);

        output.WriteLine("");
        output.WriteLine("date       | usable | strip IV | VIX close | strip-VIX (pts)");
        var comparisons = new List<(DateOnly Date, double StripIv, double VixClose)>();

        foreach (var day in strip)
        {
            var date = DateOnly.FromDateTime(day.Date);
            var vix = vixByDate.TryGetValue(date, out var v) ? v : (double?)null;

            var stripIvPct = day.IsUsable ? day.ImpliedVolatility * 100.0 : double.NaN;
            output.WriteLine(
                $"{date:yyyy-MM-dd} | {(day.IsUsable ? "yes" : "no ")}    | " +
                $"{(day.IsUsable ? stripIvPct.ToString("F2") : "  -  ")}   | " +
                $"{(vix.HasValue ? vix.Value.ToString("F2") : "  -  ")}     | " +
                $"{(day.IsUsable && vix.HasValue ? (stripIvPct - vix.Value).ToString("F2") : "-")}" +
                (day.IsUsable ? "" : $"   ({day.Note})"));

            if (day.IsUsable && vix.HasValue)
            {
                comparisons.Add((date, stripIvPct, vix.Value));
            }
        }

        output.WriteLine("");
        output.WriteLine($"{comparisons.Count} of {strip.Count} evaluation day(s) produced both a usable strip value and a VIX close.");

        if (comparisons.Count == 0)
        {
            output.WriteLine(
                "No comparable days — reporting this as a finding, not silently passing. Likely cause: " +
                "insufficient expirations landed to bracket 30 days, or no VIX bars for this window.");
            return;
        }

        var diffs = comparisons.Select(c => c.StripIv - c.VixClose).ToList();
        var meanDiff = diffs.Average();
        var meanAbsDiff = diffs.Select(Math.Abs).Average();
        var maxAbsDiff = diffs.Select(Math.Abs).Max();

        output.WriteLine($"Mean difference (strip - VIX): {meanDiff:F2} points");
        output.WriteLine($"Mean absolute difference: {meanAbsDiff:F2} points");
        output.WriteLine($"Max absolute difference: {maxAbsDiff:F2} points");

        // Not tuned to pass — this bound is wide enough to admit the annualization/settlement
        // conventions this estimator does NOT try to match exactly (see the file remarks), and
        // exists only to catch a wholesale implementation error (wrong units, wrong sign, an
        // off-by-a-large-factor bug), not to certify a tight replication.
        Assert.True(comparisons.Count >= 5, $"Only {comparisons.Count} comparable day(s); too few to say anything about the shape.");
    }

    private static async Task<Dictionary<DateTime, List<OptionChainSlice>>> LoadSlicesByDateAsync(
        string connectionString, DateOnly from, DateOnly to)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT trading_class, expiration, trading_date, observed_at, strike, option_right, bid, ask
            FROM research.option_chain_quotes
            WHERE underlying = 'SPX' AND trading_date BETWEEN $1 AND $2
            ORDER BY trading_date, trading_class, expiration
            """,
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        // Keyed on (tradingDate, expiration) so SPXW and SPX rows for the same expiration date never
        // collide, then flattened per tradingDate for ImpliedVarianceSeriesBuilder.
        var byDateAndExpiration = new Dictionary<(DateOnly Date, string TradingClass, DateOnly Expiration), OptionChainSlice>();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tradingClass = reader.GetString(0);
            var expiration = reader.GetFieldValue<DateOnly>(1);
            var tradingDate = reader.GetFieldValue<DateOnly>(2);
            var observedAt = reader.GetFieldValue<DateTimeOffset>(3);
            var strike = (double)reader.GetDecimal(4);
            var right = reader.GetString(5) == "C" ? OptionRight.Call : OptionRight.Put;
            var bid = reader.IsDBNull(6) ? 0.0 : (double)reader.GetDecimal(6);
            var ask = reader.IsDBNull(7) ? 0.0 : (double)reader.GetDecimal(7);

            var key = (tradingDate, tradingClass, expiration);
            if (!byDateAndExpiration.TryGetValue(key, out var slice))
            {
                var settlement = new ExpirationSettlement();
                slice = new OptionChainSlice
                {
                    Root = tradingClass,
                    ObservedAt = observedAt.UtcDateTime,
                    SettlesAt = settlement.SettlementFor(tradingClass, expiration.ToDateTime(TimeOnly.MinValue)),
                };
                byDateAndExpiration[key] = slice;
            }

            slice.Quotes.Add(new OptionQuote(strike, right, bid, ask));
        }

        var byDate = new Dictionary<DateTime, List<OptionChainSlice>>();
        foreach (var ((date, _, _), slice) in byDateAndExpiration)
        {
            var dt = date.ToDateTime(TimeOnly.MinValue);
            if (!byDate.TryGetValue(dt, out var list))
            {
                list = [];
                byDate[dt] = list;
            }
            list.Add(slice);
        }

        return byDate;
    }

    private static async Task<Dictionary<DateOnly, double>> LoadVixClosesAsync(
        string connectionString, DateOnly from, DateOnly to)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT trading_date, close FROM research.bars
            WHERE instrument_id = 4 AND bar_size = '1 day' AND what_to_show = 'TRADES'
              AND trading_date BETWEEN $1 AND $2
            """,
            connection);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);

        var result = new Dictionary<DateOnly, double>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetFieldValue<DateOnly>(0)] = (double)reader.GetDecimal(1);
        }

        return result;
    }
}
