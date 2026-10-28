using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CheapHours.Host.Api;

/// <summary>Healthy once the API has created its indexes and seeded the configured keys.</summary>
/// <param name="state">The API state.</param>
internal sealed class ApiReadinessHealthCheck(ApiState state) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsReady ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Starting."));
}
