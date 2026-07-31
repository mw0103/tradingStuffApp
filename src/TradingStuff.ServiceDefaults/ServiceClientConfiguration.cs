using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

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
}
