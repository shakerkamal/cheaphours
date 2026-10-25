using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CheapHours.Host.Gateway;

/// <summary>Healthy when the gateway has finished its startup work and is not shutting down.</summary>
/// <param name="state">The gateway state.</param>
internal sealed class GatewayReadinessHealthCheck(GatewayState state) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state switch
        {
            { IsDraining: true } => HealthCheckResult.Unhealthy("Shutting down."),
            { IsInitialised: false } => HealthCheckResult.Unhealthy("Starting."),
            _ => HealthCheckResult.Healthy(),
        });
}
