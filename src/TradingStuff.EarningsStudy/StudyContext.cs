using TradingStuff.ResearchContracts;

namespace TradingStuff.EarningsStudy;

/// <summary>
/// Where each table lives. One directory, fixed names, so a verb's input is always the previous
/// verb's committed output and nothing is passed around in memory between runs.
/// </summary>
public sealed class StudyPaths(string dataDirectory)
{
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);

    /// <summary>Large re-fetchable vendor pulls (chains, bar series). Gitignored.</summary>
    public string RawDirectory => Path.Combine(DataDirectory, "raw");
    public string RawChainsDirectory => Path.Combine(RawDirectory, "chains");
    public string RawBarsDirectory => Path.Combine(RawDirectory, "bars");
    public string RawEdgarDirectory => Path.Combine(RawDirectory, "edgar");

    /// <summary>The frozen CBOE optionable-symbol directory the universe is seeded from (survivorship bias logged).</summary>
    public string UniverseSeed => Path.Combine(DataDirectory, "universe-seed-cboe-2026-09-12.csv");

    public string Universe => Path.Combine(DataDirectory, "universe.csv");
    public string Events => Path.Combine(DataDirectory, "events.csv");
    public string SharesFacts => Path.Combine(DataDirectory, "shares_facts.csv");
    public string EventTiming => Path.Combine(DataDirectory, "event_timing.csv");
    public string OptionMeasures => Path.Combine(DataDirectory, "option_measures.csv");
    public string Closes => Path.Combine(DataDirectory, "closes.csv");
    public string EventTable => Path.Combine(DataDirectory, "c1_event_table.csv");
    public string Memo => Path.Combine(DataDirectory, "c1_memo.md");

    /// <summary>Per-verb gate counts, appended by every step so the exclusion table is assembled from records rather than remembered.</summary>
    public string GateCounts => Path.Combine(DataDirectory, "gate_counts.csv");
}

/// <summary>Everything a step needs that is not on its command line.</summary>
public sealed class StudyContext(StudyPaths paths, ISessionClock clock, TextWriter output, TimeProvider time)
{
    public StudyPaths Paths { get; } = paths;

    /// <summary>The platform's NYSE calendar. The only source of "is this a trading day" in the study.</summary>
    public ISessionClock Clock { get; } = clock;

    public TextWriter Output { get; } = output;
    public TimeProvider Time { get; } = time;

    public void Log(string message) =>
        Output.WriteLine($"[{Time.GetUtcNow():HH:mm:ss}] {message}");
}

/// <summary>One CLI verb. Implementations live one per work package and own their own files.</summary>
public interface IStudyStep
{
    string Verb { get; }
    string Description { get; }
    Task<int> RunAsync(StudyContext context, IReadOnlyList<string> args, CancellationToken cancellationToken);
}
