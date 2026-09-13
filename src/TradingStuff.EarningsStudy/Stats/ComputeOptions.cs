namespace TradingStuff.EarningsStudy.Stats;

/// <summary>
/// The compute verb's command line. <c>--reps</c> and <c>--seed</c> default to the registered values.
///
/// <c>--require-full-window</c> is the switch for the FINAL run: with it, a run that would produce a
/// PROVISIONAL memo — one whose deliverable subset is not the whole registered window — refuses,
/// writes nothing, and exits non-zero. Without it the v2 deadline fallback applies automatically and
/// the memo labels itself provisional. The default is the fallback because the deadline is the thing
/// that is fixed; the flag exists so the owed full-window rerun cannot quietly deliver a subset.
/// </summary>
public sealed record ComputeOptions(int Replications, int Seed, bool RequireFullWindow)
{
    public const string RequireFullWindowFlag = "--require-full-window";

    public static bool TryParse(IReadOnlyList<string> args, out ComputeOptions options, out string usage)
    {
        var replications = C1Registration.BootstrapReplications;
        var seed = C1Registration.BootstrapSeed;
        var requireFullWindow = false;
        usage = "";

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--reps" when i + 1 < args.Count && int.TryParse(args[i + 1], out var reps) && reps > 0:
                    replications = reps;
                    i++;
                    break;
                case "--seed" when i + 1 < args.Count && int.TryParse(args[i + 1], out var parsedSeed):
                    seed = parsedSeed;
                    i++;
                    break;
                case RequireFullWindowFlag:
                    requireFullWindow = true;
                    break;
                default:
                    options = new ComputeOptions(replications, seed, requireFullWindow);
                    usage = $"compute: cannot read the option '{args[i]}'.\n" +
                            $"usage: compute [--reps N] [--seed N] [{RequireFullWindowFlag}]\n" +
                            $"  --reps N   bootstrap replications (default {C1Registration.BootstrapReplications}, the registered value)\n" +
                            $"  --seed N   bootstrap seed (default {C1Registration.BootstrapSeed}, the registered value)\n" +
                            $"  {RequireFullWindowFlag}\n" +
                            "             refuse to write a PROVISIONAL memo: every calendar quarter of the registered\n" +
                            "             window must be fully fetched, or the run exits non-zero having written nothing";
                    return false;
            }
        }

        options = new ComputeOptions(replications, seed, requireFullWindow);
        return true;
    }
}
