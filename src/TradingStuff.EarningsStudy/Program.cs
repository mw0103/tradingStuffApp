using TradingStuff.EarningsStudy;
using TradingStuff.EarningsStudy.Closes;
using TradingStuff.EarningsStudy.Edgar;
using TradingStuff.EarningsStudy.Model;
using TradingStuff.EarningsStudy.Options;
using TradingStuff.EarningsStudy.Stats;
using TradingStuff.EarningsStudy.Timing;
using TradingStuff.ResearchService.Sessions;

return await StudyCli.RunAsync(args, Console.Out, CancellationToken.None);

namespace TradingStuff.EarningsStudy
{
    /// <summary>
    /// <c>dotnet run --project src/TradingStuff.EarningsStudy -- &lt;verb&gt; [--data-dir path] [verb args]</c>.
    /// Verbs run in pipeline order; each reads the previous verb's table from the data directory.
    /// </summary>
    public static class StudyCli
    {
        public const string DefaultDataDirectory = "data/earnings-c1";

        public static IReadOnlyList<IStudyStep> Steps { get; } =
        [
            new UniverseStep(),
            new EventsStep(),
            new TimingStep(),
            new ChainsStep(),
            new ClosesStep(),
            new ComputeStep()
        ];

        public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                WriteUsage(output);
                return args.Length == 0 ? 2 : 0;
            }

            var verb = args[0];
            var step = Steps.FirstOrDefault(s => string.Equals(s.Verb, verb, StringComparison.OrdinalIgnoreCase));
            if (step is null)
            {
                output.WriteLine($"Unknown verb '{verb}'.");
                WriteUsage(output);
                return 2;
            }

            var dataDirectory = DefaultDataDirectory;
            var rest = new List<string>();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--data-dir" && i + 1 < args.Length)
                {
                    dataDirectory = args[++i];
                    continue;
                }
                rest.Add(args[i]);
            }

            var context = new StudyContext(new StudyPaths(dataDirectory), new SessionClock(), output, TimeProvider.System);
            Directory.CreateDirectory(context.Paths.DataDirectory);
            context.Log($"{step.Verb}: data directory {context.Paths.DataDirectory}");

            var dropped = GateCountsFile.DropStep(context.Paths.GateCounts, step.Verb);
            if (dropped > 0) context.Log($"{step.Verb}: replaced {dropped} gate tally row(s) from a previous run");

            return await step.RunAsync(context, rest, cancellationToken);
        }

        private static void WriteUsage(TextWriter output)
        {
            output.WriteLine("Earnings vol premium study, claim C1 (docs/research/c1-preregistration-v0.md).");
            output.WriteLine("usage: earnings-study <verb> [--data-dir data/earnings-c1] [verb options]");
            output.WriteLine();
            foreach (var step in Steps)
            {
                output.WriteLine($"  {step.Verb,-10} {step.Description}");
            }
        }
    }
}
