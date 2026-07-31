using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using TradingStuff.ServiceDefaults;

namespace TradingStuff.Tests;

/// <summary>
/// Coverage for the internal HTTP client setup.
/// </summary>
/// <remarks>
/// Regression: <see cref="ServiceClientConfiguration.DisableAutomaticRetries"/> originally set
/// <c>MaxRetryAttempts = 0</c>, which passes compilation and every unit test but fails options
/// validation at host startup — <c>the field Retry.MaxRetryAttempts must be between 1 and
/// Int32.MaxValue</c> — taking ExecutionService down on boot. Resolving a client through the factory
/// builds the resilience pipeline and validates its options, which is what catches that.
/// </remarks>
public sealed class ServiceClientConfigurationTests
{
    [Fact]
    public void Disabling_retries_produces_a_client_that_can_actually_be_built()
    {
        var services = new ServiceCollection();

        services.AddHttpClient("orders", client => client.BaseAddress = new Uri("http://localhost"))
            .DisableAutomaticRetries(TimeSpan.FromSeconds(60));

        using var provider = services.BuildServiceProvider();

        // Creating the client materialises the handler pipeline and validates its options.
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Assert.NotNull(client);
    }

    [Fact]
    public async Task Disabling_retries_refuses_every_outcome_and_waits_out_a_settling_order()
    {
        var services = new ServiceCollection();

        services.AddHttpClient("orders", client => client.BaseAddress = new Uri("http://localhost"))
            .DisableAutomaticRetries(TimeSpan.FromSeconds(60));

        using var provider = services.BuildServiceProvider();

        // The standard handler owns timing — it sets HttpClient.Timeout to infinite — so the
        // attempt timeout has to be read off the options, not the client.
        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get("orders-standard");

        // Must exceed IBKR:OrderSettleTimeoutSeconds (20s), or the request is abandoned while the
        // order is still live at the broker.
        Assert.True(options.AttemptTimeout.Timeout > TimeSpan.FromSeconds(20));
        Assert.True(options.TotalRequestTimeout.Timeout >= options.AttemptTimeout.Timeout);

        // Nothing is retryable: a transient failure on an order is the caller's to resolve, never
        // the pipeline's to repeat.
        Assert.False(await options.Retry.ShouldHandle(
            new RetryPredicateArguments<HttpResponseMessage>(
                ResilienceContextPool.Shared.Get(),
                Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                attemptNumber: 0)));
    }
}
