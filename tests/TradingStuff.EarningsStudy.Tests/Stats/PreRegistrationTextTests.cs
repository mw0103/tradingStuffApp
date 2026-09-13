using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The memo quotes two registrations: v2, which governs, and v0, which it supersedes but whose
/// universe rule, window, measures and biases v2 section 6 leaves standing. A quotation that has
/// drifted from the file is worse than no quotation, because it reads as authority. These tests
/// compare the strings in <see cref="MemoWriter"/> against the files themselves.
/// </summary>
public sealed class PreRegistrationTextTests
{
    [Fact]
    public void The_memo_names_the_superseded_file_and_its_freeze_date_as_that_file_states_them()
    {
        var text = System.IO.File.ReadAllText(File(MemoWriter.SupersededRegistrationPath));
        var frozen = text.Split('\n').Single(l => l.StartsWith("Frozen:", StringComparison.Ordinal));

        Assert.Contains(MemoWriter.PreRegistrationFrozen, frozen);
        Assert.Contains("prior to any query execution", frozen);
    }

    [Fact]
    public void The_memo_names_the_governing_file_and_its_amendment_date_as_that_file_states_them()
    {
        var text = System.IO.File.ReadAllText(File(MemoWriter.PreRegistrationPath));
        var amended = text.Split('\n').Single(l => l.StartsWith("Amended:", StringComparison.Ordinal));

        Assert.Contains(MemoWriter.PreRegistrationAmended, amended);
        Assert.Contains("SUPERSEDES v0", text);
        Assert.Contains("PRE-RESULT AMENDMENT", text);
    }

    /// <summary>
    /// The decision rule the memo prints, against the rule the governing file registers. The memo's
    /// sentence is the only statement of the criterion most readers will see; if it drifts from the
    /// registration, the memo is claiming authority it does not have.
    /// </summary>
    [Fact]
    public void The_printed_criterion_is_the_one_v2_registers()
    {
        var text = System.IO.File.ReadAllText(File(MemoWriter.PreRegistrationPath)).Replace("\r\n", "\n");

        Assert.Contains("PASS = mean(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for\n       mean(RF/IM) excludes 1.", text);
        Assert.Equal(
            "PASS = mean(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for mean(RF/IM) excludes 1. FAIL = otherwise.",
            C1Criterion.Rule);

        // The demotions, named in the file and honoured in code.
        Assert.Contains("Demoted to descriptive readouts (reported, never deciding):", text);
        Assert.Contains("median(RF/IM) and P(RF < IM)", text);

        // The null value the criterion is built on.
        Assert.Contains("Null under fair per-event pricing (IM_i = E[RF_i]): mean(RF/IM) = 1", text);
        Assert.Equal(1m, C1Verdict.NullRatio);
    }

    [Fact]
    public void The_registered_biases_are_quoted_verbatim_rather_than_paraphrased()
    {
        var text = System.IO.File.ReadAllText(File(MemoWriter.SupersededRegistrationPath)).Replace("\r\n", "\n");
        var start = text.IndexOf("## Known v0 biases (logged, with direction)", StringComparison.Ordinal);
        Assert.True(start >= 0, "the pre-registration no longer has a 'Known v0 biases' section");

        var body = text[(start + "## Known v0 biases (logged, with direction)".Length)..];
        var end = body.IndexOf("\n## ", StringComparison.Ordinal);
        var section = (end >= 0 ? body[..end] : body).Trim();

        Assert.Equal(section, MemoWriter.RegisteredBiases.Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public void The_registered_constants_are_the_ones_the_file_registers()
    {
        var text = System.IO.File.ReadAllText(File(MemoWriter.SupersededRegistrationPath));

        Assert.Contains("Price >= $10 at entry close", text);
        Assert.Equal(10m, C1Registration.MinimumEntryPrice);

        Assert.Contains("combined ATM spread <= 15% of", text);
        Assert.Equal(0.15m, C1Registration.TradableSpreadFraction);

        Assert.Contains("10,000 reps, 95% CI", text);
        Assert.Equal(10_000, C1Registration.BootstrapReplications);
        Assert.Equal(0.95m, C1Registration.ConfidenceLevel);

        Assert.Contains("DTE <= 7 vs > 7", text);
        Assert.Equal(7, C1Registration.ShortDteThresholdDays);

        Assert.Contains("mean log(RF/IM) (and 1% trimmed)", text);
        Assert.Equal(0.01m, C1Registration.TrimFraction);

        // v0's own criterion, still quoted here as the thing v2 replaced. v2 section 6 lists what it
        // leaves unchanged, and the criterion is not on that list.
        Assert.Contains("median(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for", text);
        Assert.Contains("P(RF < IM) excludes 0.5", text);

        var v2 = System.IO.File.ReadAllText(File(MemoWriter.PreRegistrationPath));
        Assert.Contains("Universe rule and gate order, window and time-only subsetting, measure", v2);
    }

    /// <summary>Walks up from the test binary to the repository root; both files are committed beside the code that quotes them.</summary>
    private static string File(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (System.IO.File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
