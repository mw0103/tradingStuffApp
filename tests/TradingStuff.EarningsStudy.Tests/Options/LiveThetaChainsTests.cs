using System.Net.Sockets;
using TradingStuff.EarningsStudy.Options;
using TradingStuff.Volatility.ThetaData;
using Xunit.Abstractions;

namespace TradingStuff.EarningsStudy.Tests.Options;

/// <summary>
/// The chains verb's fetch-and-measure path, run against a real Theta Terminal rather than assumed.
/// Needs <c>TRADING_TEST_THETA=host:port</c>; run with <c>--filter "Category=RequiresThetaTerminal"</c>.
/// Gating mirrors <c>LiveThetaTerminalTests</c> exactly, including the TCP probe before connecting -
/// duration is not evidence a live test ran (docs/LESSONS.md 11), so a skipped run must say so rather
/// than pass in 5ms looking identical to a real one.
/// </summary>
[Trait("Category", "RequiresThetaTerminal")]
public sealed class LiveThetaChainsTests(ITestOutputHelper output)
{
    private static readonly DateOnly PreEntry = new(2024, 1, 31);
    private static readonly DateOnly Entry = new(2024, 2, 1);
    private static readonly DateOnly Exit = new(2024, 2, 2);

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

        using var probe = new TcpClient();
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

        return new ThetaDataClient(new ThetaDataOptions { BaseAddress = $"http://{endpoint.Host}:{endpoint.Port}" });
    }

    [Fact]
    public async Task AAplEarningsChainProducesAPlausibleOkRowFromTheLiveTerminal()
    {
        using var client = Connect();
        if (client is null) return;

        using var tempDir = new TempStudyDirectory();
        var fetcher = new ThetaChainFetcher(client, new StudyPaths(tempDir.Path), "eod", delayMs: 0, log: output.WriteLine);

        var fetch = await fetcher.FetchAsync("AAPL", PreEntry, Exit, CancellationToken.None);
        Assert.True(fetch.Succeeded, $"fetch refused: {fetch.FailureStatus} ({fetch.Note})");

        var row = ChainMeasureBuilder.Build("live-aapl-2024q1", PreEntry, Entry, Exit, fetch);
        output.WriteLine($"snapshot={row.SnapshotKind} status={row.FetchStatus} note={row.Note}");

        if (row.SnapshotKind == "minute")
        {
            // The task's fallback clause: EOD was refused (subscription or otherwise), and the
            // minute snapshot is what this subscription can actually serve.
            output.WriteLine("EOD was refused by this subscription; verifying the minute fallback instead.");
            Assert.Equal("ok", row.FetchStatus);
            return;
        }

        var entryQuotes = fetch.QuotesByDate.GetValueOrDefault(Entry) ?? [];
        output.WriteLine($"entry quote rows: {entryQuotes.Count}; underlying_price present: {fetch.UnderlyingByDate.ContainsKey(Entry)}");

        var parity = ChainSelection.ImpliedSpotWithReason(entryQuotes);
        Assert.True(parity.Parity is not null, $"parity refused: {parity.Reason} ({parity.Detail})");
        output.WriteLine($"parity spot={parity.Parity!.Spot} strike={parity.Parity.Strike} bracketed={parity.Parity.Bracketed}");
        Assert.True(parity.Parity.Bracketed, "the parity-minimising strike should sit strictly inside the listed strike ladder for a real front-month AAPL chain");

        Assert.Equal(true, ChainSelection.LooksLikeDollarScale(entryQuotes, parity.Parity.Spot));

        Assert.NotNull(row.StraddleMidEntry);
        var fraction = row.StraddleMidEntry!.Value / parity.Parity.Spot;
        output.WriteLine($"straddle mid={row.StraddleMidEntry} spot={parity.Parity.Spot} fraction={fraction:P2}");
        Assert.InRange(fraction, 0.01m, 0.15m);

        Assert.Equal("ok", row.FetchStatus);
    }
}
