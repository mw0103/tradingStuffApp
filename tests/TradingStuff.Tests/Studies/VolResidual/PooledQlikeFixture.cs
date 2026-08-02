using System.Globalization;
using System.Reflection;
using TradingStuff.ResearchService.Studies.VolResidual;

namespace TradingStuff.Tests.Studies.VolResidual;

/// <summary>
/// The frozen daily QLIKE loss series from the real 2010-2023 development run (1509 scored days
/// across the three registered folds), reshaped into <see cref="VolResidualFoldResult"/>s.
/// </summary>
/// <remarks>
/// <para>
/// This is an ORACLE fixture, not a snapshot of whatever the code currently does. The Diebold-Mariano
/// figures asserted against it were computed independently, in Python, outside this codebase, before
/// any of the C# adjudication existed:
/// </para>
/// <code>
/// CORRECTED vs HARX, tau=0     mean_d=+0.006016  DM=+1.526  p1=0.0636
/// CORRECTED vs HARX, tau=0.02  mean_d=+0.002034  DM=+0.517  p1=0.3027
/// HARX      vs HAR,  tau=0     mean_d=+0.018561  DM=+3.053  p1=0.0011
/// </code>
/// <para>
/// Freezing the loss series rather than re-running the study means these tests exercise the
/// statistics on a fixed input: a change to the models cannot make them pass or fail, and a change
/// to the statistics cannot hide behind a change to the models.
/// </para>
/// </remarks>
internal static class PooledQlikeFixture
{
    private const string ResourceName =
        "TradingStuff.Tests.Studies.VolResidual.Fixtures.pooled-qlike-2018-2023.csv";

    internal sealed record Row(DateOnly Date, int Fold, double Har, double Vix, double HarX, double Corrected);

    internal static IReadOnlyList<Row> Rows { get; } = Load();

    private static List<Row> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{ResourceName}' is missing. Without it the adjudication tests would " +
                "silently degrade to testing nothing.");

        using var reader = new StreamReader(stream);
        var rows = new List<Row>();

        _ = reader.ReadLine(); // header

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var parts = line.Split(',');

            rows.Add(new Row(
                DateOnly.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture),
                double.Parse(parts[4], CultureInfo.InvariantCulture),
                double.Parse(parts[5], CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    internal static IReadOnlyList<double> Losses(string modelKey) => modelKey switch
    {
        VolResidualModelKeys.Har => Rows.Select(r => r.Har).ToList(),
        VolResidualModelKeys.Vix => Rows.Select(r => r.Vix).ToList(),
        VolResidualModelKeys.HarX => Rows.Select(r => r.HarX).ToList(),
        VolResidualModelKeys.Corrected => Rows.Select(r => r.Corrected).ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(modelKey), modelKey, "Not a fixture column."),
    };

    /// <summary>
    /// The fixture as fold results the adjudicator consumes.
    /// </summary>
    /// <param name="vixRegime">
    /// Assigns each day a VIX half. The fixture predates the regime column, so the real run's
    /// train-median split is not recoverable from it; tests that care about the halves supply their
    /// own assignment and say what it represents, rather than a fabricated one masquerading as the
    /// production split.
    /// </param>
    internal static List<VolResidualFoldResult> AsFoldResults(Func<Row, string>? vixRegime = null)
    {
        vixRegime ??= _ => VolResidualVixRegimes.Low;

        return Rows
            .GroupBy(r => r.Fold)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var days = g.OrderBy(r => r.Date).Select(r => new VolResidualDailyResult(
                    r.Date,
                    $"F{r.Fold}",
                    ActualVariance: 1e-4,
                    Forecasts: new Dictionary<string, double>(),
                    Qlike: new Dictionary<string, double>
                    {
                        [VolResidualModelKeys.Har] = r.Har,
                        [VolResidualModelKeys.Vix] = r.Vix,
                        [VolResidualModelKeys.HarX] = r.HarX,
                        [VolResidualModelKeys.Corrected] = r.Corrected,
                    },
                    PriorVix: 0.0,
                    VixRegime: vixRegime(r))).ToList();

                return new VolResidualFoldResult(
                    $"F{g.Key}", days[0].Date, days[0].Date, days[0].Date, days[^1].Date, 0, days);
            })
            .ToList();
    }
}
