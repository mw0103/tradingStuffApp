using System.Net.Http.Json;
using TradingStuff.Contracts;

namespace TradingStuff.ResearchService.Gateway;

/// <summary>
/// A chain window as the gateway cut it — mirrors the gateway's <c>OptionChainResult</c> by property
/// name, the same convention <see cref="IbkrGatewayClient"/> uses.
/// </summary>
/// <param name="Contracts">The listed contracts inside the window. Empty when <see cref="SpotCentred"/> is false.</param>
/// <param name="SpotCentred">
/// Whether the window was actually cut around a reference price the gateway read. A caller that
/// needs a spot price MUST branch on this and refuse rather than deriving one from the contracts it
/// got back — see <see cref="ReferencePrice"/>.
/// </param>
/// <param name="ReferencePrice">
/// The spot the window is centred on. Null exactly when <see cref="SpotCentred"/> is false.
/// <para>
/// This field exists because its absence cost a node grid. The gateway used to answer a failed spot
/// lookup by returning every listed strike, unmarked, and <c>NodeSelector</c> reconstructed a spot
/// proxy as the median strike of the response — valid only for a spot-centred window, and off by
/// hundreds to thousands of points on the degraded one, which silently rebound all 54 nodes to
/// deep-OTM contracts that then ticked normally and reported full coverage. The reference price the
/// gateway already had is now simply reported, so nobody has to invent one.
/// </para>
/// </param>
/// <param name="Expiration">The listed expiration these contracts belong to. It is the expiration TWS
/// actually lists, which need not be the one that was asked for.</param>
/// <param name="WindowLow">Lowest strike in the window.</param>
/// <param name="WindowHigh">Highest strike in the window.</param>
/// <param name="Unavailable">Why no window could be cut. Null on the healthy path.</param>
public sealed record ChainWindow(
    IReadOnlyList<OptionContract> Contracts,
    bool SpotCentred,
    decimal? ReferencePrice,
    DateOnly? Expiration,
    decimal? WindowLow,
    decimal? WindowHigh,
    string? Unavailable);

/// <summary>
/// Fetches option chain windows from the gateway for callers that need to know how the window was
/// cut, not just what came back.
/// </summary>
/// <remarks>
/// Separate from <see cref="IbkrGatewayClient"/> deliberately: strike selection needs to know HOW a
/// window was cut, not just what came back, and a flattened <c>IReadOnlyList&lt;OptionContract&gt;</c>
/// cannot say that. <see cref="IbkrGatewayClient"/> used to have its own <c>GetChainAsync</c> that
/// did exactly that flattening — folding a failed request into an empty list along with the healthy
/// case — and it was removed for that reason. It must not be reintroduced: a caller that cannot tell
/// "no window" from "a window with no listed strikes" will reach for the contracts themselves to
/// reconstruct what it needs, the way <c>NodeSelector</c> once derived a spot proxy as the median
/// strike of an unmarked response. That proxy was valid only for a spot-centred window and off by
/// hundreds to thousands of points on a degraded one, and it silently rebound all 54 research nodes
/// to deep-OTM contracts that then ticked normally and reported full coverage. <see cref="ChainWindow"/>
/// exists so that failure mode has no shape to hide in — see <see cref="ChainWindow.SpotCentred"/> and
/// <see cref="ChainWindow.ReferencePrice"/> above.
/// </remarks>
public sealed class OptionChainClient(HttpClient httpClient, ILogger<OptionChainClient> logger)
{
    /// <summary>
    /// The chain for <paramref name="tradingClass"/> nearest <paramref name="expiration"/>, covering
    /// <c>spot × [1 − moneynessHalfWidth, 1 + moneynessHalfWidth]</c>.
    /// </summary>
    /// <remarks>
    /// The width is a fraction of spot, never a strike count. A strike count cannot express a
    /// moneyness requirement: how far N strikes reaches depends on the local increment, which for
    /// SPX is 5 points near the money — so the 41 strikes nearest a 7,440 spot span ±1.3%, and every
    /// node target beyond that lands on the window's edge.
    /// </remarks>
    public async Task<ChainWindow> GetChainAsync(
        string underlying,
        DateOnly expiration,
        string tradingClass,
        decimal moneynessHalfWidth,
        CancellationToken cancellationToken)
    {
        var path = $"/ibkr/options/chains/{Uri.EscapeDataString(underlying)}" +
                   $"?expiration={expiration:yyyy-MM-dd}" +
                   $"&moneynessHalfWidth={moneynessHalfWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                   $"&tradingClass={Uri.EscapeDataString(tradingClass)}";

        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Chain request failed for {Underlying}/{TradingClass} near {Expiration}: {Message}",
                underlying, tradingClass, expiration, ex.Message);

            return Unavailable($"The gateway could not be reached: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Chain request failed for {Underlying}/{TradingClass} near {Expiration}: {Status}.",
                    underlying, tradingClass, expiration, response.StatusCode);

                return Unavailable($"The gateway answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var window = await response.Content.ReadFromJsonAsync<ChainWindow>(cancellationToken);

            // A null body is not an empty chain; both are unusable here, but only one of them is
            // allowed to look like a successful read.
            return window ?? Unavailable("The gateway returned an empty body.");
        }
    }

    private static ChainWindow Unavailable(string reason) =>
        new([], SpotCentred: false, ReferencePrice: null, Expiration: null,
            WindowLow: null, WindowHigh: null, reason);
}
