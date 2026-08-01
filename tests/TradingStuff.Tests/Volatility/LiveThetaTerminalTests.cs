using System.Net.Sockets;
using TradingStuff.Volatility.ImpliedVolatility;
using TradingStuff.Volatility.ThetaData;
using Xunit.Abstractions;

namespace TradingStuff.Tests.Volatility;

/// <summary>
/// The ThetaData request path, measured against a running Terminal rather than assumed. Needs
/// <c>TRADING_TEST_THETA=host:port</c>; run with <c>--filter "Category=RequiresThetaTerminal"</c>.
/// </summary>
/// <remarks>
/// This suite exists because the client was written against API v2 documentation and had never
/// met a Terminal. Every request it made was rejected the first time one was available: v2 is
/// removed rather than deprecated, the REST port moved from 25510 to 25503, <c>root</c> became
/// <c>symbol</c>, <c>use_csv</c> became <c>format</c>, millisecond intervals became duration
/// strings, and <c>strike=*</c> — the documented v2 way to request a whole chain — is answered
/// with <c>400 Invalid right: *</c>. No mocked test could have found any of it, because a mock
/// asserts the shape you already believe.
/// <para>
/// Measured 2026-08-01 against Terminal build 202607271 on a FREE subscription. SPXW lists 2226
/// expirations from 2012-06-01. The 2024-03-15 chain at 15:45 on 2024-03-04 returns 804 quotes
/// spanning strikes 200–8000, from which the model-free integral used 340 strikes on a 5-point
/// grid, recovering an implied volatility of 13.46% against a forward of 5143.95 and K0 of 5140.
/// VIX closed at 13.94 that day; an 11-day measure sitting a little under the 30-day index is the
/// right direction for that term structure, which is a sanity check rather than a replication.
/// </para>
/// <para>
/// Subscription coverage is asset-class gated. Option endpoints are open on FREE; index and stock
/// history return 403, so <see cref="ThetaDataClient.GetIndexPriceAsync"/> and
/// <see cref="ThetaDataClient.GetStockOhlcAsync"/> are confirmed only as far as the subscription
/// check. Read-only throughout.
/// </para>
/// </remarks>
[Trait("Category", "RequiresThetaTerminal")]
public sealed class LiveThetaTerminalTests(ITestOutputHelper output)
{
    private static readonly DateTime Expiration = new(2024, 3, 15);
    private static readonly DateTime ObservedOn = new(2024, 3, 4);

    private static (string Host, int Port)? Endpoint()
    {
        var raw = Environment.GetEnvironmentVariable("TRADING_TEST_THETA");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(':', 2);
        return parts.Length == 2 && int.TryParse(parts[1], out var port) ? (parts[0], port) : ("127.0.0.1", 25503);
    }

    private ThetaDataClient? Connect()
    {
        if (Endpoint() is not { } endpoint)
        {
            output.WriteLine("TRADING_TEST_THETA is unset; skipping.");
            return null;
        }

        using (var probe = new TcpClient())
        {
            try
            {
                if (!probe.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(3)))
                {
                    output.WriteLine($"No Terminal on {endpoint.Host}:{endpoint.Port}; skipping.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"No Terminal on {endpoint.Host}:{endpoint.Port} ({ex.GetType().Name}); skipping.");
                return null;
            }
        }

        return new ThetaDataClient(new ThetaDataOptions
        {
            BaseAddress = $"http://{endpoint.Host}:{endpoint.Port}",
        });
    }

    [Fact]
    public async Task ExpirationsAreListedForALiveSymbol()
    {
        using var client = Connect();
        if (client is null) return;

        var table = await client.ListExpirationsAsync("SPXW");

        Assert.True(table.Count > 0);
        Assert.Contains("expiration", table.ColumnNames);

        // Quotes are stripped, so a date parses rather than arriving as "2012-06-01".
        var first = CsvTable.GetString(table.Rows[0], table.RequireColumn("expiration"));
        Assert.True(DateTime.TryParse(first, out _), $"expiration '{first}' did not parse");
        output.WriteLine($"{table.Count} expirations, first {first}");
    }

    [Fact]
    public async Task AWholeChainIsReturnedByOmittingStrikeAndRight()
    {
        using var client = Connect();
        if (client is null) return;

        var table = await client.GetDailyChainQuotesAsync("SPXW", Expiration, ObservedOn, ObservedOn);

        // The bulk form: many strikes and both rights, without a wildcard anywhere.
        Assert.True(table.Count > 100, $"expected a full chain, got {table.Count} rows");
        Assert.Contains("timestamp", table.ColumnNames);
        Assert.Contains("strike", table.ColumnNames);
        output.WriteLine($"{table.Count} quote rows");
    }

    [Fact]
    public async Task TheChainLoaderParsesLiveQuotes()
    {
        using var client = Connect();
        if (client is null) return;

        var table = await client.GetDailyChainQuotesAsync("SPXW", Expiration, ObservedOn, ObservedOn);
        var slices = new ThetaDataChainLoader(client.Options)
            .Parse(table, "SPXW", Expiration, expectedUnderlyingLevel: 5100.0);

        Assert.Single(slices);
        Assert.True(slices[0].Quotes.Count > 100);

        // The snapshot is pinned to one minute, so every row shares an observation time.
        Assert.Equal(ObservedOn.Add(client.Options.SnapshotTimeOfDay), slices[0].ObservedAt);

        // Strikes in dollars. A divisor left at the v2 value of 1000 would put these near 5,
        // which is what the underlying cross-check exists to catch.
        Assert.Contains(slices[0].Quotes, q => q.Strike > 4000.0 && q.Strike < 6000.0);
        output.WriteLine($"{slices[0].Quotes.Count} quotes, strikes " +
            $"{slices[0].Quotes.Min(q => q.Strike)}..{slices[0].Quotes.Max(q => q.Strike)}");
    }

    [Fact]
    public async Task TheModelFreeIntegralRecoversAPlausibleVolatilityFromLiveQuotes()
    {
        using var client = Connect();
        if (client is null) return;

        var table = await client.GetDailyChainQuotesAsync("SPXW", Expiration, ObservedOn, ObservedOn);
        var slice = new ThetaDataChainLoader(client.Options).Parse(table, "SPXW", Expiration)[0];

        var result = ModelFreeVariance.Compute(slice, riskFreeRate: 0.0525);

        output.WriteLine($"IV={result.ImpliedVolatility:P3} strikes={result.StrikesUsed} " +
            $"F={result.Forward:F2} K0={result.AtTheMoneyStrike} spacing={result.MedianStrikeSpacing}");

        // Deliberately wide. This asserts the integral produces something in the right
        // universe from real quotes, not a reproduction of a published index: SPX sat near
        // 5130 with VIX at 13.94 on this date.
        Assert.InRange(result.ImpliedVolatility, 0.05, 0.40);
        Assert.InRange(result.Forward, 4800.0, 5500.0);
        Assert.True(result.StrikesUsed > 50);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public async Task IndexHistoryIsGatedBySubscriptionRatherThanFailingObscurely()
    {
        using var client = Connect();
        if (client is null) return;

        // On a FREE subscription this is a 403. The point of the assertion is that it
        // surfaces as its own exception type, so a caller can skip the endpoint instead of
        // retrying a request that no argument change will fix.
        try
        {
            await client.GetIndexPriceAsync("SPX", ObservedOn, ObservedOn, TimeSpan.FromMinutes(1));
            output.WriteLine("index history is available on this subscription");
        }
        catch (ThetaDataSubscriptionException ex)
        {
            Assert.Contains("subscription", ex.Message, StringComparison.OrdinalIgnoreCase);
            output.WriteLine("index history is not covered by this subscription, reported cleanly");
        }
    }
}
