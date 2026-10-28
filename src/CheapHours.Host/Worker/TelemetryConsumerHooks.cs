using RabbitMQ.Client;

namespace CheapHours.Host.Worker;

/// <summary>Test-only hooks into the telemetry consumer; registered by integration tests, never in production.</summary>
internal sealed class TelemetryConsumerHooks
{
    /// <summary>Gets or sets a step run after a batch is stored and before it is acknowledged.</summary>
    public Func<IChannel, CancellationToken, Task>? AfterHandled { get; set; }
}
