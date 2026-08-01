using TradingStuff.Contracts;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// A chain window, plus everything a caller needs to know about how it was cut.
/// </summary>
/// <remarks>
/// This used to be a bare <c>IReadOnlyList&lt;OptionContract&gt;</c>, and that shape is what let two
/// defects hide.
/// <list type="number">
/// <item>When the spot lookup failed, the gateway logged a warning and returned the <em>entire</em>
/// listed strike set instead of a window — indistinguishable, at the caller, from a healthy
/// response. <c>NodeSelector</c> took the median strike of that response as a spot proxy, which is
/// only valid for a spot-centred window; on the degraded path the median of every listed SPXW strike
/// (400 → 10,000+) is off by thousands of points, and all 54 nodes silently bound to deep-OTM
/// contracts that then ticked normally and reported full coverage.</item>
/// <item>A caller that needs a reference price had no way to get the one the gateway already had,
/// so it invented one.</item>
/// </list>
/// Hence <see cref="SpotCentred"/> and <see cref="ReferencePrice"/>: a window is now either
/// spot-centred with a stated reference price, or it is empty with a stated reason. There is no
/// third shape, and in particular there is no shape that looks healthy and is not.
/// </remarks>
/// <param name="Contracts">The listed contracts inside the window. Empty when <see cref="SpotCentred"/> is false.</param>
/// <param name="SpotCentred">
/// True only when a reference price was actually read and the window was cut around it.
/// </param>
/// <param name="ReferencePrice">The spot price the window was centred on; null exactly when <see cref="SpotCentred"/> is false.</param>
/// <param name="Expiration">The listed expiration the contracts belong to.</param>
/// <param name="WindowLow">Lowest strike in the window.</param>
/// <param name="WindowHigh">Highest strike in the window.</param>
/// <param name="Unavailable">
/// Why the window could not be cut, when it could not be. Null on the healthy path.
/// </param>
public sealed record OptionChainResult(
    IReadOnlyList<OptionContract> Contracts,
    bool SpotCentred,
    decimal? ReferencePrice,
    DateOnly? Expiration,
    decimal? WindowLow,
    decimal? WindowHigh,
    string? Unavailable)
{
    /// <summary>A window that could not be produced, and why. Never carries contracts.</summary>
    public static OptionChainResult NotAvailable(string reason, DateOnly? expiration = null) =>
        new([], SpotCentred: false, ReferencePrice: null, expiration, WindowLow: null, WindowHigh: null, reason);
}
