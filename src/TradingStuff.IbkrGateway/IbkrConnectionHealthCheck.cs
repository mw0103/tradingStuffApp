using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingStuff.IbkrGateway;

/// <summary>
/// Reports the TWS socket state so Aspire shows "unhealthy" rather than "running" while the gateway
/// is up but disconnected from TWS.
/// </summary>
public sealed class IbkrConnectionHealthCheck(IbkrConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = connection.GetStatus();

        var data = new Dictionary<string, object>
        {
            ["host"] = status.Host,
            ["port"] = status.Port,
            ["clientId"] = status.ClientId,
            ["serverVersion"] = status.ServerVersion,
            ["marketDataType"] = status.MarketDataType,
            ["tradingPermitted"] = status.TradingPermitted,
            ["inFlightRequests"] = status.InFlightRequests,
        };

        if (!status.Connected)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                status.LastError ?? $"Not connected to TWS at {status.Host}:{status.Port}.",
                data: data));
        }

        // Connected but order routing is gated — data works, so this is degraded rather than down.
        if (!status.TradingPermitted && status.TradingBlockedReason is { } reason)
        {
            return Task.FromResult(HealthCheckResult.Degraded(reason, data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Connected to TWS.", data));
    }
}
