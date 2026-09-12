using TradingStuff.EarningsStudy.Csv;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Stats;
using TradingStuff.EarningsStudy.Timing;
using TradingStuff.ResearchContracts;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// A study data directory built in code: the six input tables plus the gate ledger, written to a
/// temporary directory, and the compute step run against them with the WP2 stubs substituted.
///
/// Events are declared by the two numbers the claim is about — the realized move and the implied
/// move — and the harness derives closes and quotes that produce EXACTLY those, so a test asserts on
/// a ratio it chose rather than on one it has to re-derive. Entry close is 100, so RF = rf and
/// IM = im by construction.
/// </summary>
internal sealed class StudyHarness : IDisposable
{
    public StudyHarness(string? directory = null, bool writesTables = true)
    {
        Directory = directory ?? Path.Combine(Path.GetTempPath(), $"c1-compute-{Guid.NewGuid():N}");
        WritesTables = writesTables;
        System.IO.Directory.CreateDirectory(Directory);
        Context = new StudyContext(new StudyPaths(Directory), new StubSessionClock(), Output, TimeProvider.System);
    }

    /// <summary>False when the directory already holds the tables (the committed fixture), so a run does not overwrite its own inputs.</summary>
    public bool WritesTables { get; }

    public string Directory { get; }
    public StudyContext Context { get; }
    public StringWriter Output { get; } = new();
    public StudyPaths Paths => Context.Paths;

    public List<UniverseRow> Universe { get; } = [new("TEST", "TEST INC", 1, "TEST", "Test Inc", "Nasdaq", true, null, null)];
    public List<EventRow> Events { get; } = [];
    public List<SharesFactRow> SharesFacts { get; } = [];
    public List<EventTimingRow> Timings { get; } = [];
    public List<OptionMeasuresRow> Measures { get; } = [];
    public List<ClosesRow> Closes { get; } = [];

    /// <summary>What the earlier verbs recorded. Gate 07 is present by default so the quarantine rate has a denominator.</summary>
    public List<GateCountRow> GateCounts { get; } =
    [
        new("universe", 1, Gates.SeedOptionable, 10, 0, 10, null),
        new("universe", 2, Gates.CikMapped, 10, 2, 8, null),
        new("universe", 3, Gates.PrimaryListing, 8, 1, 7, null),
        new("events", 4, Gates.HasItem202, 7, 0, 7, null),
        new("events", 5, Gates.NotAmendment, 7, 0, 7, null),
        new("events", 6, Gates.Dedup, 7, 0, 7, null),
        new("timing", 7, Gates.TimingClassified, 100, 4, 96, "2 acceptances on a non-trading day")
    ];

    /// <summary>The WP2 price QA. Passes everything unless a test says otherwise.</summary>
    public Func<EventTimingRow, ClosesRow, TimingQaResult> PriceQa { get; set; } =
        (_, _) => new TimingQaResult(false, null, null, null);

    /// <summary>A stand-in for WP2's as-of pick: the latest fact filed on or before the entry date.</summary>
    public Func<IEnumerable<SharesFactRow>, DateOnly, SharesFactRow?> SharesAsOf { get; set; } =
        (facts, entry) => facts.Where(f => f.Filed <= entry).OrderBy(f => f.Filed).ThenBy(f => f.PeriodEnd).LastOrDefault();

    public Func<string> DescribePriceQa { get; set; } = () => "STUB QA RULE: the two-signal price check.";

    /// <summary>
    /// Declares one event. <paramref name="rf"/> and <paramref name="im"/> are what the compute step
    /// must measure; everything else defaults to a clean, tradable, quotable event.
    /// </summary>
    public StudyHarness AddEvent(
        string id,
        decimal rf = 0.05m,
        decimal im = 0.08m,
        string? week = "2024-W05",
        string timingClass = "AMC",
        bool quarantinedAtTiming = false,
        bool keptAfterDedup = true,
        bool withTiming = true,
        bool withCloses = true,
        bool withMeasures = true,
        string closesStatus = "ok",
        string fetchStatus = "ok",
        decimal? closeEntryOverride = null,
        decimal? closeExitOverride = null,
        decimal? closePreEntry = 99m,
        decimal? straddleMidOverride = null,
        decimal? spotFeed = 100m,
        decimal? spotParity = null,
        decimal? callBid = null,
        decimal? putBid = null,
        decimal? combinedSpread = null,
        decimal? straddleMidExit = null,
        bool withStraddleExit = true,
        DateOnly? expiration = null,
        int? dte = 3,
        long cik = 1,
        string symbol = "TEST",
        string snapshotKind = "eod",
        string? snapshotTime = "16:00:00",
        decimal? shares = 1_000_000m)
    {
        var entryClose = closeEntryOverride ?? 100m;
        var exitClose = closeExitOverride ?? entryClose * (1m + rf);
        var straddleMid = straddleMidOverride ?? im * 100m;

        Events.Add(new EventRow(id, cik, symbol, $"{id}-accession", "8-K", "2.02",
            new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 1), new DateTime(2024, 2, 1, 16, 30, 0), true, keptAfterDedup, null));

        if (withTiming)
        {
            Timings.Add(new EventTimingRow(id, timingClass, new DateOnly(2024, 2, 1),
                new DateOnly(2024, 1, 30), new DateOnly(2024, 1, 31), new DateOnly(2024, 2, 2),
                week, quarantinedAtTiming, quarantinedAtTiming ? "acceptance is intraday" : null));
        }

        if (withCloses)
        {
            Closes.Add(new ClosesRow(id, symbol, closePreEntry, entryClose, exitClose, 0.02m, closesStatus, "ibkr", null));
        }

        if (withMeasures)
        {
            Measures.Add(new OptionMeasuresRow(id, symbol, expiration ?? new DateOnly(2024, 2, 9), dte,
                snapshotKind, snapshotTime, spotParity, spotFeed, 100m,
                callBid ?? straddleMid / 2m, (callBid ?? straddleMid / 2m) + 0.05m,
                putBid ?? straddleMid / 2m, (putBid ?? straddleMid / 2m) + 0.05m,
                straddleMid, combinedSpread ?? straddleMid * 0.02m,
                null, null, withStraddleExit ? straddleMidExit ?? straddleMid * 0.6m : null, null, fetchStatus, null));
        }

        if (shares is not null && SharesFacts.All(f => f.Cik != cik))
        {
            SharesFacts.Add(new SharesFactRow(cik, new DateOnly(2023, 12, 31), shares.Value, new DateOnly(2024, 1, 15), "10-K", "acc", null));
        }

        return this;
    }

    /// <summary>Writes every table, then runs the compute step with the stubs injected.</summary>
    public async Task<int> RunAsync(params string[] args)
    {
        Write();
        var step = new ComputeStep(PriceQa, SharesAsOf, DescribePriceQa);
        return await step.RunAsync(Context, args, CancellationToken.None);
    }

    /// <summary>
    /// Writes every table and runs the step with its REAL defaults — WP2's <c>TimingQa.Evaluate</c>,
    /// <c>AsOf.SharesOutstanding</c> and <c>TimingQa.Describe</c>. This is what the CLI does.
    /// </summary>
    public async Task<int> RunWithRealDefaultsAsync(params string[] args)
    {
        Write();
        return await new ComputeStep().RunAsync(Context, args, CancellationToken.None);
    }

    public void Write()
    {
        if (!WritesTables) return;

        CsvFile.Write(Paths.Universe, Universe);
        CsvFile.Write(Paths.Events, Events);
        CsvFile.Write(Paths.SharesFacts, SharesFacts);
        CsvFile.Write(Paths.EventTiming, Timings);
        CsvFile.Write(Paths.OptionMeasures, Measures);
        CsvFile.Write(Paths.Closes, Closes);
        if (GateCounts.Count > 0) CsvFile.Write(Paths.GateCounts, GateCounts);
    }

    public string Memo => File.ReadAllText(Paths.Memo);

    public List<C1EventRow> EventTable => CsvFile.Read<C1EventRow>(Paths.EventTable);

    public List<GateCountRow> WrittenGateCounts => CsvFile.Read<GateCountRow>(Paths.GateCounts);

    public C1EventRow Row(string eventId) => EventTable.Single(r => r.EventId == eventId);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is not a test failure.
        }
    }

    /// <summary>The compute step never asks the calendar anything; this exists to satisfy the context.</summary>
    private sealed class StubSessionClock : ISessionClock
    {
        public TradingSession? SessionAt(string calendar, DateTimeOffset instantUtc) => null;

        public DateOnly TradingDateOf(string calendar, DateTimeOffset instantUtc) => DateOnly.FromDateTime(instantUtc.UtcDateTime);

        public IReadOnlyList<TradingSession> SessionsBetween(string calendar, DateOnly from, DateOnly to) => [];
    }
}
