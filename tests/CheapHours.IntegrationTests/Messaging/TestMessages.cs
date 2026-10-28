using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Messaging.Contracts;

namespace CheapHours.IntegrationTests.Messaging;

/// <summary>Sample messages for the messaging tests.</summary>
internal static class TestMessages
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    public const string Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    /// <summary>A telemetry message with <paramref name="readings"/> energy-only readings, 15 s apart.</summary>
    public static TelemetryReceived Telemetry(DeviceId device, int readings = 1, long firstEnergyWh = 1000, Guid? messageId = null, string? traceparent = null, int startSeconds = 0) =>
        new(
            messageId ?? Guid.CreateVersion7(),
            T0,
            traceparent,
            device,
            "ocpp",
            Enumerable.Range(0, readings).Select(i => new DeviceReading("CP001", T0.AddSeconds(startSeconds + (15 * i)), 0, firstEnergyWh + (10 * i))).ToArray());

    public static DeviceId NewDevice() => DeviceId.Parse($"dev_{Guid.NewGuid():N}");
}
