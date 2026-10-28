using CheapHours.Core.Devices;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Telemetry;

/// <summary>One delivered message's readings, as the telemetry writer stores them.</summary>
/// <param name="MessageId">The message id; readings are stored once per id and consumer.</param>
/// <param name="DeviceId">The device the readings belong to.</param>
/// <param name="ProviderId">The provider that reported them.</param>
/// <param name="Readings">The readings, in the order the device reported them.</param>
/// <param name="IsRepeat">
/// Whether this message may have been written before without being recorded as handled (a redelivery, a
/// retry or a repeated batch); its earlier readings are then removed first.
/// </param>
public sealed record TelemetryBatchItem(Guid MessageId, DeviceId DeviceId, string ProviderId, IReadOnlyList<DeviceReading> Readings, bool IsRepeat);
