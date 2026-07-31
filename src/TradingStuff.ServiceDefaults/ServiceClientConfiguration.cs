using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace TradingStuff.ServiceDefaults;

/// <summary>
/// Shared setup for service-to-service HTTP calls inside the mesh: base address plus the internal
/// bearer token.
/// </summary>
/// <remarks>
/// Lives here rather than in any one service so every caller configures internal clients
/// identically. Replace the development token with Keycloak-issued JWTs alongside
/// <c>DevelopmentJwtAuthenticationHandler</c>.
/// </remarks>
public static class ServiceClientConfiguration
{
    public static void ConfigureInternalClient(
        HttpClient httpClient,
        IConfiguration configuration,
        string baseUrlKey,
        string fallback)
    {
        httpClient.BaseAddress = new Uri(configuration[baseUrlKey] ?? fallback);

        var token = configuration["Authentication:DevelopmentToken"] ?? "dev-internal-token";
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Strips automatic retries from a client whose requests are <em>not</em> safe to repeat.
    /// </summary>
    /// <remarks>
    /// <see cref="Extensions.AddServiceDefaults"/> applies the standard resilience handler to every
    /// client, which retries on a per-attempt timeout. That is right for a read and catastrophic for
    /// an order: the gateway waits <c>IBKR:OrderSettleTimeoutSeconds</c> for an order to settle, so an
    /// order that rests longer than the attempt timeout gets re-sent and reaches the broker twice
    /// under two different broker order ids — while the caller sees only the last attempt's outcome.
    /// <para>
    /// Observed live on 2026-07-31: a resting SPXW combo was transmitted as order 16, retried as
    /// order 17, and the caller recorded only 17's rejection. Order 16 stayed working at TWS,
    /// unknown to the service that placed it.
    /// </para>
    /// <paramref name="attemptTimeout"/> must exceed the gateway's order settle timeout, or the
    /// request is abandoned while the order is still live.
    /// </remarks>
    public static IHttpClientBuilder DisableAutomaticRetries(
        this IHttpClientBuilder builder,
        TimeSpan attemptTimeout)
    {
        // RemoveAllResilienceHandlers is marked experimental. Suppressed deliberately: the alternative
        // is stacking a second handler on the default one, which leaves the retrying handler in the
        // pipeline — exactly what must not happen here.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 0;
            options.AttemptTimeout.Timeout = attemptTimeout;
            options.TotalRequestTimeout.Timeout = attemptTimeout + TimeSpan.FromSeconds(10);

            // The handler requires a sampling window of at least twice the attempt timeout.
            options.CircuitBreaker.SamplingDuration = attemptTimeout * 2;
        });

        return builder;
    }
}
