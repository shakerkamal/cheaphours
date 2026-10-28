using CheapHours.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CheapHours.Host.Gateway;

/// <summary>Ready only while the broker connection is open; the description never holds connection details.</summary>
internal sealed class RabbitMqHealthCheck(RabbitMqConnection broker) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(broker.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("The message broker connection is not open."));
}
