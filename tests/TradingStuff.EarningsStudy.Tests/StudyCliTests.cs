using TradingStuff.EarningsStudy;

namespace TradingStuff.EarningsStudy.Tests;

public sealed class StudyCliTests
{
    [Fact]
    public async Task No_verb_prints_usage_and_fails()
    {
        var output = new StringWriter();
        var code = await StudyCli.RunAsync([], output, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("compute", output.ToString());
    }

    [Fact]
    public async Task Unknown_verb_is_refused()
    {
        var output = new StringWriter();
        var code = await StudyCli.RunAsync(["frobnicate"], output, CancellationToken.None);
        Assert.Equal(2, code);
        Assert.Contains("Unknown verb", output.ToString());
    }

    [Fact]
    public void Every_verb_is_unique_and_in_pipeline_order()
    {
        var verbs = StudyCli.Steps.Select(s => s.Verb).ToList();
        Assert.Equal(["universe", "events", "timing", "chains", "closes", "compute"], verbs);
        Assert.Equal(verbs.Count, verbs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
