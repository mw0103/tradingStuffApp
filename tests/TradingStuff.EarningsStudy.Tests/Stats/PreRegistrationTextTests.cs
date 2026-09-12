using TradingStuff.EarningsStudy.Stats;

namespace TradingStuff.EarningsStudy.Tests.Stats;

/// <summary>
/// The memo quotes the frozen pre-registration: its path, its freeze date, and its registered biases.
/// A quotation that has drifted from the file is worse than no quotation, because it reads as
/// authority. These tests compare the strings in <see cref="MemoWriter"/> against the file itself.
/// </summary>
public sealed class PreRegistrationTextTests
{
    [Fact]
    public void The_memo_names_the_frozen_file_and_its_freeze_date_as_the_file_states_them()
    {
        var text = File.ReadAllText(PreRegistrationFile());
        var frozen = text.Split('\n').Single(l => l.StartsWith("Frozen:", StringComparison.Ordinal));

        Assert.Contains(MemoWriter.PreRegistrationFrozen, frozen);
        Assert.Contains("prior to any query execution", frozen);
    }

    [Fact]
    public void The_registered_biases_are_quoted_verbatim_rather_than_paraphrased()
    {
        var text = File.ReadAllText(PreRegistrationFile()).Replace("\r\n", "\n");
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
        var text = File.ReadAllText(PreRegistrationFile());

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

        Assert.Contains("median(RF/IM) < 1 AND the week-clustered bootstrap 95% CI for", text);
        Assert.Contains("P(RF < IM) excludes 0.5", text);
    }

    /// <summary>Walks up from the test binary to the repository root; the file is committed beside the code that quotes it.</summary>
    private static string PreRegistrationFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, MemoWriter.PreRegistrationPath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{MemoWriter.PreRegistrationPath} was not found above {AppContext.BaseDirectory}.");
    }
}
