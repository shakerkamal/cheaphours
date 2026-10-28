namespace CheapHours.Core.Devices;

/// <summary>
/// Something a provider observed about a device, keyed by the vendor's device id. The set is closed: the
/// private constructor stops other subtypes. Translators report bad input as <see cref="MalformedInput"/>
/// instead of throwing.
/// </summary>
public abstract record DeviceEvent
{
    private DeviceEvent(DateTimeOffset at) => At = Guard.Utc(at);

    /// <summary>Gets when the event happened (UTC).</summary>
    public DateTimeOffset At { get; }

    /// <summary>The device reported a measurement.</summary>
    public sealed record ReadingReported : DeviceEvent
    {
        /// <summary>Creates the event; its time is the reading's timestamp.</summary>
        /// <param name="reading">The normalised reading.</param>
        public ReadingReported(DeviceReading reading)
            : base((reading ?? throw new ArgumentNullException(nameof(reading))).MeasurementTime) => Reading = reading;

        /// <summary>Gets the reading.</summary>
        public DeviceReading Reading { get; }
    }

    /// <summary>The device reported a new operating status.</summary>
    public sealed record StatusChanged : DeviceEvent
    {
        /// <summary>Creates the event.</summary>
        /// <param name="externalId">The vendor's id for the device.</param>
        /// <param name="at">When the status changed (UTC).</param>
        /// <param name="status">The new status.</param>
        public StatusChanged(string externalId, DateTimeOffset at, DeviceStatus status)
            : base(at)
        {
            ExternalId = Guard.NotBlank(externalId);
            Status = status;
        }

        /// <summary>Gets the vendor's id for the device.</summary>
        public string ExternalId { get; }

        /// <summary>Gets the new status.</summary>
        public DeviceStatus Status { get; }
    }

    /// <summary>The device connected (for example an OCPP <c>BootNotification</c>).</summary>
    public sealed record Connected : DeviceEvent
    {
        /// <summary>Creates the event.</summary>
        /// <param name="externalId">The vendor's id for the device.</param>
        /// <param name="at">When it connected (UTC).</param>
        /// <param name="hardware">What the device reported about itself, or <see langword="null"/> when it reported nothing.</param>
        public Connected(string externalId, DateTimeOffset at, DeviceHardware? hardware = null)
            : base(at)
        {
            ExternalId = Guard.NotBlank(externalId);
            Hardware = hardware;
        }

        /// <summary>Gets the vendor's id for the device.</summary>
        public string ExternalId { get; }

        /// <summary>Gets what the device reported about itself, or <see langword="null"/> when unknown.</summary>
        public DeviceHardware? Hardware { get; }
    }

    /// <summary>The device disconnected or stopped answering.</summary>
    public sealed record Disconnected : DeviceEvent
    {
        /// <summary>Creates the event.</summary>
        /// <param name="externalId">The vendor's id for the device.</param>
        /// <param name="at">When it was seen to disconnect (UTC).</param>
        public Disconnected(string externalId, DateTimeOffset at)
            : base(at) => ExternalId = Guard.NotBlank(externalId);

        /// <summary>Gets the vendor's id for the device.</summary>
        public string ExternalId { get; }
    }

    /// <summary>The vendor reports the device as unknown or deleted; it must never be ignored silently.</summary>
    public sealed record DeviceRemoved : DeviceEvent
    {
        /// <summary>Creates the event.</summary>
        /// <param name="externalId">The vendor's id for the device.</param>
        /// <param name="at">When the removal was observed (UTC).</param>
        public DeviceRemoved(string externalId, DateTimeOffset at)
            : base(at) => ExternalId = Guard.NotBlank(externalId);

        /// <summary>Gets the vendor's id for the device.</summary>
        public string ExternalId { get; }
    }

    /// <summary>Input could not be understood (bad JSON, schema mismatch); reported instead of thrown.</summary>
    public sealed record MalformedInput : DeviceEvent
    {
        /// <summary>Creates the event.</summary>
        /// <param name="at">When the input was received (UTC).</param>
        /// <param name="reason">What was wrong with it.</param>
        public MalformedInput(DateTimeOffset at, string reason)
            : base(at) => Reason = Guard.NotBlank(reason);

        /// <summary>Gets what was wrong with the input.</summary>
        public string Reason { get; }
    }
}
