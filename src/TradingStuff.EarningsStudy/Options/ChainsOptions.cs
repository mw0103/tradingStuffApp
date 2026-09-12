using System.Globalization;
using TradingStuff.Volatility.ThetaData;

namespace TradingStuff.EarningsStudy.Options;

/// <summary>Parsed <c>chains</c> verb options. See <see cref="TryParse"/> for precedence.</summary>
internal sealed record ChainsOptions(
    string ThetaUrl, string SnapshotKind, TimeSpan SnapshotTime, int DelayMs, int Concurrency, int? Limit)
{
    public const string DefaultSnapshotKind = "eod";

    /// <summary>
    /// CLI flag wins; then the matching Aspire-convention environment variable; then the client's own
    /// default. Unlike <c>closes</c>' <c>--gateway-url</c>, a missing Theta URL defaults rather than
    /// failing: the local Terminal always listens on the same well-known port
    /// (<see cref="ThetaDataOptions.BaseAddress"/>'s default), so requiring the flag every run would
    /// be pure friction for the common case.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out ChainsOptions options, out string? error)
    {
        string? thetaUrl = null;
        var snapshotKind = DefaultSnapshotKind;
        var snapshotTime = new ThetaDataOptions().SnapshotTimeOfDay;
        var delayMs = 0;
        var concurrency = 1;
        int? limit = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--theta-url":
                    if (!TryTakeValue(args, ref i, out var urlValue))
                        return Fail("--theta-url requires a value.", out options, out error);
                    thetaUrl = urlValue;
                    break;

                case "--snapshot":
                    if (!TryTakeValue(args, ref i, out var snapshotValue))
                        return Fail("--snapshot requires a value.", out options, out error);
                    if (snapshotValue is not ("eod" or "minute"))
                        return Fail($"--snapshot must be 'eod' or 'minute', was '{snapshotValue}'.", out options, out error);
                    snapshotKind = snapshotValue;
                    break;

                case "--snapshot-time":
                    if (!TryTakeValue(args, ref i, out var timeValue))
                        return Fail("--snapshot-time requires a value.", out options, out error);
                    if (!TryParseTimeOfDay(timeValue, out snapshotTime))
                        return Fail($"--snapshot-time must be HH:mm, was '{timeValue}'.", out options, out error);
                    break;

                case "--delay-ms":
                    if (!TryTakeValue(args, ref i, out var delayValue))
                        return Fail("--delay-ms requires a value.", out options, out error);
                    if (!int.TryParse(delayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out delayMs) || delayMs < 0)
                        return Fail($"--delay-ms must be a non-negative integer, was '{delayValue}'.", out options, out error);
                    break;

                case "--concurrency":
                    if (!TryTakeValue(args, ref i, out var concurrencyValue))
                        return Fail("--concurrency requires a value.", out options, out error);
                    if (!int.TryParse(concurrencyValue, out concurrency) || concurrency < 1)
                        return Fail($"--concurrency must be a positive integer, was '{concurrencyValue}'.", out options, out error);
                    break;

                case "--limit":
                    if (!TryTakeValue(args, ref i, out var limitValue))
                        return Fail("--limit requires a value.", out options, out error);
                    if (!int.TryParse(limitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limitParsed) || limitParsed < 1)
                        return Fail($"--limit must be a positive integer, was '{limitValue}'.", out options, out error);
                    limit = limitParsed;
                    break;

                default:
                    return Fail($"Unrecognised argument '{args[i]}'.", out options, out error);
            }
        }

        thetaUrl ??= Environment.GetEnvironmentVariable("THETA__BaseAddress") ?? new ThetaDataOptions().BaseAddress;

        options = new ChainsOptions(thetaUrl, snapshotKind, snapshotTime, delayMs, concurrency, limit);
        error = null;
        return true;
    }

    private static bool TryParseTimeOfDay(string text, out TimeSpan timeOfDay)
    {
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out timeOfDay))
        {
            return true;
        }

        timeOfDay = default;
        return false;
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, out string value)
    {
        if (i + 1 >= args.Count)
        {
            value = "";
            return false;
        }

        value = args[++i];
        return true;
    }

    private static bool Fail(string message, out ChainsOptions options, out string? error)
    {
        options = null!;
        error = message;
        return false;
    }
}
