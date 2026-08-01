using System.Net.Http.Json;
using TradingStuff.Contracts;

namespace TradingStuff.ExecutionService;

public interface IPortfolioProvider
{
    /// <summary>
    /// Which <see cref="PortfolioSources"/> value this instance actually is.
    /// </summary>
    /// <remarks>
    /// Reported rather than derived from configuration by whoever asks. The two are the same right
    /// up until they are not — an unrecognised <c>Portfolio:Source</c> resolves the development
    /// provider while the configuration string still reads like an opt-in — and it is precisely that
    /// divergence a caller checking coherence needs to see. Mirrors
    /// <c>IOptionMarketDataProvider.Source</c>, which exists for the same reason.
    /// </remarks>
    string Source { get; }

    Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Values accepted by the <c>Portfolio:Source</c> setting, which selects where risk inputs come from.
/// </summary>
public static class PortfolioSources
{
    /// <summary>Fixed development figures. The default, and what the test suite relies on.</summary>
    public const string Development = "development";

    /// <summary>The real IBKR account, read through the gateway.</summary>
    public const string Ibkr = "ibkr";

    /// <summary>
    /// True only for the exact opt-in value. Anything unrecognised — including null — stays on the
    /// development provider, matching how <see cref="OrderRouters.UsesIbkr"/> and the market-data
    /// source switch degrade.
    /// </summary>
    public static bool UsesIbkr(string? source) =>
        string.Equals(source, Ibkr, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Fixed portfolio figures for local work.
/// </summary>
/// <remarks>
/// Buying power is configurable, daily P&amp;L is zero, and there are no positions — so
/// <c>MAX_DAILY_LOSS</c> can never fire and the Greek limits measure only the incoming order. Fine
/// for simulated execution; not fine behind real order routing, which is what
/// <see cref="IbkrPortfolioProvider"/> exists for.
/// </remarks>
public sealed class DevelopmentPortfolioProvider(IConfiguration configuration) : IPortfolioProvider
{
    public string Source => PortfolioSources.Development;

    public Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken)
    {
        var buyingPower = Decimal("Portfolio:BuyingPower", 25_000m);
        var dailyPnL = Decimal("Portfolio:DailyPnL", 0m);

        var snapshot = new PortfolioSnapshot(
            accountId,
            buyingPower,
            dailyPnL,
            GreeksVector.Zero,
            []);

        return Task.FromResult(snapshot);
    }

    private decimal Decimal(string key, decimal fallback) =>
        decimal.TryParse(configuration[key], out var value) ? value : fallback;
}

/// <summary>
/// Reads the real account state from the IBKR gateway.
/// </summary>
/// <remarks>
/// There is deliberately no fallback to the development provider. Substituting fabricated buying
/// power when the broker is unreachable is how an order gets approved against numbers nobody
/// checked — the failure surfaces as <see cref="PortfolioUnavailableException"/> and the order does
/// not proceed.
/// </remarks>
public sealed class IbkrPortfolioProvider(HttpClient httpClient, ILogger<IbkrPortfolioProvider> logger)
    : IPortfolioProvider
{
    public string Source => PortfolioSources.Ibkr;

    public async Task<PortfolioSnapshot> GetPortfolioAsync(string accountId, CancellationToken cancellationToken)
    {
        // accountId is not forwarded on purpose. Orders are placed against the gateway's configured
        // account (IbkrOrderClient uses IBKR:AccountId), so risk must be evaluated against that same
        // account rather than whatever the caller named.
        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync("/ibkr/account/portfolio", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new PortfolioUnavailableException(
                "The IBKR gateway is unreachable, so no portfolio could be read.",
                exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);

            throw new PortfolioUnavailableException(
                $"The IBKR gateway returned {(int)response.StatusCode} for the account portfolio: {problem}");
        }

        var snapshot = await response.Content.ReadFromJsonAsync<IbkrPortfolioResponse>(cancellationToken)
                       ?? throw new PortfolioUnavailableException("The IBKR gateway returned an empty portfolio.");

        WarnAboutGaps(snapshot);

        return snapshot.Portfolio;
    }

    /// <summary>
    /// Reports what the broker could not establish, because each gap silently weakens a risk check.
    /// </summary>
    private void WarnAboutGaps(IbkrPortfolioResponse snapshot)
    {
        if (!snapshot.DailyPnLAvailable)
        {
            logger.LogWarning(
                "IBKR reported no daily P&L; MAX_DAILY_LOSS is evaluated against zero and cannot fire.");
        }

        if (!snapshot.GreeksComplete)
        {
            logger.LogWarning(
                "Greeks are missing for some open positions; the Greek limits under-report existing exposure.");
        }

        if (snapshot.NonOptionPositionCount > 0)
        {
            logger.LogWarning(
                "{Count} non-option position(s) are not represented in the portfolio snapshot; " +
                "their exposure is not counted against the Greek limits.",
                snapshot.NonOptionPositionCount);
        }
    }

    /// <summary>Mirror of the gateway's portfolio response.</summary>
    private sealed record IbkrPortfolioResponse(
        PortfolioSnapshot Portfolio,
        DateTimeOffset CapturedAt,
        bool DailyPnLAvailable,
        bool GreeksComplete,
        int OptionPositionCount,
        int NonOptionPositionCount);
}

/// <summary>
/// No trustworthy portfolio could be read, so risk cannot be evaluated and the order must not
/// proceed.
/// </summary>
public sealed class PortfolioUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
