using TradingStuff.EarningsStudy.Csv;

namespace TradingStuff.EarningsStudy.Model;

/// <summary>
/// The gate tally file is append-only from a verb's point of view, and a verb is re-run freely
/// (a warm cache makes that cheap). Without this, every re-run would append a second row for the
/// same gate and the exclusion table would double-count. So the CLI drops a verb's own rows before
/// the verb runs: the file always holds exactly one tally per (verb, gate), from the latest run.
/// </summary>
public static class GateCountsFile
{
    public static int DropStep(string path, string step)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return 0;

        var rows = CsvFile.Read<GateCountRow>(path);
        var kept = rows.Where(r => !string.Equals(r.Step, step, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == rows.Count) return 0;

        CsvFile.Write(path, kept);
        return rows.Count - kept.Count;
    }
}
