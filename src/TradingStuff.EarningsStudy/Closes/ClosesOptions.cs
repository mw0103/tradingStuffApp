namespace TradingStuff.EarningsStudy.Closes;

/// <summary>Parsed <c>closes</c> verb options. See <see cref="TryParse"/> for precedence.</summary>
internal sealed record ClosesOptions(string GatewayUrl, string Token, string Duration, int Concurrency)
{
    /// <summary>The window 2022-2025 plus the 20 trading days before it and the exits after it, in one call per name.</summary>
    public const string DefaultDuration = "5 Y";

    public const string DefaultToken = "dev-internal-token";

    /// <summary>
    /// CLI flag wins; then the matching Aspire-convention environment variable; then (token only) a
    /// literal default. <c>--gateway-url</c> has no literal default because a batch pull against the
    /// wrong gateway is not a safe thing to default silently — the caller must say where to send it,
    /// once, either way.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out ClosesOptions options, out string? error)
    {
        string? gatewayUrl = null;
        string? token = null;
        var duration = DefaultDuration;
        var concurrency = 1;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--gateway-url":
                    if (!TryTakeValue(args, ref i, out var gatewayValue))
                    {
                        return Fail("--gateway-url requires a value.", out options, out error);
                    }
                    gatewayUrl = gatewayValue;
                    break;

                case "--token":
                    if (!TryTakeValue(args, ref i, out var tokenValue))
                    {
                        return Fail("--token requires a value.", out options, out error);
                    }
                    token = tokenValue;
                    break;

                case "--duration":
                    if (!TryTakeValue(args, ref i, out var durationValue))
                    {
                        return Fail("--duration requires a value.", out options, out error);
                    }
                    duration = durationValue;
                    break;

                case "--concurrency":
                    if (!TryTakeValue(args, ref i, out var concurrencyValue))
                    {
                        return Fail("--concurrency requires a value.", out options, out error);
                    }
                    if (!int.TryParse(concurrencyValue, out concurrency) || concurrency < 1)
                    {
                        return Fail(
                            $"--concurrency must be a positive integer, was '{concurrencyValue}'.",
                            out options, out error);
                    }
                    break;

                default:
                    return Fail($"Unrecognised argument '{args[i]}'.", out options, out error);
            }
        }

        gatewayUrl ??= Environment.GetEnvironmentVariable("IBKR__GatewayUrl");
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return Fail(
                "closes requires --gateway-url <url> or the IBKR__GatewayUrl environment variable.",
                out options, out error);
        }

        token ??= Environment.GetEnvironmentVariable("Authentication__DevelopmentToken") ?? DefaultToken;

        options = new ClosesOptions(gatewayUrl, token, duration, concurrency);
        error = null;
        return true;
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

    private static bool Fail(string message, out ClosesOptions options, out string? error)
    {
        options = null!;
        error = message;
        return false;
    }
}
